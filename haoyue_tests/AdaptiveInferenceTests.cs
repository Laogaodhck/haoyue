using System.Text;
using Haoyue.Runtime.Providers;
using Xunit;
using Configuration = Haoyue.Runtime.Configuration;

namespace Haoyue.Tests;

/// <summary>
/// 自适应推理（P1~P5）的核心逻辑：GPU 探测解析、GGUF 张量结构读取、装载档位计算、
/// 反馈调档（滑动窗口 / 迟滞 / 冷却 / OOM）、遥测计量与 OOM 特征匹配。
/// 全部为纯逻辑测试，不加载真实模型。
/// </summary>
public sealed class AdaptiveInferenceTests
{
    // ------------------------------------------------ GpuProbe

    [Fact]
    public void GpuProbe_Parse_ValidOutput()
    {
        var gpu = GpuProbe.TryParse("NVIDIA GeForce RTX 3070 Ti, 8192, 6144\n");
        Assert.NotNull(gpu);
        Assert.Equal("NVIDIA GeForce RTX 3070 Ti", gpu.Name);
        Assert.Equal(8192, gpu.TotalMib);
        Assert.Equal(6144, gpu.FreeMib);
    }

    [Fact]
    public void GpuProbe_Parse_Garbage_ReturnsNull()
    {
        Assert.Null(GpuProbe.TryParse(""));
        Assert.Null(GpuProbe.TryParse("no csv here"));
        Assert.Null(GpuProbe.TryParse(", , \n"));
        Assert.Null(GpuProbe.TryParse("GPU, abc, def\n"));
        // free > total 是不可能状态：按无效处理，绝不把坏数据喂给装载决策。
        Assert.Null(GpuProbe.TryParse("GPU, 8192, 9216\n"));
    }

    // ------------------------------------------------ OomPattern

    [Theory]
    [InlineData("CUDA error: out of memory", true)]
    [InlineData("GGML_CUDA_ERROR: cudaMalloc failed: out of memory", true)]
    [InlineData("failed to allocate KV cache buffer", true)]
    [InlineData("llama_kv_cache: not enough memory for context", true)]
    [InlineData("decode returned -1", false)]
    [InlineData("", false)]
    public void OomPattern_MatchesKnownSignals(string message, bool expected)
    {
        Assert.Equal(expected, OomPattern.Matches(message));
        Assert.False(OomPattern.Matches(null));
    }

    // ------------------------------------------------ InferenceMeter

    [Fact]
    public void InferenceMeter_ComputesAllMetrics()
    {
        var meter = new InferenceMeter(prefixReuse: true);
        for (var i = 0; i < 5; i++)
        {
            var observed = meter.Observe(new LlmTextDelta("字"));
            Assert.IsType<LlmTextDelta>(observed); // 事件原样透传
        }
        var completed = meter.Finalize(new LlmCompleted(new LlmCompletion
        {
            Text = "测试输出",
            Usage = new TokenUsage(1000, 5) { CachedInputTokens = 750 },
        }));

        var perf = completed.Completion.Performance;
        Assert.NotNull(perf);
        Assert.True(perf.TtftMs > 0);
        Assert.True(perf.TotalMs >= perf.TtftMs);
        Assert.True(perf.DecodeTokensPerSecond > 0);
        Assert.Equal(1000, perf.PromptTokens);
        Assert.Equal(750, perf.CachedTokens);
        Assert.Equal(0.75, perf.CacheHitRate, precision: 3);
        Assert.True(perf.PrefixReuse);
        // 生成计数 = 实际增量数与 usage 输出数的较大者（流中断时以增量为准）。
        Assert.Equal(5, perf.GeneratedTokens);
    }

    [Fact]
    public void InferenceMeter_NoDelta_StillYieldsTiming()
    {
        var meter = new InferenceMeter(prefixReuse: false);
        var completed = meter.Finalize(new LlmCompleted(new LlmCompletion
        {
            Usage = new TokenUsage(0, 0),
        }));
        Assert.NotNull(completed.Completion.Performance);
        Assert.Equal(0, completed.Completion.Performance.DecodeTokensPerSecond);
    }

    // ------------------------------------------------ GgufProbe 张量结构字段

    [Fact]
    public void GgufProbe_ReadsTensorStructureFields()
    {
        var bytes = GgufFixture.Create(
            ("general.architecture", "qwen3"),
            ("qwen3.context_length", 131_072u),
            ("qwen3.block_count", 28u),
            ("qwen3.attention.head_count", 16u),
            ("qwen3.attention.head_count_kv", 8u),
            ("qwen3.attention.key_length", 128u),
            ("qwen3.attention.value_length", 128u),
            ("qwen3.embedding_length", 4096u),
            ("general.name", "shape model"));
        var path = WriteTemp("shape.gguf", bytes);

        var header = GgufProbe.Read(path);
        Assert.Equal(131_072, header.ContextLength);
        Assert.Equal(28, header.BlockCount);
        // head_count_kv 与 head_count 是后缀关系，读反了就是 GQA 估算的硬伤。
        Assert.Equal(16, header.HeadCount);
        Assert.Equal(8, header.HeadCountKv);
        Assert.Equal(128, header.KeyLength);
        Assert.Equal(128, header.ValueLength);
        Assert.Equal(4096, header.EmbeddingLength);
    }

    [Fact]
    public void GgufProbe_MinimalHeader_StillWorks()
    {
        var bytes = GgufFixture.Create(("general.architecture", "llama"));
        var header = GgufProbe.Read(WriteTemp("minimal.gguf", bytes));
        Assert.Equal("llama", header.Architecture);
        Assert.Null(header.BlockCount);
        Assert.Null(header.HeadCountKv);
    }

    // ------------------------------------------------ ModelShape / ComputeTier

    private static GpuInfo Gpu(long totalMib, long freeMib) => new("test-gpu", totalMib, freeMib);

    [Fact]
    public void ComputeTier_NoGpu_ReturnsNull()
    {
        var shape = new ModelShape(2L * 1024 * 1024 * 1024, 24, 1024);
        Assert.Null(AdaptiveInferenceTuner.ComputeTier(shape, null));
    }

    [Fact]
    public void ComputeTier_SmallModelFullyFits_Level1()
    {
        // 2GB 权重 + 24 层 × 1024 维 KV（32k ctx ≈ 3GB f16）≤ 8GB 预算 → 全量卸载 f16 KV。
        var shape = new ModelShape(2L * 1024 * 1024 * 1024, 24, 1024);
        var tier = AdaptiveInferenceTuner.ComputeTier(shape, Gpu(8192, 7600));
        Assert.NotNull(tier);
        Assert.Equal(1, tier.Level);
        Assert.Equal(InferenceTier.FullOffloadLayers, tier.GpuLayers);
        Assert.True(tier.KvOffload);
        Assert.Equal("none", tier.KvQuant);
    }

    [Fact]
    public void ComputeTier_MediumModel_NeedsKvQuant_Level2Or3()
    {
        // 5GB 权重 + 32k f16 KV（28 层 × 1024 维 ≈ 3.5GB）超 8GB 预算；
        // q8_0 后 ≈ 1.9GB，5+1.9 > 预算(6.8×0.9−0.5=6.3)，q4_0 后 ≈ 1.1GB 可容纳 → Level 3。
        var shape = new ModelShape(5L * 1024 * 1024 * 1024, 28, 1024);
        var tier = AdaptiveInferenceTuner.ComputeTier(shape, Gpu(8192, 7600));
        Assert.NotNull(tier);
        Assert.Equal(3, tier.Level);
        Assert.Equal("q4_0", tier.KvQuant);
        Assert.True(tier.KvOffload);
    }

    [Fact]
    public void ComputeTier_WeightsFitKvNot_Level4()
    {
        // KV 未知宽度（KvDim null）时跳过 KV 驻显存档位；权重放得下 → Level 4。
        var shape = new ModelShape(3L * 1024 * 1024 * 1024, 24, null);
        var tier = AdaptiveInferenceTuner.ComputeTier(shape, Gpu(8192, 7600));
        Assert.NotNull(tier);
        Assert.Equal(4, tier.Level);
        Assert.False(tier.KvOffload);
        Assert.Equal("none", tier.KvQuant);
    }

    [Fact]
    public void ComputeTier_LargeModel_PartialOffload_UsesPerLayerBudget()
    {
        // 12GB 权重、40 层、预算 ≈ 6.34GB → 约 25 层上 GPU（≥8），其余驻 CPU。
        var shape = new ModelShape(12L * 1024 * 1024 * 1024, 40, 1024);
        var tier = AdaptiveInferenceTuner.ComputeTier(shape, Gpu(8192, 7600));
        Assert.NotNull(tier);
        Assert.Equal(5, tier.Level);
        Assert.InRange(tier.GpuLayers, 8, 40);
        Assert.Equal(1024, tier.UBatch);
        Assert.False(tier.KvOffload);
    }

    [Fact]
    public void ComputeTier_VramTooSmallEvenForPartial_GoesCpu()
    {
        // 预算只够 3 层（< 8）：PCIe 往返得不偿失，直接纯 CPU。
        var shape = new ModelShape(12L * 1024 * 1024 * 1024, 40, 1024);
        var tier = AdaptiveInferenceTuner.ComputeTier(shape, Gpu(2048, 1024));
        Assert.NotNull(tier);
        Assert.Equal(0, tier.Level);
        Assert.Equal(0, tier.GpuLayers);
    }

    [Fact]
    public void ComputeTier_ZeroBudget_GoesCpu()
    {
        var shape = new ModelShape(1024L * 1024 * 1024, 16, 512);
        var tier = AdaptiveInferenceTuner.ComputeTier(shape, Gpu(512, 100));
        Assert.NotNull(tier);
        Assert.Equal(0, tier.Level);
    }

    // ------------------------------------------------ 反馈调档

    [Fact]
    public void Tuner_StartsAtHighestAffordableTier()
    {
        var tuner = new AdaptiveInferenceTuner();
        var shape = new ModelShape(2L * 1024 * 1024 * 1024, 24, 1024);
        var tier = tuner.EnsureTier("m.gguf", shape, Gpu(8192, 7600));
        Assert.NotNull(tier);
        Assert.Equal(1, tier.Level);
        Assert.Equal(tier, tuner.Current);
    }

    [Fact]
    public void Tuner_NoGpuOrShape_YieldsNull()
    {
        var tuner = new AdaptiveInferenceTuner();
        Assert.Null(tuner.EnsureTier("m.gguf", new ModelShape(1024, 24, 512), null));
        Assert.Null(tuner.Current);
    }

    [Fact]
    public void Tuner_ThreeConsecutiveSlowTurns_DowngradeThenCooldown()
    {
        var tuner = new AdaptiveInferenceTuner();
        var shape = new ModelShape(2L * 1024 * 1024 * 1024, 24, 1024);
        tuner.EnsureTier("m.gguf", shape, Gpu(8192, 7600));
        Assert.Equal(1, tuner.Current!.Level);

        var slow = Perf(decodeTps: 5); // 目标 60 tok/s，60% 以下为慢
        Assert.Null(tuner.OnObservation(slow));  // 第 1 轮：不降
        Assert.Null(tuner.OnObservation(slow));  // 第 2 轮：不降
        var adjusted = tuner.OnObservation(slow); // 第 3 轮：降档
        Assert.NotNull(adjusted);
        Assert.Equal(2, tuner.Current!.Level);
        Assert.NotNull(tuner.LastAdjustment);

        // 冷却期内慢速不再连续降档。
        Assert.Null(tuner.OnObservation(slow));
        Assert.Null(tuner.OnObservation(slow));
        Assert.Null(tuner.OnObservation(slow));
        Assert.Equal(2, tuner.Current.Level);
    }

    [Fact]
    public void Tuner_SlowStreakBrokenByHealthyTurn_DoesNotDowngrade()
    {
        var tuner = new AdaptiveInferenceTuner();
        var shape = new ModelShape(2L * 1024 * 1024 * 1024, 24, 1024);
        tuner.EnsureTier("m.gguf", shape, Gpu(8192, 7600));
        Assert.Null(tuner.OnObservation(Perf(5)));
        Assert.Null(tuner.OnObservation(Perf(5)));
        Assert.Null(tuner.OnObservation(Perf(80))); // 健康轮打断连续计数
        Assert.Null(tuner.OnObservation(Perf(5)));
        Assert.Equal(1, tuner.Current!.Level);
    }

    [Fact]
    public void Tuner_OomDowngradesImmediately_EvenDuringCooldown()
    {
        var tuner = new AdaptiveInferenceTuner();
        var shape = new ModelShape(2L * 1024 * 1024 * 1024, 24, 1024);
        tuner.EnsureTier("m.gguf", shape, Gpu(8192, 7600));
        var first = tuner.OnOom();
        Assert.NotNull(first);
        Assert.Equal(2, tuner.Current!.Level);
        // 冷却期内 OOM 仍然立即降档。
        Assert.Equal(3, tuner.OnOom()!.Level);
        Assert.Equal(3, tuner.Current.Level);
    }

    [Fact]
    public void Tuner_CpuTierIsTerminal()
    {
        var tuner = new AdaptiveInferenceTuner();
        // 显存极小 → 初始即 CPU 档。
        tuner.EnsureTier("m.gguf", new ModelShape(12L * 1024 * 1024 * 1024, 40, 1024), Gpu(512, 100));
        Assert.Equal(0, tuner.Current!.Level);
        Assert.Null(tuner.OnOom());
        Assert.Null(tuner.OnObservation(Perf(5)));
    }

    [Fact]
    public void Tuner_IdleWindow_UpgradesAfterCooldown()
    {
        var tuner = new AdaptiveInferenceTuner();
        // 5GB 权重 + 28 层 × 1024 维 KV：q4_0 后可容纳 32k 上下文 → 初始 Level 3。
        var shape = new ModelShape(5L * 1024 * 1024 * 1024, 28, 1024);
        tuner.EnsureTier("m.gguf", shape, Gpu(8192, 7600));
        Assert.Equal(3, tuner.Current!.Level);

        tuner.OnOom(); // → Level 4，进入冷却
        Assert.Equal(4, tuner.Current.Level);

        // 冷却期 20 轮：快速空闲观测不升档（计数只递减）。
        for (var i = 0; i < 20; i++)
            Assert.Null(tuner.OnObservation(Perf(120))); // 目标 25 tok/s，2 倍以上为闲

        // 冷却结束，窗口已满且均值远超目标、显存余量充足 → 升回 Level 3。
        var upgraded = tuner.OnObservation(Perf(120));
        Assert.NotNull(upgraded);
        Assert.Equal(3, tuner.Current!.Level);
    }

    [Fact]
    public void Tuner_SwitchingModel_ResetsState()
    {
        var tuner = new AdaptiveInferenceTuner();
        var small = new ModelShape(2L * 1024 * 1024 * 1024, 24, 1024);
        tuner.EnsureTier("a.gguf", small, Gpu(8192, 7600));
        tuner.OnOom();
        Assert.Equal(2, tuner.Current!.Level);

        var tier = tuner.EnsureTier("b.gguf", new ModelShape(1L * 1024 * 1024 * 1024, 16, 512), Gpu(8192, 7600));
        Assert.Equal(1, tier!.Level);
        Assert.Equal(1, tuner.Current.Level);
    }

    [Fact]
    public void Tuner_TracksCacheHitRate()
    {
        var tuner = new AdaptiveInferenceTuner();
        tuner.EnsureTier("m.gguf", new ModelShape(2L * 1024 * 1024 * 1024, 24, 1024), Gpu(8192, 7600));
        tuner.OnObservation(Perf(80, cacheHitRate: 0.42));
        Assert.Equal(0.42, tuner.LastCacheHitRate, precision: 3);
    }

    // ------------------------------------------------ 档位接入加载参数与签名

    [Fact]
    public void BuildLoadParams_TierOverridesGpuAndKvAndUbatch()
    {
        var provider = new Configuration.ProviderConfig { Id = "local", Kind = "local" };
        var tier = new InferenceTier
        {
            Level = 4,
            GpuLayers = InferenceTier.FullOffloadLayers,
            KvOffload = false,
            KvQuant = "none",
            UBatch = 1024,
        };
        var factory = LocalLlmClient.BuildLoadParams("m.gguf", provider, null, tier);
        var parameters = factory(false);
        Assert.Equal(InferenceTier.FullOffloadLayers, parameters.GpuLayerCount);
        Assert.True(parameters.NoKqvOffload);
        Assert.Equal(1024u, parameters.UBatchSize);
        // CPU 降级变体仍然只清 GPU 层数。
        Assert.Equal(0, factory(true).GpuLayerCount);
    }

    [Fact]
    public void BuildLoadParams_NoTier_KeepsProviderDefaults()
    {
        var provider = new Configuration.ProviderConfig { Id = "local", Kind = "local", GpuLayers = 12 };
        var parameters = LocalLlmClient.BuildLoadParams("m.gguf", provider, null)(false);
        Assert.Equal(12, parameters.GpuLayerCount);
        Assert.False(parameters.NoKqvOffload);
        Assert.Equal(512u, parameters.UBatchSize);
    }

    [Fact]
    public void LoadSignature_TierChangesSignature()
    {
        var provider = new Configuration.ProviderConfig { Id = "local", Kind = "local" };
        var baseLine = LocalLlmClient.LoadSignature(provider);
        var tierA = new InferenceTier { Level = 1, GpuLayers = 999, KvOffload = true, KvQuant = "none" };
        var tierB = new InferenceTier { Level = 2, GpuLayers = 999, KvOffload = true, KvQuant = "q8_0" };
        var withA = LocalLlmClient.LoadSignature(provider, null, tierA);
        var withB = LocalLlmClient.LoadSignature(provider, null, tierB);
        Assert.NotEqual(baseLine, withA);
        Assert.NotEqual(withA, withB); // 调档必须触发权重重载
        Assert.Contains("tier1", withA);
    }

    [Fact]
    public void BuildShape_ComputesKvDimFromHeader()
    {
        var bytes = GgufFixture.Create(
            ("qwen3.block_count", 28u),
            ("qwen3.attention.head_count_kv", 8u),
            ("qwen3.attention.key_length", 128u),
            ("qwen3.embedding_length", 4096u));
        var path = WriteTemp("kvdim.gguf", bytes);
        var shape = LocalLlmClient.BuildShape(path);
        Assert.NotNull(shape);
        Assert.Equal(28, shape.BlockCount);
        Assert.Equal(8 * 128, shape.KvDim);
        // f16 KV 字节数：2(K+V) × 层数 × kvDim × ctx × 2 字节。
        Assert.Equal(2L * 28 * 1024 * 32768 * 2, shape.KvBytesFor(32_768));
    }

    [Fact]
    public void BuildShape_MissingFile_ReturnsNull()
    {
        Assert.Null(LocalLlmClient.BuildShape("Z:/不存在的模型.gguf"));
    }

    [Fact]
    public void EstimateContextForTier_FollowsRegistrationWindow()
    {
        var header = new GgufProbe.Header(ContextLength: 131_072);
        var registered = new Configuration.ModelConfig { Id = "m", ContextWindow = 32_768 };
        Assert.Equal(32_768, LocalLlmClient.EstimateContextForTier(registered, header));
        // 未注册窗口时钳制到默认扫描窗口（与 BuildContextParams 的 auto 分支口径一致）。
        Assert.Equal(32_768, LocalLlmClient.EstimateContextForTier(null, header));
        var trained8k = new GgufProbe.Header(ContextLength: 8192);
        Assert.Equal(8192, LocalLlmClient.EstimateContextForTier(null, trained8k));
    }

    // ------------------------------------------------ 辅助

    private static InferencePerformance Perf(double decodeTps, double cacheHitRate = 0) => new()
    {
        TtftMs = 100,
        TotalMs = 1000,
        DecodeMs = 900,
        DecodeTokensPerSecond = decodeTps,
        PromptTokens = 100,
        CachedTokens = (long)(100 * cacheHitRate),
        CacheHitRate = cacheHitRate,
    };

    private static string _tempDir = Path.Combine(Path.GetTempPath(), "haoyue-adaptive-tests");

    private static string WriteTemp(string fileName, byte[] bytes)
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Builds the smallest GGUF header these tests have to understand.</summary>
    private static class GgufFixture
    {
        public static byte[] Create(params (string Key, object Value)[] entries)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write(0x46554747u); // "GGUF"
            writer.Write(3u);           // version
            writer.Write(0UL);          // tensor count
            writer.Write((ulong)entries.Length);
            foreach (var (key, value) in entries)
            {
                WriteString(writer, key);
                switch (value)
                {
                    case string text:
                        writer.Write(8u);
                        WriteString(writer, text);
                        break;
                    case uint number:
                        writer.Write(4u);
                        writer.Write(number);
                        break;
                }
            }
            writer.Flush();
            return stream.ToArray();
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.Write((ulong)bytes.Length);
            writer.Write(bytes);
        }
    }
}
