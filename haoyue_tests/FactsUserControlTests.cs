using Haoyue.Runtime.Data;

namespace Haoyue.Tests;

public sealed class FactsUserControlTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "haoyue-facts-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _otherRoot = Path.Combine(Path.GetTempPath(), "haoyue-facts-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FactsStore _store = new();

    public void Dispose()
    {
        foreach (var dir in new[] { _root, _otherRoot })
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    [Fact]
    public void ListAll_IncludesAutoFacts_WithSourceAndConfirmation()
    {
        var result = _store.AddDetailed(_root, "项目使用 .NET 10", "stack", "agent", 0.8, sessionId: "s-1");

        Assert.Equal("auto", result.Entry.Kind);
        Assert.False(result.Entry.Confirmed);
        Assert.Equal("s-1", result.Entry.SourceSessionId);

        var listed = Assert.Single(_store.ListAllForRoot(_root));
        Assert.Equal("项目使用 .NET 10", listed.Content);
        Assert.Equal("agent", listed.Source);
    }

    [Fact]
    public void Update_EditsContentAndConfirmsAutoFact()
    {
        var entry = _store.Add(_root, "端口是 8080", "http", "agent", 0.6);

        var updated = _store.UpdateForRoot(_root, entry.Id, content: "端口是 9090", confirmed: true);

        Assert.NotNull(updated);
        Assert.Equal("端口是 9090", updated!.Entry.Content);
        Assert.True(updated.Entry.Confirmed);
        Assert.Equal("端口是 9090", Assert.Single(_store.ListAllForRoot(_root)).Content);
    }

    [Fact]
    public void Pin_MakesFactMandatoryInInjection()
    {
        var pinned = _store.AddDetailed(_root, "部署前必须跑全量测试", "流程", "agent", 0.5).Entry;
        _store.AddDetailed(_root, "数据库使用 SQLite", "stack", "agent", 0.9);
        _store.PinForRoot(_root, pinned.Id, true);

        var selection = _store.SelectForInjection(_root, "今天天气怎么样", 1);

        Assert.Contains(selection.Facts, fact => fact.Id == pinned.Id && fact.Pinned);
    }

    [Fact]
    public void AddDetailed_ReportsSupersededFact_InsteadOfSilentOverwrite()
    {
        var old = _store.AddDetailed(_root, "default branch is master", "git", "agent", 0.7).Entry;

        var result = _store.AddDetailed(_root, "default branch is main", "git", "agent", 0.9);

        Assert.Single(result.Superseded);
        Assert.Equal(old.Id, result.Superseded[0].Id);
        var entries = _store.ListAllForRoot(_root);
        Assert.Equal(result.Entry.Id, entries.Single(fact => fact.Id == old.Id).SupersededBy);
    }

    [Fact]
    public void Restore_UndoesConsolidation_AndMarksReplacement()
    {
        var old = _store.AddDetailed(_root, "default branch is master", "git", "agent", 0.7).Entry;
        var winner = _store.AddDetailed(_root, "default branch is main", "git", "agent", 0.9).Entry;

        var restored = _store.RestoreForRoot(_root, old.Id);

        Assert.NotNull(restored);
        Assert.Null(restored!.SupersededBy);
        var entries = _store.ListAllForRoot(_root);
        Assert.Equal(old.Id, entries.Single(fact => fact.Id == winner.Id).SupersededBy);
    }

    [Fact]
    public void Delete_RemovesFact()
    {
        var entry = _store.Add(_root, "临时事实", "tmp", "user", 1.0);

        Assert.True(_store.ForgetForRoot(_root, entry.Id));
        Assert.Empty(_store.ListAllForRoot(_root));
    }

    [Fact]
    public void Facts_AreIsolatedPerWorkspace()
    {
        _store.Add(_root, "仅项目 A 可见", "scope", "user", 1.0);

        Assert.Empty(_store.ListAllForRoot(_otherRoot));
        var selection = _store.SelectForInjection(_otherRoot, "仅项目 A 可见", 5);
        Assert.Empty(selection.Facts);
    }

    [Fact]
    public void UserFacts_AreManualAndConfirmed()
    {
        var result = _store.AddDetailed(_root, "用户明确要求使用中文", "pref", "user", 1.0);

        Assert.Equal("manual", result.Entry.Kind);
        Assert.True(result.Entry.Confirmed);
    }
}
