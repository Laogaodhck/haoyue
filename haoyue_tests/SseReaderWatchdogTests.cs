using Haoyue.Runtime.Providers;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// SseReader 滑动空闲看门狗的三个时间敏感用例（真实 Timer/Task.Delay + 管道流）。
/// 单独跑全绿、全量并行时偶发失败：线程池被其余测试占满时，产块 Task.Delay 与
/// 读取循环的调度延迟会挤爆空闲窗口。独立 collection 串行化（与 oauth-loopback
/// 同法），并保持与其他测试不并行。
/// </summary>
[Collection("sse-watchdog")]
public sealed class SseReaderWatchdogTests
{
    [Fact]
    public async Task SseReader_SlidingIdleWatchdog_AllowsLongStreamWithActivity()
    {
        var pipe = new System.IO.Pipelines.Pipe();
        var stream = pipe.Reader.AsStream();

        // 首帧先落管道：读取循环启动时不依赖后台任务的首次调度（满载时会晚数秒）。
        await pipe.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes("data: chunk1\n\n"));

        var producer = Task.Run(async () =>
        {
            for (var i = 2; i <= 3; i++)
            {
                await Task.Delay(100);
                var bytes = System.Text.Encoding.UTF8.GetBytes($"data: chunk{i}\n\n");
                await pipe.Writer.WriteAsync(bytes);
            }
            pipe.Writer.Complete();
        });

        var messages = new List<string>();
        // 空闲阈值必须远大于产块间隔：覆盖插桩 + xUnit 并行会把线程池延迟放大到秒级，
        // 旧参数（间隔 100ms / 阈值 250ms，仅 2.5 倍余量）会在满载时误判为「流已空闲」。
        await foreach (var msg in SseReader.ReadAsync(stream, TimeSpan.FromSeconds(10)))
        {
            messages.Add(msg.Data);
        }

        await producer;
        Assert.Equal(["chunk1", "chunk2", "chunk3"], messages);
    }

    [Fact]
    public async Task SseReader_SlidingIdleWatchdog_TimesOutWhenIdleExceedsThreshold()
    {
        var pipe = new System.IO.Pipelines.Pipe();
        var stream = pipe.Reader.AsStream();

        // 首帧先落管道，生产者只负责在静默期后关闭流——首帧不参与调度竞争。
        await pipe.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes("data: first\n\n"));

        var producer = Task.Run(async () =>
        {
            await Task.Delay(1500);
            pipe.Writer.Complete();
        });

        var messages = new List<string>();
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            // 阈值 300ms、静默期 1.5s：既保住「空闲超时」语义，又给读取首帧留出余量；
            // 生产者续体即使被拖到数秒后才关闭流，也不会影响超时判定。
            await foreach (var msg in SseReader.ReadAsync(stream, TimeSpan.FromMilliseconds(300)))
            {
                messages.Add(msg.Data);
            }
        });

        await producer;
        Assert.Single(messages);
        Assert.Equal("first", messages[0]);
    }

    [Fact]
    public async Task SseReader_SlidingIdleWatchdog_CommentsResetIdleTimeout()
    {
        var pipe = new System.IO.Pipelines.Pipe();
        var stream = pipe.Reader.AsStream();

        // 首帧在启动读取循环前写入管道：Task.Run 的首次调度在满载 CI 上可能被推迟数秒，
        // 若把首帧也寄托在后台任务里，看门狗会先于生产者启动而误判空闲超时。
        await pipe.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes("data: hello\n\n"));

        var producer = Task.Run(async () =>
        {
            await Task.Delay(600);
            var ping = System.Text.Encoding.UTF8.GetBytes(": keep-alive\n\n");
            await pipe.Writer.WriteAsync(ping);
            await Task.Delay(600);
            var bytes2 = System.Text.Encoding.UTF8.GetBytes("data: world\n\n");
            await pipe.Writer.WriteAsync(bytes2);
            pipe.Writer.Complete();
        });

        var messages = new List<string>();
        // Each gap stays inside the idle window while their sum exceeds it, so the
        // stream only survives if the comment frame re-arms the watchdog. 间隔 600ms 对
        // 10s 阈值留 16 倍余量：覆盖插桩 + 全量并行的调度抖动不再误触发空闲超时。
        await foreach (var msg in SseReader.ReadAsync(stream, TimeSpan.FromSeconds(10)))
        {
            messages.Add(msg.Data);
        }

        await producer;
        Assert.Equal(["hello", "world"], messages);
    }
}
