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
    private async Task Handle_daemon_lan_get(JsonObject request, long id, ConnectionContext context)
    {
        await WriteAsync(context.Writer, context.WriterGate, id, "result",
            _lan.Status().ToJsonString(), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_daemon_lan_set(JsonObject request, long id, ConnectionContext context)
    {
        // 配置写与监听重启都在管理闸门内：并发 set 不能互相覆盖端口，
        // 也不该出现「旧配置上的 listener 还在接受连接」的窗口。
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_lan.Apply(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_workspace_init(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.InitializeWorkspace()), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_workspace_get(JsonObject request, long id, ConnectionContext context)
    {
        await WriteAsync(context.Writer, context.WriterGate, id, "result", WorkspaceJson(_runtime.Workspace), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_workspace_files(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.ListWorkspaceFiles(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_workspace_open(JsonObject request, long id, ConnectionContext context)
    {
        var path = request["params"]?["path"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(path))
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error", "params.path is required", context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
return;
        }

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error", $"Invalid workspace path: {ex.Message}", context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
return;
        }

        if (!Directory.Exists(fullPath))
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error", $"Workspace directory not found: {fullPath}", context.ConnectionCt, code: DaemonErrorCode.NotFound).ConfigureAwait(false);
return;
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
    }

    private async Task Handle_workspace_untrust(JsonObject request, long id, ConnectionContext context)
    {
        var rawPath = request["params"]?["path"]?.GetValue<string>();
        var trusted = request["method"]?.GetValue<string>() == "workspace.trust";
        string trustPath;
        try
        {
            trustPath = string.IsNullOrWhiteSpace(rawPath)
                ? _runtime.Workspace.Root
                : Path.GetFullPath(rawPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error", $"Invalid workspace path: {ex.Message}", context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
return;
        }

        if (!Directory.Exists(trustPath))
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error", $"Workspace directory not found: {trustPath}", context.ConnectionCt, code: DaemonErrorCode.NotFound).ConfigureAwait(false);
return;
        }

        await _adminGate.WaitAsync(context.ConnectionCt).ConfigureAwait(false);

        try
        {
            _runtime.ApplyWorkspaceTrust(trustPath, trusted);
            await WriteAsync(context.Writer, context.WriterGate, id, "result", WorkspaceJson(_runtime.Workspace), context.ConnectionCt).ConfigureAwait(false);
        }
        finally
        {
            _adminGate.Release();
        }

        // Trust changes which workspace MCP servers are active, so reload them
        // without blocking the admin gate or the connection.
        _ = Task.Run(async () =>
        {
            try
            {
                await _runtime.ConnectMcpAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch { }
        }, _shutdown.Token);
    }

    private async Task Handle_config_save(JsonObject request, long id, ConnectionContext context)
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
return;
        }
        await _adminGate.WaitAsync(context.ConnectionCt).ConfigureAwait(false);
        try
        {
            // params.workspace 存在时写的是 <ws>/.haoyue/config.json（项目级覆盖），
            // 否则写全局配置。两条分支同属「单写者」，都必须持 _adminGate。
            var scoped = request["params"]?["workspace"] is not null;
            var written = scoped
                ? _admin.SaveWorkspaceConfig(Params(request), fields)
                : SaveGlobalPatch(fields);
            await WriteAsync(context.Writer, context.WriterGate, id, "result",
                written, context.ConnectionCt).ConfigureAwait(false);
        }
        finally
        {
            _adminGate.Release();
        }
    }

    private async Task Handle_config_get(JsonObject request, long id, ConnectionContext context)
    {
        await WriteAsync(context.Writer, context.WriterGate, id, "result",
            _admin.GetConfig(Params(request)), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_config_status(JsonObject request, long id, ConnectionContext context)
    {
        await WriteAsync(context.Writer, context.WriterGate, id, "result", _admin.GetConfigStatus(), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_config_rebuild(JsonObject request, long id, ConnectionContext context)
    {
        await CancelAllTurnsAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.RebuildConfigAndDatabase()), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_advanced_get(JsonObject request, long id, ConnectionContext context)
    {
        await WriteAsync(context.Writer, context.WriterGate, id, "result", _admin.GetAdvancedConfig(), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_advanced_set(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            token => _admin.SetAdvancedConfigAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_web_allowedGet(JsonObject request, long id, ConnectionContext context)
    {
        await WriteAsync(context.Writer, context.WriterGate, id, "result", _admin.GetWebAccess(), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_web_allowedSet(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.SetWebAccess(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_hooks_list(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.ListHooks()), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_hooks_save(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.SaveHook(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_hooks_delete(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.DeleteHook(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_hooks_test(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            token => _admin.TestHookAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
    }

}
