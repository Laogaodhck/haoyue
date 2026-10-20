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

    // ---------------------------------------------------------------- 混合检索融合
    // 这一组是绝对口径的门槛，不是相对权重游戏：语义侧用原始余弦，侧写「最大值归一」
    // 会让每条查询都必有一个满权重项（详见 KnowledgeSemanticIndex.SearchHybridAsync）。
    // 数值是首批可用基线，后续由 KnowledgeRetrievalEvaluation 的正/负例指标驱动调整。

    /// <summary>词法分在融合结果中的权重。</summary>
    public const double HybridLexicalWeight = 0.6;

    /// <summary>语义分（绝对余弦）在融合结果中的权重。</summary>
    public const double HybridSemanticWeight = 0.4;

    /// <summary>低于该余弦相似度的语义信号直接归零：弱相关不该换算成排名加分。</summary>
    public const double MinSemanticSimilarity = 0.25;

    /// <summary>
    /// 词法证据的绝对下限（归一口径：<c>score / (tokens × (3+2+2))</c>）。
    /// 取代原先的融合分门槛：0.6 权重乘在词法分上，再要求总分 ≥ 0.15，等价于把词法
    /// 下限偷偷抬到 0.25——而错别字回退通道每词满分只有 1（归一后 1/7 ≈ 0.143），
    /// 于是「唯一证据是纠错命中」的条目永远进不来，混合通道一启用就把容错能力弄丢了。
    /// 现在两通道各设各的门：任一边过线即入围，融合分只负责排序。
    /// 0.12 高于「多词查询只蹭到一个词」的噪声（1/21 ≈ 0.048），低于单词纠错满分。
    /// </summary>
    public const double MinLexicalScore = 0.12;

    /// <summary>
    /// 单次检索现场补嵌的条目上限。刻意只够「刚存的几条立刻可语义召回」：整库补齐属于显式
    /// 建索引通道（knowledge.semantic.build），塞进模型的工具调用里会让本地通道一次前向
    /// 几十遍，回合卡到像没输出。超出的计入 PendingVectors 并回报给用户。
    /// </summary>
    public const int MaxEmbedPerSearch = 8;

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
    private static Dictionary<string, FileStamp> _timestamps = [];

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

    /// <summary>
    /// 丢掉已记录的指纹，下一次访问强制重读。
    /// </summary>
    /// <remarks>
    /// **自身写盘路径必须调用**（daemon 的 knowledge.synonyms.save）：写盘与随后的
    /// 检索可能落在同一个时间戳桶里，此时指纹相同推不出内容相同——显式失效不依赖
    /// 文件系统精度，是唯一确定的做法。
    /// </remarks>
    public static void Invalidate()
    {
        lock (Gate) _timestamps.Clear();
    }

    private static void EnsureFresh()
    {
        lock (Gate)
        {
            var nowUtc = DateTime.UtcNow;
            var changed = false;
            for (var i = 0; i < _paths.Length; i++)
            {
                var path = _paths[i];
                if (string.IsNullOrEmpty(path)) continue;
                FileStamp stamp;
                try
                {
                    stamp = StatOf(path);
                }
                catch (IOException)
                {
                    continue;
                }
                // 指纹相同但时间戳还没「沉淀」时不能断定未变：粗粒度文件系统会把
                // 新旧两次写入取整成同一个值（详见 <see cref="StampSettleWindow"/>）。
                if (!_timestamps.TryGetValue(path, out var known) || known != stamp || !IsSettled(stamp, nowUtc))
                    changed = true;
            }
            if (!changed) return;

            var merged = BuildDefaultSynonyms();
            foreach (var path in _paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                var (ok, entries, stamp) = TryRead(path);
                // 读失败（文件被占用/已删除）也记指纹：否则每次都判「有变化」，
                // 把一次瞬时故障放大成每次检索都重试读盘。记下的指纹若与真实状态
                // 不符，下次访问自然会再次命中差异并重试。
                _timestamps[path] = stamp;
                if (!ok) continue;
                foreach (var (key, values) in entries)
                    merged[key] = values;
            }
            _synonyms = merged;
        }
    }

    /// <summary>
    /// 时间戳「沉淀窗口」：刚落盘的文件，其时间戳仍可能被文件系统按粗粒度取整
    /// （NTFS ≈15.6 ms，FAT 与部分 SMB 共享可达 2 s）。窗口内一律重读，不看指纹。
    /// </summary>
    /// <remarks>
    /// 这一条覆盖的是**外部写入**（用户用编辑器改文件）：若上次检索恰好落在同一个
    /// 时间戳桶里，指纹会与新内容相等而漏检。同义词表只有几十行，重读代价远低于
    /// 读到旧表——窗口取 2 s 是为了同时兜住 FAT/SMB 的粗粒度，实际开销是窗口内每次
    /// 检索多读一个小文本文件。
    ///
    /// 文件时间戳来自未来（跨机拷贝、时钟回拨）时窗口永不满足，等价于「每次都重读」：
    /// 结论仍然正确，只是多一次小文件读取，属可接受退让。
    /// </remarks>
    private static readonly TimeSpan StampSettleWindow = TimeSpan.FromSeconds(2);

    /// <summary>文件不存在（或尚未读到）视为已沉淀：否则「无文件」会永远强制重读。</summary>
    private static bool IsSettled(FileStamp stamp, DateTime nowUtc) =>
        stamp.WrittenUtc == DateTime.MinValue || stamp.WrittenUtc <= nowUtc - StampSettleWindow;

    /// <summary>
    /// 变更指纹 = 最后写入时间 + 文件长度。
    /// </summary>
    /// <remarks>
    /// 只看 <see cref="File.GetLastWriteTimeUtc"/> 会漏掉「同一时钟 tick 内的连续两次写入」：
    /// NTFS 的时间戳更新受系统时钟粒度约束（Windows 上约 15.6 ms，快照/CI/共享目录更粗），
    /// 两次写入可能拿到完全相同的时间戳 → 判定为未变化 → 热重载拿不到新内容。
    /// 加上长度能兜住绝大多数真实编辑；「同长度且同 tick」的改写由
    /// <see cref="StampSettleWindow"/> 与 <see cref="Invalidate"/> 两条路兜底。
    /// </remarks>
    private static FileStamp StatOf(string path)
    {
        var info = new FileInfo(path);
        return info.Exists
            ? new FileStamp(info.LastWriteTimeUtc, info.Length)
            : new FileStamp(DateTime.MinValue, 0);
    }

    private readonly record struct FileStamp(DateTime WrittenUtc, long Length);

    private static (bool Ok, Dictionary<string, string[]> Entries, FileStamp Stamp) TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return (false, [], new FileStamp(DateTime.MinValue, 0));
            var stamp = StatOf(path);
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
            return (false, [], new FileStamp(DateTime.MinValue, 0));
        }
        catch (UnauthorizedAccessException)
        {
            return (false, [], new FileStamp(DateTime.MinValue, 0));
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
