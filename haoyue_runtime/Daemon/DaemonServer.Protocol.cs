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
    private async Task Handle_ping(JsonObject request, long id, ConnectionContext context)
    {
        await WriteAsync(context.Writer, context.WriterGate, id, "pong", "", context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_protocol_info(JsonObject request, long id, ConnectionContext context)
    {
        await WriteAsync(context.Writer, context.WriterGate, id, "result", DaemonProtocol.Info(), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_events_recent(JsonObject request, long id, ConnectionContext context)
    {
        // 断线重连的补播通道：客户端带上见过的最大 id，只取之后的行。
        var limit = Params(request)["limit"]?.GetValue<int>() ?? 100;
        var afterId = Params(request)["after"]?.GetValue<long>() ?? 0;
        var journal = new JsonArray();
        foreach (var persisted in _runtime.Database.RecentEvents(limit, afterId))
            journal.Add((JsonNode)EventJournal.Project(persisted));
        await WriteAsync(context.Writer, context.WriterGate, id, "result", journal.ToJsonString(), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_shutdown(JsonObject request, long id, ConnectionContext context)
    {
        foreach (var turn in context.ActiveTurns.Values) turn.Cancellation.Cancel();
        foreach (var turn in context.ActiveTurns.Values)
            if (turn.Task is not null) await ObserveAsync(turn.Task).ConfigureAwait(false);
        await WriteAsync(context.Writer, context.WriterGate, id, "bye", "", context.ConnectionCt).ConfigureAwait(false);
        _shutdown.Cancel();
    }

    private async Task Handle_contract_export(JsonObject request, long id, ConnectionContext context)
    {
        await WriteAsync(context.Writer, context.WriterGate, id, "result", DaemonContract.ExportJson().ToJsonString(), context.ConnectionCt).ConfigureAwait(false);
    }

}
