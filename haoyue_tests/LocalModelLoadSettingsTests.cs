using System.Text.Json.Nodes;
using Haoyue.Runtime.Providers;
using Xunit;

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
}
