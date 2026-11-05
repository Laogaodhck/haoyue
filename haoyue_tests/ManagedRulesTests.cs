using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

public sealed class ManagedRulesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "haoyue-managed-rules-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _otherRoot = Path.Combine(Path.GetTempPath(), "haoyue-managed-rules-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _globalDir = Path.Combine(Path.GetTempPath(), "haoyue-global-rules-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        foreach (var dir in new[] { _root, _otherRoot, _globalDir })
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    private WorkspaceInfo Workspace(string? root = null) => new()
    {
        Root = root ?? _root,
        ProjectKinds = [],
        Config = new WorkspaceConfig(),
        IsGlobal = false,
    };

    [Fact]
    public void Save_And_List_RoundTripsWorkspaceRule()
    {
        var saved = ManagedRulesStore.Save(Workspace(), new ManagedRuleEntry
        {
            Title = "提交规范",
            Content = "提交信息使用中文",
            Priority = 80,
        });

        Assert.StartsWith("rule-", saved.Id);
        Assert.Equal("workspace", saved.Scope);
        Assert.True(File.Exists(Path.Combine(_root, ".haoyue", "memory", "rules.json")));

        var listed = ManagedRulesStore.List(Workspace());
        var rule = Assert.Single(listed);
        Assert.Equal("提交规范", rule.Title);
        Assert.Equal(80, rule.Priority);
        Assert.True(rule.Enabled);
    }

    [Fact]
    public void Save_UpdatesExistingById_And_PersistsEnabled()
    {
        var saved = ManagedRulesStore.Save(Workspace(), new ManagedRuleEntry
        {
            Title = "命名", Content = "用 camelCase",
        });

        ManagedRulesStore.Save(Workspace(), new ManagedRuleEntry
        {
            Id = saved.Id, Title = "命名", Content = "用 PascalCase", Enabled = false, Priority = 10,
        });

        var rule = Assert.Single(ManagedRulesStore.List(Workspace()));
        Assert.Equal("用 PascalCase", rule.Content);
        Assert.False(rule.Enabled);
        Assert.Empty(ManagedRulesStore.Effective(Workspace()));
    }

    [Fact]
    public void Effective_OrdersByPriorityDescending()
    {
        ManagedRulesStore.Save(Workspace(), new ManagedRuleEntry { Title = "低", Content = "a", Priority = 10 });
        ManagedRulesStore.Save(Workspace(), new ManagedRuleEntry { Title = "高", Content = "b", Priority = 90 });
        ManagedRulesStore.Save(Workspace(), new ManagedRuleEntry { Title = "中", Content = "c", Priority = 50 });

        var effective = ManagedRulesStore.Effective(Workspace());

        Assert.Equal(["高", "中", "低"], effective.Select(rule => rule.Title));
    }

    [Fact]
    public void Conflicts_DetectSameTitleAcrossScopes_AndWorkspaceWins()
    {
        ManagedRulesStore.Save(Workspace(), new ManagedRuleEntry
        {
            Title = "代码风格", Content = "全局版本", Scope = "global",
        }, _globalDir);
        ManagedRulesStore.Save(Workspace(), new ManagedRuleEntry
        {
            Title = "代码风格", Content = "项目版本", Scope = "workspace",
        });

        var conflict = Assert.Single(ManagedRulesStore.Conflicts(Workspace(), _globalDir));
        Assert.Equal("代码风格", conflict.Title);
        Assert.Equal("workspace", conflict.Winner);

        var rendered = ManagedRulesStore.RenderEffectiveText(Workspace(), _globalDir);
        Assert.Contains("项目版本", rendered);
        Assert.Contains("全局版本", rendered);
        Assert.Contains("### 代码风格（global", rendered);
        Assert.Contains("### 代码风格（workspace", rendered);
    }

    [Fact]
    public void Effective_ReflectsSaveImmediately_HotReload()
    {
        Assert.Equal("", ManagedRulesStore.RenderEffectiveText(Workspace(), _globalDir));

        ManagedRulesStore.Save(Workspace(), new ManagedRuleEntry
        {
            Title = "禁止直推主分支", Content = "先开分支再提 PR",
        });

        var rendered = ManagedRulesStore.RenderEffectiveText(Workspace(), _globalDir);
        Assert.Contains("禁止直推主分支", rendered);
        Assert.Contains("先开分支再提 PR", rendered);
    }

    [Fact]
    public void Delete_RemovesOnlyTargetRule()
    {
        var keep = ManagedRulesStore.Save(Workspace(), new ManagedRuleEntry { Title = "保留", Content = "x" });
        var drop = ManagedRulesStore.Save(Workspace(), new ManagedRuleEntry { Title = "删除", Content = "y" });

        Assert.True(ManagedRulesStore.Delete(Workspace(), drop.Id, "workspace"));
        Assert.False(ManagedRulesStore.Delete(Workspace(), drop.Id, "workspace"));
        var rule = Assert.Single(ManagedRulesStore.List(Workspace()));
        Assert.Equal(keep.Id, rule.Id);
    }

    [Fact]
    public void Rules_AreIsolatedPerWorkspace()
    {
        ManagedRulesStore.Save(Workspace(), new ManagedRuleEntry { Title = "项目A", Content = "only-a" });

        Assert.Empty(ManagedRulesStore.List(Workspace(_otherRoot)));
        Assert.DoesNotContain("only-a", ManagedRulesStore.RenderEffectiveText(Workspace(_otherRoot)));
    }

    [Fact]
    public void Save_RejectsEmptyTitleOrOversizedContent()
    {
        Assert.Throws<ArgumentException>(() => ManagedRulesStore.Save(Workspace(), new ManagedRuleEntry
        {
            Title = " ", Content = "x",
        }));
        Assert.Throws<ArgumentException>(() => ManagedRulesStore.Save(Workspace(), new ManagedRuleEntry
        {
            Title = "a", Content = new string('x', ManagedRulesStore.MaxContentChars + 1),
        }));
    }

    private void SeedConflict()
    {
        ManagedRulesStore.Save(Workspace(), new ManagedRuleEntry
        {
            Title = "代码风格", Content = "全局版本", Scope = "global",
        }, _globalDir);
        ManagedRulesStore.Save(Workspace(), new ManagedRuleEntry
        {
            Title = "代码风格", Content = "项目版本", Scope = "workspace",
        });
        Assert.Single(ManagedRulesStore.Conflicts(Workspace(), _globalDir));
    }

    [Fact]
    public void Resolve_WorkspaceWins_DisablesGlobalAndRecordsActor()
    {
        SeedConflict();

        var resolved = ManagedRulesStore.Resolve(Workspace(), "代码风格", "workspace", "tester", _globalDir);

        Assert.Equal("workspace", resolved.Winner);
        Assert.Empty(ManagedRulesStore.Conflicts(Workspace(), _globalDir));
        var global = ManagedRulesStore.List(Workspace(), _globalDir).Single(rule => rule.Scope == "global");
        Assert.False(global.Enabled);
        var hit = Assert.Single(ManagedRulesStore.ListHits(Workspace()));
        Assert.Equal("resolve:workspace", hit.Action);
        Assert.Equal("tester", hit.Actor);
    }

    [Fact]
    public void Resolve_GlobalWins_DisablesWorkspaceRule()
    {
        SeedConflict();

        var resolved = ManagedRulesStore.Resolve(Workspace(), "代码风格", "global", "tester", _globalDir);

        Assert.Equal("global", resolved.Winner);
        Assert.Empty(ManagedRulesStore.Conflicts(Workspace(), _globalDir));
        var local = ManagedRulesStore.List(Workspace(), _globalDir).Single(rule => rule.Scope == "workspace");
        Assert.False(local.Enabled);
        Assert.Equal("resolve:global", Assert.Single(ManagedRulesStore.ListHits(Workspace())).Action);
    }

    [Fact]
    public void Resolve_Merge_CombinesContentIntoWorkspaceRule()
    {
        SeedConflict();

        var resolved = ManagedRulesStore.Resolve(Workspace(), "代码风格", "merge", "tester", _globalDir);

        Assert.Equal("workspace", resolved.Winner);
        Assert.Empty(ManagedRulesStore.Conflicts(Workspace(), _globalDir));
        var local = ManagedRulesStore.List(Workspace(), _globalDir).Single(rule => rule.Scope == "workspace");
        Assert.True(local.Enabled);
        Assert.Contains("项目版本", local.Content);
        Assert.Contains("全局版本", local.Content);
        var global = ManagedRulesStore.List(Workspace(), _globalDir).Single(rule => rule.Scope == "global");
        Assert.False(global.Enabled);
    }

    [Fact]
    public void Resolve_RejectsUnknownConflictAndBadMode()
    {
        SeedConflict();

        Assert.Throws<InvalidOperationException>(() =>
            ManagedRulesStore.Resolve(Workspace(), "不存在", "workspace", "tester", _globalDir));
        Assert.Throws<ArgumentException>(() =>
            ManagedRulesStore.Resolve(Workspace(), "代码风格", "bogus", "tester", _globalDir));
    }

    [Fact]
    public void RecordHits_DeduplicatesAndReadsBack()
    {
        var rule = ManagedRulesStore.Save(Workspace(), new ManagedRuleEntry { Title = "命中", Content = "x" });

        ManagedRulesStore.RecordHits(Workspace(), [rule.Id], "session-1");
        ManagedRulesStore.RecordHits(Workspace(), [rule.Id], "session-1");

        var hit = Assert.Single(ManagedRulesStore.ListHits(Workspace()));
        Assert.Equal(rule.Id, hit.RuleId);
        Assert.Equal("session-1", hit.SessionId);
    }
}
