using System.Text;

namespace Haoyue.Runtime.Data;

/// <summary>
/// User-extensible tuning for <see cref="KnowledgeSearchRanker"/>, loaded from
/// <c>~/.haoyue/knowledge/synonyms.txt</c> (created on demand, hot-reloaded when
/// the file's timestamp changes — same philosophy as the prompts/ directory).
///
/// File format, one entry per line:
/// <code>
///   # comment
///   部署 = 发布, 上线        (also accepts ":" as the separator)
///   密钥: key
/// </code>
/// Keys and values are normalized (FormKC + lowercase) on load, so full-width
/// input in the file behaves exactly like user queries. Entries are merged on
/// top of the built-in high-precision table: a line whose key already exists
/// there extends it, new keys are added as-is.
///
/// Weights and edit-distance thresholds stay as named constants here rather
/// than user config on purpose — they are algorithm tuning where a bad value
/// silently degrades ranking quality, while the synonym table is the part
/// vertical domains (medical, legal, in-house jargon) genuinely need to edit.
/// </summary>
public static class KnowledgeTuning
{
    /// <summary>Score granted when a query token hits the entry title.</summary>
    public const int TitleWeight = 3;

    /// <summary>Score granted when a query token hits the entry content.</summary>
    public const int ContentWeight = 2;

    /// <summary>Score granted when a query token hits the entry tags.</summary>
    public const int TagsWeight = 2;

    /// <summary>Words up to this length get edit distance ≤ ShortWordDistance.</summary>
    public const int FuzzyShortWordLength = 6;

    /// <summary>Allowed edit distance for short words.</summary>
    public const int FuzzyShortWordDistance = 1;

    /// <summary>Allowed edit distance for longer words.</summary>
    public const int FuzzyLongWordDistance = 2;

    private static readonly object Gate = new();

    /// <summary>Default synonym file location; the directory is only created when the user asks.</summary>
    /// <remarks>
    /// Must be initialized <em>before</em> <see cref="_paths"/>, which references it:
    /// static field initializers run in textual order, and a property declared below
    /// would leave a null inside <see cref="_paths"/> — which crashed the very first search.
    /// </remarks>
    private static readonly string DefaultPathValue = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".haoyue", "knowledge", "synonyms.txt");

    public static string DefaultPath => DefaultPathValue;

    /// <summary>
    /// The path the daemon's synonyms get/save IPC operates on: the first configured
    /// path — the real user file unless a test (or a future workspace-level table)
    /// reconfigures the watch list.
    /// </summary>
    public static string ActivePath => _paths[0];

    private static Dictionary<string, string[]> _synonyms = BuildDefaultSynonyms();
    private static string[] _paths = [DefaultPathValue];
    private static Dictionary<string, DateTime> _timestamps = [];

    /// <summary>
    /// Live synonym table (built-in defaults merged with user file entries).
    /// Reads are lock-free: the dictionary reference is replaced atomically on reload.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> Synonyms
    {
        get
        {
            EnsureFresh();
            return _synonyms;
        }
    }

    /// <summary>
    /// Replaces the watched file paths (test seam; also the extension point should
    /// workspace-level tables be added later) and forces a reload on next access.
    /// </summary>
    public static void ConfigurePaths(params string[] paths)
    {
        lock (Gate)
        {
            _paths = paths.Length == 0 ? [DefaultPath] : paths;
            _timestamps.Clear();
        }
    }

    /// <summary>Drops the user table and reloads from the configured paths on next access.</summary>
    public static void ResetForTests() => ConfigurePaths([]);

    private static void EnsureFresh()
    {
        lock (Gate)
        {
            var changed = false;
            for (var i = 0; i < _paths.Length; i++)
            {
                var path = _paths[i];
                if (string.IsNullOrEmpty(path)) continue;
                DateTime stamp;
                try
                {
                    stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                }
                catch (IOException)
                {
                    continue;
                }
                if (!_timestamps.TryGetValue(path, out var known) || known != stamp) changed = true;
            }
            if (!changed) return;

            var merged = BuildDefaultSynonyms();
            foreach (var path in _paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                var (ok, entries, stamp) = TryRead(path);
                if (!ok) continue;
                _timestamps[path] = stamp;
                foreach (var (key, values) in entries)
                    merged[key] = values;
            }
            _synonyms = merged;
        }
    }

    private static (bool Ok, Dictionary<string, string[]> Entries, DateTime Stamp) TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return (false, [], DateTime.MinValue);
            var stamp = File.GetLastWriteTimeUtc(path);
            var entries = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var rawLine in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                var separator = line.IndexOf('=');
                if (separator < 0) separator = line.IndexOf(':');
                if (separator <= 0) continue;

                var key = KnowledgeSearchRanker.Normalize(line[..separator].Trim());
                if (key.Length == 0) continue;

                var values = line[(separator + 1)..]
                    .Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(KnowledgeSearchRanker.Normalize)
                    .Where(v => v.Length > 0)
                    .Distinct()
                    .ToArray();
                if (values.Length == 0) continue;

                entries[key] = values; // later lines win for the same key
            }
            return (true, entries, stamp);
        }
        catch (IOException)
        {
            return (false, [], DateTime.MinValue);
        }
        catch (UnauthorizedAccessException)
        {
            return (false, [], DateTime.MinValue);
        }
    }

    /// <summary>Built-in high-precision table: common technical synonyms only; rare ones add noise, not recall.</summary>
    private static Dictionary<string, string[]> BuildDefaultSynonyms() => new()
    {
        ["部署"] = ["发布", "上线"],
        ["发布"] = ["部署", "上线"],
        ["上线"] = ["部署", "发布"],
        ["安装"] = ["安装包", "部署"],
        ["打包"] = ["构建", "编译"],
        ["构建"] = ["打包", "编译"],
        ["编译"] = ["构建", "打包"],
        ["登录"] = ["登陆", "登入"],
        ["登陆"] = ["登录", "登入"],
        ["登入"] = ["登录", "登陆"],
        ["账号"] = ["帐号", "账户"],
        ["帐号"] = ["账号", "账户"],
        ["配置"] = ["设置"],
        ["设置"] = ["配置"],
        ["报错"] = ["出错", "异常", "失败"],
        ["出错"] = ["报错", "异常"],
        ["异常"] = ["报错", "出错"],
        ["失败"] = ["报错", "出错"],
        ["崩溃"] = ["闪退"],
        ["闪退"] = ["崩溃"],
        ["卡顿"] = ["卡住", "卡死"],
        ["文档"] = ["说明", "手册"],
        ["命令"] = ["指令"],
        ["端口"] = ["port"],
        ["端口占用"] = ["端口被占用"],
        ["密钥"] = ["key"],
        ["乱码"] = ["编码"],
        ["编码"] = ["乱码"],
    };
}
