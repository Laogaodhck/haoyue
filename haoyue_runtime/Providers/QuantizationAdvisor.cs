namespace Haoyue.Runtime.Providers;

/// <summary>
/// 2.6 权重量化选型引导（探测 + 提示，零运行时风险）：对 Q8/FP16 级 GGUF 给出
/// 「换 Q4_K_M 的预期收益」提示。纯提示组件——推理路径零改动，不阻断注册。
/// </summary>
public static class QuantizationAdvisor
{
    /// <summary>建议的性价比目标档（社区共识的 K 量化甜点位）。</summary>
    public const string TargetLabel = "Q4_K_M";

    /// <summary>目标档的有效位宽（来自 llama.cpp 量化表的经验值）。</summary>
    public const double TargetBitsPerWeight = 4.85;

    /// <summary>有效位宽达到该值才提示：Q8_0 及以上属于「有明显更优选择」。</summary>
    public const double InefficientThreshold = 8.0;

    /// <summary>
    /// 量化标签 → 有效位宽（bits per weight，经验值）。文件名无法识别时为 null，
    /// 用体积/结构估算兜底；两边都拿不到就不提示（绝不瞎猜）。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, double> LabelBits = new Dictionary<string, double>
    {
        ["F32"] = 32, ["BF16"] = 16, ["F16"] = 16, ["FP16"] = 16,
        ["Q8_0"] = 8.5, ["Q6_K"] = 6.6,
        ["Q5_K_M"] = 5.7, ["Q5_K_S"] = 5.5, ["Q5_1"] = 5.6, ["Q5_0"] = 5.5,
        ["Q4_K_M"] = TargetBitsPerWeight, ["Q4_K_S"] = 4.6, ["Q4_1"] = 4.7, ["Q4_0"] = 4.55,
        ["IQ4_XS"] = 4.25, ["IQ4_NL"] = 4.5,
        ["Q3_K_L"] = 4.3, ["Q3_K_M"] = 3.9, ["Q3_K_S"] = 3.5,
        ["IQ3_M"] = 3.7, ["IQ3_S"] = 3.4, ["IQ3_XXS"] = 3.1,
        ["Q2_K"] = 3.35, ["IQ2_XS"] = 2.3, ["IQ2_XXS"] = 2.1,
    };

    /// <summary>识别出的量化信息。Advice 为空串表示无需提示。</summary>
    public sealed record Hint(string? Label, double? BitsPerWeight, bool Inefficient, string Advice)
    {
        public static readonly Hint Unknown = new(null, null, false, string.Empty);
        public bool HasAdvice => Advice.Length > 0;
    }

    /// <summary>
    /// 从文件名解析量化标签：GGUF 社区命名几乎总是带档位（model-Q4_K_M.gguf、
    /// model.q4km.gguf、model q8 0.gguf）。按 token 序列匹配，分隔符无关。
    /// </summary>
    public static string? ParseLabel(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).ToUpperInvariant();
        // 候选按 token 数降序：Q5_K_M 必须先于 Q5_K、Q4_K_M 先于 Q4 匹配。
        foreach (var (label, _) in LabelBits.OrderByDescending(pair => pair.Key.Length))
        {
            var tokens = label.Split('_');
            if (MatchesTokens(stem, tokens)) return label;
        }
        // 紧凑名兜底（q4km.gguf）：去分隔符后找连续串。只认 ≥4 字符的档位，
        // 避免 "q80" 之类数字巧合误判成 Q8_0。
        var compact = new string(stem.Where(character =>
            !char.IsWhiteSpace(character) && character is not '-' and not '_' and not '.').ToArray());
        foreach (var (label, _) in LabelBits.OrderByDescending(pair => pair.Key.Length))
        {
            var compactLabel = label.Replace("_", string.Empty);
            if (compactLabel.Length >= 4 && compact.Contains(compactLabel, StringComparison.Ordinal)) return label;
        }
        return null;
    }

    /// <summary>token 序列匹配：分隔符（- _ . 空格）切分后找连续子序列。</summary>
    private static bool MatchesTokens(string upperStem, string[] tokens)
    {
        var parts = upperStem.Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries);
        for (var start = 0; start + tokens.Length <= parts.Length; start++)
        {
            var matched = true;
            for (var offset = 0; offset < tokens.Length; offset++)
            {
                if (!parts[start + offset].Equals(tokens[offset], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }
            if (matched) return true;
        }
        return false;
    }

    /// <summary>
    /// 2.6 主入口：标签优先（确定性最高），退而用 header 维度 + 文件体积估算
    /// 有效位宽（params ≈ 12·e²·blocks 的 transformer 近似，不含 embedding，
    /// 位宽略偏高——提示文案里如实标注「估算」）。
    /// </summary>
    public static Hint Describe(string fileName, long sizeBytes, GgufProbe.Header? header = null)
    {
        if (sizeBytes <= 0) return Hint.Unknown;
        var label = ParseLabel(fileName);
        double? bits = label is not null ? LabelBits[label] : EstimateBitsPerWeight(sizeBytes, header);
        if (bits is not >= InefficientThreshold) return new Hint(label, bits, false, string.Empty);

        var bpw = bits.Value;
        var speedup = bpw / TargetBitsPerWeight;
        var sizeDrop = 1 - TargetBitsPerWeight / bpw;
        var source = label is null ? "按体积与结构估算" : $"当前量化 {label}";
        return new Hint(label, bpw, Inefficient: true,
            $"{source}（≈{bpw:0.#} 位/权重）：换 {TargetLabel} 后体积约 -{sizeDrop * 100:0}%，decode 预期提速约 {speedup:0.#}×。");
    }

    /// <summary>header 维度估算有效位宽；维度不全返回 null（不提示）。</summary>
    private static double? EstimateBitsPerWeight(long sizeBytes, GgufProbe.Header? header)
    {
        if (header?.BlockCount is not > 0 || header.EmbeddingLength is not > 0) return null;
        var parameters = 12d * header.EmbeddingLength.Value * header.EmbeddingLength.Value * header.BlockCount.Value;
        return parameters <= 0 ? null : sizeBytes * 8d / parameters;
    }
}
