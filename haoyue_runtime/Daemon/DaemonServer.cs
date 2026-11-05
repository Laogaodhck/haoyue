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
public sealed partial class DaemonServer : IAsyncDisposable
{

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



    public async Task RunAsync(CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        var runCt = linkedCts.Token;
        await RecoverInterruptedTurnsAsync(runCt).ConfigureAwait(false); Teams.TeamRunReconciler.ReconcileOnStartup();
        // 本地模型库运行时策略（驻留上限 / 并发槽 / 空闲回收）随配置装配。
        ConfigureLocalModelCache();
        // L-D 会话保留策略维护：启动执行一次（归档过期会话/清理孤儿图片/必要时
        // VACUUM），之后每 24h 一轮。后台执行、单项容错，不影响服务主循环。
        StartSessionMaintenance();
        _maintenanceTimer = new Timer(
            _ => StartSessionMaintenance(), null, TimeSpan.FromHours(24), TimeSpan.FromHours(24));
        // 本地模型空闲回收：5 分钟一轮，归还超过配置时长未使用的权重（0 = 关闭）。
        _localModelIdleTimer = new Timer(
            _ => StartLocalModelIdleReclaim(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        // 本地模型预热（可选）：后台加载活跃本地模型，第一条消息免等权重加载。
        StartLocalModelPreload();
        // GPU 后端启动自检：CPU 构建的后端 + GPU offload 请求 = 本地推理全程 CPU 化
        // （同机实测 11 倍差距）。原有一次性 stderr 警告只有 CLI 场景可见，daemon +
        // 桌面用户毫无感知——启动时显式广播，进事件台账并可经 events.recent 回放。
        PublishGpuBackendWarning();
        // H-C 记忆固化：启动执行一次，之后每 6h 扫描近期会话沉淀事实；配置关闭时
        // 完全不启用（连定时器都不挂）。
        if (_runtime.ConfigStore.Config.Agent.MemoryConsolidationEnabled)
        {
            StartConsolidation();
            _consolidationTimer = new Timer(
                _ => StartConsolidation(), null, TimeSpan.FromHours(6), TimeSpan.FromHours(6));
        }
        var schedulerTask = RunSchedulerAsync(runCt);
        // 局域网监听与本机管道 / socket 并行；配置变更时由 daemon.lan.set 热重启。
        var lanTask = Task.Run(() => _lan.RunAsync(runCt), runCt);
        _fileLog.Info(OperatingSystem.IsWindows()
            ? $"serving on named pipe {PipeName}"
            : $"serving on unix socket {SocketPath}");
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
            try
            {
                await lanTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (runCt.IsCancellationRequested)
            {
            }
            _fileLog.Marker("daemon host stopped");
        }
    }











    /// <summary>
    /// Requires {"method":"handshake","params":{"token":...}} as the first message.
    /// Returns true once authenticated; on a missing/wrong token, a malformed
    /// message or a timeout the client is told why and the link is dropped.
    /// </summary>
    private async Task<bool> AuthenticateConnectionAsync(
        BoundedLineReader lines,
        StreamWriter writer,
        SemaphoreSlim writerGate,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(HandshakeTimeoutSeconds));
        try
        {
            var read = await lines.ReadLineAsync(MaxHandshakeLineLength, timeout.Token).ConfigureAwait(false);
            // 超长握手行在读取时就被截停（TooLong）：未认证连接不值得再回包。
            if (read.TooLong) return false;
            var line = read.Line;
            if (string.IsNullOrWhiteSpace(line))
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
                await WriteAsync(writer, writerGate, id, "result", DaemonProtocol.Info(versionWarning), timeout.Token).ConfigureAwait(false);
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
                    case "ping": await Handle_ping(request, id, context).ConfigureAwait(false); break;
                    case "protocol.info": await Handle_protocol_info(request, id, context).ConfigureAwait(false); break;
                    case "daemon.lan.get": await Handle_daemon_lan_get(request, id, context).ConfigureAwait(false); break;
                    case "daemon.lan.set": await Handle_daemon_lan_set(request, id, context).ConfigureAwait(false); break;
                    case "events.recent": await Handle_events_recent(request, id, context).ConfigureAwait(false); break;
                    case "evolution.inspect": await Handle_evolution_inspect(request, id, context).ConfigureAwait(false); break;
                    case "evolution.reflect": await Handle_evolution_reflect(request, id, context).ConfigureAwait(false); break;
                    case "evolution.pending-list": await Handle_evolution_pending_list(request, id, context).ConfigureAwait(false); break;
                    case "evolution.decide": await Handle_evolution_decide(request, id, context).ConfigureAwait(false); break;
                    case "evolution.config.get": await Handle_evolution_config_get(request, id, context).ConfigureAwait(false); break;
                    case "evolution.config.set": await Handle_evolution_config_set(request, id, context).ConfigureAwait(false); break;
                    case "evolution.history": await Handle_evolution_history(request, id, context).ConfigureAwait(false); break;
                    case "evolution.stats": await Handle_evolution_stats(request, id, context).ConfigureAwait(false); break;
                    case "feedback.turn": await Handle_feedback_turn(request, id, context).ConfigureAwait(false); break;
                    case "speech.transcribe": await Handle_speech_transcribe(request, id, context).ConfigureAwait(false); break;
                    case "speech.synthesize": await Handle_speech_synthesize(request, id, context).ConfigureAwait(false); break;
                    case "workspace.init": await Handle_workspace_init(request, id, context).ConfigureAwait(false); break;
                    case "factory.reset": await Handle_factory_reset(request, id, context).ConfigureAwait(false); break;
                    case "chat": case "agent.runTurn": case "agent/runTurn": await Handle_agent_alias_runTurn(request, id, context).ConfigureAwait(false); break;
                    case "agent.steer": await Handle_agent_steer(request, id, context).ConfigureAwait(false); break;
                    case "agent.respondApproval": await Handle_agent_respondApproval(request, id, context).ConfigureAwait(false); break;
                    case "agent.interrupted": await Handle_agent_interrupted(request, id, context).ConfigureAwait(false); break;
                    case "agent.undo": await Handle_agent_undo(request, id, context).ConfigureAwait(false); break;
                    case "agent.cancel": await Handle_agent_cancel(request, id, context).ConfigureAwait(false); break;
                    case "workspace.get": await Handle_workspace_get(request, id, context).ConfigureAwait(false); break;
                    case "workspace.files": await Handle_workspace_files(request, id, context).ConfigureAwait(false); break;
                    case "workspace.open": await Handle_workspace_open(request, id, context).ConfigureAwait(false); break;
                    case "workspace.trust": case "workspace.untrust": await Handle_workspace_untrust(request, id, context).ConfigureAwait(false); break;
                    case "agent.mode.get": await Handle_agent_mode_get(request, id, context).ConfigureAwait(false); break;
                    case "agent.mode.switch": await Handle_agent_mode_switch(request, id, context).ConfigureAwait(false); break;
                    case "config.save": await Handle_config_save(request, id, context).ConfigureAwait(false); break;
                    case "config.get": await Handle_config_get(request, id, context).ConfigureAwait(false); break;
                    case "config.status": await Handle_config_status(request, id, context).ConfigureAwait(false); break;
                    case "config.rebuild": await Handle_config_rebuild(request, id, context).ConfigureAwait(false); break;
                    case "routing.get": await Handle_routing_get(request, id, context).ConfigureAwait(false); break;
                    case "routing.set": await Handle_routing_set(request, id, context).ConfigureAwait(false); break;
                    case "agent.config.get": await Handle_agent_config_get(request, id, context).ConfigureAwait(false); break;
                    case "agent.config.set": await Handle_agent_config_set(request, id, context).ConfigureAwait(false); break;
                    case "advanced.get": await Handle_advanced_get(request, id, context).ConfigureAwait(false); break;
                    case "advanced.set": await Handle_advanced_set(request, id, context).ConfigureAwait(false); break;
                    case "web.allowedGet": await Handle_web_allowedGet(request, id, context).ConfigureAwait(false); break;
                    case "web.allowedSet": await Handle_web_allowedSet(request, id, context).ConfigureAwait(false); break;
                    case "prompt.optimize": await Handle_prompt_optimize(request, id, context).ConfigureAwait(false); break;
                    case "schedule.list": await Handle_schedule_list(request, id, context).ConfigureAwait(false); break;
                    case "schedule.create": case "schedule.update": await Handle_schedule_update(request, id, context).ConfigureAwait(false); break;
                    case "schedule.toggle": await Handle_schedule_toggle(request, id, context).ConfigureAwait(false); break;
                    case "schedule.delete": await Handle_schedule_delete(request, id, context).ConfigureAwait(false); break;
                    case "schedule.run": await Handle_schedule_run(request, id, context).ConfigureAwait(false); break;
                    case "hooks.list": await Handle_hooks_list(request, id, context).ConfigureAwait(false); break;
                    case "hooks.save": await Handle_hooks_save(request, id, context).ConfigureAwait(false); break;
                    case "hooks.delete": await Handle_hooks_delete(request, id, context).ConfigureAwait(false); break;
                    case "hooks.test": await Handle_hooks_test(request, id, context).ConfigureAwait(false); break;
                    case "task.start": await Handle_task_start(request, id, context).ConfigureAwait(false); break;
                    case "task.get": await Handle_task_get(request, id, context).ConfigureAwait(false); break;
                    case "task.list": await Handle_task_list(request, id, context).ConfigureAwait(false); break;
                    case "task.cancel": await Handle_task_cancel(request, id, context).ConfigureAwait(false); break;
                    case "provider.list": await Handle_provider_list(request, id, context).ConfigureAwait(false); break;
                    case "provider.upsert": await Handle_provider_upsert(request, id, context).ConfigureAwait(false); break;
                    case "provider.use": await Handle_provider_use(request, id, context).ConfigureAwait(false); break;
                    case "provider.remove": await Handle_provider_remove(request, id, context).ConfigureAwait(false); break;
                    case "provider.test": await Handle_provider_test(request, id, context).ConfigureAwait(false); break;
                    case "provider.models.fetch": await Handle_provider_models_fetch(request, id, context).ConfigureAwait(false); break;
                    case "local.models": await Handle_local_models(request, id, context).ConfigureAwait(false); break;
                    case "model.catalog": await Handle_model_catalog(request, id, context).ConfigureAwait(false); break;
                    case "model.test": await Handle_model_test(request, id, context).ConfigureAwait(false); break;
                    case "model.status": await Handle_model_status(request, id, context).ConfigureAwait(false); break;
                    case "model.preload": await Handle_model_preload(request, id, context).ConfigureAwait(false); break;
                    case "model.unload": await Handle_model_unload(request, id, context).ConfigureAwait(false); break;
                    case "model.update": await Handle_model_update(request, id, context).ConfigureAwait(false); break;
                    case "model.config.get": await Handle_model_config_get(request, id, context).ConfigureAwait(false); break;
                    case "model.benchmark": await Handle_model_benchmark(request, id, context).ConfigureAwait(false); break;
                    case "mcp.list": await Handle_mcp_list(request, id, context).ConfigureAwait(false); break;
                    case "mcp.upsert": await Handle_mcp_upsert(request, id, context).ConfigureAwait(false); break;
                    case "mcp.remove": await Handle_mcp_remove(request, id, context).ConfigureAwait(false); break;
                    case "mcp.reload": await Handle_mcp_reload(request, id, context).ConfigureAwait(false); break;
                    case "connectors.list": await Handle_connectors_list(request, id, context).ConfigureAwait(false); break;
                    case "connectors.install": await Handle_connectors_install(request, id, context).ConfigureAwait(false); break;
                    case "connectors.uninstall": await Handle_connectors_uninstall(request, id, context).ConfigureAwait(false); break;
                    case "skill.list": await Handle_skill_list(request, id, context).ConfigureAwait(false); break;
                    case "skill.import": await Handle_skill_import(request, id, context).ConfigureAwait(false); break;
                    case "skill.toggle": await Handle_skill_toggle(request, id, context).ConfigureAwait(false); break;
                    case "skill.official.list": await Handle_skill_official_list(request, id, context).ConfigureAwait(false); break;
                    case "expert.list": await Handle_expert_list(request, id, context).ConfigureAwait(false); break;
                    case "skill.official.install": await Handle_skill_official_install(request, id, context).ConfigureAwait(false); break;
                    case "knowledge.list": await Handle_knowledge_list(request, id, context).ConfigureAwait(false); break;
                    case "knowledge.search": await Handle_knowledge_search(request, id, context).ConfigureAwait(false); break;
                    case "knowledge.save": await Handle_knowledge_save(request, id, context).ConfigureAwait(false); break;
                    case "knowledge.delete": await Handle_knowledge_delete(request, id, context).ConfigureAwait(false); break;
                    case "knowledge.import": await Handle_knowledge_import(request, id, context).ConfigureAwait(false); break;
                    case "knowledge.tags": await Handle_knowledge_tags(request, id, context).ConfigureAwait(false); break;
                    case "knowledge.export": await Handle_knowledge_export(request, id, context).ConfigureAwait(false); break;
                    case "knowledge.synonyms.get": await Handle_knowledge_synonyms_get(request, id, context).ConfigureAwait(false); break;
                    case "knowledge.synonyms.save": await Handle_knowledge_synonyms_save(request, id, context).ConfigureAwait(false); break;
                    case "knowledge.notebook.list": await Handle_knowledge_notebook_list(request, id, context).ConfigureAwait(false); break;
                    case "knowledge.notebook.save": await Handle_knowledge_notebook_save(request, id, context).ConfigureAwait(false); break;
                    case "knowledge.notebook.delete": await Handle_knowledge_notebook_delete(request, id, context).ConfigureAwait(false); break;
                    case "knowledge.source.add": await Handle_knowledge_source_add(request, id, context).ConfigureAwait(false); break;
                    case "knowledge.source.list": await Handle_knowledge_source_list(request, id, context).ConfigureAwait(false); break;
                    case "knowledge.source.read": await Handle_knowledge_source_read(request, id, context).ConfigureAwait(false); break;
                    case "knowledge.source.delete": await Handle_knowledge_source_delete(request, id, context).ConfigureAwait(false); break;
                    case "knowledge.source.refresh": await Handle_knowledge_source_refresh(request, id, context).ConfigureAwait(false); break;
                    case "knowledge.retrieve": await Handle_knowledge_retrieve(request, id, context).ConfigureAwait(false); break;
                    case "knowledge.semantic.status": await Handle_knowledge_semantic_status(request, id, context).ConfigureAwait(false); break;
                    case "knowledge.semantic.build": await Handle_knowledge_semantic_build(request, id, context).ConfigureAwait(false); break;
                    case "memory.get": await Handle_memory_get(request, id, context).ConfigureAwait(false); break;
                    case "memory.save": await Handle_memory_save(request, id, context).ConfigureAwait(false); break;
                    case "rules.list": await Handle_rules_list(request, id, context).ConfigureAwait(false); break;
                    case "rules.save": await Handle_rules_save(request, id, context).ConfigureAwait(false); break;
                    case "rules.delete": await Handle_rules_delete(request, id, context).ConfigureAwait(false); break; case "rules.resolve": await RunAdminAsync(context.Writer, context.WriterGate, id, true, _ => Task.FromResult(_admin.ResolveRules(Params(request))), context.ConnectionCt).ConfigureAwait(false); break;
                    case "facts.list": await Handle_facts_rpc(request, id, context, "list").ConfigureAwait(false); break;
                    case "facts.add": await Handle_facts_rpc(request, id, context, "add").ConfigureAwait(false); break;
                    case "facts.update": await Handle_facts_rpc(request, id, context, "update").ConfigureAwait(false); break;
                    case "facts.delete": await Handle_facts_rpc(request, id, context, "delete").ConfigureAwait(false); break;
                    case "facts.pin": await Handle_facts_rpc(request, id, context, "pin").ConfigureAwait(false); break;
                    case "facts.restore": await Handle_facts_rpc(request, id, context, "restore").ConfigureAwait(false); break;
                    case "facts.confirm": await Handle_facts_rpc(request, id, context, "confirm").ConfigureAwait(false); break;
                    case "hooks.runs": await Handle_facts_rpc(request, id, context, "hooks_runs").ConfigureAwait(false); break;
                    case "team.list": await Handle_team_rpc(request, id, context, "list").ConfigureAwait(false); break;
                    case "team.save": await Handle_team_rpc(request, id, context, "save").ConfigureAwait(false); break;
                    case "team.delete": await Handle_team_rpc(request, id, context, "delete").ConfigureAwait(false); break;
                    case "team.copy": await Handle_team_rpc(request, id, context, "copy").ConfigureAwait(false); break;
                    case "team.run": await Handle_team_rpc(request, id, context, "run").ConfigureAwait(false); break;
                    case "team.cancel": await Handle_team_rpc(request, id, context, "cancel").ConfigureAwait(false); break;
                    case "team.runs": await Handle_team_rpc(request, id, context, "runs").ConfigureAwait(false); break;
                    case "agent.subagents.list": await Handle_agent_subagents_list(request, id, context).ConfigureAwait(false); break;
                    case "agent.subagents.save": await Handle_agent_subagents_save(request, id, context).ConfigureAwait(false); break;
                    case "agent.subagents.delete": await Handle_agent_subagents_delete(request, id, context).ConfigureAwait(false); break;
                    case "usage.get": await Handle_usage_get(request, id, context).ConfigureAwait(false); break;
                    case "usage.timeline": await Handle_usage_timeline(request, id, context).ConfigureAwait(false); break;
                    case "doctor.run": await Handle_doctor_run(request, id, context).ConfigureAwait(false); break;
                    case "lock.list": await Handle_lock_list(request, id, context).ConfigureAwait(false); break;
                    case "project.list": await Handle_project_list(request, id, context).ConfigureAwait(false); break;
                    case "project.upsert": await Handle_project_upsert(request, id, context).ConfigureAwait(false); break;
                    case "project.remove": await Handle_project_remove(request, id, context).ConfigureAwait(false); break;
                    case "session.list": await Handle_session_list(request, id, context).ConfigureAwait(false); break;
                    case "session.search": await Handle_session_search(request, id, context).ConfigureAwait(false); break;
                    case "session.get": await Handle_session_get(request, id, context).ConfigureAwait(false); break;
                    case "session.image": await Handle_session_image(request, id, context).ConfigureAwait(false); break;
                    case "session.truncate": await Handle_session_truncate(request, id, context).ConfigureAwait(false); break;
                    case "session.fork": await Handle_session_fork(request, id, context).ConfigureAwait(false); break;
                    case "session.update": await Handle_session_update(request, id, context).ConfigureAwait(false); break;
                    case "session.archive": await Handle_session_archive(request, id, context).ConfigureAwait(false); break;
                    case "session.delete": await Handle_session_delete(request, id, context).ConfigureAwait(false); break;
                    case "session.resume": await Handle_session_resume(request, id, context).ConfigureAwait(false); break;
                    case "session.new": await Handle_session_new(request, id, context).ConfigureAwait(false); break;
                    case "model.list": await Handle_model_list(request, id, context).ConfigureAwait(false); break;
                    case "model.switch": await Handle_model_switch(request, id, context).ConfigureAwait(false); break;
                    case "doctor": await Handle_doctor(request, id, context).ConfigureAwait(false); break;
                    case "shutdown": await Handle_shutdown(request, id, context).ConfigureAwait(false); break;
                    case "contract.export": await Handle_contract_export(request, id, context).ConfigureAwait(false); break;
                    default:
                        await WriteAsync(context.Writer, context.WriterGate, id, "error", $"Unknown method: {method}", context.ConnectionCt, code: DaemonErrorCode.UnknownMethod).ConfigureAwait(false);
                        break;
                        }
    }




















    // Event forwarding and usage accumulation live in DaemonServer.Forwarding.cs.

















    /// <summary>
    /// 内部错误的 data 文本：方法名 + 异常类型 + 消息 + 堆栈。前端「技术详情」
    /// 直接可定位，而不是只有一句「Object reference not set...」无从查起。
    /// </summary>
    private static string FailureText(string method, Exception ex) =>
        ex.StackTrace is { Length: > 0 } stack
            ? $"{method} failed: {ex.GetType().FullName}: {ex.Message}\n{stack}"
            : $"{method} failed: {ex.GetType().Name}: {ex.Message}";

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
        // JsonNode 只允许一个父节点：广播会把同一个 details 写给每个连接，直接挂引用
        // 会在第二个客户端上抛「The node already has a parent」并掀掉整个事件泵。
        if (details is not null) payload["details"] = details.DeepClone();
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


}
