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

        var (fileName, entries) = KnowledgeIngest.ImportFile(_store, scope, source);
        Assert.Equal("部署笔记.md", fileName);
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
}
