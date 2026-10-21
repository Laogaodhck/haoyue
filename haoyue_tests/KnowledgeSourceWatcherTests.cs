using Haoyue.Runtime.Data;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// 4.4 源级增量更新：content_hash 落库（导入即写指纹）、watcher 的 stat+hash 比对
/// 与自动重导入（同 notebook、locator upsert 幂等）、未变化文件零开销跳过。
/// </summary>
public sealed class KnowledgeSourceWatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-kbwatch-" + Guid.NewGuid().ToString("N"));

    public KnowledgeSourceWatcherTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private KnowledgeStore CreateStore()
    {
        var database = new HaoyueDatabase(Path.Combine(_dir, "state.db"));
        return new KnowledgeStore(database);
    }

    [Fact]
    public void ImportFile_WritesContentFingerprint()
    {
        var store = CreateStore();
        var file = Path.Combine(_dir, "notes.md");
        File.WriteAllText(file, "# 笔记\n\n第一段内容。");

        KnowledgeIngest.ImportFile(store, "watch-scope", file);
        var source = Assert.Single(store.ListFileSources("watch-scope"));

        // 指纹 = 导入时提取文本的 SHA256：与库内 content 一致性可直接验证。
        Assert.False(string.IsNullOrEmpty(source.ContentHash));
        Assert.Equal(KnowledgeStore.ContentFingerprint(source.Content), source.ContentHash);
    }

    [Fact]
    public void Scan_UnchangedFile_ReportsZeroRefresh()
    {
        var store = CreateStore();
        var file = Path.Combine(_dir, "stable.md");
        File.WriteAllText(file, "稳定内容");
        KnowledgeIngest.ImportFile(store, "watch-scope", file);

        var logs = new List<string>();
        var watcher = KnowledgeSourceWatcher.EnsureStarted(store, "watch-scope", log: logs.Add);
        using var keepAlive = watcher;

        Assert.Equal(0, watcher.Scan());
    }

    [Fact]
    public void Scan_ExternalModification_AutoRefreshesEntriesInSameNotebook()
    {
        var store = CreateStore();
        var file = Path.Combine(_dir, " evolving.md");
        File.WriteAllText(file, "# v1\n\n原始内容。");
        var (imported, _) = KnowledgeIngest.ImportFile(store, "watch-scope", file);

        // 外部修改（模拟用户直接改文件）。
        File.WriteAllText(file, "# v2\n\n完全不同的新内容，而且更长，足以产生不同的分块。");
        var logs = new List<string>();
        var watcher = KnowledgeSourceWatcher.EnsureStarted(store, "watch-scope", log: logs.Add);
        using var keepAlive = watcher;

        Assert.Equal(1, watcher.Scan()); // 恰好刷新这一个源
        var refreshed = store.GetSource("watch-scope", imported.Id)!;
        // 同一 source（locator upsert 幂等），同 notebook，content 与指纹都已刷新。
        Assert.Equal(imported.NotebookId, refreshed.NotebookId);
        Assert.Contains("完全不同的新内容", refreshed.Content, StringComparison.Ordinal);
        Assert.Equal(KnowledgeStore.ContentFingerprint(refreshed.Content), refreshed.ContentHash);
        _ = logs;
    }

    [Fact]
    public void Scan_MissingOrBrokenFile_IsSkippedWithoutThrowing()
    {
        var store = CreateStore();
        var file = Path.Combine(_dir, "gone.md");
        File.WriteAllText(file, "将被删除的内容");
        KnowledgeIngest.ImportFile(store, "watch-scope", file);
        File.Delete(file); // 外部删除：静默跳过（不自动清源，保留用户数据）

        var logs = new List<string>();
        var watcher = KnowledgeSourceWatcher.EnsureStarted(store, "watch-scope", log: logs.Add);
        using var keepAlive = watcher;
        Assert.Equal(0, watcher.Scan());
        Assert.NotNull(store.GetSource("watch-scope", store.ListFileSources("watch-scope")[0].Id));

        // 导入后变成空内容的文件（脏场景）：ImportFile 抛「内容为空」，watcher 捕获跳过不抛。
        var becomesEmpty = Path.Combine(_dir, "broken.md");
        File.WriteAllText(becomesEmpty, "导入时还有内容");
        KnowledgeIngest.ImportFile(store, "watch-scope", becomesEmpty);
        File.WriteAllText(becomesEmpty, "   ");
        Assert.Equal(0, watcher.Scan()); // broken.md 与 gone.md 都被跳过
    }

    [Fact]
    public void EnsureStarted_IsIdempotent_PerScope()
    {
        var store = CreateStore();
        var first = KnowledgeSourceWatcher.EnsureStarted(store, "idem-scope");
        using var _ = first;
        var second = KnowledgeSourceWatcher.EnsureStarted(store, "idem-scope");
        Assert.Same(first, second); // 同 scope 复用同一 watcher，不重复起后台循环
    }
}
