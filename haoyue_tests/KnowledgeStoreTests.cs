using System.Text;
using Haoyue.Runtime.Data;

namespace Haoyue.Tests;

/// <summary>
/// KnowledgeStore store-level behaviors that do not go through the daemon IPC:
/// save-by-id in-place updates (rename without forking), exact-tag filtering
/// (full-width separators included), tag aggregation and tag-restricted search.
/// Pure file-backed state, safe to run in parallel with everything else.
/// </summary>
public class KnowledgeStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-kb-store-" + Guid.NewGuid().ToString("N"));
    private readonly KnowledgeStore _store;

    public KnowledgeStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new KnowledgeStore(new HaoyueDatabase(Path.Combine(_dir, "store.db")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Save_ById_RenamesInPlace_AndKeepsCreatedAt()
    {
        var created = _store.Save("w", "旧标题", "内容A", "build");
        Assert.True(created.Created);

        var updated = _store.Save("w", "新标题", "内容B", "build,测试", created.Entry.Id);
        Assert.False(updated.Created);
        Assert.Equal(created.Entry.Id, updated.Entry.Id);
        Assert.Equal("新标题", updated.Entry.Title);
        Assert.Equal(created.Entry.CreatedAt, updated.Entry.CreatedAt);

        var list = _store.List("w", 10);
        Assert.Single(list);
        Assert.Equal("新标题", list[0].Title);
        Assert.Equal("build,测试", list[0].Tags);
    }

    [Fact]
    public void Save_ById_MissingId_Throws()
    {
        Assert.Throws<KeyNotFoundException>(() => _store.Save("w", "标题", "内容", null, 999));
    }

    [Fact]
    public void ListByTag_MatchesExactTag_NotSubstring()
    {
        _store.Save("w", "A", "a", "build,前端");
        _store.Save("w", "B", "b", "buildtool");
        _store.Save("w", "C", "c", "构建，测试"); // full-width separator still splits

        var build = _store.ListByTag("w", "build", 100);
        var entry = Assert.Single(build);
        Assert.Equal("A", entry.Title);

        // Case-insensitive exact match: "buildtool" only matches itself, not "build".
        var buildTool = Assert.Single(_store.ListByTag("w", "BUILDTOOL", 100));
        Assert.Equal("B", buildTool.Title);

        var test = Assert.Single(_store.ListByTag("w", "测试", 100));
        Assert.Equal("C", test.Title);

        Assert.Empty(_store.ListByTag("w", "不存在的标签", 100));
    }

    [Fact]
    public void TagCounts_AggregatesAcrossEntries_AndSortsByCount()
    {
        _store.Save("w", "A", "a", "build,测试");
        _store.Save("w", "B", "b", "build");
        _store.Save("w", "C", "c", "build，测试");

        var counts = _store.TagCounts("w").Select(t => (t.Tag, t.Count)).ToArray();
        Assert.Equal([("build", 3), ("测试", 2)], counts);
    }

    [Fact]
    public void Search_TagFilter_RestrictsResults()
    {
        _store.Save("w", "构建命令", "pnpm build 构建桌面", "build");
        _store.Save("w", "构建文档", "构建流程说明", "docs");

        Assert.Equal(2, _store.Search("w", "构建", 10).Count);

        var filtered = _store.Search("w", "构建", 10, "build");
        var entry = Assert.Single(filtered);
        Assert.Equal("构建命令", entry.Title);
    }

    [Fact]
    public void Ingest_ImportFile_ChunksAndUpsertsOnReimport()
    {
        var scope = "w";
        var source = Path.Combine(_dir, "部署笔记.md");
        File.WriteAllText(source, new string('知', 7000), new UTF8Encoding(false));

        var (fileSource, entries) = KnowledgeIngest.ImportFile(_store, scope, source);
        Assert.Equal("部署笔记.md", fileSource.Title);
        Assert.Equal(2, entries);

        var saved = _store.List(scope, 10);
        Assert.Equal(2, saved.Count);
        Assert.Contains(saved, e => e.Title == "部署笔记.md · 第1/2部分");
        Assert.All(saved, e => Assert.Equal("导入,md", e.Tags));

        // 同文件重复导入：同题 upsert，不产生副本。
        var second = KnowledgeIngest.ImportFile(_store, scope, source);
        Assert.Equal(2, second.Entries);
        Assert.Equal(2, _store.List(scope, 10).Count);
    }

    [Fact]
    public void Ingest_ImportFile_RejectsMissingEmptyAndOversize()
    {
        Assert.Throws<KnowledgeImportException>(() => KnowledgeIngest.ImportFile(_store, "w", Path.Combine(_dir, "missing.md")));

        var empty = Path.Combine(_dir, "empty.txt");
        File.WriteAllText(empty, "   \n  ");
        Assert.Throws<KnowledgeImportException>(() => KnowledgeIngest.ImportFile(_store, "w", empty));
    }

    [Fact]
    public void Export_ToMarkdown_ContainsHeaderTagsAndSeparator()
    {
        var entries = new List<KnowledgeEntry>
        {
            new(1, "构建命令", "pnpm build", "build,前端", "2026-01-01T00:00:00.0000000Z", "2026-01-02T00:00:00.0000000Z"),
            new(2, "无标签", "内容", null, "2026-01-01T00:00:00.0000000Z", "2026-01-02T00:00:00.0000000Z"),
        };

        var markdown = KnowledgeExport.ToMarkdown("E:\\demo", entries);

        Assert.StartsWith("# 知识库导出", markdown);
        Assert.Contains("- 范围：E:\\demo", markdown);
        Assert.Contains("- 条目数：2", markdown);
        Assert.Contains("## 构建命令", markdown);
        Assert.Contains("> 标签：build,前端", markdown);
        Assert.Contains("pnpm build", markdown);
        Assert.Contains("## 无标签", markdown);
        Assert.Equal(2, markdown.Split("---").Length - 1);
    }

    [Fact]
    public void ListInfo_WithoutNotebookFilter_AttributesDefaultNotebook()
    {
        _store.Save("w", "构建命令", "pnpm build", "build");

        // notebookId 缺省回归：不筛选笔记本时必须返回全部条目。
        var infos = _store.ListInfo("w", 100);
        var info = Assert.Single(infos);
        Assert.Equal("构建命令", info.Entry.Title);
        Assert.Equal("默认笔记本", info.NotebookName);
        Assert.Null(info.SourceId);

        var defaultId = info.NotebookId;
        Assert.Single(_store.ListInfo("w", 100, defaultId));
        Assert.Empty(_store.ListInfo("w", 100, defaultId + 1));

        var tagged = _store.ListInfo("w", 100, null, "build");
        Assert.Single(tagged);
        Assert.Empty(_store.ListInfo("w", 100, null, "不存在的标签"));
    }

    [Fact]
    public void Notebooks_SaveListDelete_WithDefaultProtection()
    {
        _store.Save("w", "沉淀", "agent 落入默认笔记本", null);
        var defaultNotebook = Assert.Single(_store.ListNotebooks("w"));
        Assert.True(defaultNotebook.IsDefault);
        Assert.Equal(1, defaultNotebook.EntryCount);
        Assert.Throws<InvalidOperationException>(() => _store.DeleteNotebook("w", defaultNotebook.Id));

        var created = _store.SaveNotebook("w", "项目调研", "竞品资料");
        Assert.False(created.IsDefault);
        Assert.Throws<InvalidOperationException>(() => _store.SaveNotebook("w", "项目调研", null));

        var renamed = _store.SaveNotebook("w", "调研", "新描述", created.Id);
        Assert.Equal("调研", renamed.Name);
        Assert.Equal("新描述", renamed.Description);

        // 删除笔记本连带删除其中的条目。
        _store.Save("w", "调研笔记", "内容", null, null, created.Id);
        Assert.Equal(2, _store.List("w", 10).Count);
        _store.DeleteNotebook("w", created.Id);
        var remaining = _store.List("w", 10);
        Assert.Single(remaining);
        Assert.Equal("沉淀", remaining[0].Title);
        Assert.Single(_store.ListNotebooks("w"));
    }

    [Fact]
    public void Sources_SaveChunksDelete_WithChunkAttribution()
    {
        var defaultId = _store.EnsureDefaultNotebook("w");
        var (source, created) = _store.SaveSource("w", defaultId, "text", "网页摘录", "https://example.com/a", "部署说明第一段。部署说明第二段。");
        Assert.True(created);
        var (upserted, createdAgain) = _store.SaveSource("w", defaultId, "text", "网页摘录", "https://example.com/a", "更新后的正文。");
        Assert.False(createdAgain);
        Assert.Equal(source.Id, upserted.Id);

        Assert.Equal(2, _store.ReplaceSourceChunks("w", source.Id, ["部署说明第一段。", "部署说明第二段。"], "网页"));
        Assert.Equal(2, Assert.Single(_store.ListSources("w", defaultId)).ChunkCount);

        var infos = _store.ListInfo("w", 100, defaultId).OrderBy(i => i.ChunkOrdinal).ToList();
        Assert.Equal(2, infos.Count);
        Assert.All(infos, info => Assert.Equal(source.Id, info.SourceId));
        Assert.All(infos, info => Assert.Equal("网页摘录", info.SourceTitle));
        Assert.Equal([1, 2], infos.Select(info => info.ChunkOrdinal));
        Assert.Contains(infos, info => info.Entry.Title.Contains("第1/2部分"));

        _store.DeleteSource("w", source.Id);
        Assert.Empty(_store.ListInfo("w", 100, defaultId));
        Assert.Empty(_store.ListSources("w", defaultId));
    }

    [Fact]
    public void RetrieveInfo_FiltersByNotebookSourceAndTag_WithAttribution()
    {
        var frontend = _store.SaveNotebook("w", "前端", null);
        var ops = _store.SaveNotebook("w", "运维", null);

        _store.Save("w", "构建命令", "pnpm build 构建桌面", null, null, frontend.Id);
        var (manual, _) = _store.SaveSource("w", ops.Id, "text", "部署手册", null, "docker compose up -d 发布服务");
        _store.ReplaceSourceChunks("w", manual.Id, ["docker compose up -d 发布服务"], "部署");

        Assert.Single(_store.RetrieveInfo("w", "pnpm", [frontend.Id], null, 10));
        var bySource = Assert.Single(_store.RetrieveInfo("w", "docker", null, [manual.Id], 10));
        Assert.Equal("运维", bySource.NotebookName);
        Assert.Equal("部署手册", bySource.SourceTitle);
        Assert.Equal(1, bySource.ChunkOrdinal);

        // 跨笔记本检索：不限范围时两个笔记本各命中一条。
        Assert.Equal("前端", Assert.Single(_store.RetrieveInfo("w", "桌面", null, null, 10)).NotebookName);
        Assert.Equal("运维", Assert.Single(_store.RetrieveInfo("w", "发布", null, null, 10)).NotebookName);
        Assert.Single(_store.RetrieveInfo("w", "docker", null, null, 10, "部署"));
        Assert.Empty(_store.RetrieveInfo("w", "docker", [frontend.Id], null, 10));
    }
}
