using System.Text.Json;
using System.Text.Json.Nodes;

namespace Haoyue.Runtime.Providers;

/// <summary>
/// 本地模型的加载与推理设置（对应桌面端「模型加载配置」页面的全部分区）。
/// 语义约定：可空字段为 null 表示「自动 / 沿用提供商级配置」，非空为用户显式覆盖；
/// 布尔与数值字段在 <see cref="Normalize"/> 中统一钳制到 llama.cpp 接受的合法区间。
/// 分为三类：
///   1) 直接作用于加载参数（ModelParams）——上下文、GPU 卸载、线程、batch、mmap/mlock、
///      FlashAttention、KV 量化、统一 KV、KV 卸载、RoPE；
///   2) 直接作用于推理参数（InferenceParams）——采样参数、stop 串、思考开关；
///   3) 仅保存不生效的实验性记录字段——推测解码、上下文检查点、并发预测数、
///      llama.cpp 原始参数覆盖、聊天模板覆盖（字段持久化，等运行时能力补齐后启用）。
/// </summary>
public sealed class LocalModelSettings
{
    // ------------------------------------------------ 上下文与性能

    /// <summary>自动按硬件优化：开启时 GPU 卸载 / 线程 / 上下文回到「自动」（沿用提供商级配置或训练值），忽略显式覆盖。</summary>
    public bool AutoOptimize { get; set; } = true;

    /// <summary>上下文长度（token）。null = 自动（跟随模型训练值与注册的窗口值）。</summary>
    public int? ContextLength { get; set; }

    /// <summary>GPU 卸载层数。null = 自动（沿用 provider.GpuLayers）。</summary>
    public int? GpuOffload { get; set; }

    /// <summary>CPU 线程池大小。null = 自动（沿用 provider.Threads，再退到 llama.cpp 默认）。</summary>
    public int? Threads { get; set; }

    /// <summary>评估批大小（llama.cpp n_batch）。null = 2048。</summary>
    public int? EvaluationBatchSize { get; set; }

    /// <summary>物理批大小（llama.cpp ubatch）。null = 512。</summary>
    public int? PhysicalBatchSize { get; set; }

    /// <summary>最大并发预测数。本地推理在进程内严格串行，该值仅保存展示（实验性）。</summary>
    public int? MaxConcurrentPredictions { get; set; }

    /// <summary>FlashAttention。null = 沿用 provider.FlashAttention。</summary>
    public bool? FlashAttention { get; set; }

    // ------------------------------------------------ 生成

    /// <summary>采样温度。null = 0.6（内置默认，DeepSeek R1 系列官方建议值）。</summary>
    public double? Temperature { get; set; }

    /// <summary>是否用「最大输出」限制回复长度；关闭时输出预算跟随请求与模型注册值。</summary>
    public bool LimitResponseLength { get; set; }

    /// <summary>上下文溢出策略：truncateMiddle（KV 平移截断中段，当前唯一实现）。</summary>
    public string ContextOverflow { get; set; } = "truncateMiddle";

    /// <summary>停止串（映射为 llama.cpp 反提示）。</summary>
    public List<string>? StopStrings { get; set; }

    // ------------------------------------------------ 思考

    /// <summary>思考开关默认值。null = 跟随每次请求的 EnableThinking。</summary>
    public bool? EnableThinking { get; set; }

    /// <summary>思考 token 预算。当前由 agent 层（AgentConfig.ThinkingBudgetTokens）统一执行，该值保存并透传展示。</summary>
    public int? ReasoningBudget { get; set; }

    /// <summary>思考预算提示语，仅保存展示。</summary>
    public string? ReasoningBudgetMessage { get; set; }

    // ------------------------------------------------ 内存

    /// <summary>KV cache 是否随上下文卸载到显存；关闭时映射 llama.cpp no_kv_offload。</summary>
    public bool OffloadKvCacheToGpu { get; set; } = true;

    /// <summary>统一 KV cache（llama.cpp kv_unified）。null = 不显式设置（跟随后端默认）。</summary>
    public bool? UnifiedKvCache { get; set; }

    /// <summary>上下文检查点数量（实验性，仅保存）。</summary>
    public int? ContextCheckpoints { get; set; }

    /// <summary>K cache 量化类型：none | q8_0 | q4_0。null = 沿用 provider.KvCacheQuantization。</summary>
    public string? KCacheQuantType { get; set; }

    /// <summary>V cache 量化类型。null = 跟随 K cache（未单独设置时沿用 provider 配置）。</summary>
    public string? VCacheQuantType { get; set; }

    /// <summary>常驻内存（mlock 锁页）。关闭可降低常驻内存压力、支持更大模型；默认关闭。</summary>
    public bool KeepModelInMemory { get; set; }

    /// <summary>mmap 内存映射加载。null/true = 启用（降低加载峰值内存与启动耗时）；false = 常规读取。</summary>
    public bool? TryMmap { get; set; }

    // ------------------------------------------------ 推测解码

    /// <summary>推测解码：off 或草稿模型路径。当前进程内推理未实现草稿模型流水线，仅保存（实验性）。</summary>
    public string? SpeculativeDecoding { get; set; }

    // ------------------------------------------------ 高级

    public int? TopK { get; set; }
    public double? TopP { get; set; }
    public double? MinP { get; set; }
    public double? RepeatPenalty { get; set; }
    public double? PresencePenalty { get; set; }

    /// <summary>聊天模板覆盖（仅保存；当前使用 GGUF 内嵌模板 + 架构化兜底渲染）。</summary>
    public string? ChatTemplate { get; set; }

    public double? RopeFrequencyBase { get; set; }
    public double? RopeFrequencyScale { get; set; }

    /// <summary>采样种子。null = 随机种子。</summary>
    public long? Seed { get; set; }

    /// <summary>llama.cpp 参数覆盖开关（与 LlamaCppOverride 一起仅保存，等待透传能力落地）。</summary>
    public bool LlamaCppOverrideEnabled { get; set; }

    /// <summary>llama.cpp 原始参数覆盖串（仅保存）。</summary>
    public string? LlamaCppOverride { get; set; }

    /// <summary>保留默认值的深拷贝，供前端「恢复默认」。</summary>
    public LocalModelSettings Clone()
    {
        var copy = (LocalModelSettings)MemberwiseClone();
        copy.StopStrings = StopStrings is { Count: > 0 } ? [.. StopStrings] : null;
        return copy;
    }

    /// <summary>
    /// 钳制全部数值到 llama.cpp 接受的区间，并归一化枚举字符串。config.json 里手写的
    /// 非法值在这里被拉回合法域，而不是让 llama.cpp 在加载期报出难懂的错误。
    /// </summary>
    public void Normalize()
    {
        ContextLength = ClampPositive(ContextLength, 512, 4_194_304);
        GpuOffload = ClampNonNegative(GpuOffload, 4096);
        Threads = ClampPositive(Threads, 1, 1024);
        EvaluationBatchSize = ClampPositive(EvaluationBatchSize, 16, 8192);
        PhysicalBatchSize = ClampPositive(PhysicalBatchSize, 16, Math.Max(16, EvaluationBatchSize ?? 512));
        MaxConcurrentPredictions = ClampPositive(MaxConcurrentPredictions, 1, 64);
        ContextCheckpoints = ClampPositive(ContextCheckpoints, 1, 4096);
        Temperature = ClampRange(Temperature, 0, 2);
        ReasoningBudget = ClampPositive(ReasoningBudget, 256, 1_000_000);
        TopK = ClampPositive(TopK, 1, 512);
        TopP = ClampRange(TopP, 0.01, 1);
        MinP = ClampRange(MinP, 0, 1);
        RepeatPenalty = ClampRange(RepeatPenalty, 0.5, 2);
        PresencePenalty = ClampRange(PresencePenalty, -2, 2);
        RopeFrequencyBase = ClampRange(RopeFrequencyBase, 1, 10_000_000);
        RopeFrequencyScale = ClampRange(RopeFrequencyScale, 0.1, 64);
        if (StopStrings is { Count: > 0 })
            StopStrings = StopStrings.Where(s => !string.IsNullOrEmpty(s)).Take(32).ToList();
        KCacheQuantType = NormalizeKvQuant(KCacheQuantType);
        VCacheQuantType = NormalizeKvQuant(VCacheQuantType);
        if (!string.Equals(ContextOverflow, "truncateMiddle", StringComparison.Ordinal))
            ContextOverflow = "truncateMiddle";
    }

    internal static string NormalizeKvQuant(string? value) => value switch
    {
        "q8_0" => "q8_0",
        "q4_0" => "q4_0",
        _ => "none",
    };

    private static int? ClampPositive(int? value, int min, int max) =>
        value is { } v ? Math.Clamp(v, min, max) : null;

    private static int? ClampNonNegative(int? value, int max) =>
        value is { } v ? Math.Clamp(v, 0, max) : null;

    private static double? ClampRange(double? value, double min, double max) =>
        value is { } v && double.IsFinite(v) ? Math.Clamp(v, min, max) : null;

    // ------------------------------------------------ JSON 序列化（daemon 传输用）

    private static readonly string[] FieldNames =
    [
        // 上下文与性能
        "autoOptimize", "contextLength", "gpuOffload", "threads", "evaluationBatchSize",
        "physicalBatchSize", "maxConcurrentPredictions", "flashAttention",
        // 生成
        "temperature", "limitResponseLength", "contextOverflow", "stopStrings",
        // 思考
        "enableThinking", "reasoningBudget", "reasoningBudgetMessage",
        // 内存
        "offloadKvCacheToGpu", "unifiedKvCache", "contextCheckpoints",
        "kCacheQuantType", "vCacheQuantType", "keepModelInMemory", "tryMmap",
        // 推测解码
        "speculativeDecoding",
        // 高级
        "topK", "topP", "minP", "repeatPenalty", "presencePenalty", "chatTemplate",
        "ropeFrequencyBase", "ropeFrequencyScale", "seed",
        "llamaCppOverrideEnabled", "llamaCppOverride",
    ];

    private static T? Get<T>(JsonObject node, string name) where T : struct
    {
        if (node[name] is not JsonValue value) return null;
        if (value.TryGetValue<T>(out var result)) return result;
        if (typeof(T) == typeof(bool)) return null;

        // JSON 数字可能以较窄的 .NET 类型落盘（int32 值读 long、int 读 double 时直接
        // 匹配失败）。JsonElement 路径自带转换；装箱基元路径走 IConvertible。任何
        // 溢出 / 格式问题都按「未设置」处理而不是抛错。
        double? number = null;
        if (value.TryGetValue<JsonElement>(out var element))
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.Number)
                number = element.GetDouble();
        }
        else if (value.TryGetValue<object>(out var boxed) && boxed is IConvertible convertible && boxed is not bool)
        {
            try { number = convertible.ToDouble(System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is FormatException or InvalidCastException) { return null; }
        }
        if (number is not { } value2) return null;
        try
        {
            return (T)Convert.ChangeType(value2, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is OverflowException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>从 daemon 参数中的 load 对象解析；未知字段忽略，解析后统一 Normalize。永不抛出格式异常。</summary>
    public static LocalModelSettings FromJson(JsonObject node)
    {
        var settings = new LocalModelSettings
        {
            AutoOptimize = Get<bool>(node, "autoOptimize") ?? true,
            ContextLength = Get<int>(node, "contextLength"),
            GpuOffload = Get<int>(node, "gpuOffload"),
            Threads = Get<int>(node, "threads"),
            EvaluationBatchSize = Get<int>(node, "evaluationBatchSize"),
            PhysicalBatchSize = Get<int>(node, "physicalBatchSize"),
            MaxConcurrentPredictions = Get<int>(node, "maxConcurrentPredictions"),
            FlashAttention = Get<bool>(node, "flashAttention"),
            Temperature = Get<double>(node, "temperature"),
            LimitResponseLength = Get<bool>(node, "limitResponseLength") ?? false,
            ContextOverflow = node["contextOverflow"]?.GetValue<string>() ?? "truncateMiddle",
            EnableThinking = Get<bool>(node, "enableThinking"),
            ReasoningBudget = Get<int>(node, "reasoningBudget"),
            ReasoningBudgetMessage = node["reasoningBudgetMessage"]?.GetValue<string>(),
            OffloadKvCacheToGpu = Get<bool>(node, "offloadKvCacheToGpu") ?? true,
            UnifiedKvCache = Get<bool>(node, "unifiedKvCache"),
            ContextCheckpoints = Get<int>(node, "contextCheckpoints"),
            KCacheQuantType = node["kCacheQuantType"]?.GetValue<string>(),
            VCacheQuantType = node["vCacheQuantType"]?.GetValue<string>(),
            KeepModelInMemory = Get<bool>(node, "keepModelInMemory") ?? false,
            TryMmap = Get<bool>(node, "tryMmap"),
            SpeculativeDecoding = node["speculativeDecoding"]?.GetValue<string>(),
            TopK = Get<int>(node, "topK"),
            TopP = Get<double>(node, "topP"),
            MinP = Get<double>(node, "minP"),
            RepeatPenalty = Get<double>(node, "repeatPenalty"),
            PresencePenalty = Get<double>(node, "presencePenalty"),
            ChatTemplate = node["chatTemplate"]?.GetValue<string>(),
            RopeFrequencyBase = Get<double>(node, "ropeFrequencyBase"),
            RopeFrequencyScale = Get<double>(node, "ropeFrequencyScale"),
            Seed = Get<long>(node, "seed"),
            LlamaCppOverrideEnabled = Get<bool>(node, "llamaCppOverrideEnabled") ?? false,
            LlamaCppOverride = node["llamaCppOverride"]?.GetValue<string>(),
        };
        if (node["stopStrings"] is JsonArray stopStrings)
            settings.StopStrings = stopStrings
                .Select(item => item?.GetValue<string>())
                .Where(item => !string.IsNullOrEmpty(item))
                .Select(item => item!)
                .ToList();
        settings.Normalize();
        return settings;
    }

    /// <summary>序列化为 daemon 响应对象（省略 null 字段，前端按「自动」渲染）。</summary>
    public JsonObject ToJson()
    {
        var node = new JsonObject();
        void Set(string name, JsonNode? value)
        {
            // null = 「自动」，直接省略字段；前端把缺省字段渲染为 AUTO。
            if (value is not null) node[name] = value;
        }
        Set("autoOptimize", JsonValue.Create(AutoOptimize));
        Set("contextLength", ContextLength is { } ctx ? JsonValue.Create(ctx) : null);
        Set("gpuOffload", GpuOffload is { } gpu ? JsonValue.Create(gpu) : null);
        Set("threads", Threads is { } threads ? JsonValue.Create(threads) : null);
        Set("evaluationBatchSize", EvaluationBatchSize is { } nBatch ? JsonValue.Create(nBatch) : null);
        Set("physicalBatchSize", PhysicalBatchSize is { } uBatch ? JsonValue.Create(uBatch) : null);
        Set("maxConcurrentPredictions", MaxConcurrentPredictions is { } mcp ? JsonValue.Create(mcp) : null);
        Set("flashAttention", FlashAttention is { } fa ? JsonValue.Create(fa) : null);
        Set("temperature", Temperature is { } temp ? JsonValue.Create(temp) : null);
        Set("limitResponseLength", JsonValue.Create(LimitResponseLength));
        Set("contextOverflow", JsonValue.Create(ContextOverflow));
        if (StopStrings is { Count: > 0 } stops)
        {
            var array = new JsonArray();
            foreach (var stop in stops) array.Add(JsonValue.Create(stop));
            node["stopStrings"] = array;
        }
        Set("enableThinking", EnableThinking is { } thinking ? JsonValue.Create(thinking) : null);
        Set("reasoningBudget", ReasoningBudget is { } budget ? JsonValue.Create(budget) : null);
        Set("reasoningBudgetMessage", ReasoningBudgetMessage is { Length: > 0 } message ? JsonValue.Create(message) : null);
        Set("offloadKvCacheToGpu", JsonValue.Create(OffloadKvCacheToGpu));
        Set("unifiedKvCache", UnifiedKvCache is { } unified ? JsonValue.Create(unified) : null);
        Set("contextCheckpoints", ContextCheckpoints is { } checkpoints ? JsonValue.Create(checkpoints) : null);
        Set("kCacheQuantType", KCacheQuantType is { Length: > 0 } k ? JsonValue.Create(k) : null);
        Set("vCacheQuantType", VCacheQuantType is { Length: > 0 } v ? JsonValue.Create(v) : null);
        Set("keepModelInMemory", JsonValue.Create(KeepModelInMemory));
        Set("tryMmap", TryMmap is { } mmap ? JsonValue.Create(mmap) : null);
        Set("speculativeDecoding", SpeculativeDecoding is { Length: > 0 } sd ? JsonValue.Create(sd) : null);
        Set("topK", TopK is { } topK ? JsonValue.Create(topK) : null);
        Set("topP", TopP is { } topP ? JsonValue.Create(topP) : null);
        Set("minP", MinP is { } minP ? JsonValue.Create(minP) : null);
        Set("repeatPenalty", RepeatPenalty is { } rp ? JsonValue.Create(rp) : null);
        Set("presencePenalty", PresencePenalty is { } pp ? JsonValue.Create(pp) : null);
        Set("chatTemplate", ChatTemplate is { Length: > 0 } ct ? JsonValue.Create(ct) : null);
        Set("ropeFrequencyBase", RopeFrequencyBase is { } ropeBase ? JsonValue.Create(ropeBase) : null);
        Set("ropeFrequencyScale", RopeFrequencyScale is { } ropeScale ? JsonValue.Create(ropeScale) : null);
        Set("seed", Seed is { } seed ? JsonValue.Create(seed) : null);
        Set("llamaCppOverrideEnabled", JsonValue.Create(LlamaCppOverrideEnabled));
        Set("llamaCppOverride", LlamaCppOverride is { Length: > 0 } lco ? JsonValue.Create(lco) : null);
        return node;
    }

    /// <summary>
    /// 加载签名摘要：改动任一直接影响加载行为的字段都会改变签名，使权重缓存自动重载；
    /// 仅保存不生效的字段（推测解码、模板覆盖等）不参与签名，避免无意义的重载。
    /// </summary>
    public string Signature() => string.Join('|',
        AutoOptimize ? "auto" : "manual",
        ContextLength?.ToString() ?? "ctx-auto",
        GpuOffload?.ToString() ?? "gpu-auto",
        Threads?.ToString() ?? "thr-auto",
        EvaluationBatchSize?.ToString() ?? "2048",
        PhysicalBatchSize?.ToString() ?? "512",
        FlashAttention?.ToString() ?? "fa-auto",
        OffloadKvCacheToGpu ? "kvoff" : "nokvoff",
        UnifiedKvCache?.ToString() ?? "kvu-auto",
        KCacheQuantType ?? "kq-auto",
        VCacheQuantType ?? "vq-auto",
        KeepModelInMemory ? "mlock" : "nomlock",
        TryMmap is { } mmap ? $"mmap={mmap}" : "mmap-auto",
        RopeFrequencyBase?.ToString() ?? "rb-auto",
        RopeFrequencyScale?.ToString() ?? "rs-auto");

    /// <summary>推理签名摘要：改动采样 / stop / 思考默认值只影响新请求，不触发权重重载。</summary>
    public string InferenceSignature() => string.Join('|',
        Temperature?.ToString() ?? "t-auto",
        LimitResponseLength ? "limit" : "nolimit",
        TopK?.ToString() ?? "40",
        TopP?.ToString() ?? "0.95",
        MinP?.ToString() ?? "0.1",
        RepeatPenalty?.ToString() ?? "1.1",
        PresencePenalty?.ToString() ?? "0",
        Seed?.ToString() ?? "seed-auto",
        EnableThinking?.ToString() ?? "think-req",
        StopStrings is { Count: > 0 } ? string.Join(",", StopStrings) : "nostops");
}
