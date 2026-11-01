using Haoyue.Cli.Ui;

namespace Haoyue.Cli.Tests;

public sealed class AnsiTests
{
    [Theory]
    [InlineData("\x1b[2Khello")]
    [InlineData("\x1b[1;31mhello\x1b[0m")]
    [InlineData("\x1b[?25lhello")]
    [InlineData("\x1b]0;title\x07hello")]
    [InlineData("hel\x1bMlo")]
    [InlineData("\x1b(Bhello")]
    public void StripStyles_RemovesEveryEscapeFormButKeepsText(string input)
    {
        Assert.Equal("hello", Ansi.StripStyles(input));
    }

    [Fact]
    public void VisibleLength_IgnoresNonSgrSequences()
    {
        Assert.Equal(5, Ansi.VisibleLength("\x1b[2K\x1b[32mhello\x1b[0m"));
    }

    [Fact]
    public void TruncateVisible_DoesNotDropTextAfterANonSgrSequence()
    {
        // 旧实现从 ESC 一路找到下一个 'm'，会把 \x1b[2K 之后的正文当成序列吞掉。
        var truncated = Ansi.TruncateVisible("\x1b[2Kabcdefghij", 5);

        Assert.Equal("abcd…", Ansi.StripStyles(truncated));
    }

    [Fact]
    public void TruncateVisible_KeepsTextShortEnoughToFit()
    {
        const string text = "\x1b[32mok\x1b[0m";
        Assert.Equal(text, Ansi.TruncateVisible(text, 5));
    }
}
