using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;

namespace Haoyue.Runtime.Daemon;

/// <summary>
/// 「编码」配置的 JSON 桥接：承担 advanced.get 的 coding 节序列化与
/// advanced.set 的逐键应用。独立于 <see cref="DaemonAdminApi"/>（该类受巨类行数预算约束）。
/// </summary>
internal static class CodingConfigJson
{
    /// <summary>把 CodingConfig 序列化为 advanced.get 返回的 coding JSON 对象。</summary>
    public static JsonObject ToJson(CodingConfig coding)
    {
        return new JsonObject
        {
            ["git"] = new JsonObject
            {
                ["branchPrefix"] = coding.Git.BranchPrefix,
                ["forcePushWithLease"] = coding.Git.ForcePushWithLease,
            },
            ["security"] = new JsonObject
            {
                ["progressiveScan"] = coding.Security.ProgressiveScan,
            },
        };
    }

    /// <summary>
    /// 「编码」配置写入：分支前缀按字符串应用（允许空串表示不添加前缀），
    /// 开关按「键 → bool」应用；未出现的键保持原值（与 advanced 其余字段同语义）。
    /// </summary>
    public static void Apply(JsonObject? patch, CodingConfig coding)
    {
        if (patch is null) return;

        if (patch["git"] is JsonObject gitPatch)
        {
            if (gitPatch["branchPrefix"] is JsonValue prefixValue
                && prefixValue.TryGetValue<string>(out var prefix))
            {
                coding.Git.BranchPrefix = prefix;
            }
            if (gitPatch["forcePushWithLease"] is JsonValue forceValue
                && forceValue.TryGetValue<bool>(out var force))
            {
                coding.Git.ForcePushWithLease = force;
            }
        }

        if (patch["security"] is JsonObject securityPatch
            && securityPatch["progressiveScan"] is JsonValue scanValue
            && scanValue.TryGetValue<bool>(out var scan))
        {
            coding.Security.ProgressiveScan = scan;
        }
    }
}
