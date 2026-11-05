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
    private async Task Handle_session_list(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.ListSessions(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_session_search(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.SearchSessions(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_session_get(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.GetSession(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_session_image(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.GetSessionImage(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_session_truncate(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.TruncateSession(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_session_fork(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.ForkSession(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_session_update(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.UpdateSession(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_session_archive(JsonObject request, long id, ConnectionContext context)
    {
        var sessionId = request["params"]?["id"]?.GetValue<string>();
        if (context.LegacySession?.Header.Id == sessionId)
        {
            context.LegacySession = null;
        }
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.ArchiveSession(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_session_delete(JsonObject request, long id, ConnectionContext context)
    {
        var sessionId = request["params"]?["id"]?.GetValue<string>();
        if (context.LegacySession?.Header.Id == sessionId)
        {
            context.LegacySession = null;
        }
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.DeleteSession(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_session_resume(JsonObject request, long id, ConnectionContext context)
    {
        var sessionId = request["params"]?["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(sessionId))
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error", "params.id is required", context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
return;
        }
        WorkspaceInfo workspace;
        try { workspace = ResolveWorkspace(Params(request)); }
        catch (DaemonRequestException ex)
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error", ex.Message, context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
return;
        }
        var loaded = _runtime.Sessions.Load(workspace, sessionId);
        if (loaded is null)
            await WriteAsync(context.Writer, context.WriterGate, id, "error", $"Session {sessionId} not found", context.ConnectionCt, code: DaemonErrorCode.NotFound).ConfigureAwait(false);
        else
        {
            context.LegacySession = loaded;
            await WriteAsync(context.Writer, context.WriterGate, id, "result", $"resumed {loaded.Header.Id}", context.ConnectionCt).ConfigureAwait(false);
        }
    }

    private async Task Handle_session_new(JsonObject request, long id, ConnectionContext context)
    {
        WorkspaceInfo workspace;
        try { workspace = ResolveWorkspace(Params(request)); }
        catch (DaemonRequestException ex)
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error", ex.Message, context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
return;
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
return;
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
return;
        }
        context.LegacySession = _runtime.Sessions.Create(workspace, reasoningLevel, networkEnabled, expertId);
        await WriteAsync(context.Writer, context.WriterGate, id, "result", context.LegacySession.Header.Id, context.ConnectionCt).ConfigureAwait(false);
    }

}
