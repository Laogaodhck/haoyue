using Haoyue.Runtime.Providers;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// 2.6 权重量化选型引导（探测 + 提示，零运行时风险）：文件名档位识别、
/// header 维度估算兜底、低效档位的收益文案。纯提示组件——不注册不拦截。
/// </summary>
public sealed class QuantizationAdvisorTests : IDisposable
{
    private readonly string _dir;

    public QuantizationAdvisorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "haoyue-quant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录清理失败不影响结论 */ }
    }

    [Theory]
    [InlineData("gemma-3-4b-it-Q4_K_M.gguf", "Q4_K_M")]
    [InlineData("model.q4km.gguf", "Q4_K_M")] // 点分隔
    [InlineData("qwen2.5-7b-instruct-q8-0.gguf", "Q8_0")] // 连字符分隔
    [InlineData("Llama-3-FP16.gguf", "FP16")]
    [InlineData("mistral-iq4_xs.gguf", "IQ4_XS")]
    public void ParseLabel_HandlesCommunityNamingVariants(string fileName, string expected)
    {
        Assert.Equal(expected, QuantizationAdvisor.ParseLabel(fileName));
    }

    [Fact]
    public void ParseLabel_UnknownOrAbsentLabel_ReturnsNull()
    {
        Assert.Null(QuantizationAdvisor.ParseLabel("gemma-3-4b-it.gguf"));
        Assert.Null(QuantizationAdvisor.ParseLabel("model-with-q4-in-path.gguf".Replace("path", "dir"))); // 无档位
    }

    [Fact]
    public void Describe_Q8File_ProducesQ4KmAdviceWithSpeedupAndSizeDrop()
    {
        var hint = QuantizationAdvisor.Describe("llama-3-8b-Q8_0.gguf", sizeBytes: 8_500_000_000);
        Assert.Equal("Q8_0", hint.Label);
        Assert.True(hint.Inefficient);
        Assert.True(hint.HasAdvice);
        Assert.Contains("Q4_K_M", hint.Advice, StringComparison.Ordinal);
        Assert.Contains("提速", hint.Advice, StringComparison.Ordinal);
        Assert.Contains("体积", hint.Advice, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("model-F16.gguf")]
    [InlineData("model-F32.gguf")]
    public void Describe_HighPrecisionFiles_AlsoFlagged(string fileName)
    {
        var hint = QuantizationAdvisor.Describe(fileName, sizeBytes: 16_000_000_000);
        Assert.True(hint.Inefficient);
        Assert.True(hint.HasAdvice);
    }

    [Fact]
    public void Describe_SweetSpotAndBelow_NoAdvice()
    {
        // Q4_K_M 本身就是目标档；Q6_K 以下不制造噪音。
        Assert.False(QuantizationAdvisor.Describe("model-Q4_K_M.gguf", 5_000_000_000).HasAdvice);
        Assert.False(QuantizationAdvisor.Describe("model-Q6_K.gguf", 6_000_000_000).HasAdvice);
        Assert.False(QuantizationAdvisor.Describe("model-Q4_K_M.gguf", 5_000_000_000).Inefficient);
    }

    [Fact]
    public void Describe_NoLabelAndIncompleteHeader_FallsBackToSizeEstimate()
    {
        // 无档位标签但有完整维度：按 12·e²·blocks 估算位宽。
        // 构造一个「估算位宽 ≥ 8」的场景：小参数量 + 大体积。
        var header = new GgufProbe.Header(
            Architecture: "llama", BlockCount: 8, EmbeddingLength: 512);
        var hint = QuantizationAdvisor.Describe("unnamed-model.gguf", sizeBytes: 400_000_000, header);
        Assert.Null(hint.Label);
        Assert.NotNull(hint.BitsPerWeight);
        Assert.True(hint.HasAdvice); // 400MB / (12·512²·8) ≈ 15.9 位 → 提示
        Assert.Contains("估算", hint.Advice, StringComparison.Ordinal); // 如实标注「估算」
    }

    [Fact]
    public void Describe_NoLabelNoHeader_NeverGuesses()
    {
        var hint = QuantizationAdvisor.Describe("unnamed-model.gguf", sizeBytes: 9_000_000_000, null);
        Assert.False(hint.HasAdvice);
        Assert.False(hint.Inefficient);
    }

    [Fact]
    public void Describe_ZeroSize_NeverThrows()
    {
        var hint = QuantizationAdvisor.Describe("model-Q8_0.gguf", 0);
        Assert.False(hint.HasAdvice);
    }

    [Fact]
    public void LocalModelProbe_Scan_AttachesQuantizationHint()
    {
        // 端到端：Scan 出来的 LocalModelInfo 必须带量化提示字段（Q8 命名文件）。
        var source = Path.Combine(_dir, "tiny-Q8_0.gguf");
        File.WriteAllBytes(source, new byte[1024]); // ListFiles 只收非空文件；空文件头由 GgufProbe 兜底
        var models = LocalModelProbe.Scan(_dir);
        // 文件头非法（空文件）时 Scan 仍会产出条目（坏文件不藏好文件），
        // 此时按文件名标签给出提示。
        var model = Assert.Single(models);
        Assert.Equal("Q8_0", model.Quantization?.Label);
        Assert.True(model.Quantization?.HasAdvice);
    }
}
