using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

public sealed class WorkspaceRulesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "haoyue-rules-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private WorkspaceInfo Workspace(bool isGlobal = false) => new()
    {
        Root = _root,
        ProjectKinds = [],
        Config = new WorkspaceConfig(),
        IsGlobal = isGlobal,
    };

    [Fact]
    public void Save_DefaultPath_CreatesRootAgentsMd_AndListReturnsIt()
    {
        var saved = WorkspaceRules.Save(Workspace(), null, "# Rules\n- be precise");

        Assert.Equal("AGENTS.md", saved);
        Assert.True(File.Exists(Path.Combine(_root, "AGENTS.md")));

        var rules = WorkspaceRules.List(Workspace());
        var rule = Assert.Single(rules);
        Assert.Equal("AGENTS.md", rule.Path);
        Assert.True(rule.IsRoot);
        Assert.Equal("# Rules\n- be precise", rule.Content);
    }

    [Fact]
    public void Save_NestedPath_CreatesDirectory_AndListsBothLevels()
    {
        WorkspaceRules.Save(Workspace(), null, "root rules");
        WorkspaceRules.Save(Workspace(), "docs/AGENTS.md", "docs rules");

        var rules = WorkspaceRules.List(Workspace());
        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, r => r.Path == "docs/AGENTS.md" && !r.IsRoot && r.Content == "docs rules");
    }

    [Fact]
    public void Save_RejectsNonAgentsMdNames()
    {
        Assert.Throws<ArgumentException>(() => WorkspaceRules.Save(Workspace(), "README.md", "x"));
        // The exact root AGENTS.md is always allowed, nested AGENTS.md too — but the
        // file name itself must end with AGENTS.md.
        Assert.Throws<ArgumentException>(() => WorkspaceRules.Save(Workspace(), "docs/rules.txt", "x"));
    }

    [Fact]
    public void Save_RejectsEscapingPath()
    {
        Assert.Throws<ArgumentException>(() => WorkspaceRules.Save(Workspace(), "../evil/AGENTS.md", "x"));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "evil", "AGENTS.md")));
    }

    [Fact]
    public void Save_RejectsGlobalWorkspace()
    {
        Assert.Throws<InvalidOperationException>(() => WorkspaceRules.Save(Workspace(isGlobal: true), null, "x"));
        Assert.Empty(WorkspaceRules.List(Workspace(isGlobal: true)));
    }

    [Fact]
    public void List_SkipsMissingFiles()
    {
        var rules = WorkspaceRules.List(Workspace());
        Assert.Empty(rules);
    }

    [Fact]
    public void Delete_RemovesFile_AndCleansUpEmptyDirectory()
    {
        WorkspaceRules.Save(Workspace(), null, "root rules");
        WorkspaceRules.Save(Workspace(), "docs/guides/AGENTS.md", "nested rules");

        var deleted = WorkspaceRules.Delete(Workspace(), "docs/guides/AGENTS.md");

        Assert.Equal("docs/guides/AGENTS.md", deleted);
        Assert.False(File.Exists(Path.Combine(_root, "docs", "guides", "AGENTS.md")));
        Assert.False(Directory.Exists(Path.Combine(_root, "docs", "guides")));
        Assert.Single(WorkspaceRules.List(Workspace()));
    }

    [Fact]
    public void Delete_RootFile_AndMissingFileAreNotErrors()
    {
        WorkspaceRules.Save(Workspace(), null, "root rules");

        Assert.Equal("AGENTS.md", WorkspaceRules.Delete(Workspace(), null));
        Assert.False(File.Exists(Path.Combine(_root, "AGENTS.md")));
        // 根目录本身永不清理；缺失文件视为已删除。
        Assert.Equal("AGENTS.md", WorkspaceRules.Delete(Workspace(), "AGENTS.md"));
    }

    [Fact]
    public void Delete_AppliesSameValidationAsSave()
    {
        Assert.Throws<InvalidOperationException>(() => WorkspaceRules.Delete(Workspace(isGlobal: true), null));
        Assert.Throws<ArgumentException>(() => WorkspaceRules.Delete(Workspace(), "README.md"));
        Assert.Throws<ArgumentException>(() => WorkspaceRules.Delete(Workspace(), "../evil/AGENTS.md"));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "evil", "AGENTS.md")));
    }
}
