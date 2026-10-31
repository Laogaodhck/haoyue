using System.Collections.Concurrent;

namespace Haoyue.Runtime.Workspaces;

/// <summary>
/// 2.5 提示合成输入的内容缓存：6 路并行委派时每个子代理都要读 MEMORY.md /
/// AGENTS.md——指纹缓存把 IO 降为一次，后续命中直接返回内存内容。
/// 指纹 = 候选文件的（最后写入时间 + 长度）快照：时间戳单独不能作变更指纹
/// （NTFS 时钟粒度 ~15.6ms 的坑已实证），叠加长度后同 tick 多写必变指纹。
/// 指纹不匹配就重新读取，因此无需显式失效通道，也绝无陈旧内容风险。
/// </summary>
internal static class WorkspaceFileCache
{
    private sealed record Cached(string Fingerprint, object? Value);

    private static readonly ConcurrentDictionary<string, Cached> Cache = new(StringComparer.OrdinalIgnoreCase);
    internal static long Hits;
    internal static long Misses;

    /// <summary>单个文件的指纹快照；不存在也参与指纹（出现/消失都要击穿缓存）。</summary>
    public static string FileStamp(string path)
    {
        try
        {
            return File.Exists(path)
                ? $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{new FileInfo(path).Length}"
                : $"{path}|missing";
        }
        catch (IOException)
        {
            return $"{path}|unreadable"; // 瞬时共享冲突：按不可读处理，下次重扫
        }
    }

    /// <summary>指纹命中返回缓存值；否则执行 produce 并落缓存。</summary>
    public static T GetOrAdd<T>(string key, string fingerprint, Func<T> produce) where T : class?
    {
        if (Cache.TryGetValue(key, out var cached) && cached.Fingerprint == fingerprint)
        {
            Interlocked.Increment(ref Hits);
            return (T)cached.Value!;
        }
        Interlocked.Increment(ref Misses);
        var value = produce();
        Cache[key] = new Cached(fingerprint, value);
        return value;
    }

    /// <summary>测试隔离：清空缓存与计数。</summary>
    internal static void Reset()
    {
        Cache.Clear();
        Hits = 0;
        Misses = 0;
    }
}
