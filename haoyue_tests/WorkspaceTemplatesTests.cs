using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

public sealed class WorkspaceTemplatesTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "haoyue-workspace-templates-tests", Guid.NewGuid().ToString("N"));
    private readonly string _templatesRoot;

    public WorkspaceTemplatesTests()
    {
        Directory.CreateDirectory(_tempDir);
        _templatesRoot = Path.Combine(_tempDir, "templates", "workspace");
    }

    [Fact]
    public void Bootstrap_SeedsAgentsMdAndWorkspaceConfig_WithBuiltinDefaults()
    {
        var workspace = NewWorkspace("seeded");
        var created = new WorkspaceManager().Bootstrap(workspace, _templatesRoot);

        var agentsMd = Path.Combine(workspace.Root, "AGENTS.md");
        Assert.True(File.Exists(agentsMd));
        Assert.Equal(WorkspaceTemplates.DefaultAgentsMd, File.ReadAllText(agentsMd));

        var config = Path.Combine(workspace.HaoyueDir, "config.json");
        Assert.True(File.Exists(config));
        Assert.Equal(WorkspaceTemplates.DefaultWorkspaceConfig, File.ReadAllText(config));
        Assert.Contains("AGENTS.md", created);
        Assert.Contains(".haoyue/config.json", created);
    }

    [Fact]
    public void Bootstrap_UsesGlobalTemplateOverrides_IncludingPromptSeeds()
    {
        Directory.CreateDirectory(_templatesRoot);
        File.WriteAllText(Path.Combine(_templatesRoot, "AGENTS.md"), "# 自定义规则\n- 全部走 pnpm");
        File.WriteAllText(Path.Combine(_templatesRoot, "config.json"), """{ "mode": "readonly" }""");
        var promptDir = Path.Combine(_templatesRoot, "prompts", "system");
        Directory.CreateDirectory(promptDir);
        File.WriteAllText(Path.Combine(promptDir, "team.txt"), "团队系统提示词");

        var workspace = NewWorkspace("overridden");
        var created = new WorkspaceManager().Bootstrap(workspace, _templatesRoot);

        Assert.Equal("# 自定义规则\n- 全部走 pnpm",
            File.ReadAllText(Path.Combine(workspace.Root, "AGENTS.md")));
        Assert.Equal("""{ "mode": "readonly" }""",
            File.ReadAllText(Path.Combine(workspace.HaoyueDir, "config.json")));
        Assert.Equal("团队系统提示词",
            File.ReadAllText(Path.Combine(workspace.PromptsDir, "system", "team.txt")));
        Assert.Contains(Path.Combine(".haoyue", "prompts", "system", "team.txt"), created);
    }

    [Fact]
    public void Bootstrap_NeverOverwritesExistingFiles()
    {
        var workspace = NewWorkspace("existing");
        var agentsMd = Path.Combine(workspace.Root, "AGENTS.md");
        const string existing = "# 已有规则，勿动";
        File.WriteAllText(agentsMd, existing);
        var configPath = Path.Combine(workspace.HaoyueDir, "config.json");
        File.WriteAllText(configPath, """{ "mode": "plan" }""");

        var created = new WorkspaceManager().Bootstrap(workspace, _templatesRoot);

        Assert.Equal(existing, File.ReadAllText(agentsMd));
        Assert.Equal("""{ "mode": "plan" }""", File.ReadAllText(configPath));
        Assert.DoesNotContain("AGENTS.md", created);
        Assert.DoesNotContain(".haoyue/config.json", created);
    }

    [Fact]
    public void Bootstrap_TemplateSeedingIsIdempotent()
    {
        var workspace = NewWorkspace("twice");
        var manager = new WorkspaceManager();
        manager.Bootstrap(workspace, _templatesRoot);
        Assert.Empty(manager.Bootstrap(workspace, _templatesRoot));
    }

    private WorkspaceInfo NewWorkspace(string name)
    {
        var root = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(Path.Combine(root, ".haoyue"));
        return new WorkspaceManager(_tempDir).Detect(root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
    }
}
