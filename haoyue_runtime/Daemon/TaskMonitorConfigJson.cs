using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;

namespace Haoyue.Runtime.Daemon;

/// <summary>
/// 任务监控展示偏好的 JSON 桥接（纯 UI 配置，runtime 行为不读取）。
/// 独立于 <see cref="DaemonAdminApi"/>（该类已受巨类行数预算约束），
/// 承担 advanced.get 的序列化与 advanced.set 的逐键应用。
/// </summary>
internal static class TaskMonitorConfigJson
{
    /// <summary>把 TaskMonitorConfig 序列化为 advanced.get 返回的 JSON 对象。</summary>
    public static JsonObject ToJson(TaskMonitorConfig tm)
    {
        return new JsonObject
        {
            ["mode"] = tm.Mode,
            ["fixedDefaultShow"] = tm.FixedDefaultShow,
            ["progress"] = new JsonObject
            {
                ["review"] = tm.Progress.Review,
                ["goal"] = tm.Progress.Goal,
                ["plan"] = tm.Progress.Plan,
            },
            ["activity"] = new JsonObject
            {
                ["subAgents"] = tm.Activity.SubAgents,
                ["backgroundProcesses"] = tm.Activity.BackgroundProcesses,
                ["sideChat"] = tm.Activity.SideChat,
                ["skillMcp"] = tm.Activity.SkillMcp,
            },
            ["results"] = new JsonObject
            {
                ["outputs"] = tm.Results.Outputs,
                ["webVisits"] = tm.Results.WebVisits,
                ["sources"] = tm.Results.Sources,
                ["knowledge"] = tm.Results.Knowledge,
            },
            ["access"] = new JsonObject
            {
                ["memoryUpdate"] = tm.Access.MemoryUpdate,
                ["demoScreen"] = tm.Access.DemoScreen,
            },
        };
    }

    /// <summary>
    /// 任务监控偏好写入：模式枚举显式校验（写错就静默失效的字段必须报错），
    /// 组开关按「键 → bool」逐个应用，未出现的键保持原值（与 advanced 其余字段同语义）。
    /// </summary>
    public static void Apply(JsonObject? patch, TaskMonitorConfig tm)
    {
        if (patch is null) return;

        if (patch["mode"] is JsonValue modeValue)
        {
            if (!modeValue.TryGetValue<string>(out var mode) || mode is not ("fixed" or "floating"))
                throw new DaemonRequestException("params.taskMonitor.mode must be fixed or floating");
            tm.Mode = mode;
        }

        if (patch["fixedDefaultShow"] is JsonValue showValue)
        {
            if (!showValue.TryGetValue<string>(out var show) || show is not ("show" or "hidden"))
                throw new DaemonRequestException("params.taskMonitor.fixedDefaultShow must be show or hidden");
            tm.FixedDefaultShow = show;
        }

        ApplyBoolGroup(patch["progress"] as JsonObject, (key, value) =>
        {
            if (key == "review") tm.Progress.Review = value;
            else if (key == "goal") tm.Progress.Goal = value;
            else if (key == "plan") tm.Progress.Plan = value;
        });
        ApplyBoolGroup(patch["activity"] as JsonObject, (key, value) =>
        {
            if (key == "subAgents") tm.Activity.SubAgents = value;
            else if (key == "backgroundProcesses") tm.Activity.BackgroundProcesses = value;
            else if (key == "sideChat") tm.Activity.SideChat = value;
            else if (key == "skillMcp") tm.Activity.SkillMcp = value;
        });
        ApplyBoolGroup(patch["results"] as JsonObject, (key, value) =>
        {
            if (key == "outputs") tm.Results.Outputs = value;
            else if (key == "webVisits") tm.Results.WebVisits = value;
            else if (key == "sources") tm.Results.Sources = value;
            else if (key == "knowledge") tm.Results.Knowledge = value;
        });
        ApplyBoolGroup(patch["access"] as JsonObject, (key, value) =>
        {
            if (key == "memoryUpdate") tm.Access.MemoryUpdate = value;
            else if (key == "demoScreen") tm.Access.DemoScreen = value;
        });
    }

    /// <summary>组开关补丁：仅应用「键名 → 布尔」条目，非布尔条目忽略（保持原值）。</summary>
    private static void ApplyBoolGroup(JsonObject? patch, Action<string, bool> apply)
    {
        if (patch is null) return;
        foreach (var (key, node) in patch)
        {
            if (node is JsonValue value && value.TryGetValue<bool>(out var flag)) apply(key, flag);
        }
    }
}
