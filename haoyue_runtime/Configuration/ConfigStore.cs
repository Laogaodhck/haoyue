using System.Text.Json;
using System.Text.Json.Nodes;

namespace Haoyue.Runtime.Configuration;

public interface IConfigStore
{
    HaoyueConfig Config { get; }
    RuntimeState State { get; }
    bool HasAnomaly { get; }
    string? AnomalyDetail { get; }
    string? BackupConfigFile { get; }
    /// <summary>最近一次加载时 ConfigSchema 规范化/迁移产生的告警（字段回退、越界钳制、版本升级）。</summary>
    IReadOnlyList<string> ValidationWarnings { get; }

    void Save();
    void SaveState();
    void Reload();
    /// <summary>
    /// Computes a recursive JSON patch of the top-level/nested properties this process
    /// changed relative to the load-time baseline. Returns null when nothing differs
    /// (or there is no baseline), so callers can skip persistence entirely.
    /// </summary>
    JsonObject? CaptureDirtyPatch();
    /// <summary>Deep-merges the given patch into the in-memory config and persists it.</summary>
    void ApplyPatch(JsonObject patch);
    /// <summary>Restores the in-memory objects and on-disk files to factory defaults.</summary>
    void Reset();
}

/// <summary>
/// Loads / persists ~/.haoyue/config.json. On first run the store seeds the file by
/// serializing <see cref="DefaultHaoyueConfig"/> to JSON; provider and model data
/// stay fully data-driven after that point.
/// </summary>
public sealed class ConfigStore : IConfigStore
{
    private readonly Lock _gate = new();
    private readonly string _configFile;
    private readonly string _stateFile;

    // On-disk snapshots of the values this process last loaded/saved. They let Save()
    // diff "what we changed" against "what the other writer changed" so a concurrent
    // CLI/daemon pair no longer clobbers each other with stale full-file rewrites.
    private string? _configBaseline;
    private string? _stateBaseline;

    public HaoyueConfig Config { get; private set; }
    public RuntimeState State { get; private set; }
    public bool HasAnomaly { get; private set; }
    public string? AnomalyDetail { get; private set; }
    public string? BackupConfigFile { get; private set; }
    public IReadOnlyList<string> ValidationWarnings { get; private set; } = [];

    public ConfigStore(string? configFile = null, string? stateFile = null)
    {
        _configFile = configFile ?? HaoyuePaths.ConfigFile;
        _stateFile = stateFile ?? HaoyuePaths.StateFile;
        Config = LoadConfig();
        State = LoadState();
    }

    public void Reload()
    {
        lock (_gate)
        {
            Config = LoadConfig();
            State = LoadState();
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configFile)!);
            var merged = MergeWithDisk(_configFile, _configBaseline, Config,
                HaoyueJsonContext.Default.HaoyueConfig);
            var json = JsonSerializer.Serialize(merged, HaoyueJsonContext.Default.HaoyueConfig);
            WriteAtomic(_configFile, json);
            Config = merged;
            _configBaseline = json;
        }
    }

    public void SaveState()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            var merged = MergeWithDisk(_stateFile, _stateBaseline, State,
                HaoyueJsonContext.Default.RuntimeState);
            var json = JsonSerializer.Serialize(merged, HaoyueJsonContext.Default.RuntimeState);
            WriteAtomic(_stateFile, json);
            State = merged;
            _stateBaseline = json;
        }
    }

    /// <summary>
    /// Recursive diff of the in-memory config against the load-time baseline: only
    /// changed leaves are included, so a delegated save (A4 daemon single-writer)
    /// carries exactly what this process edited and a sibling edit by the other
    /// writer survives the merge on the receiving side.
    /// </summary>
    public JsonObject? CaptureDirtyPatch()
    {
        lock (_gate)
        {
            if (_configBaseline is null) return null;
            try
            {
                var baseline = JsonNode.Parse(_configBaseline) as JsonObject;
                var current = JsonNode.Parse(
                    JsonSerializer.Serialize(Config, HaoyueJsonContext.Default.HaoyueConfig)) as JsonObject;
                var patch = DiffNodes(baseline, current);
                return patch.Count == 0 ? null : patch;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>Deep-merges the patch into the in-memory config, then saves (the save
    /// itself re-merges against disk so unrelated concurrent changes stay intact).</summary>
    public void ApplyPatch(JsonObject patch)
    {
        lock (_gate)
        {
            var currentObj = JsonNode.Parse(
                JsonSerializer.Serialize(Config, HaoyueJsonContext.Default.HaoyueConfig)) as JsonObject;
            if (currentObj is null) return;
            MergePatch(currentObj, patch);
            Config = currentObj.Deserialize(HaoyueJsonContext.Default.HaoyueConfig) ?? Config;
        }
        Save();
    }

    private static JsonObject DiffNodes(JsonObject? baseline, JsonObject? current)
    {
        var patch = new JsonObject();
        if (current is null) return patch;
        foreach (var (name, value) in current)
        {
            JsonNode? baseVal = baseline is not null && baseline.TryGetPropertyValue(name, out var bv) ? bv : null;
            if (JsonNode.DeepEquals(value, baseVal)) continue;
            if (value is JsonObject curObj && baseVal is JsonObject baseObj)
            {
                var nested = DiffNodes(baseObj, curObj);
                if (nested.Count > 0) patch[name] = nested;
            }
            else
            {
                patch[name] = value is null ? null : JsonNode.Parse(value.ToJsonString());
            }
        }
        return patch;
    }

    private static void MergePatch(JsonObject target, JsonObject patch)
    {
        foreach (var (name, value) in patch)
        {
            if (value is JsonObject patchObj && target[name] is JsonObject targetObj)
                MergePatch(targetObj, patchObj);
            else
                target[name] = value is null ? null : JsonNode.Parse(value.ToJsonString());
        }
    }

    /// <summary>
    /// Merges the in-memory value with the on-disk file before saving (field-level
    /// conflict resolution against a second writer, e.g. CLI while the daemon runs).
    /// Top-level properties this process did NOT change relative to the baseline are
    /// taken from disk — preserving the other writer's concurrent update — while
    /// changed properties keep the in-memory value. The merged result becomes the new
    /// in-memory state so both sides converge instead of last-writer-wins per file.
    /// Falls back to the in-memory value when there is no baseline or the disk file
    /// is unreadable (first run, corruption, IO error) — identical to legacy behavior.
    /// </summary>
    private static T MergeWithDisk<T>(string file, string? baselineJson, T current,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            if (baselineJson is null || !File.Exists(file)) return current;
            var diskText = File.ReadAllText(file);
            if (string.IsNullOrWhiteSpace(diskText)) return current;

            var baseline = JsonNode.Parse(baselineJson) as JsonObject;
            var disk = JsonNode.Parse(diskText) as JsonObject;
            var currentObj = JsonNode.Parse(JsonSerializer.Serialize(current, typeInfo)) as JsonObject;
            if (baseline is null || disk is null || currentObj is null) return current;

            foreach (var (name, value) in currentObj)
            {
                var baseVal = baseline.TryGetPropertyValue(name, out var bv) ? bv : null;
                var locallyChanged = !JsonNode.DeepEquals(value, baseVal);
                if (locallyChanged)
                    disk[name] = value is null ? null : JsonNode.Parse(value.ToJsonString());
                // Unchanged locally → leave the disk value so the other writer's
                // concurrent update to this property survives.
            }

            return disk.Deserialize(typeInfo) ?? current;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return current;
        }
    }

    /// <summary>Temp file in the target directory + rename, so readers never observe a
    /// half-written JSON file and a crash cannot truncate the previous good state.</summary>
    private static void WriteAtomic(string file, string contents)
    {
        var temp = Path.Combine(
            Path.GetDirectoryName(file)!,
            $".{Path.GetFileName(file)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, contents);
            File.Move(temp, file, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            HasAnomaly = false;
            AnomalyDetail = null;
            BackupConfigFile = null;
            Config = DefaultHaoyueConfig.Build();
            State = new RuntimeState();
            DeleteIfExists(_configFile);
            DeleteIfExists(_stateFile);
            _configBaseline = null;
            _stateBaseline = null;
        }

        Save();
        SaveState();
    }

    private HaoyueConfig LoadConfig()
    {
        if (File.Exists(_configFile))
        {
            try
            {
                var text = File.ReadAllText(_configFile);
                if (string.IsNullOrWhiteSpace(text))
                {
                    HandleAnomaly("配置文件内容为空 (0 字节)");
                }
                else
                {
                    var root = JsonNode.Parse(text, new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                        new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                        as JsonObject;
                    if (root is null)
                    {
                        HandleAnomaly("配置文件根节点不是 JSON 对象");
                    }
                    else
                    {
                        // 统一 Schema 管道：规范化（默认值回填/越界钳制/枚举回退）→ 版本迁移 → 反序列化。
                        var (warnings, _) = ConfigSchema.Normalize(root);
                        var migrations = ConfigSchema.MigrateToCurrent(root);
                        var loaded = root.Deserialize(HaoyueJsonContext.Default.HaoyueConfig);
                        if (loaded is not null)
                        {
                            if (string.IsNullOrWhiteSpace(loaded.Model))
                                MigrateLegacyProfiles(loaded, _configFile);
                            ValidationWarnings = [.. warnings, .. migrations];
                            HasAnomaly = false;
                            AnomalyDetail = null;
                            if (migrations.Count > 0)
                            {
                                // schema 版本升级：清空基线并立即落盘，把迁移后的文件写回磁盘。
                                _configBaseline = null;
                                Config = loaded;
                                Save();
                            }
                            else
                            {
                                _configBaseline = text;
                            }
                            return loaded;
                        }
                        HandleAnomaly("配置文件反序列化结果为空");
                    }
                }
            }
            catch (Exception ex)
            {
                HandleAnomaly($"配置文件损坏或格式错误：{ex.Message}");
            }

            // Anomaly occurred: fallback to in-memory defaults, DO NOT overwrite the corrupt file on disk.
            var fallback = DefaultHaoyueConfig.Build();
            return fallback;
        }

        // First run: file does not exist, seed and save cleanly
        HasAnomaly = false;
        AnomalyDetail = null;
        BackupConfigFile = null;
        var seeded = DefaultHaoyueConfig.Build();
        Config = seeded;
        Save();
        return seeded;
    }

    private void HandleAnomaly(string detail)
    {
        HasAnomaly = true;
        AnomalyDetail = detail;
        try
        {
            var dir = Path.GetDirectoryName(_configFile) ?? "";
            var fileName = Path.GetFileName(_configFile);
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backupPath = Path.Combine(dir, $"{fileName}.corrupt.{timestamp}.bak");
            if (File.Exists(_configFile) && !File.Exists(backupPath))
            {
                File.Copy(_configFile, backupPath, true);
                BackupConfigFile = backupPath;
            }
        }
        catch
        {
            // Best effort backup: ignore I/O errors during backup
        }
    }

    private static void MigrateLegacyProfiles(HaoyueConfig config, string configFile)
    {
        try
        {
            var json = File.ReadAllText(configFile);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Object)
                return;

            string? activeName = null;
            if (root.TryGetProperty("activeProfile", out var activeProp) && activeProp.ValueKind == JsonValueKind.String)
                activeName = activeProp.GetString();

            JsonElement targetProfile = default;
            if (activeName != null && profiles.TryGetProperty(activeName, out var p))
                targetProfile = p;
            else if (profiles.TryGetProperty("default", out var defP))
                targetProfile = defP;
            else
            {
                var first = profiles.EnumerateObject().FirstOrDefault();
                targetProfile = first.Value;
            }

            if (targetProfile.ValueKind == JsonValueKind.Object)
            {
                if (targetProfile.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String)
                    config.Model = modelProp.GetString();
                if (targetProfile.TryGetProperty("provider", out var providerProp) && providerProp.ValueKind == JsonValueKind.String)
                    config.Provider = providerProp.GetString();
                if (targetProfile.TryGetProperty("temperature", out var tempProp) && tempProp.ValueKind == JsonValueKind.Number)
                    config.Temperature = tempProp.GetDouble();
            }
        }
        catch
        {
            // Best effort migration: ignore parse issues
        }
    }

    private RuntimeState LoadState()
    {
        if (!File.Exists(_stateFile)) return new RuntimeState();
        try
        {
            var text = File.ReadAllText(_stateFile);
            var state = JsonSerializer.Deserialize(text, HaoyueJsonContext.Default.RuntimeState);
            if (state is not null) _stateBaseline = text;
            return state ?? new RuntimeState();
        }
        catch (JsonException)
        {
            // Corrupt file: keep it on disk for the user to inspect, fall back to defaults.
            return new RuntimeState();
        }
    }

    private static void DeleteIfExists(string file)
    {
        if (!File.Exists(file)) return;
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The caller rebuilds the files below; keep reset best-effort.
        }
    }
}
