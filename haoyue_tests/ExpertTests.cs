using Haoyue.Runtime.Experts;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>
/// Expert persona binding: catalog resolution, session-header persistence
/// (create/update/clear/fork) and the list/search reader contract that shares
/// the expert_id ordinal with ReadHeader.
/// </summary>
public class ExpertTests : IDisposable
{
    private readonly string _root;
    private readonly SessionStore _store;
    private readonly WorkspaceInfo _workspace;

    public ExpertTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "haoyue-expert-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _store = new SessionStore(Path.Combine(_root, "haoyue.db"));
        _workspace = new WorkspaceInfo { Root = _root, ProjectKinds = [] };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void Catalog_ExposesTenPersonasWithUniqueIds()
    {
        Assert.Equal(10, ExpertCatalog.Entries.Count);
        Assert.Equal(ExpertCatalog.Entries.Count,
            ExpertCatalog.Entries.Select(entry => entry.Id).Distinct().Count());
        Assert.All(ExpertCatalog.Entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Name));
            Assert.False(string.IsNullOrWhiteSpace(entry.Prompt));
        });
    }

    [Fact]
    public void Find_ResolvesTrimmedId_AndRejectsUnknownOrBlank()
    {
        var known = ExpertCatalog.Entries[0];
        Assert.NotNull(ExpertCatalog.Find(known.Id));
        Assert.NotNull(ExpertCatalog.Find("  " + known.Id + "  "));
        Assert.Null(ExpertCatalog.Find("no-such-expert"));
        Assert.Null(ExpertCatalog.Find(""));
        Assert.Null(ExpertCatalog.Find("   "));
        Assert.Null(ExpertCatalog.Find(null));
    }

    [Fact]
    public void Create_WithExpertId_PersistsAcrossLoadListAndSearch()
    {
        var expert = ExpertCatalog.Entries[0];
        var session = _store.Create(_workspace, expertId: expert.Id);

        var loaded = _store.Load(_workspace, session.Header.Id)!;
        Assert.Equal(expert.Id, loaded.Header.ExpertId);

        var listed = _store.List(_workspace).Single();
        Assert.Equal(expert.Id, listed.ExpertId);

        _store.Append(loaded, ChatMessage.User("expert probe"));
        var hit = _store.Search(_workspace, "expert probe").Single();
        Assert.Equal(expert.Id, hit.Header.ExpertId);

        // Sessions without a binding stay null.
        var plain = _store.Create(_workspace);
        Assert.Null(_store.Load(_workspace, plain.Header.Id)!.Header.ExpertId);
    }

    [Fact]
    public void UpdateMetadata_BindsAndUnbindsExpert()
    {
        var expert = ExpertCatalog.Entries[1];
        var session = _store.Create(_workspace);

        var bound = _store.UpdateMetadata(_workspace, session.Header.Id, expertId: expert.Id);
        Assert.Equal(expert.Id, bound.ExpertId);
        Assert.Equal(expert.Id, _store.Load(_workspace, session.Header.Id)!.Header.ExpertId);

        // "" is the explicit unbind sentinel (null leaves the value untouched).
        var unbound = _store.UpdateMetadata(_workspace, session.Header.Id, expertId: "");
        Assert.Null(unbound.ExpertId);
        Assert.Null(_store.Load(_workspace, session.Header.Id)!.Header.ExpertId);
    }

    [Fact]
    public void Fork_InheritsExpertBinding()
    {
        var expert = ExpertCatalog.Entries[2];
        var session = _store.Create(_workspace, expertId: expert.Id);
        _store.Append(session, ChatMessage.User("fork base"));

        var forked = _store.Fork(_workspace, session.Header.Id, keepMessageCount: 1);
        Assert.Equal(expert.Id, forked.Header.ExpertId);
    }
}
