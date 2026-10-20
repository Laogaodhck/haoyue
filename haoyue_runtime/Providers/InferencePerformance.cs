using System.Diagnostics;

namespace Haoyue.Runtime.Providers;

/// <summary>
/// 一次本地推理的性能观测（P1 遥测）。字段口径（2026-10-20 修正：加载等待从 TTFT 中分离）：
///   - TTFT 覆盖「请求起点 → 首个流式增量」，即用户感知的首字延迟，含权重加载/排队等待；
///   - LoadWaitMs = 请求起点 → 权重就绪（lease 获取成功）的等待，冷启动时就是那几秒；
///   - prefill tok/s = (prompt - cached) / (TTFT - LoadWaitMs)，净 prefill 口径——
///     冷启动的加载等待不再摊进 prefill 速率，调优与基准判断不被污染；
///   - decode tok/s = generated / (总耗时 - TTFT)，纯解码段速率；
///   - 复用命中率 = cached / prompt，跨轮 KV 前缀复用有效性的直接度量。
/// 全部字段由 <see cref="InferenceMeter"/> 从真实流式事件测得，不做估算推断。
/// </summary>
public sealed record InferencePerformance
{
    public double TtftMs { get; init; }
    /// <summary>权重加载/排队等待（请求起点 → lease 就绪）。稳态命中为 0，冷启动即用户干等的那几秒。</summary>
    public double LoadWaitMs { get; init; }
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
/// 包裹本地推理流并测量性能的计数器：入口打点、权重就绪打点、首个增量打 TTFT、
/// 完成事件携真实 token 计数时结算全部指标。设计为纯观测组件——只读事件、改写完成
/// 事件上的 <see cref="LlmCompletion.Performance"/>，绝不改变流的文本内容与异常语义。
/// 计时起点在 lease 获取之前（LocalLlmClient.StreamOnceAsync 入口）：TTFT 因此是
/// 用户感知的首字延迟，加载等待由 <see cref="MarkWeightsReady"/> 单独切出。
/// </summary>
public sealed class InferenceMeter(bool prefixReuse = false, bool multimodal = false)
{
    private readonly long _start = Stopwatch.GetTimestamp();
    private double? _ttftMs;
    private double? _loadWaitMs;
    private long _deltas;

    /// <summary>结算出的性能数据（完成或测量结束后可用；未收到任何增量时为 null）。</summary>
    public InferencePerformance? Result { get; private set; }

    /// <summary>流式增量的个数（文本 + 思考），用于判定「首 token 已到」。</summary>
    public long DeltaCount => _deltas;

    /// <summary>本轮是否走了 KV 前缀复用路径。</summary>
    public bool PrefixReuse { get; private set; } = prefixReuse;
    /// <summary>本轮是否走了多模态（强制 CPU）路径。</summary>
    public bool Multimodal { get; private set; } = multimodal;

    /// <summary>
    /// 路径调度确定后回填复用/多模态标记：构造时尚不知道走哪条路径（KV 复用受并发、
    /// 混合架构、长文档阈值影响），流式开始前回填即可。重复调用以最后一次为准。
    /// </summary>
    public void ConfigurePath(bool prefixReuse, bool multimodal)
    {
        PrefixReuse = prefixReuse;
        Multimodal = multimodal;
    }

    /// <summary>
    /// 权重就绪（lease 获取成功）时刻打点：起点至此的耗时即加载/排队等待。仅首次
    /// 生效（回退链重试时同一 meter 不复用）；未调用时按 0 计（稳态命中场景）。
    /// </summary>
    public void MarkWeightsReady() => _loadWaitMs ??= ElapsedMs();

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
        var loadWait = Math.Max(0, _loadWaitMs ?? 0);
        var ttft = _ttftMs ?? ElapsedMs();
        var total = ElapsedMs();
        var decode = Math.Max(0, total - ttft);
        // 净 prefill 时间 = TTFT - 加载等待：冷启动的权重装载不得污染 prefill 速率口径。
        var netPrefillMs = Math.Max(0, ttft - loadWait);
        var usage = completed.Completion.Usage;
        var prompt = Math.Max(0, usage.TotalInputTokens);
        var cached = Math.Clamp(usage.CachedInputTokens, 0, prompt);
        var generated = Math.Max(_deltas, usage.OutputTokens);
        var prefill = prompt - cached;

        Result = new InferencePerformance
        {
            TtftMs = ttft,
            LoadWaitMs = loadWait,
            TotalMs = total,
            DecodeMs = decode,
            DecodeTokensPerSecond = decode > 0 ? generated * 1000.0 / decode : 0,
            PrefillTokensPerSecond = netPrefillMs > 0 && prefill > 0 ? prefill * 1000.0 / netPrefillMs : 0,
            PromptTokens = prompt,
            GeneratedTokens = generated,
            CachedTokens = cached,
            CacheHitRate = prompt > 0 ? (double)cached / prompt : 0,
            PrefixReuse = PrefixReuse,
            Multimodal = Multimodal,
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
