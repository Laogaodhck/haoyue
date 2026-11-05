using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Daemon;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Scheduling;

/// <summary>Executes a scheduled task's agent turn; overridable in tests.</summary>
public delegate Task<AgentTurnResult> ScheduleTurnRunner(
    WorkspaceInfo workspace, AgentSession session, string prompt, CancellationToken ct);

/// <summary>Manual trigger surface used by daemon admin methods.</summary>
public interface IScheduleService
{
    /// <summary>Runs a task to completion (used by tests and legacy callers).</summary>
    Task RunNowAsync(string id, CancellationToken ct);

    /// <summary>
    /// Triggers a task without waiting: the run continues in the background while
    /// the caller is acknowledged immediately.
    /// </summary>
    void StartRun(string id);
}

/// <summary>
/// Background scheduler hosted by the daemon. Fires enabled tasks when their cron
/// expression comes due, runs one agent turn per occurrence (in an isolated runtime,
/// sharing the daemon's HTTP pool / circuit breaker / file locks) and records the
/// outcome. A task that is still running when its next tick arrives is skipped.
/// </summary>
public sealed class ScheduleService : IScheduleService, IAsyncDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UpcomingNoticeWindow = TimeSpan.FromMinutes(1);

    private readonly IScheduleStore _store;
    private readonly HaoyueRuntime _runtime;
    private readonly IFileLockCoordinator _fileLocks;
    private readonly LlmHttpFactory _sharedHttp;
    private readonly CircuitBreaker _sharedBreaker;
    private readonly LocalModelCache? _sharedLocalModels;
    private readonly ScheduleTurnRunner _turnRunner;
    private readonly ScheduleWebhookNotifier _webhook;
    private readonly ApprovalRegistry? _approvals;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _running = new();
    private readonly ConcurrentDictionary<string, string> _upcomingNotified = new();
    private readonly CancellationTokenSource _dispose = new();
    private readonly CancellationTokenSource _lifetime = new();
    private TimeProvider _clock = TimeProvider.System;

    public ScheduleService(
        IScheduleStore store,
        HaoyueRuntime runtime,
        IFileLockCoordinator fileLocks,
        LlmHttpFactory sharedHttp,
        CircuitBreaker sharedBreaker,
        ScheduleTurnRunner? turnRunner = null,
        TimeProvider? clock = null,
        LocalModelCache? sharedLocalModels = null,
        ScheduleWebhookNotifier? webhook = null,
        ApprovalRegistry? approvals = null)
    {
        _store = store;
        _runtime = runtime;
        _fileLocks = fileLocks;
        _sharedHttp = sharedHttp;
        _sharedBreaker = sharedBreaker;
        _sharedLocalModels = sharedLocalModels;
        _turnRunner = turnRunner ?? RunIsolatedTurnAsync;
        _webhook = webhook ?? new ScheduleWebhookNotifier();
        _approvals = approvals;
        if (clock is not null) _clock = clock;
    }

    /// <summary>Runs the scheduler loop until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _dispose.Token);
        while (!linked.IsCancellationRequested)
        {
            try
            {
                await TickAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // A failed tick must never kill the scheduler; retry on the next interval.
            }
            try
            {
                await Task.Delay(TickInterval, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Runs every enabled task whose next run time has passed.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        foreach (var task in _store.List())
        {
            if (!task.Enabled || _running.ContainsKey(task.Id)) continue;
            if (task.NextRunAt is not { } next) continue;
            if (next > now)
            {
                PublishUpcomingNoticeIfNeeded(task, now, next);
                continue;
            }
            await RunTaskAsync(task, ct).ConfigureAwait(false);
        }
    }

    private void PublishUpcomingNoticeIfNeeded(ScheduledTask task, DateTimeOffset now, DateTimeOffset runAt)
    {
        if (runAt - now > UpcomingNoticeWindow) return;
        var key = runAt.ToString("O");
        if (_upcomingNotified.TryGetValue(task.Id, out var notified) && notified == key) return;
        _upcomingNotified[task.Id] = key;
        _runtime.Events.Publish(new ScheduledTaskUpcomingEvent(task.Id, task.DisplayName, runAt));
    }

    /// <summary>Runs a task immediately, then recomputes its next occurrence.</summary>
    public async Task RunNowAsync(string id, CancellationToken ct)
    {
        var task = _store.Get(id) ?? throw new InvalidOperationException($"Scheduled task not found: {id}");
        await RunTaskAsync(task, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Triggers a task without blocking the caller. A scheduled task that is still
    /// running from a previous trigger is silently skipped.
    /// </summary>
    public void StartRun(string id)
    {
        var task = _store.Get(id) ?? throw new InvalidOperationException($"Scheduled task not found: {id}");
        if (!_running.ContainsKey(task.Id)) _ = RunTaskAsync(task, _lifetime.Token);
    }

    private async Task RunTaskAsync(ScheduledTask task, CancellationToken ct)
    {
        // Serialize executions so scheduled runs cannot pile up on the same machine,
        // and skip a task that is still running from a previous tick.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        if (!_running.TryAdd(task.Id, 0))
        {
            _gate.Release();
            return;
        }

        // A hung turn must not wedge the scheduler forever: give every run a hard
        // wall-clock budget, then record the timeout and free the task's slot.
        var timeoutSeconds = Math.Clamp(_runtime.ConfigStore.Config.Agent.ScheduledTurnTimeoutSeconds, 60, 86_400);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        runCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var runCt = runCts.Token;
        string? sessionId = null;

        try
        {
            // 回合身份：让这次定时运行产生的事件（含审批审计）都能归到具体计划任务，
            // 而不是像无回合事件那样 TurnId 为 null。
            using var stamp = TurnStamp.BeginScope(
                $"schedule:{task.Id}:{Guid.NewGuid().ToString("N")[..8]}");
            var workspace = TaskScopes.Bind(ResolveWorkspace(task), $"schedule-{task.Id}");
            var session = _runtime.Sessions.Create(
                workspace,
                reasoningLevel: _runtime.ConfigStore.Config.Agent.ReasoningLevel,
                networkEnabled: true);
            sessionId = session.Header.Id;
            _runtime.Sessions.UpdateMetadata(workspace, session.Header.Id, title: $"{task.Name}（计划任务）");
            var result = await _turnRunner(workspace, session, task.Prompt, runCt).ConfigureAwait(false);
            await RecordRunAsync(
                task.Id,
                sessionId,
                result.Error is null ? ScheduleRunStatus.Success : ScheduleRunStatus.Error,
                result.Error,
                result.Text).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await RecordRunAsync(task.Id, sessionId, ScheduleRunStatus.Cancelled, "任务已取消").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runCt.IsCancellationRequested)
        {
            await RecordRunAsync(
                task.Id, sessionId, ScheduleRunStatus.Cancelled,
                $"任务执行超过 {timeoutSeconds} 秒，已中止").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RecordRunAsync(task.Id, sessionId, ScheduleRunStatus.Error, ex.Message).ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(task.Id, out _);
            _gate.Release();
        }
    }

    private async Task<ScheduledTask> RecordRunAsync(
        string id,
        string? sessionId,
        string status,
        string? error = null,
        string? output = null)
    {
        var updated = _store.RecordRun(id, status, error, output, sessionId);
        _runtime.Events.Publish(new ScheduledTaskCompletedEvent(
            updated.Id, updated.DisplayName, sessionId, status, updated.LastError, updated.LastOutput));

        // Fire-and-forget webhook: the scheduler must never wait on (or fail with)
        // an external endpoint; CancellationToken.None because runCt may already be
        // cancelled exactly when a timeout notification matters most.
        var webhookUrl = _runtime.ConfigStore.Config.Agent.ScheduleWebhookUrl;
        if (!string.IsNullOrWhiteSpace(webhookUrl))
            _ = Task.Run(() => _webhook.NotifyAsync(webhookUrl, updated, sessionId, CancellationToken.None));

        // 事件钩子（schedule.completed）：默认 fire-and-forget；配置了「等待完成」的钩子
        // 才会等待，且超时/失败都不阻断调度主流程。
        await HookRunner.TriggerAsync("schedule.completed", new JsonObject
        {
            ["taskId"] = updated.Id,
            ["name"] = updated.DisplayName,
            ["status"] = status,
            ["sessionId"] = sessionId,
            ["error"] = error,
        }, _runtime.ConfigStore);
        return updated;
    }

    private WorkspaceInfo ResolveWorkspace(ScheduledTask task)
    {
        // 有项目归属的定时任务必须落回该项目；路径失效时记录运行错误，
        // 不再静默改投全局目录或沿路径向上误落到别的项目。
        if (string.IsNullOrWhiteSpace(task.Workspace))
            return _runtime.Workspaces.CreateGlobal();

        string fullPath;
        try { fullPath = Path.GetFullPath(task.Workspace); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"计划任务的工作区路径无效：{task.Workspace}", ex);
        }

        if (!Directory.Exists(fullPath))
            throw new InvalidOperationException($"计划任务的工作区不存在：{fullPath}");
        return _runtime.Workspaces.Detect(fullPath);
    }

    /// <summary>
    /// 构建本次定时运行使用的隔离运行时。审批登记簿必须与手动回合共用同一个实例：
    /// 否则隔离容器会回落到 <see cref="HaoyueRuntime"/> 默认的非交互实例，高风险工具
    /// 会在「无人值守」路径上被静默放行（P0）。抽成独立方法也便于测试注入脚本模型。
    /// </summary>
    internal HaoyueRuntime CreateTurnRuntime(
        WorkspaceInfo workspace, string owner, Action<IServiceCollection>? configure = null)
    {
        return HaoyueRuntime.CreateIsolated(workspace, _fileLocks, owner, services =>
        {
            services.AddSingleton<ILlmHttpFactory>(_sharedHttp);
            services.AddSingleton(_sharedBreaker);
            if (_sharedLocalModels is not null) services.AddSingleton(_sharedLocalModels);
            if (_approvals is not null) services.AddSingleton(_approvals);
            configure?.Invoke(services);
        });
    }

    private async Task<AgentTurnResult> RunIsolatedTurnAsync(
        WorkspaceInfo workspace, AgentSession session, string prompt, CancellationToken ct)
    {
        var owner = $"{session.Header.Id}/{Guid.NewGuid().ToString("N")[..8]}";
        await using var turnRuntime = CreateTurnRuntime(workspace, owner);
        turnRuntime.Prompts.SetWorkspaceRoot(workspace.IsGlobal ? null : workspace.PromptsDir);
        turnRuntime.Skills.Attach(workspace);

        // 定时任务没有发起请求的客户端，隔离回合的审批请求不会自动回到桌面。
        // 把 ApprovalRequiredEvent 中继到宿主总线，已连接客户端才有机会批准；
        // 无人响应时审批按超时拒绝（失败关闭），不再静默放行。
        var approvalRelay = turnRuntime.Events.Subscribe();
        var relayTask = ForwardApprovalRequestsAsync(approvalRelay, ct);
        try
        {
            if (turnRuntime.Mcp.LoadServerConfigs(workspace).Count > 0)
                await turnRuntime.Mcp.ConnectAllAsync(workspace, ct).ConfigureAwait(false);
            return await turnRuntime.Agent.RunTurnAsync(session, workspace, prompt, ct).ConfigureAwait(false);
        }
        finally
        {
            approvalRelay.Dispose();
            try
            {
                await relayTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 中继失败不影响任务收尾；审批本身仍按超时/取消收敛。
            }
        }
    }

    private async Task ForwardApprovalRequestsAsync(IEventSubscription subscription, CancellationToken ct)
    {
        try
        {
            await foreach (var evt in subscription.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (evt is ApprovalRequiredEvent approval) _runtime.Events.Publish(approval);
            }
        }
        catch (Exception)
        {
            // 订阅关闭/回合取消都是正常收尾。
        }
    }

    public ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _dispose.Cancel();
        _lifetime.Dispose();
        _dispose.Dispose();
        _gate.Dispose();
        _webhook.Dispose();
        return ValueTask.CompletedTask;
    }
}
