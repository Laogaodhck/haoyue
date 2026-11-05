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
    /// <summary>
    /// Per-user pipe name: two local accounts never compete for the same endpoint.
    /// Both ends (C# daemon / CLI and the Electron client) derive the suffix from
    /// the USERNAME environment variable with one shared sanitizer, so they agree
    /// without an extra handshake.
    /// </summary>
    public static string PipeName => $"haoyue-{PipeUserSuffix()}";
    /// <summary>版本契约：daemon JSONL 协议版本（唯一声明处在 <see cref="ApiVersionContract"/>）。</summary>
    public const string ProtocolVersion = ApiVersionContract.DaemonProtocolVersion;
    public static string SocketPath => Path.Combine(HaoyuePaths.Home, "daemon.sock");
    private const int MaxImageCount = 10;
    private const int MaxImageBytes = 10 * 1024 * 1024;
    private const int MaxTotalImageBytes = 40 * 1024 * 1024;

    /// <summary>Seconds a client has to present a valid handshake before the link is dropped.</summary>
    private const int HandshakeTimeoutSeconds = 15;
    /// <summary>Upper bound for the handshake line so an unauthenticated peer cannot stream unbounded data.</summary>
    private const int MaxHandshakeLineLength = 4096;
    /// <summary>
    /// 单条请求行上限。图像随回合在 JSON 行内以 base64 传输，所以上限必须覆盖
    /// 「总图像字节 40MB → base64 ≈ 53MB」再加 JSON 外壳；同时它仍是一道硬边界，
    /// 超长行在读取阶段即被截停（见 BoundedLineReader），不会把整行读进内存。
    /// </summary>
    private const int MaxRequestLineLength = MaxTotalImageBytes / 3 * 4 + 8 * 1024 * 1024;
    private static readonly HashSet<string> SupportedImageTypes =
        ["image/png", "image/jpeg", "image/webp", "image/gif"];

    private readonly HaoyueRuntime _runtime;
    private readonly DaemonAdminApi _admin;
    private readonly WorkspaceInfo _globalWorkspace;
    private readonly Func<AgentSession, WorkspaceInfo, string, CancellationToken, Task<AgentTurnResult>>? _runTurn;
    private readonly bool _useIsolatedTurnRuntime;
    private readonly CancellationTokenSource _shutdown = new();

    // When set, every connection must authenticate with this token as its very
    // first message before it can dispatch methods or receive broadcasts.
    private readonly string? _handshakeToken;

    // Central Task Coordinator: one instance per daemon process is the single
    // source of truth for file write locks across all concurrent agent turns.
    private readonly IFileLockCoordinator _fileLocks = new FileLockCoordinator();

    // Process-wide infrastructure shared by every isolated turn runtime so the
    // HttpClient connection pool, circuit-breaker state and the loaded GGUF model
    // survive across turns instead of being rebuilt (and reset) for every agent task.
    private readonly LlmHttpFactory _sharedHttp = new();
    private readonly CircuitBreaker _sharedBreaker;
    private readonly LocalModelCache _sharedLocalModels = new();
    private readonly ScheduleService _scheduler;
    private readonly BackgroundTaskService _backgroundTasks;
    private readonly EvolutionStore _evolution;
    private readonly ReflectionRunner _reflection;
    private readonly SemaphoreSlim _reflectionGate = new(1, 1);
    private readonly IEventSubscription _runtimeEvents;
    private readonly Task _scheduleEventsTask;
    // 局域网访问：默认关闭；开启后与本机管道 / socket 并行监听，认证走 HMAC 挑战应答。
    private readonly LanAccessServer _lan;
    // Unattended auto-reflection (E2): a 5-minute poll armed from EvolutionConfig —
    // interval mode and pending-defect threshold mode coexist; disposed/rearmed on
    // config changes. The reflection turn itself stays human-gated for adoption.
    private Timer? _autoReflectTimer;
    private DateTimeOffset _lastReflectAt = DateTimeOffset.UtcNow;
    // L-D 会话保留策略维护：24h 周期的后台定时器与单航班闸门（与自动反思同风格，
    // 维护是尽力而为的后台杂务，失败留待下一周期）。
    private Timer? _maintenanceTimer;
    private readonly SemaphoreSlim _maintenanceGate = new(1, 1);
    // 本地模型空闲回收：5 分钟一轮（阈值来自 Agent.LocalModelIdleUnloadMinutes）。
    private Timer? _localModelIdleTimer;
    // H-C 记忆固化：6h 周期的后台定时器（单航班在 ConsolidationRunner 内部）。
    private Timer? _consolidationTimer;
    // 固化让路定时器：用户回合在飞时把背景固化推迟到它触发之后（并发加载两份本地
    // 权重会耗尽显存并让 daemon 被原生层 abort）。
    private Timer? _consolidationDeferralTimer;
    private static readonly TimeSpan ConsolidationDeferral = TimeSpan.FromMinutes(2);
    private readonly ConsolidationRunner _consolidation;

    // Configuration and workspace administration remains serialized, while agent turns
    // execute concurrently in isolated runtime instances.
    private readonly SemaphoreSlim _adminGate = new(1, 1);

    // Connected clients that receive unsolicited daemon events (such as scheduled-task
    // completion). Each connection owns a writer and a gate so broadcasts stay ordered.
    private readonly Lock _clientsGate = new();
    private readonly Dictionary<long, ClientSink> _clients = [];
    private long _nextClientId;
    // daemon 自身的事件日志（~/.haoyue/logs/daemon.log）：CLI / 独立 daemon 场景
    // 此前零落盘，启动失败与告警无线索可查。桌面旁路只覆盖有桌面的场景。
    private readonly DaemonFileLog _fileLog;

    // Live connection contexts (per connected client). Administration operations that
    // mutate global state (factory reset, database rebuild) use this registry to
    // coordinate with turns running on every connection, not just their own.
    private readonly ConcurrentDictionary<long, ConnectionContext> _connections = new();

    // Session-level turn mutual exclusion: at most one active turn per session across
    // all connections. Concurrent turns on the same session interleave messages in the
    // shared history and break tool-call pairing, so they are rejected at dispatch.
    private readonly ConcurrentDictionary<string, ActiveTurn> _sessionTurns =
        new(StringComparer.OrdinalIgnoreCase);

    // Crash marker: in-flight turns mirrored to disk so a process death leaves a
    // residue the next start can turn into per-session interruption notices.
    private readonly ConcurrentDictionary<string, ActiveTurnRecord> _turnJournal =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _activeTurnsFile;

    // Undo ledgers of the latest completed turn per session. Shared across isolated
    // turn runtimes so agent.undo can revert file changes after the turn's runtime
    // (and its in-scope compensation stack) is gone.
    private readonly TurnUndoRegistry _undoRegistry = new();

    // 「本次允许」审批登记簿（P1-2.3）：进程级共享，让 agent.respondApproval RPC
    // 能响应任意隔离回合运行时里挂起的审批请求。Interactive = true 表示桌面端
    // 存在真实响应通道（审批卡片）；CLI/测试运行时没有通道，请求方按拒绝立即收场。
    // auditBus 在构造器里绑定宿主事件总线：每次批准/拒绝/超时/取消都会落成
    // ApprovalDecisionEvent（Sqlite 台账），供事后核对「谁在何时放行了什么」。
    private readonly ApprovalRegistry _approvals;

    // Turn ids recovered by startup crash recovery (this daemon process lifetime).
    // Clients query these via agent.interrupted on reconnect, because the startup
    // broadcast fires before any client has connected.
    private readonly Lock _interruptedTurnsGate = new();
    private readonly List<string> _interruptedTurns = [];

    private sealed class SessionUsageAccumulator
    {
        public long LlmRounds { get; set; }
        public long ExecutionSteps { get; set; }
        public long InputTokens { get; set; }
        public long TotalInputTokens { get; set; }
        public long CachedInputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long OutputElapsedMs { get; set; }
        public int LastWorkflowStep { get; set; }

        public SessionUsage ToUsage() => new()
        {
            LlmRounds = LlmRounds,
            ExecutionSteps = ExecutionSteps,
            InputTokens = InputTokens,
            TotalInputTokens = TotalInputTokens,
            CachedInputTokens = CachedInputTokens,
            OutputTokens = OutputTokens,
            OutputElapsedMs = OutputElapsedMs,
        };

        public bool HasActivity =>
            LlmRounds > 0
            || ExecutionSteps > 0
            || InputTokens > 0
            || TotalInputTokens > 0
            || CachedInputTokens > 0
            || OutputTokens > 0
            || OutputElapsedMs > 0;
    }
}
