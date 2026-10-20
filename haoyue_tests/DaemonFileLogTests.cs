using System.Text;
using Haoyue.Runtime.Daemon;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// daemon 自身结构化文件日志（任务：CLI / 独立 daemon 场景零落盘）：
/// 滚动、禁用降级、行格式与并发写入纪律。
/// </summary>
public sealed class DaemonFileLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "haoyue-daemonlog-tests", Guid.NewGuid().ToString("N"));

    public DaemonFileLogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string LogPath => Path.Combine(_dir, DaemonFileLog.FileName);

    [Fact]
    public void Write_AppendsTimestampedLevelLines()
    {
        using (var log = DaemonFileLog.Open(LogPath))
        {
            Assert.True(log.IsEnabled);
            log.Info("daemon started");
            log.Warn("gpu backend missing");
            log.Error("load failed", new InvalidOperationException("weights gone"));
        }

        var lines = File.ReadAllLines(LogPath, Encoding.UTF8);
        Assert.Equal(3, lines.Length);
        // ISO 时间戳 + 层级 + 单行化消息。
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z \[INFO\] daemon started$", lines[0]);
        Assert.Contains("[WARN] gpu backend missing", lines[1], StringComparison.Ordinal);
        Assert.Contains("[ERROR] load failed | InvalidOperationException: weights gone", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Write_FlattensMultilineMessages_ToSingleLogLine()
    {
        using (var log = DaemonFileLog.Open(LogPath))
        {
            log.Warn("line one\nline two\r\nline three");
        }

        var lines = File.ReadAllLines(LogPath, Encoding.UTF8);
        Assert.Single(lines);
        Assert.Contains("line one line two line three", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Marker_WritesDelimitedBanner()
    {
        using (var log = DaemonFileLog.Open(LogPath))
        {
            log.Marker("daemon host starting pid=42");
        }

        var text = File.ReadAllText(LogPath, Encoding.UTF8);
        Assert.Contains("===== daemon host starting pid=42 =====", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_RollsOversizedFile_ToPreviousGeneration()
    {
        // 超过 MaxBytes 的既有文件在打开时滚到 daemon.prev.log，新写入从空文件开始。
        File.WriteAllText(LogPath, new string('x', (int)DaemonFileLog.MaxBytes), Encoding.UTF8);

        using (var log = DaemonFileLog.Open(LogPath))
        {
            log.Info("fresh start");
        }

        Assert.True(File.Exists(Path.Combine(_dir, DaemonFileLog.PreviousFileName)));
        var current = File.ReadAllText(LogPath, Encoding.UTF8);
        Assert.Contains("fresh start", current, StringComparison.Ordinal);
        Assert.DoesNotContain("xxxx", current, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_ToUnwritableLocation_ReturnsDisabledInstance_WithoutThrowing()
    {
        // Windows 保留设备名无法作为文件创建：构造路径必然失败。
        var path = OperatingSystem.IsWindows()
            ? Path.Combine(_dir, "con", "daemon.log")
            : Path.Combine(_dir, "\0invalid");
        using var log = DaemonFileLog.Open(path);

        Assert.False(log.IsEnabled);
        // 禁用实例的一切写入都是无操作，绝不抛异常。
        log.Info("no-op");
        log.Warn("no-op");
        log.Error("no-op");
        log.Marker("no-op");
    }

    [Fact]
    public async Task Write_FromMultipleThreads_ProducesIntactLines()
    {
        using (var log = DaemonFileLog.Open(LogPath))
        {
            var tasks = Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            {
                for (var i = 0; i < 50; i++)
                    log.Info($"writer-{index} line-{i}");
            }));
            await Task.WhenAll(tasks);
        }

        var lines = File.ReadAllLines(LogPath, Encoding.UTF8);
        Assert.Equal(400, lines.Length);
        Assert.All(lines, line =>
        {
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z \[INFO\] writer-\d+ line-\d+$", line);
        });
    }

    [Fact]
    public void Dispose_StopsFurtherWrites_Safely()
    {
        var log = DaemonFileLog.Open(LogPath);
        log.Info("before dispose");
        log.Dispose();
        log.Dispose(); // 双重 Dispose 必须安全。
        log.Info("after dispose");

        var text = File.ReadAllText(LogPath, Encoding.UTF8);
        Assert.Contains("before dispose", text, StringComparison.Ordinal);
        Assert.DoesNotContain("after dispose", text, StringComparison.Ordinal);
    }
}
