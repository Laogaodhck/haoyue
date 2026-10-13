using System.Buffers.Binary;
using System.Text;
using Haoyue.Runtime.Tools.Builtin;

namespace Haoyue.Tests;

/// <summary>
/// L6 收尾：capture_screen 回退路径宽高 / monitor_index 治理。
/// PNG 尺寸解析与平台脚本构建是纯函数，直接单测；GDI+ 主路径只做不依赖
/// 真实截图内容的守卫断言（越界报错 + 主屏冒烟）。
/// </summary>
public class CaptureScreenToolTests
{
    [Fact]
    public void ReadPngDimensions_ReadsIhdrWidthAndHeight()
    {
        var png = BuildPng(1920, 1080);
        var (width, height) = CaptureScreenTool.ReadPngDimensions(png);
        Assert.Equal(1920, width);
        Assert.Equal(1080, height);
    }

    [Fact]
    public void ReadPngDimensions_ReadsMinimalPng()
    {
        Assert.Equal((1, 1), CaptureScreenTool.ReadPngDimensions(BuildPng(1, 1)));
    }

    [Fact]
    public void ReadPngDimensions_RejectsNonPngTruncatedAndZeroSized()
    {
        // 长度不足 24 字节（签名 + IHDR 头 + 宽高）。
        Assert.Equal((0, 0), CaptureScreenTool.ReadPngDimensions(new byte[10]));

        // 签名正确但首个块不是 IHDR。
        var wrongChunk = BuildPng(64, 64);
        wrongChunk[12] = (byte)'g';
        Assert.Equal((0, 0), CaptureScreenTool.ReadPngDimensions(wrongChunk));

        // JPEG 魔数。
        var jpeg = new byte[24];
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;
        Assert.Equal((0, 0), CaptureScreenTool.ReadPngDimensions(jpeg));

        // 零尺寸 IHDR。
        Assert.Equal((0, 0), CaptureScreenTool.ReadPngDimensions(BuildPng(0, 0)));
    }

    [Fact]
    public void BuildWindowsFallbackScript_SelectsMonitorAndValidatesIndex()
    {
        var script = CaptureScreenTool.BuildWindowsFallbackScript(2, @"C:\tmp\shot.png");

        Assert.Contains("AllScreens", script);
        Assert.Contains("$screens[2]", script);
        Assert.Contains("monitor_index 2 out of range", script);
        // 按目标屏幕的边界（含负坐标布局）采集，而不是写死 PrimaryScreen。
        Assert.Contains("CopyFromScreen($s.Bounds.X, $s.Bounds.Y", script);
        Assert.Contains(@"C:\tmp\shot.png", script);
    }

    [Fact]
    public void BuildWindowsFallbackScript_EscapesSingleQuotesInPath()
    {
        var script = CaptureScreenTool.BuildWindowsFallbackScript(0, @"C:\it's\shot.png");
        Assert.Contains("it''s", script);
    }

    [Fact]
    public void BuildMacArgs_UsesOneBasedDisplayIndex()
    {
        var args = CaptureScreenTool.BuildMacArgs(0, "/tmp/a.png");
        Assert.Contains("-x", args);
        Assert.Contains("-D 1", args); // monitor_index 0 → 主屏（screencapture 序号 1-based）
        Assert.Contains("-D 4", CaptureScreenTool.BuildMacArgs(3, "/tmp/a.png"));
    }

    [Fact]
    public async Task LinuxFallback_MonitorIndexAboveZero_FailsFastWithClearMessage()
    {
        // 守卫必须先于 grim/import/scrot 探测触发：在没有这些工具的机器上也会得到
        // monitor 专属错误（而不是 "no supported tool"），不会静默截错区域。
        var result = await CaptureScreenTool.CaptureLinuxScreenAsync(2, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("monitor_index 2", result.Error);
        Assert.Contains("grim/import/scrot", result.Error);
    }

    [Fact]
    public void GdiCapture_MonitorIndexOutOfRange_FailsWithClearMessage()
    {
        if (!OperatingSystem.IsWindows()) return;

        // 99 号显示器在真实机器上不存在；枚举成功与枚举失败两条路径的错误消息
        // 都带 monitor_index，断言在任何 Windows 会话（含无显示环境）下稳定。
        var result = CaptureScreenTool.WindowsScreenCapture.CaptureScreen(99);

        Assert.False(result.Success);
        Assert.Contains("monitor_index 99", result.Error);
    }

    [Fact]
    public void GdiCapture_PrimaryScreen_SucceedsWithRealDimensions()
    {
        if (!OperatingSystem.IsWindows()) return;

        var result = CaptureScreenTool.WindowsScreenCapture.CaptureScreen(0);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Bytes);
        Assert.True(result.Width > 0 && result.Height > 0,
            $"expected real dimensions, got {result.Width}x{result.Height}");
        // 产出确实是 PNG（魔数 89 50 4E 47）。
        Assert.Equal(0x89, result.Bytes[0]);
        Assert.Equal((byte)'P', result.Bytes[1]);
        Assert.Equal((byte)'N', result.Bytes[2]);
        Assert.Equal((byte)'G', result.Bytes[3]);
    }

    private static byte[] BuildPng(int width, int height)
    {
        var buffer = new byte[8 + 25];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', (byte)'\r', (byte)'\n', 0x1A, (byte)'\n' }
            .CopyTo(buffer, 0);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(8, 4), 13); // IHDR 数据长度
        Encoding.ASCII.GetBytes("IHDR").CopyTo(buffer, 12);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(20, 4), height);
        buffer[24] = 8; // bit depth
        return buffer;
    }
}
