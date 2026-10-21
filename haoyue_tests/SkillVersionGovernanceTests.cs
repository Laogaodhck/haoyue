using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Skills;
using Haoyue.Runtime.Workspaces;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// 6.4 skill 版本治理：安装记录版本账本；更新只刷新 manifest 版本、保留用户对
/// prompt.txt 的本地修改；canUpdate 用语义化版本比较（目录版本更高才算）。
/// </summary>
public sealed class SkillVersionGovernanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-skill-ver-" + Guid.NewGuid().ToString("N"));

    public SkillVersionGovernanceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private (SkillManager Manager, ConfigStore Config, string GlobalDir, string StatePath) CreateManager()
    {
        var globalDir = Path.Combine(_dir, "global-skills");
        Directory.CreateDirectory(globalDir);
        var statePath = Path.Combine(_dir, $"{Guid.NewGuid():N}-state.json");
        var config = new ConfigStore(Path.Combine(_dir, $"{Guid.NewGuid():N}-config.json"), statePath);
        return (new SkillManager(config, new PromptRegistry(), globalDir), config, globalDir, statePath);
    }

    private WorkspaceInfo Workspace()
    {
        var root = Path.Combine(_dir, "ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new WorkspaceInfo { Root = root, ProjectKinds = [] };
    }

    private static OfficialSkillCatalog.OfficialSkill Entry(string version, string prompt = "官方提示词") =>
        new("test-skill", "测试技能", "测试用官方技能", version, ["测试"], prompt);

    [Fact]
    public void InstallOfficial_RecordsVersionLedger()
    {
        var (manager, config, _, statePath) = CreateManager();
        manager.InstallOfficial(Workspace(), Entry("1.0.0"));

        Assert.Equal("1.0.0", config.State.InstalledSkillVersions["test-skill"]);
        // 账本落盘：用同一 state 文件重开 ConfigStore 应读回记录。
        var reopened = new ConfigStore(Path.Combine(_dir, "reopen-config.json"), statePath);
        Assert.Equal("1.0.0", reopened.State.InstalledSkillVersions["test-skill"]);
    }

    [Fact]
    public void Update_KeepsUserPrompt_AndRefreshesManifestVersion()
    {
        var (manager, config, globalDir, _) = CreateManager();
        var workspace = Workspace();
        manager.InstallOfficial(workspace, Entry("1.0.0"));

        // 用户本地编辑了提示词。
        var skillDir = Path.Combine(globalDir, "test-skill");
        var promptPath = Path.Combine(skillDir, "prompt.txt");
        File.WriteAllText(promptPath, "我的本地定制提示词");
        var yamlPath = Path.Combine(skillDir, "skill.yaml");
        var yaml = File.ReadAllText(yamlPath);
        File.WriteAllText(yamlPath, yaml.Replace("description: \"测试用官方技能\"", "description: \"我改过的描述\""));

        // 目录版本升级到 1.1.0 → 更新。
        manager.InstallOfficial(workspace, Entry("1.1.0", prompt: "官方 1.1 提示词"));

        Assert.Equal("我的本地定制提示词", File.ReadAllText(promptPath)); // prompt 不被覆盖
        var updatedYaml = File.ReadAllText(yamlPath);
        Assert.Contains("version: \"1.1.0\"", updatedYaml, StringComparison.Ordinal);
        Assert.Contains("我改过的描述", updatedYaml, StringComparison.Ordinal); // 用户描述保留
        Assert.Equal("1.1.0", config.State.InstalledSkillVersions["test-skill"]);
    }

    [Fact]
    public void Update_AddsVersionLine_WhenManifestLacksIt()
    {
        var (manager, _, globalDir, _) = CreateManager();
        // 旧格式技能目录（无 version 行），目录名与官方 slug 一致才会被识别为已安装。
        var skillDir = Path.Combine(globalDir, "test-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "skill.yaml"), "name: 测试技能\ndescription: 旧格式");
        File.WriteAllText(Path.Combine(skillDir, "prompt.txt"), "旧提示词");

        manager.InstallOfficial(Workspace(), Entry("2.0.0"));

        Assert.Contains("version: \"2.0.0\"", File.ReadAllText(Path.Combine(skillDir, "skill.yaml")),
            StringComparison.Ordinal);
        Assert.Equal("旧提示词", File.ReadAllText(Path.Combine(skillDir, "prompt.txt")));
    }

    [Fact]
    public void IsNewer_UsesSemanticVersionComparison()
    {
        Assert.True(OfficialSkillCatalog.IsNewer("1.1.0", "1.0.9"));
        Assert.True(OfficialSkillCatalog.IsNewer("1.10.0", "1.9.0"));
        Assert.False(OfficialSkillCatalog.IsNewer("1.0.0", "1.0.0"));
        Assert.False(OfficialSkillCatalog.IsNewer("1.0.0", "1.1.0"));
        Assert.False(OfficialSkillCatalog.IsNewer("1.0.0", null)); // 本地无记录：不算更新
        Assert.False(OfficialSkillCatalog.IsNewer("abc", "1.0.0")); // 非法版本：安全回落
    }
}
