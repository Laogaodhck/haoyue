using System.Globalization;
using Haoyue.Runtime.Configuration;

namespace Haoyue.Tests;

public sealed class OutputLanguageTests
{
    // ---------------------------------------------------------------- normalize

    [Theory]
    [InlineData("zh", "zh")]
    [InlineData("EN", "en")]
    [InlineData(" en ", "en")]
    [InlineData("auto", "auto")]
    [InlineData("fr", "auto")]
    [InlineData("", "auto")]
    [InlineData(null, "auto")]
    public void Normalize_MapsKnownValues_AndFallsBackToAuto(string? input, string expected)
    {
        Assert.Equal(expected, OutputLanguage.Normalize(input));
    }

    // ---------------------------------------------------------------- resolve

    [Fact]
    public void Resolve_ExplicitLanguage_WinsOverSystemCulture()
    {
        var chinese = new AgentConfig { Language = "zh" };
        Assert.Equal("zh", OutputLanguage.Resolve(null, chinese, new CultureInfo("en-US")));
        var english = new AgentConfig { Language = "en" };
        Assert.Equal("en", OutputLanguage.Resolve(null, english, new CultureInfo("en-US")));
    }

    [Fact]
    public void Resolve_Auto_FollowsInjectedSystemCulture()
    {
        var agent = new AgentConfig();
        Assert.Equal("zh", OutputLanguage.Resolve(null, agent, new CultureInfo("zh-CN")));
        Assert.Equal("zh", OutputLanguage.Resolve(null, agent, new CultureInfo("zh-TW")));
        Assert.Equal("en", OutputLanguage.Resolve(null, agent, new CultureInfo("en-US")));
        Assert.Equal("en", OutputLanguage.Resolve(null, agent, new CultureInfo("ja-JP")));
    }

    [Fact]
    public void Resolve_WorkspaceOverride_BeatsGlobalSetting()
    {
        var agent = new AgentConfig { Language = "zh" };
        var workspace = new WorkspaceConfig { Language = "en" };
        Assert.Equal("en", OutputLanguage.Resolve(workspace, agent, new CultureInfo("zh-CN")));

        var workspaceAuto = new WorkspaceConfig { Language = "auto" };
        Assert.Equal("zh", OutputLanguage.Resolve(workspaceAuto, agent, new CultureInfo("zh-CN")));
    }

    [Fact]
    public void Resolve_UnknownValue_FallsBackToAutoThenSystemCulture()
    {
        var agent = new AgentConfig { Language = "fr" };
        Assert.Equal("en", OutputLanguage.Resolve(null, agent, new CultureInfo("fr-FR")));
        Assert.Equal("zh", OutputLanguage.Resolve(null, agent, new CultureInfo("zh-Hans-CN")));
    }

    // ---------------------------------------------------------------- instruction

    [Fact]
    public void BuildInstruction_Chinese_MentionsSourceDetectionAndChineseTarget()
    {
        var text = OutputLanguage.BuildInstruction(OutputLanguage.Chinese);
        Assert.Contains("源语言", text);
        Assert.Contains("简体中文", text);
    }

    [Fact]
    public void BuildInstruction_English_MentionsSourceDetectionAndEnglishTarget()
    {
        var text = OutputLanguage.BuildInstruction(OutputLanguage.English);
        Assert.Contains("source", text);
        Assert.Contains("English", text);
    }
}
