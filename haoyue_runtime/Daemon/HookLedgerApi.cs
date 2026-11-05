using System.Text.Json.Nodes;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Daemon;

/// <summary>
/// 钩子执行台账的 JSON 读面（hooks.list 的内嵌台账与 hooks.runs）。
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

    /// <summary>hooks.runs：读取某个工作区的钩子执行台账（最近 N 条，失败可排查）。</summary>
    public static string List(WorkspaceInfo workspace, JsonObject parameters)
    {
        var limit = Math.Clamp(parameters["limit"]?.GetValue<int?>() ?? 50, 1, HookLedger.MaxMaxRecords);
        return new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["records"] = Array(workspace.Root, limit),
        }.ToJsonString();
    }
}
