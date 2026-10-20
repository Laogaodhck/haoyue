using Haoyue.Runtime.Events;
using Haoyue.Runtime.Providers;

namespace Haoyue.Tests;

/// <summary>
/// 推理遥测消费线（2026-10-20）：usage.jsonl 落盘 ↔ 实时事件 ↔ 聚合三处口径一致，
/// 历史行（无遥测字段）与云端调用不参与均值。
/// </summary>
public sealed class UsageTelemetryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-usage-telemetry", Guid.NewGuid().ToString("N"));
    private readonly string _file;

    public UsageTelemetryTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "usage.jsonl");
    }

    [Fact]
    public void Record_WithLocalPerf_PersistsFields_AndPublishesEvent()
    {
        var bus = new EventBus();
        using var subscription = bus.Subscribe();
        new UsageTracker(bus, _file).Record(new UsageEntry
        {
            Provider = "local",
            Model = "qwen",
            InputTokens = 10,
            TotalInputTokens = 100,
            OutputTokens = 5,
            ElapsedMs = 1000,
            Success = true,
            TtftMs = 1200,
            LoadWaitMs = 300,
            PrefillTokensPerSecond = 3400,
            DecodeTokensPerSecond = 28,
            CacheHitRate = 0.92,
            PrefixReuse = true,
        });

        var published = Assert.IsType<UsageRecordedEvent>(
            subscription.Reader.ReadAsync().AsTask().GetAwaiter().GetResult());
        Assert.NotNull(published.Performance);
        Assert.Equal(1200, published.Performance!.TtftMs);
        Assert.Equal(300, published.Performance.LoadWaitMs);
        Assert.True(published.Performance.PrefixReuse);

        // JSONL 往返：重开 tracker 读回的字段不丢（老行缺字段时为 null，不是 0）。
        var read = new UsageTracker(new EventBus(), _file).ReadAll().Single();
        Assert.Equal(1200, read.TtftMs);
        Assert.Equal(0.92, read.CacheHitRate);
        Assert.True(read.PrefixReuse);
    }

    [Fact]
    public void Record_WithoutPerf_EventCarriesNullPerformance()
    {
        var bus = new EventBus();
        using var subscription = bus.Subscribe();
        new UsageTracker(bus, _file).Record(new UsageEntry
        {
            Provider = "openai", Model = "gpt", InputTokens = 1, OutputTokens = 1, ElapsedMs = 10, Success = true,
        });

        var published = Assert.IsType<UsageRecordedEvent>(
            subscription.Reader.ReadAsync().AsTask().GetAwaiter().GetResult());
        Assert.Null(published.Performance);
        Assert.Null(new UsageTracker(new EventBus(), _file).ReadAll().Single().TtftMs);
    }

    [Fact]
    public void Aggregate_AveragesTelemetryRows_AndSkipsRowsWithoutIt()
    {
        var tracker = new UsageTracker(new EventBus(), _file);
        tracker.Record(Local(ttft: 1000, loadWait: 200, prefill: 2000, decode: 20, hit: 0.5, reuse: true));
        tracker.Record(Local(ttft: 3000, loadWait: 0, prefill: 4000, decode: 40, hit: 1.0, reuse: false));
        tracker.Record(new UsageEntry { Provider = "local", Model = "m", InputTokens = 1, OutputTokens = 1, ElapsedMs = 5, Success = true });

        var aggregate = Assert.Single(tracker.Aggregate());
        Assert.Equal(3, aggregate.Calls);
        Assert.Equal(2, aggregate.TelemetryCalls);
        Assert.Equal(2000, aggregate.AvgTtftMs);
        Assert.Equal(100, aggregate.AvgLoadWaitMs);
        Assert.Equal(3000, aggregate.AvgPrefillTokensPerSecond);
        Assert.Equal(30, aggregate.AvgDecodeTokensPerSecond);
        Assert.Equal(0.75, aggregate.AvgCacheHitRate);
        Assert.Equal(0.5, aggregate.PrefixReuseRate); // 两行遥测里一行走了复用
    }

    [Fact]
    public void Aggregate_NoTelemetryRows_LeavesInferenceFieldsNull()
    {
        var tracker = new UsageTracker(new EventBus(), _file);
        tracker.Record(new UsageEntry { Provider = "openai", Model = "gpt", InputTokens = 1, OutputTokens = 1, ElapsedMs = 5, Success = true });

        var aggregate = Assert.Single(tracker.Aggregate());
        Assert.Equal(0, aggregate.TelemetryCalls);
        Assert.Null(aggregate.AvgTtftMs);
        Assert.Null(aggregate.PrefixReuseRate);
    }

    private static UsageEntry Local(double ttft, double loadWait, double prefill, double decode, double hit, bool reuse) =>
        new()
        {
            Provider = "local",
            Model = "m",
            InputTokens = 10,
            TotalInputTokens = 100,
            OutputTokens = 5,
            ElapsedMs = ttft,
            Success = true,
            TtftMs = ttft,
            LoadWaitMs = loadWait,
            PrefillTokensPerSecond = prefill,
            DecodeTokensPerSecond = decode,
            CacheHitRate = hit,
            PrefixReuse = reuse,
        };

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
