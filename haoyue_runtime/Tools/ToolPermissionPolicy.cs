using Haoyue.Runtime.Configuration;

namespace Haoyue.Runtime.Tools;

/// <summary>
/// 工具权限策略：纯函数、无状态、不持有任何租户/成员上下文。裁决点因此可以分散在
/// 委派组装（<c>AgentDelegator</c>）与执行闸门（<c>Agent.ExecuteToolAsync</c>）两处，
/// 而注册表本身只负责「这个视图能不能执行某个名字」。
///
/// 匹配规则：大小写不敏感，唯一通配符是 <c>*</c>；MCP 工具可写 <c>server/tool</c>
/// （自动映射到本地名 <c>mcp__{server}__{tool}</c>），也接受 <c>server/*</c>、
/// <c>*/tool</c> 与完整的 <c>mcp__server__*</c> 写法。
/// </summary>
public static class ToolPermissionPolicy
{
    /// <summary>工具模式的最大长度：防止有人把整段说明贴进白名单。</summary>
    public const int MaxPatternLength = 128;

    /// <summary>
    /// 校验一个工具模式是否合法（界面与服务端共用同一语义）。非法值必须拒绝保存，
    /// 不能静默忽略——否则用户以为已经收紧，实际仍是全量放行。
    /// </summary>
    public static bool IsValidPattern(string? pattern, out string? error)
    {
        var value = pattern ?? "";
        if (value.Length == 0)
        {
            error = "模式不能为空";
            return false;
        }
        if (value.Length > MaxPatternLength)
        {
            error = $"模式长度不能超过 {MaxPatternLength} 个字符";
            return false;
        }
        if (!value.Equals(value.Trim(), StringComparison.Ordinal))
        {
            error = "模式首尾不能包含空白";
            return false;
        }
        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or '*' or '/') continue;
            error = $"不允许字符 '{ch}'（只支持字母、数字、_ - . * /）";
            return false;
        }
        if (value.StartsWith('/') || value.EndsWith('/') || value.Contains("//", StringComparison.Ordinal))
        {
            error = "斜杠两侧不能为空";
            return false;
        }
        if (value.Contains("**", StringComparison.Ordinal))
        {
            error = "只支持单个 `*`，不允许 `**`";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>
    /// 判断 <paramref name="pattern"/> 是否命中 <paramref name="toolName"/>。
    /// 空模式不匹配任何工具；无 <c>*</c> 的模式按精确名字比较。
    /// </summary>
    public static bool Matches(string? pattern, string toolName)
    {
        var normalized = Normalize(pattern);
        if (normalized.Length == 0 || toolName.Length == 0) return false;
        if (normalized.IndexOf('*') < 0)
            return normalized.Equals(toolName, StringComparison.OrdinalIgnoreCase);
        return Glob(normalized, toolName);
    }

    /// <summary>白名单判定：null/空 = 不限制（保持既有配置语义）。</summary>
    public static bool IsAllowed(IReadOnlyList<string>? allow, string toolName) =>
        allow is not { Count: > 0 } || allow.Any(pattern => Matches(pattern, toolName));

    /// <summary>硬禁用判定：null/空 = 不禁用；命中即拒绝（deny 优先于 allow）。</summary>
    public static bool IsDenied(IReadOnlyList<string>? deny, string toolName) =>
        deny is { Count: > 0 } && deny.Any(pattern => Matches(pattern, toolName));

    /// <summary>
    /// 工作区级裁决：<c>disabledTools</c> 硬禁用优先于 <c>allowedTools</c> 白名单。
    /// 拒绝时 <paramref name="reason"/> 给出可写入台账的固定说明。
    /// </summary>
    public static bool TryResolveWorkspace(WorkspaceConfig? config, string toolName, out string? reason)
    {
        if (IsDenied(config?.DisabledTools, toolName))
        {
            reason = "被工作区配置禁用（disabledTools）";
            return false;
        }
        if (!IsAllowed(config?.AllowedTools, toolName))
        {
            reason = "不在工作区工具白名单（allowedTools）";
            return false;
        }
        reason = null;
        return true;
    }

    /// <summary>
    /// 把 <c>server/tool</c> 归一为本地 MCP 名字；其余模式原样保留（已 trim）。
    /// 只拆第一个 <c>/</c>，因此工具名里允许再出现斜杠。
    /// </summary>
    private static string Normalize(string? pattern)
    {
        var trimmed = pattern?.Trim() ?? "";
        if (trimmed.Length == 0) return "";
        if (trimmed.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase)) return trimmed;
        var slash = trimmed.IndexOf('/');
        return slash < 0
            ? trimmed
            : $"mcp__{trimmed[..slash]}__{trimmed[(slash + 1)..]}";
    }

    /// <summary>仅支持 <c>*</c> 的 glob：迭代 + 单星号回退，避免正则开销与注入面。</summary>
    private static bool Glob(string pattern, string text)
    {
        var p = 0;
        var t = 0;
        var star = -1;
        var mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '*' || SameChar(pattern[p], text[t])))
            {
                if (pattern[p] == '*')
                {
                    star = p++;
                    mark = t;
                }
                else
                {
                    p++;
                    t++;
                }
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static bool SameChar(char a, char b) =>
        char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
}
