using System.Globalization;

namespace Haoyue.Runtime.Configuration;

/// <summary>
/// Reply-language resolution. Allowed values are auto | zh | en; auto follows the
/// operating-system UI language (Chinese systems resolve to Simplified Chinese,
/// everything else to English). The resolved language is injected into the system
/// prompt as a short contract: the model detects the source language of each user
/// message first, then replies in the configured target language.
/// </summary>
public static class OutputLanguage
{
    public const string Auto = "auto";
    public const string Chinese = "zh";
    public const string English = "en";

    /// <summary>Normalizes user-supplied values; empty or unknown input falls back to auto.</summary>
    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Chinese => Chinese,
        English => English,
        _ => Auto,
    };

    /// <summary>
    /// Resolves the effective reply language. The workspace override wins over the
    /// global agent setting; auto follows the OS UI culture (zh → Chinese, else English).
    /// The culture parameter is injectable so tests stay independent of the host machine.
    /// </summary>
    public static string Resolve(WorkspaceConfig? workspace, AgentConfig agent, CultureInfo? culture = null)
    {
        var raw = Normalize(workspace?.Language ?? agent.Language);
        if (raw != Auto) return raw;
        var ui = (culture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName;
        return string.Equals(ui, "zh", StringComparison.OrdinalIgnoreCase) ? Chinese : English;
    }

    /// <summary>
    /// Builds the system-prompt language contract for the resolved language. The model
    /// must detect the source language of each message and the configured target language,
    /// replying in the target language by default; an explicit per-message request wins.
    /// </summary>
    public static string BuildInstruction(string resolved) => resolved == English
        ? """
          # Reply language
          - For every user message, first detect its language (source) and the configured reply language (target: English).
          - Reply in the target language by default — plans, explanations, follow-up questions and summaries included.
          - Keep code, identifiers, file paths and log excerpts verbatim; write code comments in the target language.
          - If the user explicitly asks for another language in the message, the user's request wins.
          """
        : """
          # 回复语言
          - 对每条用户消息，先识别其源语言与设定的回复语言（目标语言：简体中文）。
          - 默认始终用简体中文回复，包括计划、解释、追问与总结。
          - 代码、标识符、文件路径与日志摘录保持原文；代码注释使用目标语言。
          - 用户消息中明确要求其它语言时，以用户要求为准。
          """;
}
