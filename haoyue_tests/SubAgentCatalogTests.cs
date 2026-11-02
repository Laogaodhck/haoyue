using Haoyue.Runtime.Agents;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// 文件驱动的子智能体目录：frontmatter 解析、三层优先级、保存/重命名/删除。
/// 委派链路的实际行为（模型覆盖、工具白名单）由 AgentDelegationTests 覆盖。
/// </summary>
public sealed class SubAgentCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"haoyue-subagents-{Guid.NewGuid():N}");
    private readonly string _userDir;
    private readonly string _projectRoot;

    public SubAgentCatalogTests()
    {
        _userDir = Path.Combine(_dir, "user-agents");
        _projectRoot = Path.Combine(_dir, "project");
        Directory.CreateDirectory(_userDir);
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void Discover_ParsesFrontMatterSequenceToolsAndPromptBody()
    {
        WriteFile(_userDir, "auditor.md", """
            ---
            name: "auditor"
            description: "审计改动"
            model: "openai/strong"
            tools:
              - "read_file"
              - "grep"
            ---

            你负责审计改动。
            """);

        var entry = Assert.Single(new SubAgentCatalog(_userDir).Discover(null),
            e => e.Role.Id == "auditor");

        Assert.Equal(SubAgentScope.User, entry.Scope);
        Assert.Equal("审计改动", entry.Role.Description);
        Assert.Equal("openai/strong", entry.Role.Model);
        Assert.Equal(["read_file", "grep"], entry.Role.AllowedTools!);
        Assert.Equal("你负责审计改动。", entry.Role.Prompt);
        Assert.NotNull(entry.Path);
    }

    [Fact]
    public void Discover_AcceptsCommaSeparatedToolsString()
    {
        WriteFile(_userDir, "scout.md", """
            ---
            name: "scout"
            tools: read_file, grep; glob
            ---

            只读侦察。
            """);

        var entry = Assert.Single(new SubAgentCatalog(_userDir).Discover(null),
            e => e.Role.Id == "scout");
        Assert.Equal(["read_file", "grep", "glob"], entry.Role.AllowedTools!);
        Assert.Null(entry.Role.Model);
    }

    [Fact]
    public void Discover_ProjectOverridesUserOverridesBuiltin()
    {
        WriteFile(_userDir, "reviewer.md", """
            ---
            name: "reviewer"
            description: "用户级评审"
            ---

            用户级指令。
            """);
        var projectDir = SubAgentCatalog.ProjectAgentsDir(_projectRoot);
        Directory.CreateDirectory(projectDir);
        WriteFile(projectDir, "reviewer.md", """
            ---
            name: "reviewer"
            description: "项目级评审"
            ---

            项目级指令。
            """);
        WriteFile(projectDir, "archivist.md", """
            ---
            name: "archivist"
            description: "项目归档员"
            ---

            归档。
            """);

        var catalog = new SubAgentCatalog(_userDir);

        var merged = catalog.Discover(_projectRoot);
        var reviewer = Assert.Single(merged, e => e.Role.Id == "reviewer");
        Assert.Equal(SubAgentScope.Project, reviewer.Scope);
        Assert.Equal("项目级评审", reviewer.Role.Description);
        Assert.Equal("项目级指令。", reviewer.Role.Prompt);

        var projectOnly = Assert.Single(merged, e => e.Role.Id == "archivist");
        Assert.Equal(SubAgentScope.Project, projectOnly.Scope);

        // 全局会话没有项目根：用户级覆盖内置，项目级条目不出现。
        var global = catalog.Discover(null);
        Assert.Equal(SubAgentScope.User,
            Assert.Single(global, e => e.Role.Id == "reviewer").Scope);
        Assert.DoesNotContain(global, e => e.Role.Id == "archivist");

        // 未被覆盖的内置仍然在，且内置排在最前面。
        Assert.Equal(SubAgentScope.Builtin, merged[0].Scope);
        Assert.Contains(merged, e => e.Role.Id == "implementer");
        Assert.Contains(merged, e => e.Role.Id == "researcher");
    }

    [Fact]
    public void Discover_FileWithoutFrontMatter_UsesFileNameAndWholeBody()
    {
        WriteFile(_userDir, "plain.md", "没有 frontmatter 的指令正文。");

        var entry = Assert.Single(new SubAgentCatalog(_userDir).Discover(null),
            e => e.Role.Id == "plain");
        Assert.Equal("没有 frontmatter 的指令正文。", entry.Role.Prompt);
        Assert.Null(entry.Role.AllowedTools);
    }

    [Fact]
    public void Discover_RereadsDisk_HotReload()
    {
        WriteFile(_userDir, "draft.md", "v1");
        var catalog = new SubAgentCatalog(_userDir);
        Assert.Equal("v1", catalog.Resolve(null, "draft")!.Prompt);

        WriteFile(_userDir, "draft.md", "v2");
        Assert.Equal("v2", catalog.Resolve(null, "draft")!.Prompt);
    }

    [Fact]
    public void Save_WritesCanonicalMarkdown_AndRenameRemovesOldFile()
    {
        var catalog = new SubAgentCatalog(_userDir);
        var path = catalog.Save(null, SubAgentScope.User, new SubAgentDraft(
            "临时专家", "先试一版", "openai/strong", ["read_file", "read_file", "grep"], "执行指令"));

        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("name: \"临时专家\"", text);
        Assert.Contains("description: \"先试一版\"", text);
        Assert.Contains("model: \"openai/strong\"", text);
        Assert.Contains("  - \"read_file\"", text);
        Assert.Contains("  - \"grep\"", text);
        Assert.DoesNotContain("read_file\"\n  - \"read_file", text);
        Assert.Contains("执行指令", text);

        var renamed = catalog.Save(null, SubAgentScope.User, new SubAgentDraft(
            "正式专家", null, null, null, "最终指令"), previousName: "临时专家");

        Assert.True(File.Exists(renamed));
        Assert.False(File.Exists(path));
        var ids = catalog.Names(null);
        Assert.Contains("正式专家", ids);
        Assert.DoesNotContain("临时专家", ids);
    }

    [Fact]
    public void Save_ProjectScope_LandsInWorkspaceAndOverridesUser()
    {
        var catalog = new SubAgentCatalog(_userDir);
        catalog.Save(null, SubAgentScope.User, new SubAgentDraft("helper", null, null, null, "用户指令"));

        var path = catalog.Save(_projectRoot, SubAgentScope.Project,
            new SubAgentDraft("helper", null, null, null, "项目指令"));

        Assert.StartsWith(SubAgentCatalog.ProjectAgentsDir(_projectRoot), path);
        Assert.Equal("项目指令", catalog.Resolve(_projectRoot, "helper")!.Prompt);
        Assert.Equal("用户指令", catalog.Resolve(null, "helper")!.Prompt);
    }

    [Fact]
    public void Save_RejectsInvalidDraft()
    {
        var catalog = new SubAgentCatalog(_userDir);

        Assert.Throws<ArgumentException>(() =>
            catalog.Save(null, SubAgentScope.User, new SubAgentDraft(" ", null, null, null, "指令")));
        Assert.Throws<ArgumentException>(() =>
            catalog.Save(null, SubAgentScope.User, new SubAgentDraft(new string('x', 65), null, null, null, "指令")));
        Assert.Throws<ArgumentException>(() =>
            catalog.Save(null, SubAgentScope.User, new SubAgentDraft("ok", null, null, null, "  ")));
        Assert.Throws<InvalidOperationException>(() =>
            catalog.Save(null, SubAgentScope.Builtin, new SubAgentDraft("ok", null, null, null, "指令")));
        Assert.Throws<InvalidOperationException>(() =>
            catalog.Save(null, SubAgentScope.Project, new SubAgentDraft("ok", null, null, null, "指令")));
    }

    [Fact]
    public void Delete_RemovesCustomFile_AndReportsMissing()
    {
        var catalog = new SubAgentCatalog(_userDir);
        catalog.Save(null, SubAgentScope.User, new SubAgentDraft("tmp", null, null, null, "指令"));

        Assert.True(catalog.Delete(null, SubAgentScope.User, "tmp"));
        Assert.False(catalog.Delete(null, SubAgentScope.User, "tmp"));
        Assert.Null(catalog.Resolve(null, "tmp"));
    }

    [Fact]
    public void Discover_IgnoresMalformedYaml()
    {
        WriteFile(_userDir, "broken.md", """
            ---
            name: [unterminated
            ---

            正文
            """);
        WriteFile(_userDir, "fine.md", """
            ---
            name: "fine"
            ---

            可用
            """);

        var catalog = new SubAgentCatalog(_userDir);
        Assert.Null(catalog.Resolve(null, "broken"));
        Assert.NotNull(catalog.Resolve(null, "fine"));
    }

    private static void WriteFile(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), content);
    }
}
