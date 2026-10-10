using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace Haoyue.Runtime.Tools;

public enum ToolPolicyVerdict
{
    Allowed,
    Denied,
}

/// <summary>
/// Result of a tool-execution policy evaluation. Rule is the matched rule's
/// index/name for audit trails; Reason is the model-facing explanation.
/// </summary>
public sealed record ToolPolicyDecision(ToolPolicyVerdict Verdict, string? Rule, string Reason)
{
    public static ToolPolicyDecision Allow() =>
        new(ToolPolicyVerdict.Allowed, null, "");

    public static ToolPolicyDecision Deny(string rule, string reason) =>
        new(ToolPolicyVerdict.Denied, rule, reason);
}

/// <summary>
/// Mandatory runtime gate in front of every tool execution. Unlike the prompt-level
/// permission notice (system/permissions) this cannot be talked around by the model:
/// the Agent evaluates it after parsing arguments and before invoking the tool, in
/// every mode, for the main agent and sub-agents alike.
/// </summary>
public interface IToolExecutionPolicy
{
    ToolPolicyDecision Evaluate(ITool tool, JsonObject arguments, ToolPolicyConfig config);
}

/// <summary>
/// Rule-based tool execution policy. Evaluation order (first match wins):
/// 1. explicit allow rules (user overrides, e.g. to un-block an over-broad builtin guard),
/// 2. user deny rules,
/// 3. builtin bash destructive-command guard (when enabled),
/// 4. otherwise allowed.
/// Patterns are matched case-insensitively against the full serialized arguments JSON,
/// so a single rule can key on both tool name and argument content.
/// </summary>
public sealed partial class ToolExecutionPolicy : IToolExecutionPolicy
{
    private readonly ConcurrentDictionary<string, Regex?> _regexCache = new(StringComparer.Ordinal);

    public ToolPolicyDecision Evaluate(ITool tool, JsonObject arguments, ToolPolicyConfig config)
    {
        if (config is { Enabled: false })
            return ToolPolicyDecision.Allow();

        var json = arguments.ToJsonString();
        // JavaScriptEncoder escapes & < > ' + as \uXXXX; rules are matched against both
        // the raw serialization and a de-escaped variant so user patterns can use
        // those characters literally.
        var haystacks = new[] { json, DeEscapeJsonMarkup(json) };

        var allow = MatchIndex(config.Allow, tool, haystacks);
        if (allow >= 0)
            return ToolPolicyDecision.Allow();

        var deny = MatchIndex(config.Deny, tool, haystacks);
        if (deny >= 0)
        {
            var rule = config.Deny[deny];
            return ToolPolicyDecision.Deny(
                $"deny[{deny}]",
                rule.Reason is { Length: > 0 } ? rule.Reason : "命令或参数命中了用户配置的拒绝规则。");
        }

        if (config.BuiltinBashGuard && tool.Name.Equals("bash", StringComparison.OrdinalIgnoreCase))
        {
            var guard = EvaluateBuiltinBashGuard(haystacks);
            if (guard is not null)
                return ToolPolicyDecision.Deny("builtin-bash-guard", guard);
        }

        return ToolPolicyDecision.Allow();
    }

    private static string DeEscapeJsonMarkup(string text) => text
        .Replace("\\u0026", "&")
        .Replace("\\u003c", "<")
        .Replace("\\u003e", ">")
        .Replace("\\u0027", "'")
        .Replace("\\u002b", "+");

    private int MatchIndex(List<ToolPolicyRule> rules, ITool tool, string[] haystacks)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (string.IsNullOrWhiteSpace(rule.Pattern)) continue;
            if (!ToolMatches(rule.Tool, tool.Name)) continue;
            var regex = Compile(rule.Pattern);
            if (regex is null) continue;
            if (haystacks.Any(haystack => regex.IsMatch(haystack))) return i;
        }
        return -1;
    }

    private static bool ToolMatches(string? pattern, string toolName) =>
        string.IsNullOrWhiteSpace(pattern)
            || pattern == "*"
            || pattern.Equals(toolName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Invalid patterns are skipped (never match) instead of throwing mid-turn.</summary>
    private Regex? Compile(string pattern) =>
        _regexCache.GetOrAdd(pattern, static p =>
        {
            try
            {
                return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException)
            {
                return null;
            }
        });

    // ------------------------------------------------------------- builtin bash guard

    /// <summary>
    /// High-confidence destructive shell patterns blocked by default. The list is
    /// intentionally conservative: it targets only commands that are almost never
    /// legitimate agent work (drive-root deletion, disk wiping, fork bombs, curl|sh
    /// remote execution, shadow-copy deletion…). Everyday commands like
    /// "Remove-Item -Recurse -Force bin" stay unaffected; anything else belongs in
    /// user-defined deny rules.
    /// Returns the denial reason, or null when the command passes.
    /// </summary>
    private string? EvaluateBuiltinBashGuard(string[] haystacks)
    {
        foreach (var (pattern, reason) in BuiltinBashPatterns)
        {
            var regex = Compile(pattern);
            if (regex is not null && haystacks.Any(regex.IsMatch))
                return reason;
        }
        return null;
    }

    private static (string Pattern, string Reason)[] BuiltinBashPatterns { get; } =
    [
        // rm carrying both recursive and force flags aimed at filesystem roots, home or a
        // bare wildcard. Absolute-path "rm -rf /tmp/x" is denied too (the "/" prefix rule);
        // relative-path targets like "rm -rf build" pass. Un-block via an allow rule.
        (@"\brm\s+(?=[^;\n|]*-{1,2}[a-z]*r)(?=[^;\n|]*-{1,2}[a-z]*f)[^;\n|]*\s[""']?(?:/|~|\$HOME|\*)",
            "rm 递归强删目标为根目录/用户主目录/通配符，属高危操作。"),

        // Windows recursive deletes targeting a drive root or the Users tree root.
        // Patterns account for JSON escaping (backslashes doubled in the arguments JSON).
        (@"\b(?:rd|rmdir)\s+/s\s+/q\s+[""']?(?:[A-Za-z]:\\+(?:\s|""|$)|[A-Za-z]:\\+Users\\+[^\\\s""]*(?:\s|""|$)|~)",
            "rd /s /q 目标为盘符根目录或用户目录，属高危操作。"),
        (@"Remove-Item\s+[^;\n|]*-Recurse[^;\n|]*\s""?[A-Za-z]:\\+(?:\s|""|$)|Remove-Item\s+[^;\n|]*-Recurse[^;\n|]*\s(?:~|\$env:USERPROFILE)",
            "Remove-Item -Recurse 目标为盘符根目录或用户目录，属高危操作。"),
        (@"\bdel\s+[^;\n|]*/s\b[^;\n|]*/q\b[^;\n|]*\s""?[A-Za-z]:\\+(?:\s|""|$)",
            "del /s /q 目标为盘符根目录，属高危操作。"),

        // Disk/filesystem wiping and volume management. The colon alternative carries no
        // trailing \b — after "C:" the JSON serialization has a quote, which is a
        // non-word/non-word pair where \b never matches.
        (@"\b(?:format\s+[A-Za-z]:|mkfs(?:\.\w+)?\b|diskpart\b|cipher\s+/w)", "磁盘格式化/擦除类命令被默认禁止。"),
        (@"\bdd\s+[^;\n]*\bof=/dev/", "dd 直接写块设备被默认禁止。"),

        // System lifecycle.
        (@"\bshutdown\s+(?:-\w+|/\w+)", "关机/重启命令被默认禁止。"),

        // Remote code execution pipelines (curl|sh, irm|iex …).
        (@"\b(?:curl|wget|irm|iwr|Invoke-WebRequest|Invoke-RestMethod)\b[^;\n|]*\|\s*\(?\s*(?:ba|z|da|fi)?sh\b",
            "下载内容直接管道进 shell 执行（curl|sh）被默认禁止。"),
        (@"\b(?:irm|iwr|Invoke-WebRequest|Invoke-RestMethod|curl|wget)\b[^;\n|]*\|\s*\(?\s*(?:iex|Invoke-Expression)\b|\biex\s*\(\s*(?:iwr|irm|Invoke-WebRequest|Invoke-RestMethod|curl|wget)",
            "下载内容直接管道进 PowerShell 执行（irm|iex）被默认禁止。"),

        // Registry bulk deletion and ransomware-style shadow copy cleanup.
        (@"\breg\s+delete\s+HK", "注册表删除命令被默认禁止。"),
        (@"\bvssadmin\s+delete\s+shadows\b", "删除卷影副本（常见于勒索行为）被默认禁止。"),
        (@"\bbcdedit\b", "引导配置修改命令被默认禁止。"),

        // Classic fork bomb.
        (@":\(\)\s*\{\s*:\s*\|\s*:\s*&\s*\}\s*;\s*:", "fork 炸弹被默认禁止。"),
    ];
}

/// <summary>agent.toolPolicy 配置节：规则化的工具执行强制策略。</summary>
public sealed class ToolPolicyConfig
{
    /// <summary>总开关。关闭后跳过所有策略评估（含内置 bash 守卫），仅保留模式层约束。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>显式放行规则，优先于所有拒绝规则（用于解除内置守卫的误伤）。</summary>
    public List<ToolPolicyRule> Allow { get; set; } = [];

    /// <summary>用户拒绝规则，按序评估，首个命中即拒绝。</summary>
    public List<ToolPolicyRule> Deny { get; set; } = [];

    /// <summary>内置 bash 高危命令守卫开关。</summary>
    public bool BuiltinBashGuard { get; set; } = true;
}

/// <summary>单条策略规则：工具名（可通配）+ 参数正则 + 拒绝理由。</summary>
public sealed class ToolPolicyRule
{
    /// <summary>工具名，大小写不敏感；"*" 或空匹配任意工具。</summary>
    public string Tool { get; set; } = "*";

    /// <summary>对完整参数 JSON 的正则（IgnoreCase）。空/无效规则永不命中。</summary>
    public string Pattern { get; set; } = "";

    /// <summary>命中时返回给模型的拒绝理由；空则使用默认文案。</summary>
    public string? Reason { get; set; }
}
