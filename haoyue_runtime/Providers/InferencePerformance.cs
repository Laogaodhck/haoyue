using System.Diagnostics;

namespace Haoyue.Runtime.Providers;

/// <summary>
/// 一次本地推理的性能观测（P1 遥测）。字段口径：
///   - TTFT 覆盖「请求起点 → 首个流式增量」，含模型加载/权重等待时间（这正是用户感知的首字延迟）；
///   - prefill tok/s = (prompt - cached) / TTFT，为近似值（TTFT 内含加载等待）；
///   - decode tok/s = generated / (总耗时 - TTFT)，纯解码段速率；
///   - 复用命中率 = cached / prompt，跨轮 KV 前缀复用有效性的直接度量。
/// 全部字段由 <see cref="InferenceMeter"/> 从真实流式事件测得，不做估算推断。
/// </summary>
public sealed record InferencePerformance
{
    public double TtftMs { get; init; }
    public double TotalMs { get; init; }
    public double DecodeMs { get; init; }
    public double DecodeTokensPerSecond { get; init; }
    public double PrefillTokensPerSecond { get; init; }
    public long PromptTokens { get; init; }
    public long GeneratedTokens { get; init; }
    public long CachedTokens { get; init; }
    public double CacheHitRate { get; init; }
    /// <summary>本轮是否走了 KV 前缀复用路径。</summary>
    public bool PrefixReuse { get; init; }
    /// <summary>本轮是否走了多模态（强制 CPU）路径。</summary>
    public bool Multimodal { get; init; }
    /// <summary>自适应调度提示（如慢模型思考预算建议），为空表示无。</summary>
    public string? Hint { get; init; }
}

/// <summary>
/// 包裹本地推理流并测量性能的计数器：入口打点、首个增量打 TTFT、完成事件携真实
/// token 计数时结算全部指标。设计为纯观测组件——只读事件、改写完成事件上的
/// <see cref="LlmCompletion.Performance"/>，绝不改变流的文本内容与异常语义。
/// </summary>
public sealed class InferenceMeter(bool prefixReuse, bool multimodal = false)
{
    private readonly long _start = Stopwatch.GetTimestamp();
    private double? _ttftMs;
    private long _deltas;

    /// <summary>结算出的性能数据（完成或测量结束后可用；未收到任何增量时为 null）。</summary>
    public InferencePerformance? Result { get; private set; }

    /// <summary>流式增量的个数（文本 + 思考），用于判定「首 token 已到」。</summary>
    public long DeltaCount => _deltas;

    /// <summary>消费一个流式增量；首个增量时刻即 TTFT。返回原事件（透传）。</summary>
    public LlmStreamEvent Observe(LlmStreamEvent evt)
    {
        if (_ttftMs is null && evt is LlmTextDelta or LlmThinkingDelta)
            _ttftMs = ElapsedMs();
        if (evt is LlmTextDelta or LlmThinkingDelta)
            _deltas++;
        return evt;
    }

    /// <summary>
    /// 结算：结合完成事件里的真实 token 计数算出全部指标。返回携带 Performance 的
    /// 完成事件（可直接下发）；事件缺 token 计数（计数为 0 且无增量）时只记录时间维度。
    /// </summary>
    public LlmCompleted Finalize(LlmCompleted completed)
    {
        var ttft = _ttftMs ?? ElapsedMs();
        var total = ElapsedMs();
        var decode = Math.Max(0, total - ttft);
        var usage = completed.Completion.Usage;
        var prompt = Math.Max(0, usage.TotalInputTokens);
        var cached = Math.Clamp(usage.CachedInputTokens, 0, prompt);
        var generated = Math.Max(_deltas, usage.OutputTokens);
        var prefill = prompt - cached;

        Result = new InferencePerformance
        {
            TtftMs = ttft,
            TotalMs = total,
            DecodeMs = decode,
            DecodeTokensPerSecond = decode > 0 ? generated * 1000.0 / decode : 0,
            PrefillTokensPerSecond = ttft > 0 && prefill > 0 ? prefill * 1000.0 / ttft : 0,
            PromptTokens = prompt,
            GeneratedTokens = generated,
            CachedTokens = cached,
            CacheHitRate = prompt > 0 ? (double)cached / prompt : 0,
            PrefixReuse = prefixReuse,
            Multimodal = multimodal,
            Hint = completed.Completion.Performance?.Hint,
        };
        return completed with
        {
            Completion = completed.Completion with { Performance = Result },
        };
    }

    private double ElapsedMs() =>
        (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency;
}

/// <summary>OOM / 显存不足类错误的特征匹配（供 P4 反馈调档识别紧急降档信号）。</summary>
public static class OomPattern
{
    private static readonly string[] Signals =
    [
        "out of memory",
        "out_of_memory",
        "not enough memory",
        "insufficient memory",
        "failed to allocate",
        "allocation failed",
        "cudaMalloc",
        "cuda_error",
        "CUDA error",
        "ggml_backend_alloc",
        "device memory",
        "显存不足",
    ];

    /// <summary>消息包含已知 OOM 特征时为 true。大小写不敏感；null 恒 false。</summary>
    public static bool Matches(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        foreach (var signal in Signals)
            if (message.Contains(signal, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
