using System.Text.Json.Nodes;
using Haoyue.Runtime.Providers;
using Xunit;
using Configuration = Haoyue.Runtime.Configuration;

namespace Haoyue.Tests;

/// <summary>
/// 模型加载配置（LocalModelSettings）的解析、归一化与签名行为。加载配置页保存的
/// JSON 必须在这里被证明是安全的：未知字段忽略、非法数值钳制、签名稳定可重载。
/// </summary>
public sealed class LocalModelLoadSettingsTests
{
    [Fact]
    public void FromJson_EmptyObject_YieldsDefaults()
    {
        var settings = LocalModelSettings.FromJson(new JsonObject());
        Assert.True(settings.AutoOptimize);
        Assert.Null(settings.ContextLength);
        Assert.Null(settings.GpuOffload);
        // 温度 null = 内置默认 0.6（推理时回落），不落盘。
        Assert.Null(settings.Temperature);
        Assert.True(settings.OffloadKvCacheToGpu);
        Assert.False(settings.KeepModelInMemory);
    }

    [Fact]
    public void FromJson_UnknownFields_AreIgnored()
    {
        var node = new JsonObject
        {
            ["unknownField"] = "whatever",
            ["temperature"] = 0.9,
        };
        var settings = LocalModelSettings.FromJson(node);
        Assert.Equal(0.9, settings.Temperature);
    }

    [Fact]
    public void Normalize_ClampsIllegalValues()
    {
        var settings = new LocalModelSettings
        {
            ContextLength = 1,          // -> 512
            EvaluationBatchSize = 0,    // -> 16
            Temperature = 5,            // -> 2
            TopP = -3,                  // -> 0.01
            RepeatPenalty = 99,         // -> 2
            StopStrings = ["", "STOP", null!],
        };
        settings.Normalize();
        Assert.Equal(512, settings.ContextLength);
        Assert.Equal(16, settings.EvaluationBatchSize);
        Assert.Equal(2, settings.Temperature);
        Assert.Equal(0.01, settings.TopP);
        Assert.Equal(2, settings.RepeatPenalty);
        Assert.Equal(["STOP"], settings.StopStrings);
    }

    [Fact]
    public void FromJson_MalformedValues_NeverThrow()
    {
        var node = new JsonObject
        {
            ["contextLength"] = "not-a-number",
            ["seed"] = 12345,
        };
        var settings = LocalModelSettings.FromJson(node);
        Assert.Null(settings.ContextLength);
        Assert.Equal(12345, settings.Seed);
    }

    [Fact]
    public void ToJson_RoundTripsThroughFromJson()
    {
        var original = new LocalModelSettings
        {
            AutoOptimize = false,
            ContextLength = 8192,
            GpuOffload = 24,
            Threads = 6,
            EvaluationBatchSize = 1024,
            FlashAttention = true,
            Temperature = 0.7,
            LimitResponseLength = true,
            StopStrings = ["</s>", "USER:"],
            EnableThinking = true,
            ReasoningBudget = 4096,
            OffloadKvCacheToGpu = false,
            KeepModelInMemory = true,
            TryMmap = false,
            TopK = 64,
            Seed = 42,
        };
        original.Normalize();
        var json = original.ToJson();

        var parsed = LocalModelSettings.FromJson(json);
        Assert.Equal(original.ContextLength, parsed.ContextLength);
        Assert.Equal(original.GpuOffload, parsed.GpuOffload);
        Assert.Equal(original.Threads, parsed.Threads);
        Assert.Equal(original.EvaluationBatchSize, parsed.EvaluationBatchSize);
        Assert.Equal(original.Temperature, parsed.Temperature);
        Assert.Equal(original.LimitResponseLength, parsed.LimitResponseLength);
        Assert.Equal(original.StopStrings, parsed.StopStrings);
        Assert.Equal(original.EnableThinking, parsed.EnableThinking);
        Assert.Equal(original.ReasoningBudget, parsed.ReasoningBudget);
        Assert.Equal(original.OffloadKvCacheToGpu, parsed.OffloadKvCacheToGpu);
        Assert.Equal(original.KeepModelInMemory, parsed.KeepModelInMemory);
        Assert.Equal(original.TryMmap, parsed.TryMmap);
        Assert.Equal(original.TopK, parsed.TopK);
        Assert.Equal(original.Seed, parsed.Seed);
    }

    [Fact]
    public void Signature_LoadAffectingField_ChangesSignature()
    {
        var settings = new LocalModelSettings();
        var baseline = settings.Signature();

        settings.GpuOffload = 16;
        Assert.NotEqual(baseline, settings.Signature());

        settings = new LocalModelSettings();
        settings.Threads = 8;
        Assert.NotEqual(baseline, settings.Signature());

        settings = new LocalModelSettings();
        settings.KCacheQuantType = "q8_0";
        Assert.NotEqual(baseline, settings.Signature());
    }

    [Fact]
    public void Signature_StoreOnlyFields_DoNotChangeSignature()
    {
        var settings = new LocalModelSettings();
        var baseline = settings.Signature();
        // 仅保存不生效的字段（推测解码、llama.cpp 参数覆盖）不应触发权重重载。
        settings.SpeculativeDecoding = "draft.gguf";
        settings.LlamaCppOverride = "-ngl 99";
        settings.ReasoningBudget = 2048;
        Assert.Equal(baseline, settings.Signature());
    }

    // ------------------------------------------------ BuildLoadParams（加载参数合并）

    [Fact]
    public void BuildLoadParams_NullLoad_FallsBackToProviderDefaults()
    {
        // 回归：原实现 `load is { AutoOptimize: true } or { GpuOffload: null }` 对
        // load == null（未配置过加载页的模型，即绝大多数场景）两侧都不匹配，落入
        // else 分支对 load! 解引用 → NullReferenceException，模型调用直接失败。
        var provider = new Configuration.ProviderConfig
        {
            Id = "local", Kind = "local", GpuLayers = 18, Threads = 6,
        };
        var model = new Configuration.ModelConfig { Id = "m" };

        var factory = LocalLlmClient.BuildLoadParams("x.gguf", provider, model);

        var parameters = factory(false);
        Assert.Equal(18, parameters.GpuLayerCount);
        Assert.Equal(6, parameters.Threads);
    }

    [Fact]
    public void BuildLoadParams_AutoOptimize_IgnoresExplicitOverrides()
    {
        var provider = new Configuration.ProviderConfig { Id = "local", Kind = "local", GpuLayers = 18 };
        var model = new Configuration.ModelConfig
        {
            Id = "m",
            Load = new LocalModelSettings { AutoOptimize = true, GpuOffload = 99, Threads = 99 },
        };

        var parameters = LocalLlmClient.BuildLoadParams("x.gguf", provider, model)(false);
        Assert.Equal(18, parameters.GpuLayerCount); // AUTO：回到提供商级
        Assert.Null(parameters.Threads);
    }

    [Fact]
    public void BuildLoadParams_ExplicitOverrides_OnlyWhenAutoOptimizeDisabled()
    {
        var provider = new Configuration.ProviderConfig { Id = "local", Kind = "local", GpuLayers = 18, Threads = 6 };

        // AutoOptimize=false + 显式值 → 生效
        var explicitModel = new Configuration.ModelConfig
        {
            Id = "m",
            Load = new LocalModelSettings { AutoOptimize = false, GpuOffload = 10, Threads = 4 },
        };
        var parameters = LocalLlmClient.BuildLoadParams("x.gguf", provider, explicitModel)(false);
        Assert.Equal(10, parameters.GpuLayerCount);
        Assert.Equal(4, parameters.Threads);

        // CPU 降级请求 → GPU 归零
        Assert.Equal(0, LocalLlmClient.BuildLoadParams("x.gguf", provider, explicitModel)(true).GpuLayerCount);

        // AutoOptimize=false 但值留空 → 仍回提供商级
        var emptyModel = new Configuration.ModelConfig
        {
            Id = "m",
            Load = new LocalModelSettings { AutoOptimize = false, GpuOffload = null, Threads = null },
        };
        var fallback = LocalLlmClient.BuildLoadParams("x.gguf", provider, emptyModel)(false);
        Assert.Equal(18, fallback.GpuLayerCount);
        Assert.Equal(6, fallback.Threads);
    }
}
