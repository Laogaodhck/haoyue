using Haoyue.Runtime.Providers;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// 并发 &gt; 1 关闭 KV 前缀复用的可见警告（任务：本地推理体验默认值失当之一）。
/// 该退化此前完全静默——多轮大 prompt 每轮从头 prefill，用户不知道一个配置项就能找回。
/// </summary>
public sealed class LocalModelConcurrencyWarningTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-3)]
    public void ConcurrencyAtOrBelowOne_DoesNotWarn(int concurrency)
    {
        Assert.Null(LocalModelCache.ConcurrencyWarning(concurrency));
    }

    [Fact]
    public void ConcurrencyAboveOne_WarnsWithRecoveryGuidance()
    {
        var warning = LocalModelCache.ConcurrencyWarning(2);
        Assert.NotNull(warning);
        Assert.Contains("KV 前缀复用", warning, StringComparison.Ordinal);
        Assert.Contains("agent.localModelConcurrency", warning, StringComparison.Ordinal);
        // 不同并发值的文案带上各自读数，桌面卡片可区分。
        Assert.Contains("4", LocalModelCache.ConcurrencyWarning(4)!, StringComparison.Ordinal);
    }
}
