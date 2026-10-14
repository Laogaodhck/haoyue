namespace Haoyue.Runtime.Data;

/// <summary>
/// MEMORY.md 的体积门槛。记忆文件是追加写且会每回合整段注入，无上限时它只会一路
/// 涨到被静默截断——文件看着完整、模型只读到缺半截。这里给写入侧一个明确的额度，
/// 超限即拒绝并提示先合并旧条目，让「记忆膨胀」变成可见的维护动作而非隐形的质量退化。
/// </summary>
public static class MemoryBudget
{
    /// <summary>MEMORY.md 字符上限（约等于注入预算的量级，超过就该整理而不是继续追加）。</summary>
    public const int MaxChars = 60_000;

    /// <summary>MEMORY.md 行数上限：条目化记忆的朴素可读上限。</summary>
    public const int MaxLines = 300;

    /// <summary>到达该比例即开始提示整理，留出一次性写完的空间。</summary>
    public const double WarnRatio = 0.8;

    /// <summary>追加后的文本是否越界；越界时返回中文说明，未越界返回 null。</summary>
    public static string? Overflow(string content)
    {
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        if (content.Length > MaxChars)
            return $"MEMORY.md 已有 {content.Length} 字符，超过 {MaxChars} 字符上限。请先合并重复条目或删除过时内容，再追加新记忆。";
        if (lines > MaxLines)
            return $"MEMORY.md 已有 {lines} 行，超过 {MaxLines} 行上限。请先合并重复条目或删除过时内容，再追加新记忆。";
        return null;
    }

    /// <summary>接近上限时的整理提醒；未到提醒线返回 null。</summary>
    public static string? ApproachingWarning(string content)
    {
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        var charRatio = content.Length / (double)MaxChars;
        var lineRatio = lines / (double)MaxLines;
        if (Math.Max(charRatio, lineRatio) < WarnRatio) return null;
        return $"提醒：MEMORY.md 已用到 {content.Length}/{MaxChars} 字符、{lines}/{MaxLines} 行，"
            + "接近上限。请择期合并同类条目，否则超限时新记忆将无法写入且旧内容会开始被注入截断。";
    }
}
