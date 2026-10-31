using Haoyue.Runtime.Daemon;

namespace Haoyue.Tests;

public sealed class BoundedLineReaderTests
{
    private static BoundedLineReader Reader(string text) => new(new StringReader(text));

    [Fact]
    public async Task ReadsLinesAndStripsBothLineEndings()
    {
        var lines = Reader("a\nb\r\nc");
        Assert.Equal("a", (await lines.ReadLineAsync(16, CancellationToken.None)).Line);
        Assert.Equal("b", (await lines.ReadLineAsync(16, CancellationToken.None)).Line);
        Assert.Equal("c", (await lines.ReadLineAsync(16, CancellationToken.None)).Line);
        var eof = await lines.ReadLineAsync(16, CancellationToken.None);
        Assert.Null(eof.Line);
        Assert.False(eof.TooLong);
    }

    [Fact]
    public async Task ReturnsEmptyLineForBlankInput()
    {
        var lines = Reader("\n\n");
        Assert.Equal("", (await lines.ReadLineAsync(16, CancellationToken.None)).Line);
        Assert.Equal("", (await lines.ReadLineAsync(16, CancellationToken.None)).Line);
    }

    [Fact]
    public async Task LineOfExactlyTheLimitIsAccepted()
    {
        var lines = Reader(new string('x', 8) + "\nnext\n");
        Assert.Equal(new string('x', 8), (await lines.ReadLineAsync(8, CancellationToken.None)).Line);
        Assert.Equal("next", (await lines.ReadLineAsync(8, CancellationToken.None)).Line);
    }

    [Fact]
    public async Task LineBeyondTheLimitIsRejectedWhileReading()
    {
        var lines = Reader(new string('x', 9) + "\nnext\n");
        var read = await lines.ReadLineAsync(8, CancellationToken.None);
        Assert.Null(read.Line);
        Assert.True(read.TooLong);
    }

    [Fact]
    public async Task LongLineWithoutNewlineIsRejectedAtTheLimit()
    {
        var lines = Reader(new string('x', 100));
        var read = await lines.ReadLineAsync(8, CancellationToken.None);
        Assert.Null(read.Line);
        Assert.True(read.TooLong);
    }

    [Fact]
    public async Task ReadsAcrossBufferBoundaries()
    {
        // 4096 是内部缓冲大小：跨块的行必须完整拼接。
        var payload = new string('y', 4095) + "z";
        var lines = Reader(payload + "\n");
        Assert.Equal(payload, (await lines.ReadLineAsync(8192, CancellationToken.None)).Line);
    }
}
