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

/// <summary>
/// Exposes the runtime over a Windows named pipe or Unix domain socket using
/// newline-delimited JSON messages. Responses keep the legacy event envelope:
/// {"id":1,"event":"result","data":"..."}.
/// </summary>
public sealed class DaemonServer : IAsyncDisposable
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
    private readonly EvolutionStore _evolution;
    private readonly ReflectionRunner _reflection;
    private readonly SemaphoreSlim _reflectionGate = new(1, 1);
    private readonly IEventSubscription _runtimeEvents;
    private readonly Task _scheduleEventsTask;
    // Unattended auto-reflection (E2): a 5-minute poll armed from EvolutionConfig —
    // interval mode and pending-defect threshold mode coexist; disposed/rearmed on
    // config changes. The reflection turn itself stays human-gated for adoption.
    private Timer? _autoReflectTimer;
    private DateTimeOffset _lastReflectAt = DateTimeOffset.UtcNow;

    // Configuration and workspace administration remains serialized, while agent turns
    // execute concurrently in isolated runtime instances.
    private readonly SemaphoreSlim _adminGate = new(1, 1);

    // Connected clients that receive unsolicited daemon events (such as scheduled-task
    // completion). Each connection owns a writer and a gate so broadcasts stay ordered.
    private readonly Lock _clientsGate = new();
    private readonly Dictionary<long, ClientSink> _clients = [];
    private long _nextClientId;

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

    public DaemonServer(HaoyueRuntime runtime)
        : this(runtime, null, runtime.Workspaces.CreateGlobal())
    {
    }

    /// <summary>
    /// Production host: pass <see cref="DaemonAuth.LoadOrCreateToken"/> so local
    /// clients must present the shared handshake token before dispatching.
    /// </summary>
    public DaemonServer(HaoyueRuntime runtime, string? handshakeToken)
        : this(runtime, null, runtime.Workspaces.CreateGlobal(), handshakeToken)
    {
    }

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
        string? activeTurnsFile = null)
    {
        _runtime = runtime;
        _globalWorkspace = globalWorkspace;
        _runTurn = runTurn;
        _useIsolatedTurnRuntime = runTurn is null;
        _handshakeToken = handshakeToken;
        _activeTurnsFile = activeTurnsFile ?? HaoyuePaths.ActiveTurnsFile;
        // Live config factory: a Reload() that replaces the whole Config object must also
        // change breaker thresholds; a snapshot here would freeze the old policy.
        _sharedBreaker = new CircuitBreaker(() => runtime.ConfigStore.Config.Routing.Retry);
        // Tests inject a stub turn runner; route scheduled runs through it too so the
        // daemon harness stays deterministic. Production keeps the isolated runtime path.
        _scheduler = new ScheduleService(
            runtime.Schedules, runtime, _fileLocks, _sharedHttp, _sharedBreaker,
            runTurn is null ? null : (workspace, session, prompt, ct) => runTurn(session, workspace, prompt, ct),
            sharedLocalModels: _sharedLocalModels);
        // Evolution engine: reflection reuses the daemon's shared turn pathway so
        // tests can stub it exactly like scheduled turns.
        _evolution = new EvolutionStore(runtime.Database);
        _reflection = new ReflectionRunner(
            runtime, _fileLocks, _sharedHttp, _sharedBreaker, _evolution,
            runTurn is null ? null : (workspace, session, prompt, ct) => runTurn(session, workspace, prompt, ct),
            _sharedLocalModels);
        _runtimeEvents = _runtime.Events.Subscribe();
        _scheduleEventsTask = BroadcastScheduleEventsAsync(_runtimeEvents.Reader, _shutdown.Token);
        _admin = new DaemonAdminApi(runtime, globalWorkspace, _fileLocks, _scheduler, _shutdown.Token);
        _admin.McpStatusChanged += OnMcpStatusChanged;
        _admin.EvolutionConfigChanged += ScheduleAutoReflect;
        ScheduleAutoReflect();
    }

    /// <summary>Releases the scheduler and shared HTTP clients when the daemon host shuts down.</summary>
    public async ValueTask DisposeAsync()
    {
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
        _sharedHttp.Dispose();
        _sharedLocalModels.Dispose();
        _shutdown.Dispose();

        if (!OperatingSystem.IsWindows() && File.Exists(SocketPath))
        {
            try { File.Delete(SocketPath); } catch { }
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        var runCt = linkedCts.Token;
        await RecoverInterruptedTurnsAsync(runCt).ConfigureAwait(false);
        var schedulerTask = RunSchedulerAsync(runCt);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                while (!runCt.IsCancellationRequested)
                {
                    try
                    {
                        await ServeNamedPipeAsync(runCt).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (runCt.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        await Task.Delay(100, runCt).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                await ServeUnixSocketLoopAsync(runCt).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await schedulerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (runCt.IsCancellationRequested)
            {
            }
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

    private async Task ServeNamedPipeAsync(CancellationToken ct)
    {
        var pipe = CreatePipeServer();
        await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
        _ = Task.Run(() => RunPipeConnectionAsync(pipe, ct));
    }

    internal static string PipeUserSuffix() => SanitizePipeUser(Environment.GetEnvironmentVariable("USERNAME"));

    /// <summary>Keeps only characters that are legal in a pipe name; empty input falls back to "local".</summary>
    internal static string SanitizePipeUser(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "local";
        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw)
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '-');
        return builder.ToString();
    }

    /// <summary>
    /// Named pipes default to an ACL that lets Everyone connect. Restrict access to
    /// the current user and SYSTEM so another local account cannot open our endpoint;
    /// the handshake token stays the actual authentication on top of this.
    /// </summary>
    internal static NamedPipeServerStream CreatePipeServer(string? pipeName = null)
    {
        pipeName ??= PipeName;
        if (!OperatingSystem.IsWindows())
        {
            return new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }

        var security = new PipeSecurity();
        // CreateNewInstance 是并发第二个服务端实例的前提（MaxAllowedServerInstances），
        // 缺了它会在已有一条活跃连接时抛 UnauthorizedAccessException（1.3.15 回归）。
        security.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: 0, security);
    }

    private async Task ServeUnixSocketLoopAsync(CancellationToken ct)
    {
        var socketDir = Path.GetDirectoryName(SocketPath);
        if (!string.IsNullOrEmpty(socketDir) && !Directory.Exists(socketDir))
        {
            Directory.CreateDirectory(socketDir);
        }

        if (File.Exists(SocketPath))
        {
            try { File.Delete(SocketPath); } catch { }
        }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        listener.Listen(128);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var socket = await listener.AcceptAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => RunSocketConnectionAsync(socket, ct), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                Console.Error.WriteLine($"Daemon socket accept error: {ex.Message}");
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }

        if (File.Exists(SocketPath))
        {
            try { File.Delete(SocketPath); } catch { }
        }
    }

    private async Task RunPipeConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            try { await HandleConnectionAsync(pipe, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { Console.Error.WriteLine($"Daemon connection failed: {ex.Message}"); }
        }
    }

    private async Task RunSocketConnectionAsync(Socket socket, CancellationToken ct)
    {
        using (socket)
        await using (var stream = new NetworkStream(socket, ownsSocket: false))
        {
            try { await HandleConnectionAsync(stream, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { Console.Error.WriteLine($"Daemon connection failed: {ex.Message}"); }
        }
    }

    private sealed class ConnectionContext(
        StreamWriter writer,
        SemaphoreSlim writerGate,
        CancellationToken connectionCt)
    {
        public StreamWriter Writer { get; } = writer;
        public SemaphoreSlim WriterGate { get; } = writerGate;
        public CancellationToken ConnectionCt { get; } = connectionCt;
        public ConcurrentDictionary<long, ActiveTurn> ActiveTurns { get; } = new();
        public AgentSession? LegacySession;
    }

    internal async Task HandleConnectionAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 64 * 1024, leaveOpen: true)
        {
            AutoFlush = true,
        };
        using var writerGate = new SemaphoreSlim(1, 1);

        // Handshake gate: when a token is configured, the very first message must
        // authenticate. Unauthenticated connections never receive broadcasts and
        // cannot dispatch any method; failures drop the link immediately.
        if (_handshakeToken is not null
            && !await AuthenticateConnectionAsync(reader, writer, writerGate, ct).ConfigureAwait(false))
        {
            return;
        }

        var clientId = RegisterClient(writer, writerGate);
        var context = new ConnectionContext(writer, writerGate, ct);
        _connections[clientId] = context;

        try
        {
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonObject? request;
                try { request = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { request = null; }

                if (request is null)
                {
                    await WriteAsync(writer, writerGate, 0, "error", "Invalid JSON request", ct, code: DaemonErrorCode.InvalidRequest).ConfigureAwait(false);
                    continue;
                }

                long id;
                string method;
                try
                {
                    id = request["id"]?.GetValue<long>() ?? 0;
                    method = request["method"]?.GetValue<string>() ?? "";
                }
                catch (InvalidOperationException)
                {
                    await WriteAsync(writer, writerGate, 0, "error", "Request id and method have invalid types", ct, code: DaemonErrorCode.InvalidRequest).ConfigureAwait(false);
                    continue;
                }

                // Periodically clean up completed turns
                foreach (var (turnId, turn) in context.ActiveTurns)
                {
                    if (turn.Task?.IsCompleted == true)
                    {
                        if (turn.Task is not null) await ObserveAsync(turn.Task).ConfigureAwait(false);
                        turn.Cancellation.Dispose();
                        context.ActiveTurns.TryRemove(turnId, out _);
                    }
                }

                // Non-blocking concurrent dispatch: reader immediately loops back to read the next line!
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await DispatchMethodAsync(request, id, method, context).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await WriteAsync(context.Writer, context.WriterGate, id, "error", $"Server error: {ex.Message}", context.ConnectionCt, code: DaemonErrorCode.InternalError).ConfigureAwait(false);
                    }
                }, ct);
            }
        }
        finally
        {
            UnregisterClient(clientId);
            _connections.TryRemove(clientId, out _);
            foreach (var turn in context.ActiveTurns.Values) turn.Cancellation.Cancel();
            foreach (var turn in context.ActiveTurns.Values)
                if (turn.Task is not null) await ObserveAsync(turn.Task).ConfigureAwait(false);
            foreach (var turn in context.ActiveTurns.Values) turn.Cancellation.Dispose();
            context.ActiveTurns.Clear();
        }
    }

    /// <summary>
    /// Requires {"method":"handshake","params":{"token":...}} as the first message.
    /// Returns true once authenticated; on a missing/wrong token, a malformed
    /// message or a timeout the client is told why and the link is dropped.
    /// </summary>
    private async Task<bool> AuthenticateConnectionAsync(
        StreamReader reader,
        StreamWriter writer,
        SemaphoreSlim writerGate,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(HandshakeTimeoutSeconds));
        try
        {
            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line) || line.Length > MaxHandshakeLineLength)
                return false;

            JsonObject? request;
            try { request = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { request = null; }

            if (request is null)
            {
                await WriteAsync(writer, writerGate, 0, "error",
                    "authentication failed: invalid handshake message", timeout.Token, code: DaemonErrorCode.Unauthorized).ConfigureAwait(false);
                return false;
            }

            long id = 0;
            string? method = null;
            string? token = null;
            string? clientProtocolVersion = null;
            try
            {
                id = request["id"]?.GetValue<long>() ?? 0;
                method = request["method"]?.GetValue<string>();
                token = Params(request)["token"]?.GetValue<string>();
                clientProtocolVersion = Params(request)["protocolVersion"]?.GetValue<string>();
            }
            catch (InvalidOperationException)
            {
            }

            if (method == "handshake"
                && token is not null
                && DaemonAuth.TokensEqual(_handshakeToken!, token))
            {
                // 版本契约：客户端声明其支持的协议版本，主版本不一致时明确拒绝（Breaking Change），
                // 次版本更高时接受但返回升级提示。旧客户端不声明版本 → 跳过校验（向后兼容）。
                string? versionWarning = null;
                var versionCompatible = clientProtocolVersion is null
                    || DaemonContract.IsProtocolCompatible(ProtocolVersion, clientProtocolVersion, out versionWarning);
                if (!versionCompatible)
                {
                    await WriteAsync(writer, writerGate, id, "error",
                        $"protocol version mismatch: daemon={ProtocolVersion}, client={clientProtocolVersion}. " +
                        "Please upgrade the haoyue client or daemon to a compatible version.",
                        timeout.Token, code: DaemonErrorCode.VersionMismatch).ConfigureAwait(false);
                    return false;
                }
                await WriteAsync(writer, writerGate, id, "result", ProtocolInfoJson(versionWarning), timeout.Token).ConfigureAwait(false);
                return true;
            }

            await WriteAsync(writer, writerGate, id, "error",
                "authentication failed: missing or invalid handshake token", timeout.Token, code: DaemonErrorCode.Unauthorized).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Handshake timeout: the client never authenticated in time.
            return false;
        }
    }

    private async Task DispatchMethodAsync(JsonObject request, long id, string method, ConnectionContext context)
    {
        switch (method)
        {
                    case "ping":
                        await WriteAsync(context.Writer, context.WriterGate, id, "pong", "", context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "protocol.info":
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", ProtocolInfoJson(), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "events.recent":
                    {
                        var limit = Params(request)["limit"]?.GetValue<int>() ?? 100;
                        var journal = new JsonArray();
                        foreach (var persisted in _runtime.Database.RecentEvents(limit))
                            journal.Add(new JsonObject
                            {
                                ["id"] = persisted.Id,
                                ["timestamp"] = persisted.Timestamp,
                                ["type"] = persisted.Type,
                                ["payload"] = JsonNode.Parse(persisted.Payload),
                            });
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", journal.ToJsonString(), context.ConnectionCt).ConfigureAwait(false);
                        break;
                    }

                    case "evolution.inspect":
                    {
                        // Evolution engine E1: read-only aggregation of journaled failure
                        // signals into structured defect reports, enriched with a health
                        // score, per-day trend and kind/tool distributions (see Evolution/).
                        var limit = Params(request)["limit"]?.GetValue<int>() ?? DefectAggregator.DefaultScanLimit;
                        var aggregator = new DefectAggregator(_runtime.Database);
                        var reportList = aggregator.Aggregate(limit, _evolution.ReflectionSessionIds());
                        var reports = new JsonArray();
                        foreach (var report in reportList)
                            reports.Add(new JsonObject
                            {
                                ["fingerprint"] = report.Fingerprint,
                                ["kind"] = report.Kind.ToString(),
                                ["severity"] = report.Severity,
                                ["firstSeen"] = report.FirstSeen.ToString("O"),
                                ["lastSeen"] = report.LastSeen.ToString("O"),
                                ["occurrences"] = report.Occurrences,
                                ["toolName"] = report.ToolName,
                                ["errorSummary"] = report.ErrorSummary,
                                ["sessionId"] = report.SessionId,
                            });
                        var health = EvolutionAnalytics.ComputeHealth(reportList);
                        var trend = new JsonArray();
                        foreach (var point in EvolutionAnalytics.ComputeTrend(_runtime.Database))
                            trend.Add(new JsonObject
                            {
                                ["date"] = point.Date,
                                ["toolFailures"] = point.ToolFailures,
                                ["gaps"] = point.Gaps,
                                ["verificationFailures"] = point.VerificationFailures,
                                ["feedback"] = point.Feedback,
                                ["corrections"] = point.Corrections,
                                ["cancels"] = point.Cancels,
                                ["retries"] = point.Retries,
                            });
                        var distribution = EvolutionAnalytics.ComputeDistribution(reportList);
                        var byKind = new JsonObject();
                        foreach (var (kind, count) in distribution.ByKind)
                            byKind[kind] = count;
                        var topTools = new JsonArray();
                        foreach (var (tool, count) in distribution.TopTools)
                            topTools.Add(new JsonObject { ["tool"] = tool, ["count"] = count });
                        var result = new JsonObject
                        {
                            ["scanned"] = Math.Clamp(limit, 1, EventJournal.RetainedEvents),
                            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                            ["reports"] = reports,
                            ["health"] = new JsonObject { ["score"] = health.Score, ["grade"] = health.Grade },
                            ["trend"] = trend,
                            ["distribution"] = new JsonObject { ["byKind"] = byKind, ["topTools"] = topTools },
                        };
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", result.ToJsonString(), context.ConnectionCt).ConfigureAwait(false);
                        break;
                    }

                    case "evolution.reflect":
                    {
                        // Evolution engine E2+E3: fire-and-forget reflection pass.
                        // The turn runs in the background (it is a full agent turn);
                        // completion arrives as an EvolutionReflectionCompletedEvent.
                        if (!_reflectionGate.Wait(0))
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", "反思 turn 已在进行中", context.ConnectionCt, code: DaemonErrorCode.Conflict).ConfigureAwait(false);
                            break;
                        }
                        try
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "result",
                                new JsonObject { ["started"] = true }.ToJsonString(), context.ConnectionCt).ConfigureAwait(false);
                        }
                        catch
                        {
                            _reflectionGate.Release();
                            throw;
                        }
                        _ = Task.Run(async () =>
                        {
                            try { await _reflection.RunAsync("manual", _shutdown.Token).ConfigureAwait(false); }
                            finally { _reflectionGate.Release(); }
                        }, _shutdown.Token);
                        break;
                    }

                    case "evolution.pending-list":
                    {
                        var candidates = new JsonArray();
                        foreach (var candidate in _reflection.ListCandidates())
                        {
                            var files = new JsonArray();
                            foreach (var file in candidate.Files ?? Array.Empty<ReflectionRunner.CandidateFile>())
                                files.Add(new JsonObject { ["name"] = file.Name, ["content"] = file.Content });
                            candidates.Add(new JsonObject
                            {
                                ["fingerprint"] = candidate.Fingerprint,
                                ["kind"] = candidate.Kind,
                                ["skillName"] = candidate.SkillName,
                                ["candidateDir"] = candidate.CandidateDir,
                                ["summary"] = candidate.Summary,
                                ["createdAt"] = candidate.CreatedAt.ToString("O"),
                                ["status"] = candidate.Status,
                                ["files"] = files,
                            });
                        }
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", candidates.ToJsonString(), context.ConnectionCt).ConfigureAwait(false);
                        break;
                    }

                    case "evolution.decide":
                    {
                        var parameters = Params(request);
                        var fingerprint = parameters["fingerprint"]?.GetValue<string>()?.Trim() ?? "";
                        var decision = parameters["decision"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "";
                        var promptOverride = parameters["prompt"]?.GetValue<string>()?.Trim();
                        if (fingerprint.Length == 0 || decision is not ("adopt" or "reject" or "defer"))
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error",
                                "params.fingerprint 与 params.decision（adopt|reject|defer）均为必填", context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
                            break;
                        }
                        try
                        {
                            var record = _reflection.Decide(fingerprint, decision, promptOverride);
                            var decided = new JsonObject
                            {
                                ["fingerprint"] = record.Fingerprint,
                                ["status"] = record.Status,
                                ["updatedAt"] = record.UpdatedAt.ToString("O"),
                            };
                            await WriteAsync(context.Writer, context.WriterGate, id, "result", decided.ToJsonString(), context.ConnectionCt).ConfigureAwait(false);
                        }
                        catch (InvalidOperationException ex)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", ex.Message, context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
                        }
                        break;
                    }

                    case "evolution.config.get":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.GetEvolutionConfig()), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "evolution.config.set":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.SetEvolutionConfig(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "evolution.history":
                    {
                        var limit = Params(request)["limit"]?.GetValue<int>() ?? 20;
                        var runs = new JsonArray();
                        foreach (var run in _evolution.ListRuns(limit))
                            runs.Add(new JsonObject
                            {
                                ["id"] = run.Id,
                                ["sessionId"] = run.SessionId,
                                ["trigger"] = run.Trigger,
                                ["processed"] = run.Processed,
                                ["candidates"] = run.Candidates,
                                ["noAction"] = run.NoAction,
                                ["skipped"] = run.Skipped,
                                ["failed"] = run.Failed,
                                ["error"] = run.Error,
                                ["createdAt"] = run.CreatedAt.ToString("O"),
                            });
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", runs.ToJsonString(), context.ConnectionCt).ConfigureAwait(false);
                        break;
                    }

                    case "evolution.stats":
                    {
                        // Effectiveness tracking: adoption rate, per-skill usage and whether
                        // the originating defect has resurfaced since adoption.
                        var aggregator = new DefectAggregator(_runtime.Database);
                        var reportList = aggregator.Aggregate(DefectAggregator.DefaultScanLimit, _evolution.ReflectionSessionIds());
                        var stats = EvolutionAnalytics.ComputeStats(_runtime.Database, _evolution, reportList);
                        var skills = new JsonArray();
                        foreach (var skill in stats.Skills)
                            skills.Add(new JsonObject
                            {
                                ["fingerprint"] = skill.Fingerprint,
                                ["skillName"] = skill.SkillName,
                                ["adoptedAt"] = skill.AdoptedAt.ToString("O"),
                                ["kind"] = skill.Kind,
                                ["usageCount"] = skill.UsageCount,
                                ["resolved"] = skill.Resolved,
                            });
                        var result = new JsonObject
                        {
                            ["runs"] = stats.Runs,
                            ["candidatesProduced"] = stats.CandidatesProduced,
                            ["adopted"] = stats.Adopted,
                            ["rejected"] = stats.Rejected,
                            ["deferred"] = stats.Deferred,
                            ["noAction"] = stats.NoAction,
                            ["failed"] = stats.Failed,
                            ["adoptionRate"] = stats.AdoptionRate,
                            ["skills"] = skills,
                            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                        };
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", result.ToJsonString(), context.ConnectionCt).ConfigureAwait(false);
                        break;
                    }

                    case "feedback.turn":
                    {
                        // P4: explicit user feedback on a finished turn. Negative feedback
                        // is journaled and becomes a defect report for the next reflection.
                        var parameters = Params(request);
                        var sessionId = parameters["sessionId"]?.GetValue<string>()?.Trim() ?? "";
                        if (sessionId.Length == 0)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", "params.sessionId 为必填", context.ConnectionCt).ConfigureAwait(false);
                            break;
                        }
                        var kind = parameters["kind"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "negative";
                        var reason = parameters["reason"]?.GetValue<string>()?.Trim();
                        _runtime.Events.Publish(new UserFeedbackEvent(sessionId, kind, reason));
                        await WriteAsync(context.Writer, context.WriterGate, id, "result",
                            new JsonObject { ["recorded"] = true }.ToJsonString(), context.ConnectionCt).ConfigureAwait(false);
                        break;
                    }

                    case "workspace.init":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.InitializeWorkspace()), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "factory.reset":
                    {
                        // A rebuild drops every table: coordinate with turns on ALL
                        // connections, not just the caller's, so live SQLite writers
                        // cannot race the schema change.
                        await CancelAllTurnsAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.FactoryReset()), context.ConnectionCt).ConfigureAwait(false);
                        break;
                    }

                    case "chat":
                    case "agent.runTurn":
                    case "agent/runTurn":
                    {
                        var parameters = Params(request);
                        var message = parameters["message"]?.GetValue<string>()
                                      ?? parameters["prompt"]?.GetValue<string>()
                                      ?? "";
                        IReadOnlyList<ChatImageAttachment> images;
                        try { images = ParseImages(parameters["images"]); }
                        catch (DaemonRequestException ex)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", ex.Message, context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
                            break;
                        }
                        if (string.IsNullOrWhiteSpace(message) && images.Count == 0)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", "params.message or params.images is required", context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
                            break;
                        }
                        WorkspaceInfo workspace;
                        AgentSession turnSession;
                        var requestedSessionId = parameters["sessionId"]?.GetValue<string>();
                        try
                        {
                            workspace = ResolveWorkspace(parameters);
                            turnSession = LoadTurnSession(workspace, requestedSessionId, ref context.LegacySession);
                        }
                        catch (DaemonRequestException ex)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", ex.Message, context.ConnectionCt, requestedSessionId, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
                            break;
                        }
                        ReasoningLevel reasoningLevel;
                        string? expertId;
                        try
                        {
                            reasoningLevel = ParseReasoningLevel(
                                parameters["reasoningLevel"], turnSession.Header.ReasoningLevel);
                            expertId = ParseExpertId(parameters["expertId"], turnSession.Header.ExpertId);
                            if (turnSession.Header.ReasoningLevel != reasoningLevel
                                || turnSession.Header.ExpertId != expertId)
                            {
                                turnSession.Header.ReasoningLevel = reasoningLevel;
                                turnSession.Header.ExpertId = expertId;
                                _runtime.Sessions.UpdateMetadata(
                                    workspace, turnSession.Header.Id,
                                    reasoningLevel: reasoningLevel, expertId: expertId);
                            }
                        }
                        catch (DaemonRequestException ex)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", ex.Message, context.ConnectionCt, requestedSessionId, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
                            break;
                        }
                        var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.ConnectionCt);
                        var turn = new ActiveTurn(turnSession, workspace, turnCancellation);
                        var sessionKey = turnSession.Header.Id;
                        if (!_sessionTurns.TryAdd(sessionKey, turn))
                        {
                            turnCancellation.Dispose();
                            await WriteAsync(context.Writer, context.WriterGate, id, "error",
                                "A turn is already active for this session; wait for it to finish or send guidance with agent.steer",
                                context.ConnectionCt, sessionKey, code: DaemonErrorCode.Conflict).ConfigureAwait(false);
                            break;
                        }
                        context.ActiveTurns[id] = turn;
                        // Mirror the turn into the crash journal (production isolated
                        // runtime only; stub-runner tests stay off the real file).
                        if (_useIsolatedTurnRuntime)
                        {
                            _turnJournal[sessionKey] = new ActiveTurnRecord(
                                sessionKey, Haoyue.Runtime.Data.HaoyueDatabase.ScopeKey(workspace), workspace.Root,
                                workspace.IsGlobal, DateTimeOffset.UtcNow);
                            ActiveTurnJournal.Write(_activeTurnsFile, _turnJournal.Values.ToList());
                        }
                        turn.Task = RunTurnAsync(
                            turnSession, workspace, message, images, reasoningLevel, id, context.Writer, context.WriterGate,
                            turnCancellation.Token, context.ConnectionCt, turn.Steering);
                        // The session lock releases itself when the turn completes; the
                        // value-aware removal never evicts a newer turn's registration.
                        _ = turn.Task.ContinueWith(
                            finishedTask =>
                            {
                                _sessionTurns.TryRemove(KeyValuePair.Create(sessionKey, turn));
                                if (_turnJournal.TryRemove(sessionKey, out _))
                                    ActiveTurnJournal.Write(_activeTurnsFile, _turnJournal.Values.ToList());
                            },
                            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        break;
                    }

                    case "agent.steer":
                    {
                        var parameters = Params(request);
                        var message = parameters["message"]?.GetValue<string>()
                                      ?? parameters["prompt"]?.GetValue<string>()
                                      ?? "";
                        IReadOnlyList<ChatImageAttachment> images;
                        try { images = ParseImages(parameters["images"]); }
                        catch (DaemonRequestException ex)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", ex.Message, context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
                            break;
                        }
                        if (string.IsNullOrWhiteSpace(message) && images.Count == 0)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", "params.message or params.images is required", context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
                            break;
                        }

                        var requestedId = parameters["requestId"]?.GetValue<long?>();
                        var requestedSessionId = parameters["sessionId"]?.GetValue<string>();
                        ActiveTurn? target = requestedId is { } specific
                            ? context.ActiveTurns.GetValueOrDefault(specific)
                            : context.ActiveTurns.Values
                                .Where(turn => string.Equals(turn.Session.Header.Id, requestedSessionId, StringComparison.OrdinalIgnoreCase))
                                .OrderByDescending(turn => turn.Session.Header.UpdatedAt)
                                .FirstOrDefault();
                        if (target is null)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", "No active turn found for this session", context.ConnectionCt, requestedSessionId, code: DaemonErrorCode.NotFound).ConfigureAwait(false);
                            break;
                        }

                        if (!target.Steering.TryEnqueue(ChatMessage.User(message, images)))
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", "The active turn is already finishing", context.ConnectionCt, target.Session.Header.Id, code: DaemonErrorCode.Conflict).ConfigureAwait(false);
                            break;
                        }
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", "guidance queued", context.ConnectionCt, target.Session.Header.Id).ConfigureAwait(false);
                        break;
                    }

                    case "agent.interrupted":
                    {
                        // Session ids whose turns were interrupted by the last daemon
                        // crash, recovered at startup. Lets a reconnecting client clear
                        // stale "running" spinners it kept from before the crash.
                        List<string> snapshot;
                        lock (_interruptedTurnsGate) snapshot = _interruptedTurns.ToList();
                        await WriteAsync(context.Writer, context.WriterGate, id, "result",
                            JsonSerializer.Serialize(snapshot, HaoyueJsonContext.Compact.ListString),
                            context.ConnectionCt).ConfigureAwait(false);
                        break;
                    }

                    case "agent.undo":
                    {
                        // User-confirmed revert of the latest completed turn's builtin
                        // file-tool changes (LIFO). shell/MCP side effects were never
                        // registered as compensable, so they are never silently "undone".
                        var requestedSession = request["params"]?["sessionId"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(requestedSession))
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error",
                                "params.sessionId is required", context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
                            break;
                        }
                        if (_sessionTurns.ContainsKey(requestedSession))
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error",
                                "A turn is active for this session; wait for it to finish before undoing.",
                                context.ConnectionCt, code: DaemonErrorCode.Conflict).ConfigureAwait(false);
                            break;
                        }
                        var ledger = _undoRegistry.Take(requestedSession);
                        if (ledger is null)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "result",
                                "no undoable changes", context.ConnectionCt).ConfigureAwait(false);
                            break;
                        }

                        var (restored, failed) = await ledger.ApplyAsync(context.ConnectionCt).ConfigureAwait(false);
                        var summary = failed.Count > 0
                            ? $"撤销完成 {restored.Count} 项，失败 {failed.Count} 项：\n{string.Join("\n", failed)}"
                            : $"已撤销本回合对 {restored.Count} 个文件的修改：\n{string.Join("\n", restored)}";

                        // Record the undo in session history so the record and the file
                        // system stay consistent for later turns.
                        try
                        {
                            var workspaceForSession = TryDetectWorkspace(ledger.WorkspaceRoot)
                                                      ?? _runtime.Workspaces.CreateGlobal();
                            var undoSession = _runtime.Sessions.Load(workspaceForSession, requestedSession);
                            if (undoSession is not null)
                            {
                                var notice = failed.Count > 0
                                    ? $">>> [undo] {summary}"
                                    : $">>> [undo] 已按用户要求撤销上一回合的文件修改（{restored.Count} 项）：\n{string.Join("\n", restored)}";
                                if (failed.Count > 0) notice += $"\n失败项：{string.Join("; ", failed)}";
                                _runtime.Sessions.Append(undoSession, ChatMessage.User(notice));
                            }
                        }
                        catch
                        {
                            // History bookkeeping is best effort; the files are already reverted.
                        }

                        await WriteAsync(context.Writer, context.WriterGate, id, "result", summary,
                            context.ConnectionCt).ConfigureAwait(false);
                        break;
                    }

                    case "agent.cancel":
                    {
                        var requestedId = request["params"]?["requestId"]?.GetValue<long?>();
                        var requestedSession = request["params"]?["sessionId"]?.GetValue<string>();
                        // With no requestId the request may be scoped to a session; only
                        // when both are absent do we cancel every turn on this connection.
                        var targets = context.ActiveTurns
                            .Where(item =>
                                (requestedId is null || item.Key == requestedId) &&
                                (string.IsNullOrWhiteSpace(requestedSession) ||
                                 string.Equals(item.Value.Session.Header.Id, requestedSession, StringComparison.OrdinalIgnoreCase)))
                            .Select(item => item.Value)
                            .ToList();
                        if (targets.Count == 0)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "result", "no active turn", context.ConnectionCt).ConfigureAwait(false);
                            break;
                        }
                        foreach (var target in targets) target.Cancellation.Cancel();
                        var detail = requestedId is { } one
                            ? $"cancellation requested for {one}"
                            : $"cancellation requested for {targets.Count} active turn(s)";
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", detail, context.ConnectionCt).ConfigureAwait(false);
                        break;
                    }

                    case "workspace.get":
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", WorkspaceJson(_runtime.Workspace), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "workspace.open":
                    {
                        var path = request["params"]?["path"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", "params.path is required", context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
                            break;
                        }

                        string fullPath;
                        try { fullPath = Path.GetFullPath(path); }
                        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", $"Invalid workspace path: {ex.Message}", context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
                            break;
                        }

                        if (!Directory.Exists(fullPath))
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", $"Workspace directory not found: {fullPath}", context.ConnectionCt, code: DaemonErrorCode.NotFound).ConfigureAwait(false);
                            break;
                        }
                        await _adminGate.WaitAsync(context.ConnectionCt).ConfigureAwait(false);

                        try
                        {
                            _runtime.RefreshWorkspace(fullPath);
                            context.LegacySession = null;
                            await WriteAsync(context.Writer, context.WriterGate, id, "result", WorkspaceJson(_runtime.Workspace), context.ConnectionCt).ConfigureAwait(false);
                        }
                        finally
                        {
                            _adminGate.Release();
                        }

                        // Background connect MCP without blocking the admin gate or connection!
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _runtime.ConnectMcpAsync(_shutdown.Token).ConfigureAwait(false);
                            }
                            catch { }
                        }, _shutdown.Token);
                        break;
                    }

                    case "agent.mode.get":
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", CurrentMode(), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "agent.mode.switch":
                    {
                        var rawMode = request["params"]?["mode"]?.GetValue<string>();
                        if (!TryNormalizeMode(rawMode, out var mode))
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", "params.mode must be one of: plan, readonly, edit, auto", context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
                            break;
                        }
                        await _adminGate.WaitAsync(context.ConnectionCt).ConfigureAwait(false);

                        try
                        {
                            SaveMode(mode);
                            await WriteAsync(context.Writer, context.WriterGate, id, "result", mode, context.ConnectionCt).ConfigureAwait(false);
                        }
                        finally
                        {
                            _adminGate.Release();
                        }
                        break;
                    }

                    case "config.save":
                    {
                        // A4 single-writer: a CLI process with the daemon online delegates
                        // its config change here instead of writing ~/.haoyue/config.json
                        // itself. The payload is a recursive dirty patch (only what the
                        // CLI actually edited) deep-merged by ConfigStore.ApplyPatch, so
                        // concurrent daemon-side admin edits never get clobbered.
                        if (request["params"]?["fields"] is not JsonObject fields || fields.Count == 0)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error",
                                "params.fields must be a non-empty JSON patch object", context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
                            break;
                        }
                        await _adminGate.WaitAsync(context.ConnectionCt).ConfigureAwait(false);
                        try
                        {
                            _runtime.ConfigStore.ApplyPatch(fields);
                            await WriteAsync(context.Writer, context.WriterGate, id, "result",
                                "saved", context.ConnectionCt).ConfigureAwait(false);
                        }
                        finally
                        {
                            _adminGate.Release();
                        }
                        break;
                    }

                    case "config.status":
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", _admin.GetConfigStatus(), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "config.rebuild":
                        await CancelAllTurnsAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.RebuildConfigAndDatabase()), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "routing.get":
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", _admin.GetRoutingConfig(), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "routing.set":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.SetRoutingConfig(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "agent.config.get":
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", _admin.GetAgentConfig(), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "agent.config.set":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.SetAgentConfig(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "advanced.get":
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", _admin.GetAdvancedConfig(), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "advanced.set":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            token => _admin.SetAdvancedConfigAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "prompt.optimize":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            token => _admin.OptimizePromptAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "schedule.list":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ListSchedules()), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "schedule.create":
                    case "schedule.update":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.UpsertSchedule(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "schedule.toggle":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.ToggleSchedule(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "schedule.delete":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.DeleteSchedule(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "schedule.run":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.RunSchedule(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "provider.list":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ListProviders()), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "provider.upsert":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.UpsertProvider(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "provider.use":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.UseProvider(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "provider.remove":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.RemoveProvider(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "provider.test":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            token => _admin.TestProvidersAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "provider.models.fetch":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            token => _admin.FetchProviderModelsAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "local.models":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ScanLocalModels(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "model.catalog":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ModelCatalog()), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "model.test":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            token => _admin.TestModelAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "model.status":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ModelStatus(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "model.update":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.UpdateModel(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "mcp.list":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ListMcpServers()), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "mcp.upsert":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            token => _admin.UpsertMcpServerAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "mcp.remove":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            token => _admin.RemoveMcpServerAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "mcp.reload":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _admin.ReloadMcpAsync, context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "skill.list":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ListSkills()), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "skill.import":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.ImportSkill(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "skill.toggle":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.ToggleSkill(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "skill.official.list":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ListOfficialSkills()), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "expert.list":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ListExperts()), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "skill.official.install":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.InstallOfficialSkill(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "knowledge.list":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ListKnowledge(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "knowledge.search":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.SearchKnowledge(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "knowledge.save":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.SaveKnowledge(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "knowledge.delete":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.DeleteKnowledge(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "knowledge.import":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.ImportKnowledge(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "knowledge.tags":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.KnowledgeTags(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "knowledge.export":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ExportKnowledge(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "knowledge.synonyms.get":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.GetKnowledgeSynonyms()), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "knowledge.synonyms.save":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.SaveKnowledgeSynonyms(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "knowledge.notebook.list":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ListKnowledgeNotebooks(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "knowledge.notebook.save":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.SaveKnowledgeNotebook(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "knowledge.notebook.delete":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.DeleteKnowledgeNotebook(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "knowledge.source.add":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.AddKnowledgeSource(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "knowledge.source.list":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ListKnowledgeSources(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "knowledge.source.read":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ReadKnowledgeSource(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "knowledge.source.delete":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.DeleteKnowledgeSource(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "knowledge.source.refresh":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.RefreshKnowledgeSource(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "knowledge.retrieve":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.RetrieveKnowledge(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "memory.get":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.GetMemory(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "memory.save":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.SaveMemory(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "rules.list":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ListRules(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "rules.save":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.SaveRules(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "rules.delete":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.DeleteRules(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "usage.get":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.Usage(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "usage.timeline":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.UsageTimeline(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "doctor.run":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _admin.DoctorAsync, context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "lock.list":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ListLocks()), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "project.list":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ListProjects()), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "project.upsert":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.UpsertProject(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "project.remove":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.RemoveProject(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "session.list":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ListSessions(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "session.search":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.SearchSessions(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "session.get":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.GetSession(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "session.truncate":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.TruncateSession(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "session.fork":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.ForkSession(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "session.update":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.UpdateSession(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "session.archive":
                    {
                        var sessionId = request["params"]?["id"]?.GetValue<string>();
                        if (context.LegacySession?.Header.Id == sessionId)
                        {
                            context.LegacySession = null;
                        }
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.ArchiveSession(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;
                    }

                    case "session.delete":
                    {
                        var sessionId = request["params"]?["id"]?.GetValue<string>();
                        if (context.LegacySession?.Header.Id == sessionId)
                        {
                            context.LegacySession = null;
                        }
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.DeleteSession(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;
                    }

                    case "session.resume":
                    {
                        var sessionId = request["params"]?["id"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(sessionId))
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", "params.id is required", context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
                            break;
                        }
                        WorkspaceInfo workspace;
                        try { workspace = ResolveWorkspace(Params(request)); }
                        catch (DaemonRequestException ex)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", ex.Message, context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
                            break;
                        }
                        var loaded = _runtime.Sessions.Load(workspace, sessionId);
                        if (loaded is null)
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", $"Session {sessionId} not found", context.ConnectionCt, code: DaemonErrorCode.NotFound).ConfigureAwait(false);
                        else
                        {
                            context.LegacySession = loaded;
                            await WriteAsync(context.Writer, context.WriterGate, id, "result", $"resumed {loaded.Header.Id}", context.ConnectionCt).ConfigureAwait(false);
                        }
                        break;
                    }

                    case "session.new":
                    {
                        WorkspaceInfo workspace;
                        try { workspace = ResolveWorkspace(Params(request)); }
                        catch (DaemonRequestException ex)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", ex.Message, context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
                            break;
                        }
                        ReasoningLevel reasoningLevel;
                        try
                        {
                            reasoningLevel = ParseReasoningLevel(
                                Params(request)["reasoningLevel"], _runtime.ConfigStore.Config.Agent.ReasoningLevel);
                        }
                        catch (DaemonRequestException ex)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", ex.Message, context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
                            break;
                        }
                        var networkEnabled = Params(request)["networkEnabled"]?.GetValue<bool?>()
                            ?? _runtime.ConfigStore.Config.Agent.NetworkEnabled;
                        string? expertId;
                        try
                        {
                            // Absent node → null (no expert); "" would also unbind, but a
                            // fresh session has nothing to unbind.
                            expertId = ParseExpertId(Params(request)["expertId"], null);
                        }
                        catch (DaemonRequestException ex)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", ex.Message, context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
                            break;
                        }
                        context.LegacySession = _runtime.Sessions.Create(workspace, reasoningLevel, networkEnabled, expertId);
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", context.LegacySession.Header.Id, context.ConnectionCt).ConfigureAwait(false);
                        break;
                    }

                    case "model.list":
                    {
                        var models = _runtime.Models.All().Select(m => m.Ref).ToList();
                        var json = JsonSerializer.Serialize(models, HaoyueJsonContext.Default.ListString);
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", json, context.ConnectionCt).ConfigureAwait(false);
                        break;
                    }

                    case "model.switch":
                    {
                        var modelRef = request["params"]?["model"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(modelRef))
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", "params.model is required", context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
                            break;
                        }
                        await _adminGate.WaitAsync(context.ConnectionCt).ConfigureAwait(false);

                        try
                        {
                            var model = _runtime.Models.Resolve(modelRef);
                            if (model is null)
                                await WriteAsync(context.Writer, context.WriterGate, id, "error", $"Unknown model {modelRef}", context.ConnectionCt, code: DaemonErrorCode.NotFound).ConfigureAwait(false);
                            else
                            {
                                var config = _runtime.ConfigStore.Config;
                                config.Provider = model.Provider.Id;
                                config.Model = model.Model.Id;
                                _runtime.ConfigStore.Save();
                                await WriteAsync(context.Writer, context.WriterGate, id, "result", $"switched to {model.Ref}", context.ConnectionCt).ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            _adminGate.Release();
                        }
                        break;
                    }

                    case "doctor":
                    {
                        var checks = _runtime.Health.RunChecks(_runtime.Workspace);
                        var summary = string.Join("\n", checks.Select(c => $"{(c.Ok ? "[OK]" : "[FAIL]")} {c.Name}: {c.Detail}"));
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", summary, context.ConnectionCt).ConfigureAwait(false);
                        break;
                    }

                    case "shutdown":
                        foreach (var turn in context.ActiveTurns.Values) turn.Cancellation.Cancel();
                        foreach (var turn in context.ActiveTurns.Values)
                            if (turn.Task is not null) await ObserveAsync(turn.Task).ConfigureAwait(false);
                        await WriteAsync(context.Writer, context.WriterGate, id, "bye", "", context.ConnectionCt).ConfigureAwait(false);
                        _shutdown.Cancel();
                        break;

                    case "contract.export":
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", DaemonContract.ExportJson().ToJsonString(), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    default:
                        await WriteAsync(context.Writer, context.WriterGate, id, "error", $"Unknown method: {method}", context.ConnectionCt, code: DaemonErrorCode.UnknownMethod).ConfigureAwait(false);
                        break;
                        }
    }

    private void OnMcpStatusChanged() => _ = BroadcastMcpStatusAsync();

    /// <summary>
    /// Rearms the unattended auto-reflection poll from the current evolution config.
    /// Called at construction and again whenever SetEvolutionConfig persists changes.
    /// </summary>
    private void ScheduleAutoReflect()
    {
        _autoReflectTimer?.Dispose();
        _autoReflectTimer = null;
        var config = _runtime.ConfigStore.Config.Evolution;
        if (!config.AutoReflect && !config.ThresholdEnabled) return;
        _lastReflectAt = DateTimeOffset.UtcNow;
        _autoReflectTimer = new Timer(
            _ => AutoReflectTick(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Polls whether an unattended reflection is due: either the configured interval
    /// elapsed, or enough pending defect signals accumulated (threshold mode, with a
    /// 30-minute throttle). Whichever condition comes first wins.
    /// </summary>
    private void AutoReflectTick()
    {
        if (_shutdown.IsCancellationRequested) return;
        var config = _runtime.ConfigStore.Config.Evolution;
        var now = DateTimeOffset.UtcNow;
        var dueByInterval = config.AutoReflect &&
            now - _lastReflectAt >= TimeSpan.FromMinutes(Math.Clamp(config.IntervalMinutes, 30, 10080));
        var dueByThreshold = false;
        if (!dueByInterval && config.ThresholdEnabled &&
            now - _lastReflectAt >= TimeSpan.FromMinutes(30))
        {
            try
            {
                var pending = new DefectAggregator(_runtime.Database)
                    .Aggregate(DefectAggregator.DefaultScanLimit, _evolution.ReflectionSessionIds())
                    .Count(r => _evolution.Get(r.Fingerprint) is not { } existing ||
                                !EvolutionStatus.BlocksRetry(existing.Status));
                dueByThreshold = pending >= Math.Clamp(config.ThresholdSignals, 1, 50);
            }
            catch (Exception)
            {
                // A poll tick must never kill the daemon process; the next tick retries.
                return;
            }
        }
        if (!dueByInterval && !dueByThreshold) return;
        _lastReflectAt = now;
        StartAutoReflectTurn(dueByInterval ? "auto" : "threshold");
    }

    /// <summary>
    /// Fires one reflection turn if none is running. Conflicts (a manual or scheduled
    /// reflection still in flight) are silently skipped — the next tick retries, and
    /// outcomes reach clients through the evolution.reflected broadcast either way.
    /// </summary>
    private void StartAutoReflectTurn(string trigger)
    {
        if (_shutdown.IsCancellationRequested) return;
        if (!_reflectionGate.Wait(0)) return;
        _ = Task.Run(async () =>
        {
            try { await _reflection.RunAsync(trigger, _shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            finally { _reflectionGate.Release(); }
        }, _shutdown.Token);
    }

    /// <summary>
    /// Tells every connected client that a background MCP reconnect finished, so the
    /// server list can show real connection results without the client polling.
    /// </summary>
    private async Task BroadcastMcpStatusAsync()
    {
        try
        {
            await BroadcastAsync(0, "mcp.updated", "", _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The daemon is shutting down or a client vanished mid-broadcast.
        }
    }

    private async Task BroadcastScheduleEventsAsync(ChannelReader<RuntimeEvent> reader, CancellationToken ct)
    {
        await foreach (var evt in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (evt is ScheduledTaskUpcomingEvent upcoming)
            {
                var upcomingDetails = new JsonObject
                {
                    ["taskId"] = upcoming.TaskId,
                    ["name"] = upcoming.Name,
                    ["runAt"] = upcoming.RunAt.ToString("O"),
                };
                await BroadcastAsync(0, "schedule.upcoming", upcoming.Name, ct, upcomingDetails)
                    .ConfigureAwait(false);
                continue;
            }
            if (evt is EvolutionReflectionCompletedEvent reflected)
            {
                // Surface reflection outcomes so the desktop can raise its candidate
                // review banner even when the reflection ran unattended (cron/manual).
                var reflectedDetails = new JsonObject
                {
                    ["sessionId"] = reflected.SessionId,
                    ["processed"] = reflected.Processed,
                    ["candidates"] = reflected.Candidates,
                    ["noAction"] = reflected.NoAction,
                    ["skipped"] = reflected.Skipped,
                };
                if (!string.IsNullOrWhiteSpace(reflected.Error)) reflectedDetails["error"] = reflected.Error;
                await BroadcastAsync(0, "evolution.reflected", "进化引擎反思完成", ct, reflectedDetails)
                    .ConfigureAwait(false);
                continue;
            }
            if (evt is not ScheduledTaskCompletedEvent schedule) continue;
            var details = new JsonObject
            {
                ["taskId"] = schedule.TaskId,
                ["name"] = schedule.Name,
                ["status"] = schedule.Status,
            };
            if (!string.IsNullOrWhiteSpace(schedule.SessionId)) details["sessionId"] = schedule.SessionId;
            if (!string.IsNullOrWhiteSpace(schedule.Error)) details["error"] = schedule.Error;
            await BroadcastAsync(0, "schedule.updated", schedule.TaskId, ct, details).ConfigureAwait(false);
        }
    }

    private async Task BroadcastAsync(
        long id,
        string eventName,
        string data,
        CancellationToken ct,
        JsonObject? details = null)
    {
        List<ClientSink> clients;
        lock (_clientsGate) clients = _clients.Values.ToList();
        foreach (var client in clients)
        {
            try
            {
                await WriteAsync(client.Writer, client.WriterGate, id, eventName, data, ct, details: details)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // The client disconnected while the broadcast was in flight.
            }
        }
    }

    private long RegisterClient(StreamWriter writer, SemaphoreSlim writerGate)
    {
        var clientId = Interlocked.Increment(ref _nextClientId);
        lock (_clientsGate) _clients[clientId] = new ClientSink(writer, writerGate);
        return clientId;
    }

    private void UnregisterClient(long clientId)
    {
        lock (_clientsGate) _clients.Remove(clientId);
    }

    private async Task RunTurnAsync(
        AgentSession session,
        WorkspaceInfo workspace,
        string message,
        IReadOnlyList<ChatImageAttachment> images,
        ReasoningLevel reasoningLevel,
        long id,
        StreamWriter writer,
        SemaphoreSlim writerGate,
        CancellationToken turnCt,
        CancellationToken connectionCt,
        AgentSteeringQueue steering)
    {
        // Unique per-turn identity for file write-lock ownership; released when the
        // turn ends even if a tool was interrupted before its own finally ran.
        var owner = $"{session.Header.Id}/{Guid.NewGuid().ToString("N")[..8]}";
        await using var turnRuntime = _useIsolatedTurnRuntime
            ? HaoyueRuntime.CreateIsolated(workspace, _fileLocks, owner, services =>
              {
                  // Register the process-wide instances AFTER the default type
                  // registrations so DI resolves these for every turn.
                  services.AddSingleton<ILlmHttpFactory>(_sharedHttp);
                  services.AddSingleton(_sharedBreaker);
                  services.AddSingleton(_sharedLocalModels);
                  services.AddSingleton(_undoRegistry);
              })
            : null;
        var runtime = turnRuntime ?? _runtime;
        using var subscription = runtime.Events.Subscribe();
        var sessionUsage = new SessionUsageAccumulator();
        var forwarder = ForwardEventsAsync(
            subscription, writer, writerGate, id, session.Header.Id, connectionCt, sessionUsage);
        AgentTurnResult? result = null;
        Exception? failure = null;

        try
        {
            runtime.Prompts.SetWorkspaceRoot(workspace.IsGlobal ? null : workspace.PromptsDir);
            runtime.Skills.Attach(workspace);
            if (_useIsolatedTurnRuntime && runtime.Mcp.LoadServerConfigs(workspace).Count > 0)
                await runtime.Mcp.ConnectAllAsync(workspace, turnCt).ConfigureAwait(false);

            // Crash-reconciliation hook: mutating steps update the in-memory journal
            // record (and rewrite the crash file) so a process death leaves not just a
            // "was mid-turn" marker but an executed-steps + changed-files ledger.
            if (_useIsolatedTurnRuntime)
            {
                runtime.Agent.MutatingStepObserved = stepRecord =>
                    UpdateTurnJournalSteps(session.Header.Id, stepRecord);
            }

            result = _runTurn is null
                ? await runtime.Agent.RunTurnAsync(
                    session, workspace, message, turnCt, reasoningLevel, images, steering).ConfigureAwait(false)
                : await _runTurn(session, workspace, message, turnCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (turnCt.IsCancellationRequested)
        {
            result = new AgentTurnResult("", true, null);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            subscription.Dispose();
            try { await forwarder.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            if (sessionUsage.HasActivity)
            {
                try
                {
                    var persisted = _runtime.Sessions.RecordUsage(workspace, session.Header.Id, sessionUsage.ToUsage());
                    session.Header.LlmRounds = persisted.LlmRounds;
                    session.Header.ExecutionSteps = persisted.ExecutionSteps;
                    session.Header.InputTokens = persisted.InputTokens;
                    session.Header.TotalInputTokens = persisted.TotalInputTokens;
                    session.Header.CachedInputTokens = persisted.CachedInputTokens;
                    session.Header.OutputTokens = persisted.OutputTokens;
                    session.Header.OutputElapsedMs = persisted.OutputElapsedMs;
                    session.Header.UpdatedAt = persisted.UpdatedAt;
                }
                catch
                {
                    // Usage persistence must not fail the turn.
                }
            }
            _fileLocks.ReleaseAll(owner);
        }

        try
        {
            // Surface the turn's revertible file list on the terminal envelope so the
            // client can offer a one-click undo (agent.undo) without another RPC.
            JsonObject? terminalDetails = null;
            var undoable = runtime.UndoRegistry.Peek(session.Header.Id);
            if (undoable is { Changes.Count: > 0 })
            {
                terminalDetails = new JsonObject
                {
                    ["undoableFiles"] = new JsonArray(undoable.Changes
                        .Select(c => JsonValue.Create(
                            c.AbsolutePath.StartsWith(workspace.Root, StringComparison.OrdinalIgnoreCase)
                                ? c.AbsolutePath[workspace.Root.Length..].TrimStart('/', '\\')
                                : c.AbsolutePath))
                        .ToArray<JsonNode?>()),
                };
            }

            if (failure is not null)
            {
                // 回合失败的终态 error 也必须携带契约错误码（details 已有 undoableFiles 时合并）。
                terminalDetails ??= new JsonObject();
                terminalDetails["code"] = DaemonErrorCode.InternalError.ToWire();
                await WriteAsync(writer, writerGate, id, "error", failure.Message, connectionCt, session.Header.Id, terminalDetails).ConfigureAwait(false);
            }
            else if (result!.Cancelled)
                await WriteAsync(writer, writerGate, id, "cancelled", result.Text, connectionCt, session.Header.Id, terminalDetails).ConfigureAwait(false);
            else
                await WriteAsync(writer, writerGate, id,
                    result.Error is null ? "done" : "error",
                    result.Error ?? result.Text, connectionCt, session.Header.Id, terminalDetails).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The client disconnected while the turn was ending.
        }
    }

    private static async Task ForwardEventsAsync(
        IEventSubscription subscription,
        StreamWriter writer,
        SemaphoreSlim writerGate,
        long id,
        string sessionId,
        CancellationToken ct,
        SessionUsageAccumulator usage)
    {
        await foreach (var evt in subscription.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            AccumulateSessionUsage(usage, evt);
            // ErrorEvent is a runtime diagnostic, not a terminal protocol response. The turn
            // result below sends the final `error` envelope with the provider's full detail.
            // Forwarding this event as `error` would terminate desktop clients early and lose
            // ErrorEvent.Detail (for example the HTTP status and API response message).
            var payload = evt switch
            {
                AssistantTextDeltaEvent delta => (Name: (string?)"delta", Data: delta.Delta, Details: (JsonObject?)null),
                ThinkingDeltaEvent thinking => (Name: (string?)"thinking", Data: thinking.Delta, Details: (JsonObject?)null),
                UserSteerEvent steer => (Name: (string?)"steer", Data: steer.Instruction, Details: (JsonObject?)null),
                ImageViewedEvent image => (
                    Name: (string?)"image_view",
                    Data: image.Name,
                    Details: new JsonObject
                    {
                        ["imageId"] = image.ImageId,
                        ["mediaType"] = image.MediaType,
                    }),
                StatusEvent status => (Name: (string?)"status", Data: status.Status, Details: (JsonObject?)null),
                ToolCallStartedEvent tool => (
                    Name: (string?)"tool_start",
                    Data: tool.ToolName,
                    Details: new JsonObject
                    {
                        ["callId"] = tool.CallId,
                        ["summary"] = tool.ArgumentSummary,
                    }),
                ToolCallCompletedEvent tool => (
                    Name: (string?)"tool_done",
                    Data: tool.ResultSummary,
                    Details: new JsonObject
                    {
                        ["callId"] = tool.CallId,
                        ["success"] = tool.Success,
                        ["durationMs"] = tool.Duration.TotalMilliseconds,
                    }),
                FileDiffEvent diff => (
                    Name: (string?)"file_diff",
                    Data: diff.FilePath,
                    Details: new JsonObject
                    {
                        ["callId"] = diff.CallId,
                        ["diff"] = diff.UnifiedDiff,
                    }),
                ModelInvocationStartedEvent model => (
                    Name: (string?)"model_start",
                    Data: $"{model.ProviderId}/{model.ModelId}",
                    Details: new JsonObject
                    {
                        ["provider"] = model.ProviderId,
                        ["model"] = model.ModelId,
                        ["step"] = model.Step,
                    }),
                UsageRecordedEvent tokenEvent => (
                    Name: (string?)"usage",
                    Data: $"{tokenEvent.ProviderId}/{tokenEvent.ModelId}",
                    Details: new JsonObject
                    {
                        ["provider"] = tokenEvent.ProviderId,
                        ["model"] = tokenEvent.ModelId,
                        ["inputTokens"] = tokenEvent.InputTokens,
                        ["outputTokens"] = tokenEvent.OutputTokens,
                        ["totalInputTokens"] = tokenEvent.TotalInputTokens,
                        ["cachedInputTokens"] = tokenEvent.CachedInputTokens,
                        ["elapsedMs"] = tokenEvent.Elapsed.TotalMilliseconds,
                    }),
                WorkflowEvent workflow => (
                    Name: (string?)"workflow",
                    Data: workflow.Label,
                    Details: new JsonObject
                    {
                        ["step"] = workflow.Step,
                        ["kind"] = workflow.Kind,
                        ["label"] = workflow.Label,
                        ["detail"] = workflow.Detail,
                    }),
                PlanUpdatedEvent plan => (
                    Name: (string?)"plan_update",
                    Data: plan.StepsJson,
                    Details: new JsonObject
                    {
                        ["steps"] = JsonNode.Parse(plan.StepsJson),
                        ["explanation"] = plan.Explanation,
                    }),
                _ => (Name: (string?)null, Data: "", Details: (JsonObject?)null),
            };
            if (payload.Name is not null)
                await WriteAsync(writer, writerGate, id, payload.Name, payload.Data, ct, sessionId, payload.Details).ConfigureAwait(false);
        }
    }

    private static void AccumulateSessionUsage(SessionUsageAccumulator usage, RuntimeEvent evt)
    {
        switch (evt)
        {
            case ModelInvocationStartedEvent:
                usage.LlmRounds++;
                break;
            case WorkflowEvent workflow:
                if (workflow.Step > usage.LastWorkflowStep)
                {
                    usage.ExecutionSteps += workflow.Step - usage.LastWorkflowStep;
                    usage.LastWorkflowStep = workflow.Step;
                }
                break;
            case UsageRecordedEvent token:
                usage.InputTokens += token.InputTokens;
                usage.TotalInputTokens += token.TotalInputTokens;
                usage.CachedInputTokens += token.CachedInputTokens;
                usage.OutputTokens += token.OutputTokens;
                usage.OutputElapsedMs += (long)token.Elapsed.TotalMilliseconds;
                break;
        }
    }

    private WorkspaceInfo ResolveWorkspace(JsonObject parameters)
    {
        if (parameters["global"]?.GetValue<bool?>() == true)
            return _globalWorkspace;

        var path = parameters["workspace"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(path)) return _runtime.Workspace;

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DaemonRequestException($"Invalid workspace path: {ex.Message}");
        }

        if (!Directory.Exists(fullPath))
            throw new DaemonRequestException($"Workspace directory not found: {fullPath}");
        return _runtime.Workspaces.Detect(fullPath);
    }

    private static IReadOnlyList<ChatImageAttachment> ParseImages(JsonNode? node)
    {
        if (node is null) return [];
        if (node is not JsonArray array)
            throw new DaemonRequestException("params.images must be an array");
        if (array.Count > MaxImageCount)
            throw new DaemonRequestException($"A turn supports at most {MaxImageCount} images");

        var images = new List<ChatImageAttachment>(array.Count);
        long totalBytes = 0;
        foreach (var item in array)
        {
            if (item is not JsonObject image)
                throw new DaemonRequestException("Each params.images item must be an object");
            var id = image["id"]?.GetValue<string>()?.Trim();
            var name = image["name"]?.GetValue<string>()?.Trim();
            var mediaType = image["mediaType"]?.GetValue<string>()?.Trim().ToLowerInvariant();
            var data = image["data"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(data))
                throw new DaemonRequestException("Each image requires name and base64 data");
            if (mediaType is null || !SupportedImageTypes.Contains(mediaType))
                throw new DaemonRequestException($"Unsupported image type for {name}: {mediaType ?? "unknown"}");
            if (data.Length > ((MaxImageBytes + 2L) / 3L * 4L) + 4L)
                throw new DaemonRequestException($"Image {name} exceeds the {MaxImageBytes / 1024 / 1024} MB limit");

            byte[] decoded;
            try { decoded = Convert.FromBase64String(data); }
            catch (FormatException)
            {
                throw new DaemonRequestException($"Image {name} contains invalid base64 data");
            }
            if (decoded.Length > MaxImageBytes)
                throw new DaemonRequestException($"Image {name} exceeds the {MaxImageBytes / 1024 / 1024} MB limit");
            totalBytes += decoded.Length;
            if (totalBytes > MaxTotalImageBytes)
                throw new DaemonRequestException(
                    $"Images exceed the {MaxTotalImageBytes / 1024 / 1024} MB total limit");

            var safeName = new string(name.Replace('\\', '/').Split('/').Last()
                .Where(character => !char.IsControl(character)).Take(180).ToArray());
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "image";
            images.Add(new ChatImageAttachment(
                string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id[..Math.Min(id.Length, 128)],
                safeName,
                mediaType,
                data,
                decoded.Length));
        }
        return images;
    }

    private AgentSession LoadTurnSession(
        WorkspaceInfo workspace,
        string? requestedSessionId,
        ref AgentSession? legacySession)
    {
        if (!string.IsNullOrWhiteSpace(requestedSessionId))
        {
            var loaded = _runtime.Sessions.Load(workspace, requestedSessionId);
            return loaded ?? throw new DaemonRequestException($"Session {requestedSessionId} not found");
        }

        if (legacySession is not null && SessionBelongsTo(legacySession, workspace))
            return legacySession;

        legacySession = _runtime.Sessions.LoadLatest(workspace)
                        ?? _runtime.Sessions.Create(workspace);
        return legacySession;
    }

    private sealed class ActiveTurn(
        AgentSession session,
        WorkspaceInfo workspace,
        CancellationTokenSource cancellation)
    {
        public AgentSession Session { get; } = session;
        public WorkspaceInfo Workspace { get; } = workspace;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public AgentSteeringQueue Steering { get; } = new();
        public Task? Task { get; set; }
    }

    /// <summary>
    /// Turn-resume semantics after a daemon crash: any session still recorded in the
    /// crash journal was mid-turn when the process died. Best effort only — the
    /// session gets an interruption notice appended to its history and every client
    /// receives a <c>turn.interrupted</c> broadcast; the turn is never re-run
    /// automatically because half-executed tools make a blind retry unsafe. When the
    /// journal carries a mutating-step ledger, the notice lists what may have changed.
    /// </summary>
    internal async Task RecoverInterruptedTurnsAsync(CancellationToken ct)
    {
        var records = ActiveTurnJournal.Read(_activeTurnsFile);
        if (records.Count == 0) return;

        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var workspace = record.IsGlobal
                    ? _runtime.Workspaces.CreateGlobal()
                    : TryDetectWorkspace(record.WorkspaceRoot);
                if (workspace is not null)
                {
                    var session = _runtime.Sessions.Load(workspace, record.SessionId);
                    if (session is not null)
                    {
                        var notice = ">>> [turn interrupted] 上次回合因 Runtime 异常退出而中断，上方可能缺少本轮的最终回答。可重新发送消息或要求重试。";
                        if (record.Steps is { Count: > 0 })
                        {
                            // Reconciliation, not re-run: list what the interrupted turn
                            // touched so the user can verify the workspace against the
                            // conversation record. Builtin file tools are revertible via
                            // agent.undo if the daemon deposited a ledger — but after a
                            // crash the ledger is gone, so only the paths are shown.
                            var lines = record.Steps.Select(s =>
                                $"  - {s.Tool} → {s.Target}{(s.Compensable ? "" : "（不可自动恢复）")}");
                            notice += $"\n本轮已执行到以下修改步骤（共 {record.Steps.Count} 项），请核对这些文件的当前状态：\n{string.Join("\n", lines)}";
                        }
                        _runtime.Sessions.Append(session, ChatMessage.User(notice));
                    }
                }
            }
            catch
            {
                // Recovery is best effort: an unloadable workspace or session is skipped.
            }

            var details = new JsonObject
            {
                ["sessionId"] = record.SessionId,
                ["reason"] = "daemon_crash",
            };
            lock (_interruptedTurnsGate) _interruptedTurns.Add(record.SessionId);
            try
            {
                await BroadcastAsync(0, "turn.interrupted", "上次回合被 Runtime 重启中断", ct, details)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Clients are not connected yet (typical at startup) or are vanishing.
            }
        }

        ActiveTurnJournal.Clear(_activeTurnsFile);
    }

    private WorkspaceInfo? TryDetectWorkspace(string root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
            return _runtime.Workspaces.Detect(root);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Appends one mutating step to the turn's crash-journal record and rewrites the
    /// journal file. Called from the agent via <see cref="Agent.MutatingStepObserved"/>
    /// — only for mutating steps, so the write amplification stays proportional to the
    /// number of file changes, not the number of tool calls. Best effort by design.
    /// </summary>
    private void UpdateTurnJournalSteps(string sessionId, TurnStepRecord step)
    {
        if (!_turnJournal.TryGetValue(sessionId, out var record)) return;
        var steps = (record.Steps ?? []).ToList();
        steps.Add(new TurnStepSummary(step.Tool, step.Target, step.Compensable));
        _turnJournal[sessionId] = record with { Steps = steps };
        try
        {
            ActiveTurnJournal.Write(_activeTurnsFile, _turnJournal.Values.ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Journal is an optimization; never fail the turn over it.
        }
    }

    private string CurrentMode() =>
        AgentModeExtensions.Parse(
            _runtime.Workspace.Config?.Mode ?? _runtime.ConfigStore.Config.Agent.Mode)
        .ToString().ToLowerInvariant();

    private void SaveMode(string mode)
    {
        var config = _runtime.ConfigStore.Config;
        config.Agent.Mode = mode;
        _runtime.ConfigStore.Save();

        if (_runtime.Workspace.Config is not { } workspaceConfig) return;
        workspaceConfig.Mode = mode;
        Directory.CreateDirectory(_runtime.Workspace.HaoyueDir);
        var path = Path.Combine(_runtime.Workspace.HaoyueDir, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(workspaceConfig, HaoyueJsonContext.Default.WorkspaceConfig));
    }

    private static bool TryNormalizeMode(string? rawMode, out string mode)
    {
        mode = rawMode?.Trim().ToLowerInvariant() ?? "";
        return mode is "plan" or "readonly" or "edit" or "auto";
    }

    private static ReasoningLevel ParseReasoningLevel(JsonNode? node, ReasoningLevel fallback)
    {
        if (node is null) return fallback;
        var value = node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
            ? text
            : null;
        if (ReasoningLevelExtensions.TryParse(value, out var level)) return level;
        throw new DaemonRequestException(
            "params.reasoningLevel must be one of: none, low, medium, high, max, xhigh, ultra");
    }

    /// <summary>
    /// expertId: absent → keep the session's current binding; "" → unbind;
    /// otherwise must match an ExpertCatalog id. Returns the resolved id (null = none).
    /// </summary>
    private static string? ParseExpertId(JsonNode? node, string? current)
    {
        if (node is null) return current;
        if (node is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var text))
            throw new DaemonRequestException("params.expertId must be a string (expert id from expert.list)");
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (ExpertCatalog.Find(text) is null)
            throw new DaemonRequestException(
                $"Unknown expert '{text.Trim()}' — run expert.list for valid ids");
        return text.Trim();
    }

    private string WorkspaceJson(WorkspaceInfo workspace)
    {
        var kinds = new JsonArray(workspace.ProjectKinds.Select(kind => JsonValue.Create(kind)).ToArray());
        return new JsonObject
        {
            ["path"] = workspace.Root,
            ["name"] = workspace.Name,
            ["projectKinds"] = kinds,
            ["mode"] = CurrentMode(),
        }.ToJsonString();
    }

    // 方法清单的唯一事实源是 DaemonContract.Methods（Contract First）；
    // 此处只做投影，禁止手工增删方法名。
    private static string ProtocolInfoJson(string? versionWarning = null)
    {
        var info = new JsonObject
        {
            ["version"] = ProtocolVersion,
            ["transport"] = "jsonl",
            ["capabilities"] = new JsonArray(
                "chat", "image-input", "concurrent-turns", "reasoning-level", "agent.steer", "agent.cancel", "agent.mode", "workspace", "provider",
                "model", "mcp", "skill", "usage", "project", "session", "global-session", "doctor", "file-locks", "routing", "schedule", "factory-reset", "prompt-optimize", "config-status", "config-rebuild", "contract"),
            ["methods"] = new JsonArray(DaemonContract.MethodNames.Select(name => (JsonNode)name).ToArray()),
        };
        if (versionWarning is not null) info["versionWarning"] = versionWarning;
        return info.ToJsonString();
    }

    /// <summary>
    /// Cancels every active turn across all connections and waits (bounded) for them
    /// to finish. Used by administration operations that mutate global state — factory
    /// reset and database rebuild drop SQLite tables, which must not race live writers.
    /// </summary>
    private async Task CancelAllTurnsAsync(TimeSpan timeout)
    {
        var turns = new List<ActiveTurn>();
        foreach (var connection in _connections.Values)
        {
            foreach (var turn in connection.ActiveTurns.Values)
            {
                turn.Cancellation.Cancel();
                turns.Add(turn);
            }
        }
        if (turns.Count == 0) return;
        try
        {
            await Task.WhenAll(turns.Select(turn => turn.Task ?? Task.CompletedTask))
                .WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Timeout or a failing turn must not block the reset; exceptions are
            // observed (and swallowed) below.
        }
        foreach (var turn in turns)
            if (turn.Task is not null) await ObserveAsync(turn.Task).ConfigureAwait(false);
    }

    private async Task RunAdminAsync(
        StreamWriter writer,
        SemaphoreSlim writerGate,
        long id,
        bool exclusive,
        Func<CancellationToken, Task<string>> action,
        CancellationToken ct)
    {
        if (exclusive) await _adminGate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var data = await action(ct).ConfigureAwait(false);
            await WriteAsync(writer, writerGate, id, "result", data, ct).ConfigureAwait(false);
        }
        catch (DaemonRequestException ex)
        {
            await WriteAsync(writer, writerGate, id, "error", ex.Message, ct, code: ex.Code).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            await WriteAsync(writer, writerGate, id, "error", $"Invalid request: {ex.Message}", ct, code: DaemonErrorCode.InvalidRequest).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteAsync(writer, writerGate, id, "error", ex.Message, ct, code: DaemonErrorCode.InternalError).ConfigureAwait(false);
        }
        finally
        {
            if (exclusive) _adminGate.Release();
        }
    }

    private static JsonObject Params(JsonObject request) =>
        request["params"] as JsonObject ?? new JsonObject();

    private static async Task WriteAsync(
        StreamWriter writer,
        SemaphoreSlim writerGate,
        long id,
        string eventName,
        string data,
        CancellationToken ct,
        string? sessionId = null,
        JsonObject? details = null,
        DaemonErrorCode code = DaemonErrorCode.RequestFailed)
    {
        // 错误事件必须携带契约错误码：调用方未显式给 details 时自动补 details.code。
        if (eventName == "error" && details is null)
            details = new JsonObject { ["code"] = code.ToWire() };
        var payload = new JsonObject { ["id"] = id, ["event"] = eventName, ["data"] = data };
        if (!string.IsNullOrWhiteSpace(sessionId)) payload["sessionId"] = sessionId;
        if (details is not null) payload["details"] = details;
        await writerGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(payload.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
        }
        finally
        {
            writerGate.Release();
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private sealed record ClientSink(StreamWriter Writer, SemaphoreSlim WriterGate);

    private static bool SessionBelongsTo(AgentSession session, WorkspaceInfo workspace) =>
        workspace.IsGlobal
            ? string.IsNullOrWhiteSpace(session.Header.Workspace)
            : string.Equals(session.Header.Workspace, workspace.Root, StringComparison.OrdinalIgnoreCase);
}
