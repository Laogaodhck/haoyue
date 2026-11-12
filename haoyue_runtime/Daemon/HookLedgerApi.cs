using System.Text.Json.Nodes;

namespace Haoyue.Runtime.Daemon;

/// <summary>
/// 钩子执行台账的 JSON 读面（hooks.list 的 ledger 节点）。
/// 独立类落地，避免继续向 <see cref="DaemonAdminApi"/> 堆叠行数
/// （ADR-20261015-giant-class-budget.md 决策 4）。
/// </summary>
internal static class HookLedgerApi
{
    public static JsonArray Array(string root, int limit)
    {
        var array = new JsonArray();
        foreach (var record in HookLedger.Read(root, limit))
        {
            array.Add((JsonNode)new JsonObject
            {
                ["at"] = record.At,
                ["trigger"] = record.Trigger,
                ["hook"] = record.Hook,
                ["target"] = record.Target,
                ["ok"] = record.Ok,
                ["durationMs"] = record.DurationMs,
                ["error"] = record.Error,
                ["output"] = record.Output,
            });
        }
        return array;
    }
}
