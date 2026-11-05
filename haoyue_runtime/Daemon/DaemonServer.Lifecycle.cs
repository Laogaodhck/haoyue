using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Evolution;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Experts;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Scheduling;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Daemon;

/// <summary>DaemonServer 的按域分部类（成员仅搬运自 DaemonServer.cs，逻辑未改动）。</summary>
public sealed partial class DaemonServer
{
    internal DaemonServer(
        HaoyueRuntime runtime,
        Func<AgentSession, WorkspaceInfo, string, CancellationToken, Task<AgentTurnResult>>? runTurn)
        : this(runtime, runTurn, runtime.Workspaces.CreateGlobal())
    {
    }

    internal DaemonServer(
        HaoyueRuntime runtime,
        Func<AgentSession, WorkspaceInfo, string, CancellationToken, Task<AgentTurnResult>>? runTurn,
        WorkspaceInfo globalWorkspace,
        string? handshakeToken = null,
        string? activeTurnsFile = null,
        string? daemonLogFile = null)
    {
        _runtime = runtime;
        _globalWorkspace = globalWorkspace;
        // 审批决策审计走宿主事件总线（JournaledEventBus），保证每个决策都有落盘记录。
        _approvals = new ApprovalRegistry(runtime.Events) { Interactive = true };
        _runTurn = runTurn;
        _useIsolatedTurnRuntime = runTurn is null;
        _handshakeToken = handshakeToken;
        _activeTurnsFile = activeTurnsFile ?? HaoyuePaths.ActiveTurnsFile;
        // 事件日志先行打开：构造期发布的启动告警（GPU 自检等）也要有落盘证据。
        _fileLog = daemonLogFile is null ? DaemonFileLog.OpenDefault() : DaemonFileLog.Open(daemonLogFile);
        _fileLog.Marker($"daemon host starting pid={Environment.ProcessId} pipe={PipeName}"
            + $" protocol={ProtocolVersion}");
        // Live config factory: a Reload() that replaces the whole Config object must also
        // change breaker thresholds; a snapshot here would freeze the old policy.
        _sharedBreaker = new CircuitBreaker(() => runtime.ConfigStore.Config.Routing.Retry);
        // Tests inject a stub turn runner; route scheduled runs through it too so the
        // daemon harness stays deterministic. Production keeps the isolated runtime path.
        _scheduler = new ScheduleService(
            runtime.Schedules, runtime, _fileLocks, _sharedHttp, _sharedBreaker,
            runTurn is null ? null : (workspace, session, prompt, ct) => runTurn(session, workspace, prompt, ct),
            sharedLocalModels: _sharedLocalModels,
            approvals: _approvals);
        // 后台任务队列与调度任务共用同一套隔离回合通道与共享基础设施；测试用同一个
        // stub turn runner，保持 harness 确定性。
        _backgroundTasks = new BackgroundTaskService(
            runtime, _fileLocks, _sharedHttp, _sharedBreaker, _sharedLocalModels,
            runTurn is null ? null : (workspace, session, prompt, ct) => runTurn(session, workspace, prompt, ct));
        // Evolution engine: reflection reuses the daemon's shared turn pathway so
        // tests can stub it exactly like scheduled turns.
        _evolution = new EvolutionStore(runtime.Database);
        _reflection = new ReflectionRunner(
            runtime, _fileLocks, _sharedHttp, _sharedBreaker, _evolution,
            runTurn is null ? null : (workspace, session, prompt, ct) => runTurn(session, workspace, prompt, ct),
            _sharedLocalModels);
        // H-C 记忆固化：与反思同构的隔离回合通道（测试 stub 一致），定时把近期
        // 会话经验沉淀为项目事实。
        _consolidation = new ConsolidationRunner(
            runtime, _fileLocks, _sharedHttp, _sharedBreaker,
            runTurn is null ? null : (workspace, session, prompt, ct) => runTurn(session, workspace, prompt, ct),
            _sharedLocalModels);
        _runtimeEvents = _runtime.Events.Subscribe();
        _scheduleEventsTask = BroadcastHostEventsAsync(_runtimeEvents.Reader, _shutdown.Token);
        _lan = new LanAccessServer(
            runtime.ConfigStore, _fileLog, handshakeToken,
            (stream, ct) => HandleConnectionAsync(stream, ct, authenticate: false));
        _admin = new DaemonAdminApi(runtime, globalWorkspace, _fileLocks, _scheduler, _shutdown.Token, _sharedLocalModels);
        _admin.McpStatusChanged += OnMcpStatusChanged;
        // 回合内的懒加载也走同一条进度通道（调用方不传 IProgress，旁路事件覆盖全部加载）。
        _sharedLocalModels.LoadProgressObserved += PublishModelLoadProgress;
        _admin.EvolutionConfigChanged += ScheduleAutoReflect;
        ScheduleAutoReflect();
    }

    /// <summary>Releases the scheduler and shared HTTP clients when the daemon host shuts down.</summary>
    public async ValueTask DisposeAsync()
    {
        _consolidationTimer?.Dispose();
        _consolidationTimer = null;
        _consolidationDeferralTimer?.Dispose();
        _consolidationDeferralTimer = null;
        _maintenanceTimer?.Dispose();
        _maintenanceTimer = null;
        _localModelIdleTimer?.Dispose();
        _localModelIdleTimer = null;
        _autoReflectTimer?.Dispose();
        _autoReflectTimer = null;
        _shutdown.Cancel();
        try
        {
            await _scheduleEventsTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _runtimeEvents.Dispose();
        await _scheduler.DisposeAsync().ConfigureAwait(false);
        await _lan.DisposeAsync().ConfigureAwait(false);
        _backgroundTasks.Dispose();
        _sharedHttp.Dispose();
        _sharedLocalModels.Dispose();
        _shutdown.Dispose();
        _fileLog.Dispose();

        if (!OperatingSystem.IsWindows() && File.Exists(SocketPath))
        {
            try { File.Delete(SocketPath); } catch { }
        }
    }

    private async Task RunSchedulerAsync(CancellationToken ct)
    {
        try
        {
            await _scheduler.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
