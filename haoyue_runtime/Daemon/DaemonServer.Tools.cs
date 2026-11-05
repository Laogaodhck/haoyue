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
    private async Task Handle_mcp_list(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.ListMcpServers()), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_mcp_upsert(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            token => _admin.UpsertMcpServerAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_mcp_remove(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            token => _admin.RemoveMcpServerAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_mcp_reload(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _admin.ReloadMcpAsync, context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_connectors_list(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.ListConnectors()), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_connectors_install(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            token => _admin.InstallConnectorAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_connectors_uninstall(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            token => _admin.UninstallConnectorAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_skill_list(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.ListSkills()), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_skill_import(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.ImportSkill(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_skill_toggle(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.ToggleSkill(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_skill_official_list(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.ListOfficialSkills()), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_skill_official_install(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.InstallOfficialSkill(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

}
