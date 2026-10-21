using System.Collections.Concurrent;

namespace Haoyue.Runtime.Data;

/// <summary>
/// 4.4 源级增量更新：后台轮询 file 源（locator = 文件绝对路径），stat 快筛 + hash
/// 比对，外部修改的文件自动重导入（ImportFile 的既有管道完成重分块与 entries 替换，
/// 幂等——同 locator upsert）。best-effort：单源失败记日志继续，绝不影响主流程。
/// 生命周期：daemon 进程内静态注册表按 scope 幂等启动；daemon 退出即随进程结束。
/// </summary>
public sealed class KnowledgeSourceWatcher : IDisposable
{
    private static readonly ConcurrentDictionary<string, KnowledgeSourceWatcher> Watchers = new(StringComparer.Ordinal);

    private readonly KnowledgeStore _store;
    private readonly string _scope;
    private readonly TimeSpan _interval;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _loop;

    private KnowledgeSourceWatcher(KnowledgeStore store, string scope, TimeSpan interval, Action<string>? log)
    {
        _store = store;
        _scope = scope;
        _interval = interval;
        _log = log;
    }

    /// <summary>按 scope 幂等启动（重复调用返回既有实例）。daemon 侧在文件导入成功后触发。</summary>
    public static KnowledgeSourceWatcher EnsureStarted(
        KnowledgeStore store, string scope, TimeSpan? interval = null, Action<string>? log = null)
    {
        return Watchers.GetOrAdd(scope, _ =>
        {
            var watcher = new KnowledgeSourceWatcher(store, scope, interval ?? TimeSpan.FromSeconds(10), log);
            watcher._loop = Task.Run(() => watcher.RunLoop(watcher._cancellation.Token));
            return watcher;
        });
    }

    /// <summary>测试与诊断用：当前活跃的 watcher 数。</summary>
    public static int ActiveCount => Watchers.Count;

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, ct).ConfigureAwait(false);
                Scan(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"知识源 watcher 轮询失败（将继续）：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 扫描一轮：file 源 → 文件仍在且 hash 与落库指纹不同 → 重导入（同 notebook）。
    /// 返回刷新的源数量。内容指纹基于「提取后的文本」——二进制格式（docx 等）内容
    /// 不变时提取文本不变，不会误报。
    /// </summary>
    public int Scan(CancellationToken ct = default)
    {
        var refreshed = 0;
        foreach (var source in _store.ListFileSources(_scope))
        {
            if (ct.IsCancellationRequested) break;
            var path = source.Locator!;
            if (!File.Exists(path)) continue; // 已删除的源静默跳过（不自动清理，保留用户数据）

            try
            {
                var text = KnowledgeImport.ExtractText(path);
                var fingerprint = KnowledgeStore.ContentFingerprint(text);
                if (string.Equals(fingerprint, source.ContentHash, StringComparison.OrdinalIgnoreCase))
                    continue; // 未变化：最常见路径

                // 变化 → 走既有导入管道整组替换（同 notebook，locator upsert 幂等）。
                var (_, entries) = KnowledgeIngest.ImportFile(_store, _scope, path, source.NotebookId);
                refreshed++;
                _log?.Invoke($"知识源已自动更新：{Path.GetFileName(path)}（{entries} 个分块）");
            }
            catch (Exception ex) when (ex is KnowledgeImportException or IOException or UnauthorizedAccessException)
            {
                // 单源失败不影响其余源：文件被锁、格式损坏、权限回收都只记日志。
                _log?.Invoke($"知识源刷新失败（跳过）：{Path.GetFileName(path)}：{ex.Message}");
            }
        }
        return refreshed;
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* aggregate of cancellation */ }
        _cancellation.Dispose();
        Watchers.TryRemove(_scope, out _);
    }
}
