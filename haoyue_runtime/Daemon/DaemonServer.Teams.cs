using System.Text.Json.Nodes;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Daemon;

/// <summary>智能体团体 RPC 处理：配置 CRUD、运行、取消与轨迹读取。</summary>
public sealed partial class DaemonServer
{
    private WorkspaceInfo TeamWorkspace(JsonObject request) => ResolveWorkspace(Params(request));

    /// <summary>团体请求入口：list/runs 只读，其余写操作串行化。</summary>
    private async Task Handle_team_rpc(JsonObject request, long id, ConnectionContext context, string action)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, action is not ("list" or "runs"),
            ct => action switch
            {
                "list" => Task.FromResult(TeamApi.List(TeamWorkspace(request))),
                "save" => Task.FromResult(TeamApi.Save(TeamWorkspace(request), Params(request))),
                "delete" => Task.FromResult(TeamApi.Delete(TeamWorkspace(request), Params(request))),
                "copy" => Task.FromResult(TeamApi.Copy(TeamWorkspace(request), Params(request))),
                "run" => Task.FromResult(TeamApi.Run(TeamWorkspace(request), Params(request), _runtime)),
                "cancel" => Task.FromResult(TeamApi.Cancel(Params(request)["runId"]?.GetValue<string>() ?? "")),
                _ => Task.FromResult(TeamApi.RunList(TeamWorkspace(request), Params(request))),
            },
            context.ConnectionCt).ConfigureAwait(false);
    }
}
