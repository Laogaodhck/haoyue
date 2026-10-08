using System.Text;

namespace Haoyue.Benchmarks.LocalModelEval;

/// <summary>
/// 流畅性量化指标：中文按字符切片计算 n-gram 多样性（distinct-n），跨轮复读检测，
/// 以及基于设计检查点的上下文记忆命中率。全部为确定性计算，不依赖模型自评。
/// </summary>
public static class FluencyMetrics
{
    /// <summary>去掉空白与标点的归一化文本（保留中文、字母、数字）。</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (char.IsLetterOrDigit(ch) || ch >= 0x4E00 && ch <= 0x9FFF)
                sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>distinct-n：unique n-gram 数 / n-gram 总数，越接近 1 越无复读。</summary>
    public static double DistinctN(string text, int n)
    {
        var normalized = Normalize(text);
        if (normalized.Length < n) return 1.0;
        var grams = new HashSet<string>();
        var total = 0;
        for (var i = 0; i + n <= normalized.Length; i++)
        {
            grams.Add(normalized.Substring(i, n));
            total++;
        }
        return total == 0 ? 1.0 : (double)grams.Count / total;
    }

    /// <summary>
    /// 最长重复片段检测：同一段 ≥ run 长度的文本在回复中反复出现的次数。
    /// 用于识别"逐句复读循环"（回复陷入机械重复）。
    /// </summary>
    public static (int MaxRun, string Sample) LongestRepeatedRun(string text, int runLength = 12)
    {
        var normalized = Normalize(text);
        if (normalized.Length < runLength * 2) return (0, "");
        var seen = new Dictionary<string, int>();
        var maxRun = 0;
        var sample = "";
        for (var i = 0; i + runLength <= normalized.Length; i++)
        {
            var gram = normalized.Substring(i, runLength);
            var count = seen.TryGetValue(gram, out var c) ? c + 1 : 1;
            seen[gram] = count;
            if (count > maxRun)
            {
                maxRun = count;
                sample = gram;
            }
        }
        return (maxRun, sample);
    }

    /// <summary>
    /// 跨轮复读：相邻两轮回复之间重复出现的 ≥8 字符共享片段数量（同一模板句反复使用）。
    /// </summary>
    public static int CrossTurnEcho(string previous, string current, int runLength = 8)
    {
        var prevGrams = new HashSet<string>();
        var a = Normalize(previous);
        var b = Normalize(current);
        if (a.Length < runLength || b.Length < runLength) return 0;
        for (var i = 0; i + runLength <= a.Length; i++)
            prevGrams.Add(a.Substring(i, runLength));

        var echoes = new HashSet<string>();
        for (var i = 0; i + runLength <= b.Length; i++)
        {
            var gram = b.Substring(i, runLength);
            if (prevGrams.Contains(gram)) echoes.Add(gram);
        }
        return echoes.Count;
    }

    /// <summary>上下文记忆检查点：回答需包含的关键词组（任一命中即算通过）。</summary>
    public static bool HitCheckpoint(string answer, params string[] anyOf)
    {
        var normalized = Normalize(answer);
        return anyOf.Any(keyword => normalized.Contains(Normalize(keyword), StringComparison.Ordinal));
    }

    /// <summary>回复长度波动系数（标准差/均值），过低说明每轮输出机械等长。</summary>
    public static double LengthVariation(IReadOnlyList<int> lengths)
    {
        if (lengths.Count < 2) return 0;
        var mean = lengths.Average();
        if (mean == 0) return 0;
        var variance = lengths.Sum(l => (l - mean) * (l - mean)) / lengths.Count;
        return Math.Sqrt(variance) / mean;
    }
}
