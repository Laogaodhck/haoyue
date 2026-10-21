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

        var producer = Task.Run(async () =>
        {
            for (var i = 1; i <= 3; i++)
            {
                await Task.Delay(100);
                var bytes = System.Text.Encoding.UTF8.GetBytes($"data: chunk{i}\n\n");
                await pipe.Writer.WriteAsync(bytes);
            }
            pipe.Writer.Complete();
        });

        var messages = new List<string>();
        // 空闲阈值必须远大于产块间隔：xUnit 并行跑整套测试时线程池延迟可达数百毫秒，
        // 旧参数（间隔 100ms / 阈值 250ms，仅 2.5 倍余量）会在满载时误判为「流已空闲」
        // 并抛 TimeoutException，造成随机失败。
        await foreach (var msg in SseReader.ReadAsync(stream, TimeSpan.FromSeconds(2)))
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

        var producer = Task.Run(async () =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("data: first\n\n");
            await pipe.Writer.WriteAsync(bytes);
            await Task.Delay(1500);
            pipe.Writer.Complete();
        });

        var messages = new List<string>();
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            // 阈值放宽到 300ms 并让静默期拉到 1.5s：既保住「空闲超时」语义，又给
            // 满载环境留出读取第一帧的调度余量（100ms/400ms 的旧组合会偶发读不到首帧）。
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

        var producer = Task.Run(async () =>
        {
            var bytes1 = System.Text.Encoding.UTF8.GetBytes("data: hello\n\n");
            await pipe.Writer.WriteAsync(bytes1);
            await Task.Delay(600);
            var ping = System.Text.Encoding.UTF8.GetBytes(": keep-alive\n\n");
            await pipe.Writer.WriteAsync(ping);
            await Task.Delay(600);
            var bytes2 = System.Text.Encoding.UTF8.GetBytes("data: world\n\n");
            await pipe.Writer.WriteAsync(bytes2);
            pipe.Writer.Complete();
        });

        var messages = new List<string>();
        // Each gap stays inside the 3s window while their sum exceeds it, so the
        // stream only survives if the comment frame re-arms the watchdog. 间隔 600ms 对
        // 3s 阈值留 5 倍余量（原 600ms/1s 仅 1.67 倍，满载时会误超时）。
        await foreach (var msg in SseReader.ReadAsync(stream, TimeSpan.FromSeconds(3)))
        {
            messages.Add(msg.Data);
        }

        await producer;
        Assert.Equal(["hello", "world"], messages);
    }
}
