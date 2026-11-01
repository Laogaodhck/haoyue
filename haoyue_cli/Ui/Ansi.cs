using System.Text.RegularExpressions;

namespace Haoyue.Cli.Ui;

/// <summary>Minimal ANSI styling helpers for the live renderer (Spectre is used only for static command output).</summary>
public static partial class Ansi
{
    public const string Reset = "\x1b[0m";
    public const string Bold = "\x1b[1m";
    public const string Dim = "\x1b[2m";
    public const string Italic = "\x1b[3m";

    public const string Red = "\x1b[31m";
    public const string Green = "\x1b[32m";
    public const string Yellow = "\x1b[33m";
    public const string Blue = "\x1b[34m";
    public const string Magenta = "\x1b[35m";
    public const string Cyan = "\x1b[36m";
    public const string Gray = "\x1b[90m";

    public static string Style(this string text, string style) => style + text + Reset;

    // 只认 SGR（…m）会漏掉光标控制、清行、OSC 标题等序列：模型的原始输出里混进
    // \x1b[2K 这类字节时，Vim 式手动跳转（见 TruncateVisible）会连同后续文本一起算进
    // 可见宽度，折行与截断因此错位。这里覆盖 CSI、OSC、以及两字节 ESC 序列。
    [GeneratedRegex(@"\x1b(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1b]*(?:\x07|\x1b\\)|[()#][0-9A-Za-z]|[@-Z\\-_])")]
    private static partial Regex EscapeSequences();

    public static string StripStyles(string text) =>
        text.Contains('\x1b') ? EscapeSequences().Replace(text, "") : text;

    public static int VisibleLength(string text) => StripStyles(text).Length;

    /// <summary>24-bit foreground color.</summary>
    public static string Rgb(int r, int g, int b) => $"\x1b[38;2;{r};{g};{b}m";

    /// <summary>Truncates to a visible width, preserving escape sequences and closing styles.</summary>
    public static string TruncateVisible(string text, int maxWidth)
    {
        if (VisibleLength(text) <= maxWidth) return text;

        var visible = 0;
        var i = 0;
        while (i < text.Length && visible < maxWidth - 1)
        {
            if (text[i] == '\x1b')
            {
                var match = EscapeSequences().Match(text, i);
                // 未匹配的孤立 ESC 直接跳过，免得把后续字符当成序列的一部分吞掉。
                i = match.Success && match.Index == i ? i + match.Length : i + 1;
                continue;
            }
            visible++;
            i++;
        }
        return text[..i] + Reset + "…";
    }
}
