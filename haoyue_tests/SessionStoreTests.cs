using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>
/// Session full-text search: Chinese must match through the \uXXXX escaping used by
/// the JSON payload encoder, ASCII matches raw, titles match unescaped, archived
/// sessions stay hidden by default, and results rank by hit count then recency.
/// </summary>
public class SessionStoreTests : IDisposable
{
    private readonly string _root;
    private readonly SessionStore _store;
    private readonly WorkspaceInfo _workspace;
    private readonly string _dbPath;

    public SessionStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "haoyue-ss-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "haoyue.db");
        _store = new SessionStore(_dbPath);
        _workspace = new WorkspaceInfo { Root = _root, ProjectKinds = [] };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string NewSessionWithTitle(string? title = null)
    {
        var session = _store.Create(_workspace);
        if (title is not null)
            _store.UpdateMetadata(_workspace, session.Header.Id, title: title);
        return session.Header.Id;
    }

    [Fact]
    public void Search_FindsChineseThroughEscapedPayload()
    {
        var id = NewSessionWithTitle();
        _store.Append(_store.Load(_workspace, id)!, ChatMessage.User("麒麟踏雪 quasiflux 检索演练"));

        var hits = _store.Search(_workspace, "麒麟踏雪");

        var hit = Assert.Single(hits);
        Assert.Equal(id, hit.Header.Id);
        Assert.True(hit.MatchCount >= 1);
    }

    [Fact]
    public void Search_FindsAsciiAndTitleMatches()
    {
        var id = NewSessionWithTitle("雪麒麟计划");
        _store.Append(_store.Load(_workspace, id)!, ChatMessage.User("quasiflux token accounting"));

        var byBody = _store.Search(_workspace, "quasiflux");
        var bodyHit = Assert.Single(byBody);
        Assert.Equal(id, bodyHit.Header.Id);

        // Title match works with raw (unescaped) text and counts as one hit.
        var byTitle = _store.Search(_workspace, "雪麒麟");
        var titleHit = Assert.Single(byTitle);
        Assert.Equal(id, titleHit.Header.Id);
        Assert.True(titleHit.MatchCount >= 1);
    }

    [Fact]
    public void Search_MissAndBlankQueries_ReturnNothing()
    {
        var id = NewSessionWithTitle();
        _store.Append(_store.Load(_workspace, id)!, ChatMessage.User("普通内容"));

        Assert.Empty(_store.Search(_workspace, "absent-token-xyz"));
        Assert.Empty(_store.Search(_workspace, ""));
        Assert.Empty(_store.Search(_workspace, "   "));
        // LIKE wildcards must be matched literally, not as patterns.
        Assert.Empty(_store.Search(_workspace, "%普通%"));
    }

    [Fact]
    public void Search_ExcludesArchivedByDefault_AndIncludesOnDemand()
    {
        var id = NewSessionWithTitle();
        _store.Append(_store.Load(_workspace, id)!, ChatMessage.User("归档检索目标 quasiflux"));
        _store.UpdateMetadata(_workspace, id, archived: true);

        Assert.Empty(_store.Search(_workspace, "quasiflux"));
        var archived = _store.Search(_workspace, "quasiflux", includeArchived: true);
        Assert.Single(archived);
    }

    [Fact]
    public void Search_RanksByMatchCount_ThenRecency()    {
        var richer = NewSessionWithTitle();
        var poorer = NewSessionWithTitle();
        // matchCount counts matching *messages*: two for `richer`, one for `poorer`.
        _store.Append(_store.Load(_workspace, richer)!, ChatMessage.User("重复词 第一条"));
        _store.Append(_store.Load(_workspace, richer)!, ChatMessage.Assistant("重复词 第二条"));
        _store.Append(_store.Load(_workspace, poorer)!, ChatMessage.User("重复词 只此一条"));

        var hits = _store.Search(_workspace, "重复词");
        Assert.Equal(2, hits.Count);
        Assert.Equal(richer, hits[0].Header.Id); // more matching messages first
        Assert.True(hits[0].MatchCount > hits[1].MatchCount);
    }

    [Fact]
    public void Search_IsScopedToWorkspace()
    {
        var otherRoot = Path.Combine(Path.GetTempPath(), "haoyue-ss-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(otherRoot);
        try
        {
            var otherWorkspace = new WorkspaceInfo { Root = otherRoot, ProjectKinds = [] };
            var otherStore = new SessionStore(_dbPath); // same database, different scope
            var otherSession = otherStore.Create(otherWorkspace);
            otherStore.Append(otherStore.Load(otherWorkspace, otherSession.Header.Id)!, ChatMessage.User("跨工作区隔离目标词"));
            otherStore.Append(otherStore.Load(otherWorkspace, otherSession.Header.Id)!, ChatMessage.User("补一条历史"));

            var id = NewSessionWithTitle();
            _store.Append(_store.Load(_workspace, id)!, ChatMessage.User("本工作区目标词"));

            Assert.Empty(_store.Search(otherWorkspace, "本工作区目标词"));
            var hit = Assert.Single(_store.Search(_workspace, "本工作区目标词"));
            Assert.Equal(id, hit.Header.Id);
        }
        finally
        {
            try { Directory.Delete(otherRoot, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void SearchAll_FindsHitsAcrossWorkspaces_AndStillRespectsArchived()
    {
        var otherRoot = Path.Combine(Path.GetTempPath(), "haoyue-ss-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(otherRoot);
        try
        {
            var otherWorkspace = new WorkspaceInfo { Root = otherRoot, ProjectKinds = [] };
            var otherStore = new SessionStore(_dbPath); // same database, different scope
            var otherSession = otherStore.Create(otherWorkspace);
            otherStore.Append(otherStore.Load(otherWorkspace, otherSession.Header.Id)!, ChatMessage.User("跨工作区全局检索词"));

            var local = NewSessionWithTitle();
            _store.Append(_store.Load(_workspace, local)!, ChatMessage.User("跨工作区全局检索词 本区"));

            var hits = _store.SearchAll("跨工作区全局检索词");
            Assert.Equal(2, hits.Count);
            Assert.Contains(hits, hit => hit.Header.Id == otherSession.Header.Id);
            Assert.Contains(hits, hit => hit.Header.Id == local);
            // 命中的 workspace 字段必须可映射回项目路径（桌面据此定位项目）。
            Assert.Contains(hits, hit => hit.Header.Workspace == otherRoot);

            _store.UpdateMetadata(_workspace, local, archived: true);
            Assert.Single(_store.SearchAll("跨工作区全局检索词"));
            Assert.Equal(2, _store.SearchAll("跨工作区全局检索词", includeArchived: true).Count);
            Assert.Empty(_store.SearchAll(""));
        }
        finally
        {
            try { Directory.Delete(otherRoot, recursive: true); }
            catch (IOException) { }
        }
    }

    // ------------------------------------------------- L2: FTS5 index synchronization

    [Fact]
    public void Search_FindsMessageBody_EvenWhenTitleMissing()
    {
        // 消息体命中必须来自 FTS/LIKE 对 payload 的检索，而不是首条消息成为标题的副作用：
        // 先用一条普通消息占住标题，再追加含目标词的 assistant 消息。
        var id = NewSessionWithTitle();
        var session = _store.Load(_workspace, id)!;
        _store.Append(session, ChatMessage.User("开场白"));
        _store.Append(session, ChatMessage.Assistant("唯一标识词 ploverquark 在这里"));

        var hit = Assert.Single(_store.Search(_workspace, "ploverquark"));
        Assert.Equal(id, hit.Header.Id);
        Assert.True(hit.MatchCount >= 1);
    }

    [Fact]
    public void Search_ReflectsReplaceHistory_NotDeletedContent()
    {
        // 先设显式标题：否则首条用户消息会成为标题，标题匹配会污染断言。
        var id = NewSessionWithTitle("压缩演练");
        var session = _store.Load(_workspace, id)!;
        _store.Append(session, ChatMessage.User("旧内容被压缩替换词"));

        // 压缩：历史替换为新文本，旧内容必须从搜索结果消失。
        _store.ReplaceHistory(session, [ChatMessage.User("新摘要 只有这一句")]);
        Assert.Empty(_store.Search(_workspace, "被压缩替换词"));
        var hit = Assert.Single(_store.Search(_workspace, "新摘要"));
        Assert.Equal(id, hit.Header.Id);
    }

    [Fact]
    public void Search_ReflectsTruncate_AndSessionDelete()
    {
        var id = NewSessionWithTitle("截断演练");
        var session = _store.Load(_workspace, id)!;
        _store.Append(session, ChatMessage.User("保留段"));
        _store.Append(session, ChatMessage.User("将被截断的目标词 ploverquark"));

        // 截断保 1 条：历史尾部（含目标词）离开后不再命中（AFTER DELETE 触发器同步索引）。
        _store.Truncate(_workspace, id, 1);
        Assert.Empty(_store.Search(_workspace, "ploverquark"));
        Assert.Single(_store.Search(_workspace, "保留段"));

        // 会话删除级联清空其消息，搜索随之无结果。
        _store.Delete(_workspace, id);
        Assert.Empty(_store.Search(_workspace, "保留段"));
    }
}
