namespace Haoyue.Runtime.Providers;

/// <summary>
/// 参与 CUDA 装载档位计算的模型静态形状（来自 GGUF header 与文件大小，P2/P3）。
/// KvDim 为每层 K（或 V）的宽度 = n_kv_heads × head_dim；估算不出时为 null——
/// 此时跳过「KV 驻显存」的档位（L1~L3），只做权重显存预算，宁可保守不猜。
/// </summary>
public sealed record ModelShape(
    long WeightBytes,
    long? BlockCount,
    long? KvDim)
{
    /// <summary>指定上下文长度下 f16 KV cache 的字节数（K+V 两个张量 × 每 token 每层 kvDim×2 字节）。</summary>
    public long? KvBytesFor(long contextSize) =>
        BlockCount is { } layers && KvDim is { } dim
            ? 2L * layers * dim * contextSize * 2
            : null;

    /// <summary>模型规模档位（解码速率目标与自适应策略的分档依据）：小 &lt; 4GB / 中 &lt; 10GB / 大其余。</summary>
    public string SizeClass => WeightBytes switch
    {
        < 4L * 1024 * 1024 * 1024 => "small",
        < 10L * 1024 * 1024 * 1024 => "medium",
        _ => "large",
    };

    /// <summary>该规模档位的解码速率目标（tok/s）。低于其 60% 视为「慢」，高于 2× 视为「闲」。</summary>
    public double TargetDecodeTokensPerSecond => SizeClass switch
    {
        "small" => 60,
        "medium" => 25,
        _ => 8,
    };
}

/// <summary>
/// 一档推理装载参数（P3 规则引擎的输出）。Level 1 最激进（权重+KV 全驻显存），
/// 5 最保守的部分卸载，0 为纯 CPU。Key 进入加载签名——档位变化即触发既有
/// 权重重载 + 512 自检 + 降级通道，自适应调档不引入新的加载路径。
/// </summary>
public sealed record InferenceTier
{
    /// <summary>全量卸载时传给 llama.cpp 的层数（后端自动钳制到模型实际层数）。</summary>
    public const int FullOffloadLayers = 999;

    public int Level { get; init; }
    public int GpuLayers { get; init; }
    public bool KvOffload { get; init; }
    /// <summary>none | q8_0 | q4_0。非 none 时要求 FlashAttention（llama.cpp 的硬性前提）。</summary>
    public string KvQuant { get; init; } = "none";
    public int UBatch { get; init; } = 512;
    public string Reason { get; init; } = "";

    /// <summary>进入加载签名 / 上下文签名的档位身份。</summary>
    public string Key => $"tier{Level}|gpu={GpuLayers}|kvo={(KvOffload ? 1 : 0)}|kvq={KvQuant}|ub={UBatch}";
}

/// <summary>
/// 自适应推理调优器（P3 装载档位 + P4 反馈调档）。仅在「自动」语义下生效：
/// AutoOptimize=true 且提供商级未显式配置 GpuLayers——用户手动配置的任何值都不被
/// 本组件覆盖。运行时严格串行（本地推理单门），实例方法不加锁。
///
/// 调档逻辑（对应优化方案 §3.3）：
///   - 首个请求前按显存预算一次性算出可达的最高档位并从该档启动（保守启动）；
///   - 观测进 10 深度滑动窗口；最近 3 个样本 decode 均低于目标 60% → 降一档；
///     窗口满且均值高于目标 200%、显存余量充足 → 升一档；
///   - OOM 立即降档（不受冷却期约束），其余调整后进入 20 轮冷却，冷却期只响应 OOM。
/// </summary>
public sealed class AdaptiveInferenceTuner
{
    /// <summary>档位梯子：索引即降级次序，0 为纯 CPU（终端档，不再回升直到换模型）。</summary>
    private static readonly int[] LadderLevels = [1, 2, 3, 4, 5, 0];

    private const int WindowSize = 10;
    private const int CooldownTurns = 20;
    private const double SlowFactor = 0.6;
    private const double IdleFactor = 2.0;
    /// <summary>装载预算的工作区余量（MB）：CUDA workspace、激活与临时缓冲。</summary>
    public const long WorkspaceMib = 512;
    /// <summary>部分卸载时 KV 留在系统内存仍需的预留（MB）。</summary>
    public const long KvCpuReserveMib = 256;

    private readonly Queue<double> _decodeWindow = new();
    private InferenceTier[]? _ladder;
    private int _index;
    private int _cooldown;
    private int _slowStreak;
    private ModelShape? _shape;
    private GpuInfo? _gpu;

    public string? ModelPath { get; private set; }
    public InferenceTier? Current => _ladder is { } ladder ? ladder[_index] : null;
    public string? LastAdjustment { get; private set; }
    public double LastCacheHitRate { get; private set; }

    /// <summary>
    /// 为模型初始化（或因换模型重置）档位梯子。返回当前档位；硬件不可探测或
    /// 形状信息缺失到无法决策时返回 null（保持提供商默认行为，绝不猜测）。
    /// </summary>
    public InferenceTier? EnsureTier(string modelPath, ModelShape shape, GpuInfo? gpu)
    {
        if (ModelPath == modelPath && _ladder is not null) return Current;
        ModelPath = modelPath;
        _shape = shape;
        _gpu = gpu;
        _decodeWindow.Clear();
        _cooldown = 0;
        LastAdjustment = null;

        var top = ComputeTier(shape, gpu);
        if (top is null)
        {
            _ladder = null;
            return null;
        }
        // 梯子 = 从可达最高档一路降到纯 CPU；从最高档启动（保守启动 = 该模型能吃到的最好配置）。
        var startIndex = Array.IndexOf(LadderLevels, top.Level);
        _ladder = LadderLevels
            .Select(level => BuildTier(level, top))
            .ToArray();
        _index = startIndex < 0 ? 0 : startIndex;
        return Current;
    }

    /// <summary>
    /// 按显存预算计算可达的最高档位（纯函数，测试直连）。gpu 为 null（无 NVIDIA GPU）
    /// 返回 null。预算 = 可用显存 × 0.9 − 工作区余量；KV 估算缺失时跳过 KV 驻显存档位。
    /// </summary>
    public static InferenceTier? ComputeTier(ModelShape shape, GpuInfo? gpu, long contextSize = 32_768)
    {
        if (gpu is null) return null;
        var budgetMib = (long)(gpu.FreeMib * 0.9) - WorkspaceMib;
        var weightMib = shape.WeightBytes / (1024 * 1024);
        if (budgetMib <= 0 || weightMib <= 0) return Tier(0, 0, false, "none", $"显存预算不足（free {gpu.FreeMib}MiB）");

        var kvMib = shape.KvBytesFor(Math.Max(512, contextSize)) / (1024 * 1024);
        if (kvMib is { } kv)
        {
            if (weightMib + kv <= budgetMib)
                return Tier(1, InferenceTier.FullOffloadLayers, true, "none",
                    $"权重 {weightMib}MiB + f16 KV {kv}MiB 均可驻显存");
            var kvQ8 = (long)(kv * 0.55);
            if (weightMib + kvQ8 <= budgetMib)
                return Tier(2, InferenceTier.FullOffloadLayers, true, "q8_0",
                    $"权重全驻显存，KV q8_0 后 {kvQ8}MiB 可容纳 32k 上下文");
            var kvQ4 = (long)(kv * 0.30);
            if (weightMib + kvQ4 <= budgetMib)
                return Tier(3, InferenceTier.FullOffloadLayers, true, "q4_0",
                    $"权重全驻显存，KV q4_0 后 {kvQ4}MiB 可容纳 32k 上下文");
        }
        if (weightMib <= budgetMib)
            return Tier(4, InferenceTier.FullOffloadLayers, false, "none",
                kvMib is null ? "KV 宽度未知，权重全驻显存、KV 留系统内存" : "KV 超预算，权重全驻显存、KV 留系统内存");

        // 部分卸载：层数按显存预算切分；可用层数过少时 PCIe 往返得不偿失，直接纯 CPU。
        if (shape.BlockCount is { } layers && layers > 0)
        {
            var perLayerMib = (double)weightMib / layers;
            var fit = (int)((budgetMib - KvCpuReserveMib) / perLayerMib);
            var gpuLayers = Math.Clamp(fit, 0, (int)layers);
            if (gpuLayers >= 8)
                return new InferenceTier
                {
                    Level = 5,
                    GpuLayers = gpuLayers,
                    KvOffload = false,
                    KvQuant = "none",
                    UBatch = 1024,
                    Reason = $"显存仅容纳 {gpuLayers}/{layers} 层，其余驻 CPU，ubatch 提至 1024",
                };
        }
        return Tier(0, 0, false, "none", "显存不足以有效卸载，保持纯 CPU");
    }

    private static InferenceTier Tier(int level, int gpuLayers, bool kvOffload, string kvQuant, string reason) =>
        new()
        {
            Level = level,
            GpuLayers = gpuLayers,
            KvOffload = kvOffload,
            KvQuant = kvQuant,
            UBatch = level == 5 ? 1024 : 512,
            Reason = reason,
        };

    /// <summary>按档位级别从可达最高档 <paramref name="top"/> 派生梯子成员（层复用 top 的部分卸载层数）。</summary>
    private static InferenceTier BuildTier(int level, InferenceTier top) => level switch
    {
        1 => Tier(1, InferenceTier.FullOffloadLayers, true, "none", "全量卸载 + f16 KV"),
        2 => Tier(2, InferenceTier.FullOffloadLayers, true, "q8_0", "全量卸载 + KV q8_0"),
        3 => Tier(3, InferenceTier.FullOffloadLayers, true, "q4_0", "全量卸载 + KV q4_0"),
        4 => Tier(4, InferenceTier.FullOffloadLayers, false, "none", "全量卸载 + KV 留系统内存"),
        5 => top.Level == 5 ? top : Tier(5, top.GpuLayers, false, "none", "部分卸载"),
        _ => Tier(0, 0, false, "none", "纯 CPU"),
    };

    /// <summary>消费一轮性能观测（P4）。返回值非空表示档位已调整，调用方应使其在下一请求生效。</summary>
    public InferenceTier? OnObservation(InferencePerformance performance)
    {
        LastCacheHitRate = performance.CacheHitRate;
        var shape = _shape;
        var ladder = _ladder;
        if (shape is null || ladder is null || performance.DecodeTokensPerSecond <= 0) return null;

        _decodeWindow.Enqueue(performance.DecodeTokensPerSecond);
        while (_decodeWindow.Count > WindowSize) _decodeWindow.Dequeue();

        // 慢速连续计数：连续 3 轮低于目标 60% 才降档，单轮抖动不触发。
        if (IsSlow(shape, performance.DecodeTokensPerSecond)) _slowStreak++;
        else _slowStreak = 0;

        if (_cooldown > 0)
        {
            _cooldown--;
            return null;
        }

        if (_slowStreak >= 3 && _index < ladder.Length - 1)
            return Adjust(ladder[_index + 1], "连续 3 轮慢速（decode < 目标 60%），降档释放显存");

        // 闲档升级：窗口满、均值显著超目标、显存余量充足才升，且不越过初始最高档。
        if (_decodeWindow.Count >= WindowSize)
        {
            var avg = _decodeWindow.Average();
            var headroom = _gpu is { } gpu ? (double)gpu.FreeMib / Math.Max(1, gpu.TotalMib) : 0;
            if (avg > shape.TargetDecodeTokensPerSecond * IdleFactor
                && headroom > 0.30 && _index > 0)
                return Adjust(ladder[_index - 1], $"decode 均值 {avg:F1} tok/s 远超目标且有显存余量，升档");
        }
        return null;
    }

    private static bool IsSlow(ModelShape shape, double decode) =>
        decode < shape.TargetDecodeTokensPerSecond * SlowFactor;

    /// <summary>
    /// OOM 紧急降档（P4）：不受冷却期约束，立即降到下一档并重置冷却。
    /// 返回值非空表示档位已调整。
    /// </summary>
    public InferenceTier? OnOom()
    {
        var ladder = _ladder;
        if (ladder is null || _index >= ladder.Length - 1) return null;
        var next = Array.FindIndex(ladder, tier => tier.Level == ladder[_index + 1].Level);
        if (next < 0 || next == _index) return null;
        _index = next;
        _decodeWindow.Clear();
        _slowStreak = 0;
        _cooldown = CooldownTurns;
        LastAdjustment = $"{DateTimeOffset.Now:HH:mm:ss} {ladder[_index].Level} 档：检测到显存不足（OOM），紧急降档";
        return Current;
    }

    private InferenceTier? Adjust(InferenceTier target, string reason)
    {
        var ladder = _ladder!;
        var next = Array.FindIndex(ladder, tier => tier.Level == target.Level);
        if (next < 0 || next == _index) return null;
        _index = next;
        _decodeWindow.Clear();
        _slowStreak = 0;
        _cooldown = CooldownTurns;
        LastAdjustment = $"{DateTimeOffset.Now:HH:mm:ss} {target.Level} 档：{reason}";
        return Current;
    }
}
