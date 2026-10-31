using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haoyue.Runtime.Providers;

/// <summary>
/// 本地模型加载指纹（P-缓存 / 错误恢复）。按模型文件路径记录：
/// 1) 最近若干次成功加载的耗时 —— 进度事件的 ETA 估计来源；
/// 2) 上次成功时的加载签名、后端构建号、GGUF 版本、架构 —— 加载失败时的版本/ABI
///    诊断依据（"该模型曾用后端构建 X 加载成功"）；
/// 3) 最近一次失败的时间与错误摘要 —— model.status 展示连续失败，避免用户在
///    坏配置上反复试错。
///
/// 持久化在 ~/.haoyue/model-fingerprints.json（测试可传临时路径）。写盘走
/// 临时文件 + 原子替换，进程内单航班串行化，绝不因并发加载写坏文件。文件损坏
/// 或 schema 不匹配时静默重建——指纹是优化 hint，不是事实来源。
/// </summary>
public sealed class LocalModelFingerprints
{
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly LocalModelFingerprintJsonContext Json = new(SerializerOptions);

    private readonly Lock _gate = new();
    private readonly string _filePath;
    private Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    public LocalModelFingerprints(string? filePath = null)
    {
        _filePath = filePath ?? System.IO.Path.Combine(
            Configuration.HaoyuePaths.Home, "model-fingerprints.json");
        Load();
    }

    /// <summary>默认指纹文件路径（~/.haoyue/model-fingerprints.json）。</summary>
    public static string DefaultFilePath => System.IO.Path.Combine(
        Configuration.HaoyuePaths.Home, "model-fingerprints.json");

    /// <summary>保留的耗时样本数（中位数 ETA 的数据源）。</summary>
    public const int MaxSamples = 8;

    /// <summary>
    /// 历史加载耗时中位数（毫秒）；无历史返回 null。跨签名统计——加载耗时对
    /// GPU/CPU 档位不敏感到影响 ETA 的数量级。
    /// </summary>
    public long? EstimatedLoadMs(string path)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(NormalizeKey(path), out var entry) || entry.LoadDurationsMs.Count == 0)
                return null;
            var sorted = entry.LoadDurationsMs.OrderBy(x => x).ToList();
            var middle = sorted.Count / 2;
            return sorted.Count % 2 == 1
                ? sorted[middle]
                : (sorted[middle - 1] + sorted[middle]) / 2;
        }
    }

    /// <summary>上次成功加载的签名（加载设置摘要）；无记录返回 null。</summary>
    public string? LastGoodSignature(string path)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(NormalizeKey(path), out var entry)
                ? entry.LastGoodSignature
                : null;
        }
    }

    /// <summary>上次成功加载时的后端构建号（LLamaSharp 程序集版本）。</summary>
    public string? BackendBuildAtLastSuccess(string path)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(NormalizeKey(path), out var entry)
                ? entry.BackendBuild
                : null;
        }
    }

    /// <summary>最近一次失败信息（时间 + 错误摘要）；无返回 null。</summary>
    public (DateTimeOffset At, string Error)? LastFailure(string path)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(NormalizeKey(path), out var entry) || entry.LastFailureAt is not { } at)
                return null;
            return (at, entry.LastFailureError ?? "");
        }
    }

    /// <summary>连续失败次数（成功即清零）。</summary>
    public int ConsecutiveFailures(string path)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(NormalizeKey(path), out var entry)
                ? entry.ConsecutiveFailures
                : 0;
        }
    }

    public void RecordSuccess(string path, string? loadSignature, long durationMs, string? backendBuild, int? ggufVersion, string? architecture)
    {
        lock (_gate)
        {
            var entry = EntryFor(path);
            entry.LastGoodSignature = loadSignature;
            entry.BackendBuild = backendBuild;
            entry.GgufVersion = ggufVersion;
            entry.Architecture = architecture;
            entry.LastSuccessUtc = DateTimeOffset.UtcNow;
            entry.ConsecutiveFailures = 0;
            entry.LastFailureAt = null;
            entry.LastFailureError = null;
            if (durationMs > 0)
            {
                entry.LoadDurationsMs.Add(durationMs);
                while (entry.LoadDurationsMs.Count > MaxSamples)
                    entry.LoadDurationsMs.RemoveAt(0);
            }
            _dirty = true;
            Save();
        }
    }

    public void RecordFailure(string path, string error)
    {
        lock (_gate)
        {
            var entry = EntryFor(path);
            entry.ConsecutiveFailures++;
            entry.LastFailureAt = DateTimeOffset.UtcNow;
            entry.LastFailureError = error.Length > 500 ? error[..500] : error;
            _dirty = true;
            Save();
        }
    }

    /// <summary>遗忘某个文件的全部指纹（模型文件被替换/删除时调用）。</summary>
    public void Forget(string path)
    {
        lock (_gate)
        {
            if (_entries.Remove(NormalizeKey(path)))
            {
                _dirty = true;
                Save();
            }
        }
    }

    private Entry EntryFor(string path)
    {
        var key = NormalizeKey(path);
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new Entry();
            _entries[key] = entry;
        }
        return entry;
    }

    private static string NormalizeKey(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch (Exception) { return path; }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var file = JsonSerializer.Deserialize(json, Json.FingerprintFile);
            if (file?.Entries is not { } entries || file.Version != FormatVersion)
            {
                _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                return;
            }
            _entries = entries.ToDictionary(
                pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // 指纹是优化 hint：损坏即重建，绝不影响模型加载本身。
            _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        if (!_dirty) return;
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temp = _filePath + ".tmp";
            var file = new FingerprintFile { Version = FormatVersion, Entries = _entries };
            File.WriteAllText(temp, JsonSerializer.Serialize(file, Json.FingerprintFile));
            File.Move(temp, _filePath, overwrite: true);
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 只读文件系统 / 权限不足：指纹降级为进程内内存态，不影响加载。
        }
    }

    internal sealed class Entry
    {
        public string? LastGoodSignature { get; set; }
        public string? BackendBuild { get; set; }
        public int? GgufVersion { get; set; }
        public string? Architecture { get; set; }
        public DateTimeOffset? LastSuccessUtc { get; set; }
        public DateTimeOffset? LastFailureAt { get; set; }
        public string? LastFailureError { get; set; }
        public int ConsecutiveFailures { get; set; }
        public List<long> LoadDurationsMs { get; set; } = [];
    }

    internal sealed class FingerprintFile
    {
        public int Version { get; set; }
        public Dictionary<string, Entry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Source-generated metadata for the on-disk fingerprint cache (trim/AOT safe).</summary>
[JsonSerializable(typeof(LocalModelFingerprints.FingerprintFile))]
internal sealed partial class LocalModelFingerprintJsonContext : JsonSerializerContext;
