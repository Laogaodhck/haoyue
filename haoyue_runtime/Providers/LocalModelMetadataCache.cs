using System.Collections.Concurrent;

namespace Haoyue.Runtime.Providers;

/// <summary>
/// 本地模型元数据缓存（P-缓存）。GGUF header 解析与 mmproj 目录扫描原本在每个请求
/// 的路径上同步执行：BuildShape 每次完整读一遍 header（metadata KV 逐对解析），
/// ResolveMmprojPath 每次枚举整个 models 目录。两者都由文件内容决定，按
/// (路径, 大小, mtime) / (目录, mtime) 缓存后，重复请求零 IO。
///
/// 线程安全；容量有上限（超限整体清空——元数据体积很小，LRU 复杂度不必要）。
/// 测试用 <see cref="Clear"/> 复位。
/// </summary>
public sealed class LocalModelMetadataCache
{
    private readonly record struct HeaderKey(string Path, long Length, long MtimeTicks);

    private readonly ConcurrentDictionary<HeaderKey, GgufProbe.Header> _headers = new();
    private readonly ConcurrentDictionary<string, MmprojEntry> _mmproj = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private int _headerReads;
    private int _directoryScans;

    private sealed record MmprojEntry(string? Path, long DirectoryMtimeTicks);

    /// <summary>缓存条目上限（header 只有几十字节，512 条覆盖任何真实模型目录）。</summary>
    public const int MaxEntries = 512;

    /// <summary>实际发生的 header 读取次数（缓存命中 = 不增长）。测试与基准用。</summary>
    public int HeaderReads
    {
        get { lock (_gate) return _headerReads; }
    }

    /// <summary>实际发生的目录扫描次数。测试与基准用。</summary>
    public int DirectoryScans
    {
        get { lock (_gate) return _directoryScans; }
    }

    /// <summary>
    /// 解析 GGUF header；命中缓存时不触盘。缓存键含文件大小与 mtime——替换模型
    /// 文件（同名校验、量化版本切换）自动失效，绝不返回过期元数据。
    /// </summary>
    public GgufProbe.Header Header(string path)
    {
        string full;
        long length;
        long mtime;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new GgufProbe.Header();
            full = info.FullName;
            length = info.Length;
            mtime = info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new GgufProbe.Header();
        }

        var key = new HeaderKey(full, length, mtime);
        if (_headers.TryGetValue(key, out var cached)) return cached;

        lock (_gate) _headerReads++;
        var header = GgufProbe.Read(full);
        if (_headers.Count >= MaxEntries) _headers.Clear();
        _headers[key] = header;
        return header;
    }

    /// <summary>
    /// 解析多模态投影器路径：显式配置优先（不缓存，本就是 O(1)）；否则扫描目录并
    /// 按目录 mtime 缓存——目录内容变化（增删 mmproj 文件）自动失效。
    /// </summary>
    public string? ResolveMmproj(string directory, string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            try { return Path.GetFullPath(explicitPath.Trim()); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        long mtime;
        try
        {
            if (!Directory.Exists(directory)) return null;
            mtime = new DirectoryInfo(directory).LastWriteTimeUtc.Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (_mmproj.TryGetValue(directory, out var cached) && cached.DirectoryMtimeTicks == mtime)
            return cached.Path;

        lock (_gate) _directoryScans++;
        string? resolved;
        try
        {
            var candidates = LocalModels.ListFiles(directory)
                .Where(file => file.FileName.Contains("mmproj", StringComparison.OrdinalIgnoreCase))
                .ToList();
            resolved = candidates.Count == 1 ? candidates[0].Path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            resolved = null;
        }
        if (_mmproj.Count >= MaxEntries) _mmproj.Clear();
        _mmproj[directory] = new MmprojEntry(resolved, mtime);
        return resolved;
    }

    /// <summary>测试/配置变更后复位全部缓存与计数器。</summary>
    public void Clear()
    {
        _headers.Clear();
        _mmproj.Clear();
        lock (_gate)
        {
            _headerReads = 0;
            _directoryScans = 0;
        }
    }
}
