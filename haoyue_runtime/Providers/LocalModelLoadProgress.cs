namespace Haoyue.Runtime.Providers;

/// <summary>
/// 本地模型加载阶段（顺序固定）。llama.cpp 的权重加载是单次原生调用，没有细粒度
/// 回调，因此进度按「阶段边界 + 历史 ETA 插值」诚实估计，绝不伪装字节级真实进度。
/// </summary>
public enum LocalLoadPhase
{
    /// <summary>已排队，等待加载门（前一个加载/卸载正在进行）。</summary>
    Queued = 0,
    /// <summary>读 GGUF header 与兼容性检查（版本门 / 架构提示）。</summary>
    Probing = 1,
    /// <summary>mmap 权重文件 + 后端分配（最耗时阶段）。</summary>
    Loading = 2,
    /// <summary>512 窗口小上下文真实 decode 自检。</summary>
    Verifying = 3,
    /// <summary>多模态投影器（mmproj）加载；纯文本模型跳过该阶段。</summary>
    Projector = 4,
    /// <summary>加载成功，权重可服务。</summary>
    Ready = 5,
    /// <summary>全部尝试失败（含降级重试）。</summary>
    Failed = 6,
}

/// <summary>
/// 一次本地模型加载的进度快照。通过 <see cref="IProgress{T}"/> 或 daemon 的
/// model.load.progress 广播送达桌面端加载条。
/// </summary>
public sealed record LocalLoadProgress
{
    public required string ModelPath { get; init; }

    public LocalLoadPhase Phase { get; init; }

    /// <summary>第几次尝试（1 = 首次；GPU 降级重试时递增）。</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>本次尝试是否已降级（GPU→CPU）。</summary>
    public bool Degraded { get; init; }

    public long ElapsedMs { get; init; }

    /// <summary>基于历史加载耗时的剩余时间估计（毫秒）；无历史时为 null。</summary>
    public long? EstimatedRemainingMs { get; init; }

    public string? Message { get; init; }

    /// <summary>
    /// 加载完成度（0..1）。由生产者按阶段边界 + ETA 插值计算（见
    /// <see cref="InterpolatedPercent"/>）；无 ETA 时保持阶段边界不动。
    /// </summary>
    public double Percent { get; init; }

    /// <summary>阶段边界百分比（0..1）。阶段内的插值见 <see cref="InterpolatedPercent"/>。</summary>
    public double PercentHint => Phase switch
    {
        LocalLoadPhase.Queued => 0.0,
        LocalLoadPhase.Probing => 0.05,
        LocalLoadPhase.Loading => 0.55,
        LocalLoadPhase.Verifying => 0.75,
        LocalLoadPhase.Projector => 0.90,
        LocalLoadPhase.Ready => 1.0,
        LocalLoadPhase.Failed => 1.0,
        _ => 0.0,
    };

    /// <summary>
    /// 阶段内按「已耗时 / (已耗时 + 剩余估计)」在阶段边界之间插值。无剩余估计时
    /// 返回阶段下界（宁可不动也不跳动）；剩余估计耗尽（&lt;=0，即已超时）时钉在
    /// 阶段上界——加载比历史慢时不谎报完成，只让本阶段看起来即将结束，
    /// 下一阶段事件会重新锚定。终态阶段（Ready/Failed）恒为 1。
    /// </summary>
    public static double InterpolatedPercent(LocalLoadPhase phase, long elapsedMs, long? remainingMs)
    {
        var (from, to) = Bounds(phase);
        if (from >= to) return from;
        if (remainingMs is not { } remaining) return from;
        var fraction = Math.Clamp((double)elapsedMs / (elapsedMs + Math.Max(0, remaining)), 0, 1);
        return from + (to - from) * fraction;
    }

    private static (double From, double To) Bounds(LocalLoadPhase phase) => phase switch
    {
        LocalLoadPhase.Queued => (0.0, 0.05),
        LocalLoadPhase.Probing => (0.05, 0.55),
        LocalLoadPhase.Loading => (0.55, 0.75),
        LocalLoadPhase.Verifying => (0.75, 0.90),
        LocalLoadPhase.Projector => (0.90, 1.0),
        _ => (1.0, 1.0),
    };
}
