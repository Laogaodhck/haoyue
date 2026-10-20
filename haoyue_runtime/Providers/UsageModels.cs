namespace Haoyue.Runtime.Providers;

/// <summary>One LLM invocation, appended to ~/.haoyue/usage.jsonl.</summary>
public sealed class UsageEntry
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public long InputTokens { get; set; }
    public long TotalInputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long CacheCreationInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal Cost { get; set; }
    public double ElapsedMs { get; set; }
    public bool Success { get; set; }
    // ── 本地推理性能遥测（P1，2026-10-20 接通）：仅本地 provider 的真实流式测量有值，
    // 历史行与云端调用缺省 null（不参与均值）。TTFT 为用户感知首字延迟（含加载等待），
    // LoadWaitMs 是其中权重装载/排队的那段；prefill 为净口径（TTFT-LoadWait）。
    public double? TtftMs { get; set; }
    public double? LoadWaitMs { get; set; }
    public double? PrefillTokensPerSecond { get; set; }
    public double? DecodeTokensPerSecond { get; set; }
    public double? CacheHitRate { get; set; }
    public bool? PrefixReuse { get; set; }
}

/// <summary>Aggregated statistics for a provider/model pair.</summary>
public sealed class UsageAggregate
{
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public long Calls { get; set; }
    public long Failures { get; set; }
    public long InputTokens { get; set; }
    public long TotalInputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long CacheCreationInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal Cost { get; set; }
    public double AvgLatencyMs { get; set; }

    // 推理性能均值：只对有遥测的行取平均；全无遥测时保持 null（前端据此隐藏该列）。
    public double? AvgTtftMs { get; set; }
    public double? AvgLoadWaitMs { get; set; }
    public double? AvgPrefillTokensPerSecond { get; set; }
    public double? AvgDecodeTokensPerSecond { get; set; }
    public double? AvgCacheHitRate { get; set; }
    /// <summary>有遥测的行中走了 KV 前缀复用的比例（0..1）；无遥测行为 null。</summary>
    public double? PrefixReuseRate { get; set; }
    /// <summary>携带推理遥测的调用数（分母，供前端判断样本量）。</summary>
    public long TelemetryCalls { get; set; }

    public long TotalTokens => TotalInputTokens + OutputTokens;
    public double SuccessRate => Calls == 0 ? 1.0 : (double)(Calls - Failures) / Calls;
}
