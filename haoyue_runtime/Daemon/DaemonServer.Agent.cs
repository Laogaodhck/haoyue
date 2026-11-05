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

/// <summary>按域拆分的 RPC 处理方法（仅搬运自 DaemonServer.DispatchMethodAsync，逻辑未改动）。</summary>
public sealed partial class DaemonServer
{
    private async Task Handle_agent_alias_runTurn(JsonObject request, long id, ConnectionContext context)
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
return;
        }
        if (string.IsNullOrWhiteSpace(message) && images.Count == 0)
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error", "params.message or params.images is required", context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
return;
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
return;
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
return;
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
return;
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
        // Turn-acceptance ack: desktop clients arm a first-event timeout
        // on chat (any event cancels it). Isolated-runtime construction,
        // MCP connects and local model cold loads can each take longer
        // than that window, so confirm the turn was accepted right away,
        // before any of that slow startup work produces its first event.
        await WriteAsync(context.Writer, context.WriterGate, id, "status", "Preparing turn", context.ConnectionCt, turnSession.Header.Id).ConfigureAwait(false);
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
    }

    private async Task Handle_agent_steer(JsonObject request, long id, ConnectionContext context)
    {
        var parameters = Params(request);
        // M-B: abort=true 是显式 opt-in 的硬中止（区别于默认的"下一回合
        // 生效"引导）。先解析它——abort 不需要 message/images。
        var abort = parameters["abort"]?.GetValue<bool?>() ?? false;
        ActiveTurn? abortTarget;
        if (abort)
        {
            var abortRequestedId = parameters["requestId"]?.GetValue<long?>();
            var abortRequestedSessionId = parameters["sessionId"]?.GetValue<string>();
            abortTarget = abortRequestedId is { } abortSpecific
                ? context.ActiveTurns.GetValueOrDefault(abortSpecific)
                : context.ActiveTurns.Values
                    .Where(turn => string.Equals(turn.Session.Header.Id, abortRequestedSessionId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(turn => turn.Session.Header.UpdatedAt)
                    .FirstOrDefault();
            if (abortTarget is null)
            {
                await WriteAsync(context.Writer, context.WriterGate, id, "error", "No active turn found for this session", context.ConnectionCt, abortRequestedSessionId, code: DaemonErrorCode.NotFound).ConfigureAwait(false);
return;
            }
            var accepted = abortTarget.Steering.RequestAbort();
            if (accepted)
            {
                await WriteAsync(context.Writer, context.WriterGate, id, "result",
                    "abort requested", context.ConnectionCt, abortTarget.Session.Header.Id).ConfigureAwait(false);
            }
            else
            {
                await WriteAsync(context.Writer, context.WriterGate, id, "error",
                    "the active turn is already finishing", context.ConnectionCt,
                    abortTarget.Session.Header.Id, code: DaemonErrorCode.Conflict).ConfigureAwait(false);
            }
return;
        }

        var message = parameters["message"]?.GetValue<string>()
                      ?? parameters["prompt"]?.GetValue<string>()
                      ?? "";
        IReadOnlyList<ChatImageAttachment> images;
        try { images = ParseImages(parameters["images"]); }
        catch (DaemonRequestException ex)
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error", ex.Message, context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
return;
        }
        if (string.IsNullOrWhiteSpace(message) && images.Count == 0)
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error", "params.message or params.images is required", context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
return;
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
return;
        }

        if (!target.Steering.TryEnqueue(ChatMessage.User(message, images)))
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error", "The active turn is already finishing", context.ConnectionCt, target.Session.Header.Id, code: DaemonErrorCode.Conflict).ConfigureAwait(false);
return;
        }
        await WriteAsync(context.Writer, context.WriterGate, id, "result", "guidance queued", context.ConnectionCt, target.Session.Header.Id).ConfigureAwait(false);
    }

    private async Task Handle_agent_respondApproval(JsonObject request, long id, ConnectionContext context)
    {
        // 「本次允许」审批响应（P1-2.3）：桌面审批卡片的选择经此回传。
        // 仅响应内置高危守卫挂起的审批；未知/已超时的 callId 按拒绝收场。
        var parameters = Params(request);
        var callId = parameters["callId"]?.GetValue<string>();
        var approve = parameters["approve"]?.GetValue<bool?>() ?? false;
        if (string.IsNullOrWhiteSpace(callId))
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error", "params.callId is required", context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
return;
        }
        var matched = _approvals.Respond(callId, approve);
        if (!matched)
        {
            // 审批已超时被清理（或 callId 从未登记）：按拒绝语义告知客户端。
            await WriteAsync(context.Writer, context.WriterGate, id, "error",
                "No pending approval for this callId (expired or unknown)",
                context.ConnectionCt, code: DaemonErrorCode.NotFound).ConfigureAwait(false);
return;
        }
        await WriteAsync(context.Writer, context.WriterGate, id, "result",
            approve ? "approved" : "denied", context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_agent_interrupted(JsonObject request, long id, ConnectionContext context)
    {
        // Session ids whose turns were interrupted by the last daemon
        // crash, recovered at startup. Lets a reconnecting client clear
        // stale "running" spinners it kept from before the crash.
        List<string> snapshot;
        lock (_interruptedTurnsGate) snapshot = _interruptedTurns.ToList();
        await WriteAsync(context.Writer, context.WriterGate, id, "result",
            JsonSerializer.Serialize(snapshot, HaoyueJsonContext.Compact.ListString),
            context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_agent_undo(JsonObject request, long id, ConnectionContext context)
    {
        // User-confirmed revert of the latest completed turn's builtin
        // file-tool changes (LIFO). shell/MCP side effects were never
        // registered as compensable, so they are never silently "undone".
        var requestedSession = request["params"]?["sessionId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(requestedSession))
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error",
                "params.sessionId is required", context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
return;
        }
        if (_sessionTurns.ContainsKey(requestedSession))
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error",
                "A turn is active for this session; wait for it to finish before undoing.",
                context.ConnectionCt, code: DaemonErrorCode.Conflict).ConfigureAwait(false);
return;
        }
        var ledger = _undoRegistry.Take(requestedSession);
        if (ledger is null)
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "result",
                "no undoable changes", context.ConnectionCt).ConfigureAwait(false);
return;
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
    }

    private async Task Handle_agent_cancel(JsonObject request, long id, ConnectionContext context)
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
return;
        }
        foreach (var target in targets) target.Cancellation.Cancel();
        var detail = requestedId is { } one
            ? $"cancellation requested for {one}"
            : $"cancellation requested for {targets.Count} active turn(s)";
        await WriteAsync(context.Writer, context.WriterGate, id, "result", detail, context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_agent_mode_get(JsonObject request, long id, ConnectionContext context)
    {
        await WriteAsync(context.Writer, context.WriterGate, id, "result", CurrentMode(), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_agent_mode_switch(JsonObject request, long id, ConnectionContext context)
    {
        var rawMode = request["params"]?["mode"]?.GetValue<string>();
        if (!TryNormalizeMode(rawMode, out var mode))
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error", "params.mode must be one of: plan, readonly, edit, auto", context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
return;
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
    }

    private async Task Handle_agent_config_get(JsonObject request, long id, ConnectionContext context)
    {
        await WriteAsync(context.Writer, context.WriterGate, id, "result", _admin.GetAgentConfig(), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_agent_config_set(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.SetAgentConfig(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_agent_subagents_list(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.ListSubAgents(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_agent_subagents_save(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.SaveSubAgent(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_agent_subagents_delete(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.DeleteSubAgent(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

}
