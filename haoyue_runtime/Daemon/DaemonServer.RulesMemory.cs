using System.Text.Json.Nodes;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Daemon;

/// <summary>规则/记忆/钩子台账相关 RPC 处理（按域拆分的 partial）。</summary>
public sealed partial class DaemonServer
{
    private WorkspaceInfo FactsWorkspace(JsonObject request) => ResolveWorkspace(Params(request));

    /// <summary>记忆事实与钩子台账的读/写入口，按动作分派到 RulesMemoryApi / HookLedgerApi。</summary>
    private async Task Handle_facts_rpc(JsonObject request, long id, ConnectionContext context, string action)
    {
        var exclusive = action is not ("list" or "hooks_runs");
        await RunAdminAsync(context.Writer, context.WriterGate, id, exclusive,
            _ => Task.FromResult(action switch
            {
                "list" => RulesMemoryApi.ListFacts(FactsWorkspace(request), Params(request)),
                "add" => RulesMemoryApi.AddFact(FactsWorkspace(request), Params(request)),
                "update" => RulesMemoryApi.UpdateFact(FactsWorkspace(request), Params(request)),
                "delete" => RulesMemoryApi.DeleteFact(FactsWorkspace(request), Params(request)),
                "pin" => RulesMemoryApi.PinFact(FactsWorkspace(request), Params(request)),
                "restore" => RulesMemoryApi.RestoreFact(FactsWorkspace(request), Params(request)),
                "confirm" => RulesMemoryApi.ConfirmFact(FactsWorkspace(request), Params(request)),
                _ => HookLedgerApi.List(FactsWorkspace(request), Params(request)),
            }),
            context.ConnectionCt).ConfigureAwait(false);
    }
}
