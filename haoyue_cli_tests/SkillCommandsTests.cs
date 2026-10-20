using System.CommandLine;
using Haoyue.Cli.Commands;
using Xunit;

namespace Haoyue.Cli.Tests;

/// <summary>
/// skill install / import 的命令树接线冒烟：marketplace README 宣传的这两条命令
/// 曾一度不存在（文档漂移实锤）。行为测试在 haoyue_tests CoreTests（SkillManager
/// 的 InstallOfficial / ImportGlobal），这里只锁 CLI 面是否真的暴露。
/// </summary>
public sealed class SkillCommandsTests
{
    [Fact]
    public void SkillCommand_ExposesInstallAndImportSubcommands()
    {
        var skill = SkillCommands.Build();

        Assert.Contains(skill.Subcommands, command => command.Name == "install");
        Assert.Contains(skill.Subcommands, command => command.Name == "import");
    }

    [Fact]
    public void SkillInstall_ParsesSlugArgument()
    {
        var result = SkillCommands.Build().Parse(["install", "code-review"]);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void SkillInstall_WithoutSlug_ReportsParseError()
    {
        var result = SkillCommands.Build().Parse(["install"]);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void SkillImport_ParsesFileArgument()
    {
        var result = SkillCommands.Build().Parse(["import", "my-skill.md"]);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void SkillImport_WithoutFile_ReportsParseError()
    {
        var result = SkillCommands.Build().Parse(["import"]);
        Assert.NotEmpty(result.Errors);
    }
}
