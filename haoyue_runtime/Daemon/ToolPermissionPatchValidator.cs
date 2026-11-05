using System.Text.Json.Nodes;
using Haoyue.Runtime.Tools;

namespace Haoyue.Runtime.Daemon;

/// <summary>
/// 工具白名单的写侧校验：只在补丁真的改到 <c>allowedTools</c> 时检查合并结果，
/// 非法模式（空白、非法字符、空斜杠段、<c>**</c>、超长）直接拒绝落盘。
/// 显式传 null 视为「清空白名单 = 不限制」，不属于非法值。
///
/// 独立文件而非挂在 <c>DaemonAdminApi</c> partial 内：那个类有行数硬预算
/// （ADR-20261015-giant-class-budget.md），且本校验不依赖任何 daemon 状态。
/// </summary>
internal static class ToolPermissionPatchValidator
{
    public static void Validate(JsonObject merged, JsonObject patch)
    {
        if (!patch.ContainsKey("allowedTools")) return;
        var node = merged["allowedTools"];
        if (node is null) return;
        if (node is not JsonArray array)
            throw new DaemonRequestException("allowedTools 必须是字符串数组", DaemonErrorCode.InvalidParams);
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonValue value || !value.TryGetValue<string>(out var pattern))
                throw new DaemonRequestException(
                    $"allowedTools 第 {index + 1} 项必须是字符串", DaemonErrorCode.InvalidParams);
            if (!ToolPermissionPolicy.IsValidPattern(pattern, out var error))
                throw new DaemonRequestException(
                    $"allowedTools 第 {index + 1} 项「{pattern}」{error}", DaemonErrorCode.InvalidParams);
        }
    }
}
