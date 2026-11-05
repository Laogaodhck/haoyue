using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Scheduling;

/// <summary>后台任务状态机：running → completed | failed | cancelled。</summary>
public static class BackgroundTaskStatus
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>后台任务的对外快照（ daemon RPC 返回值）。</summary>
public sealed record BackgroundTaskInfo(
    string Id,
    string Prompt,
    string WorkspaceRoot,
    string Status,
    string? SessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EndedAt,
    string? Error,
    string? Output);

/// <summary>执行后台任务的 agent 回合；测试可注入替身。</summary>
public delegate Task<AgentTurnResult> BackgroundTurnRunner(
    WorkspaceInfo workspace, AgentSession session, string prompt, CancellationToken ct);

/// <summary>长任务转后台执行的管理面：启动、查询、取消。</summary>
public interface IBackgroundTaskService
{
    /// <summary>提交一个后台回合，立即返回任务快照；回合在后台继续执行。</summary>
    BackgroundTaskInfo Start(WorkspaceInfo workspace, string prompt, string? title = null);

    BackgroundTaskInfo? Get(string id);

    /// <summary>全部任务（新→旧）。终态任务保留最近 <see cref="HistoryLimit"/> 条。</summary>
    IReadOnlyList<BackgroundTaskInfo> List();

    /// <summary>取消一个仍在运行的后台任务；已终态的任务返回 null。</summary>
    BackgroundTaskInfo? Cancel(string id);
}

/// <summary>
/// Daemon 托管的后台任务队列。每个任务在隔离运行时中跑一个完整 agent 回合
/// （共享 daemon 的 HTTP 连接池 / 熔断器 / 文件锁），进度经共享事件总线广播，
/// 会话持久化可回放——前端断开后可用 events.recent / session resume 重连进度。
/// </summary>
public sealed class BackgroundTaskService : IBackgroundTaskService, IDisposable
{
    /// <summary>并发上限：隔离运行时各自持有模型上下文与 MCP 连接，无限制会拖垮机器。</summary>
    public const int MaxConcurrent = 4;

    /// <summary>终态任务的历史保留条数。</summary>
    public const int HistoryLimit = 100;

    private readonly HaoyueRuntime _runtime;
    private readonly IFileLockCoordinator _fileLocks;
    private readonly LlmHttpFactory _sharedHttp;
    private readonly CircuitBreaker _sharedBreaker;
    private readonly LocalModelCache? _sharedLocalModels;
    private readonly BackgroundTurnRunner _turnRunner;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
    private readonly ConcurrentDictionary<string, Entry> _tasks = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public required string Id { get; init; }
        public required string Prompt { get; init; }
        public required WorkspaceInfo Workspace { get; init; }
        public string Status { get; set; } = BackgroundTaskStatus.Running;
        public string? SessionId { get; set; }
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? EndedAt { get; set; }
        public string? Error { get; set; }
        public string? Output { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
        /// <summary>是否已拿到并发槽开跑；排队阶段取消要能立刻收敛，而不是空操作。</summary>
        public bool Started { get; set; }
        /// <summary>状态与 Cancellation 的同步点：Cancel 与 RunAsync 收尾互斥。</summary>
        public readonly object Gate = new();
    }

    public BackgroundTaskService(
        HaoyueRuntime runtime,
        IFileLockCoordinator fileLocks,
        LlmHttpFactory sharedHttp,
        CircuitBreaker sharedBreaker,
        LocalModelCache? sharedLocalModels = null,
        BackgroundTurnRunner? turnRunner = null)
    {
        _runtime = runtime;
        _fileLocks = fileLocks;
        _sharedHttp = sharedHttp;
        _sharedBreaker = sharedBreaker;
        _sharedLocalModels = sharedLocalModels;
        _turnRunner = turnRunner ?? RunIsolatedTurnAsync;
    }

    public BackgroundTaskInfo Start(WorkspaceInfo workspace, string prompt, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("后台任务的 prompt 不能为空。", nameof(prompt));

        var id = $"bg-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        // 项目任务落到项目根下的 .haoyue/tasks/<id>/，产物也写在那里；无项目归属
        // 的全局任务保持原落点。绑定失败（目录越界/项目根不存在）直接拒绝受理，
        // 绝不让任务悄悄写回全局状态目录。
        var scoped = TaskScopes.Bind(workspace, id);
        var entry = new Entry { Id = id, Prompt = prompt, Workspace = scoped };
        // 取消令牌在受理时创建（不是拿到并发槽后），排队任务才能被真正取消。
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        entry.Cancellation = runCts;
        _tasks[id] = entry;
        PruneHistory();

        _ = Task.Run(() => RunAsync(entry, runCts, title), CancellationToken.None);
        return Snapshot(entry);
    }

    public BackgroundTaskInfo? Get(string id) =>
        _tasks.TryGetValue(id, out var entry) ? Snapshot(entry) : null;

    public IReadOnlyList<BackgroundTaskInfo> List() =>
        // 显式 ToList()：daemon RPC 用 HaoyueJsonContext 注册的 List<BackgroundTaskInfo>
        // 序列化 task.list 响应，集合表达式在 IReadOnlyList 目标下会生成数组，
        // object 重载拆箱为 List 抛 InvalidCastException（2026-11-03 实测 task.list 失败）。
        _tasks.Values.OrderByDescending(e => e.CreatedAt).Select(Snapshot).ToList();

    public BackgroundTaskInfo? Cancel(string id)
    {
        if (!_tasks.TryGetValue(id, out var entry)) return null;
        lock (entry.Gate)
        {
            if (entry.EndedAt is not null || entry.Cancellation is null) return null;
            entry.Cancellation.Cancel();
            if (!entry.Started)
            {
                // 仍在排队：同步收敛为 cancelled，RPC 立刻返回正确快照；
                // RunAsync 醒来只负责发一次终态事件，不会真的开跑。
                entry.Status = BackgroundTaskStatus.Cancelled;
                entry.EndedAt = DateTimeOffset.UtcNow;
                entry.Error = "任务在排队中被取消。";
            }
        }
        return Snapshot(entry);
    }

    private async Task RunAsync(Entry entry, CancellationTokenSource runCts, string? title)
    {
        var runCt = runCts.Token;
        var acquired = false;
        var timeoutSeconds = Math.Clamp(
            _runtime.ConfigStore.Config.Agent.ScheduledTurnTimeoutSeconds, 60, 86_400);

        try
        {
            // 并发闸：超限时排队等待。用任务自己的令牌等待，排队阶段也能被取消。
            await _slots.WaitAsync(runCt).ConfigureAwait(false);
            acquired = true;
            lock (entry.Gate)
            {
                // 拿到槽之前被取消的任务已在 Cancel 里收敛，不必再跑。
                if (runCt.IsCancellationRequested)
                {
                    entry.Status = BackgroundTaskStatus.Cancelled;
                    entry.Error ??= "任务在排队中被取消。";
                    return;
                }
                entry.Started = true;
            }
            runCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var session = _runtime.Sessions.Create(
                entry.Workspace,
                reasoningLevel: _runtime.ConfigStore.Config.Agent.ReasoningLevel,
                networkEnabled: true);
            entry.SessionId = session.Header.Id;
            _runtime.Sessions.UpdateMetadata(
                entry.Workspace, session.Header.Id, title: $"{title ?? "后台任务"}（{entry.Id}）");

            var result = await _turnRunner(entry.Workspace, session, entry.Prompt, runCt).ConfigureAwait(false);
            entry.Output = result.Text;
            entry.Error = result.Error;
            lock (entry.Gate)
            {
                // 取消后即使回合返回成功，也不能把最终状态覆盖成 completed。
                entry.Status = runCt.IsCancellationRequested || result.Cancelled
                    ? BackgroundTaskStatus.Cancelled
                    : !string.IsNullOrWhiteSpace(result.Error)
                        ? BackgroundTaskStatus.Failed
                        : BackgroundTaskStatus.Completed;
            }
        }
        catch (OperationCanceledException) when (runCt.IsCancellationRequested)
        {
            lock (entry.Gate)
            {
                entry.Status = BackgroundTaskStatus.Cancelled;
                entry.Error ??= $"任务被取消（或超过 {timeoutSeconds} 秒时限）。";
            }
        }
        catch (Exception ex)
        {
            lock (entry.Gate)
            {
                entry.Status = BackgroundTaskStatus.Failed;
                entry.Error = ex.Message;
            }
        }
        finally
        {
            // 只有真正拿到槽才归还，避免等待被取消时误放信号量。
            if (acquired) _slots.Release();
            lock (entry.Gate)
            {
                entry.EndedAt ??= DateTimeOffset.UtcNow;
                entry.Cancellation = null;
            }
            runCts.Dispose();
            _runtime.Events.Publish(new BackgroundTaskEvent(
                entry.Id, entry.Status, entry.SessionId, entry.Error));
        }
    }

    private async Task<AgentTurnResult> RunIsolatedTurnAsync(
        WorkspaceInfo workspace, AgentSession session, string prompt, CancellationToken ct)
    {
        var owner = $"{session.Header.Id}/{Guid.NewGuid().ToString("N")[..8]}";
        await using var turnRuntime = HaoyueRuntime.CreateIsolated(workspace, _fileLocks, owner, services =>
        {
            services.AddSingleton<ILlmHttpFactory>(_sharedHttp);
            services.AddSingleton(_sharedBreaker);
            if (_sharedLocalModels is not null) services.AddSingleton(_sharedLocalModels);
        });
        turnRuntime.Prompts.SetWorkspaceRoot(workspace.IsGlobal ? null : workspace.PromptsDir);
        turnRuntime.Skills.Attach(workspace);
        if (turnRuntime.Mcp.LoadServerConfigs(workspace).Count > 0)
            await turnRuntime.Mcp.ConnectAllAsync(workspace, ct).ConfigureAwait(false);
        return await turnRuntime.Agent.RunTurnAsync(session, workspace, prompt, ct).ConfigureAwait(false);
    }

    private void PruneHistory()
    {
        var finished = _tasks.Values
            .Where(e => e.Status != BackgroundTaskStatus.Running)
            .OrderByDescending(e => e.EndedAt ?? e.CreatedAt)
            .Skip(HistoryLimit)
            .Select(e => e.Id)
            .ToList();
        foreach (var id in finished)
        {
            if (!_tasks.TryRemove(id, out var removed)) continue;
            // 历史已被裁剪的任务，其任务目录一并清理，避免 .haoyue/tasks 无限增长。
            TaskScopes.Remove(removed.Workspace, id);
        }
    }

    private static BackgroundTaskInfo Snapshot(Entry entry) => new(
        entry.Id, entry.Prompt, entry.Workspace.Root, entry.Status,
        entry.SessionId, entry.CreatedAt, entry.EndedAt, entry.Error, entry.Output);

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _slots.Dispose();
    }
}
