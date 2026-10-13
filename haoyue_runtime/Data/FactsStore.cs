using System.Text.Json;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Data;

/// <summary>一条项目事实：内容 + 来源 + 置信度 + 可选过期时间。</summary>
public sealed class FactEntry
{
    public string Id { get; set; } = "";
    public string Content { get; set; } = "";
    public string? Topic { get; set; }
    /// <summary>来源：agent（模型沉淀）| user（用户明确给出）。</summary>
    public string Source { get; set; } = "agent";
    /// <summary>置信度 0..1：低置信事实先被降级/清理，且注入时标注"待确认"。</summary>
    public double Confidence { get; set; } = 0.8;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>过期时间；null 表示长期有效。过期事实不注入、可被清理。</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>
    /// M-D 冲突仲裁：被哪条事实取代（保留行供审计，不再注入/检索）。矛盾事实同时
    /// 注入会让模型每回合猜；新事实通过显式 supersedes 或同主题高重合自动仲裁
    /// 顶掉旧事实，旧的标记到这里而不是直接删除。
    /// </summary>
    public string? SupersededBy { get; set; }
}

/// <summary>
/// 记忆分层中的「项目事实层」：介于 MEMORY.md（用户策展的长期记忆）与知识库
/// （大容量文档检索）之间，存放带来源与置信度的短句事实，按工作区隔离持久化
/// （<c>&lt;workspace&gt;/.haoyue/facts.json</c>），到期自动失效。写入为原子替换。
/// </summary>
public sealed class FactsStore
{
    /// <summary>单工作区事实条数上限：事实层是提示注入的一部分，必须保持精炼。</summary>
    public const int CapacityPerWorkspace = 100;

    /// <summary>
    /// M-D 自动仲裁的重合度阈值：同主题事实的词条 Jaccard 重合达到该值即视为
    /// "同一事物的两种说法"（矛盾或修订），新事实在置信度不低于旧事实时取代之。
    /// </summary>
    private const double ConflictOverlapThreshold = 0.45;

    /// <summary>
    /// 进程内互斥：守护 Load→Modify→Save 读改写序列，防止同进程内多回合并发写丢更新。
    /// 跨进程由 <see cref="CrossProcessLock"/>（锁文件）兜底：CLI 与 daemon 并存时
    /// 也不会出现后写覆盖先写。读路径无需加锁——写入是原子替换，读到的要么是旧文件
    /// 要么是新文件，不存在半截视图。
    /// </summary>
    private static readonly object ProcessGate = new();

    public static string FactsFile(WorkspaceInfo workspace) => FactsFileForRoot(workspace.Root);

    public static string FactsFileForRoot(string root) =>
        Path.Combine(root, ".haoyue", "facts.json");

    public IReadOnlyList<FactEntry> ListActive(WorkspaceInfo workspace) => ListActiveForRoot(workspace.Root);

    public IReadOnlyList<FactEntry> ListActiveForRoot(string root)
    {
        var all = Load(root);
        var now = DateTimeOffset.UtcNow;
        // M-D: 被取代的事实（SupersededBy 非空）与过期事实一样不注入、不检索。
        return [.. all.Where(f => f.SupersededBy is null && (f.ExpiresAt is null || f.ExpiresAt > now))];
    }

    public FactEntry Add(
        string root, string content, string? topic = null,
        string source = "agent", double confidence = 0.8, int? ttlDays = null,
        string? supersedes = null)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("事实内容不能为空。", nameof(content));
        confidence = Math.Clamp(confidence, 0, 1);

        lock (ProcessGate)
        using (CrossProcessLock.Acquire(root))
        {
            var facts = Load(root);
            // 幂等：内容相同（忽略大小写）的事实不重复写入，仅刷新置信度。
            var existing = facts.FirstOrDefault(f =>
                f.Content.Equals(content.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Confidence = Math.Max(existing.Confidence, confidence);
                existing.ExpiresAt = ttlDays is { } days ? DateTimeOffset.UtcNow.AddDays(days) : existing.ExpiresAt;
                Save(root, facts);
                return existing;
            }

            var entry = new FactEntry
            {
                Id = $"fact-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
                Content = content.Trim(),
                Topic = string.IsNullOrWhiteSpace(topic) ? null : topic!.Trim(),
                Source = source is "user" ? "user" : "agent",
                Confidence = confidence,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = ttlDays is { } ttl ? DateTimeOffset.UtcNow.AddDays(ttl) : null,
            };

            // M-D 冲突仲裁（写时解决，避免矛盾事实同时注入让模型每回合猜）：
            // ① 显式 supersedes：调用方指定要取代的旧事实（id 或完整内容）；
            // ② 自动仲裁：同主题 + 词条重合度达标 + 新事实置信度不低于旧事实 →
            //    视为对旧事实的修订，顶掉旧的（旧行标记 SupersededBy 保留供审计）。
            var supersededIds = new List<string>();
            if (!string.IsNullOrWhiteSpace(supersedes))
            {
                supersededIds.AddRange(facts
                    .Where(f => f.SupersededBy is null && MatchesFact(f, supersedes!))
                    .Select(f => f.Id));
            }
            else if (!string.IsNullOrWhiteSpace(topic))
            {
                var terms = Tokenize(content).ToHashSet(StringComparer.OrdinalIgnoreCase);
                supersededIds.AddRange(facts
                    .Where(f => f.SupersededBy is null
                                && string.Equals(f.Topic, entry.Topic, StringComparison.OrdinalIgnoreCase)
                                && confidence + 0.001 >= f.Confidence
                                && Jaccard(terms, Tokenize(f.Content).ToHashSet(StringComparer.OrdinalIgnoreCase)) >= ConflictOverlapThreshold)
                    .Select(f => f.Id));
            }
            foreach (var id in supersededIds)
            {
                var target = facts.First(f => f.Id == id);
                target.SupersededBy = entry.Id;
            }

            facts.Add(entry);
            // 容量淘汰：先丢弃低置信度，再按创建时间淘汰最旧。
            if (facts.Count > CapacityPerWorkspace)
            {
                facts = [.. facts
                    .OrderByDescending(f => f.Confidence)
                    .ThenByDescending(f => f.CreatedAt)
                    .Take(CapacityPerWorkspace)];
            }
            Save(root, facts);
            return entry;
        }
    }

    /// <summary>M-D: supersedes 参数既匹配事实 id，也匹配完整内容（忽略大小写）。</summary>
    private static bool MatchesFact(FactEntry fact, string supersedes) =>
        fact.Id.Equals(supersedes, StringComparison.OrdinalIgnoreCase)
        || fact.Content.Equals(supersedes.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>M-D: 词条 Jaccard 重合度（0..1），用于判断两条同主题事实是否在说同一事物。</summary>
    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersection = a.Count(b.Contains);
        return intersection / (double)(a.Count + b.Count - intersection);
    }

    public bool Forget(WorkspaceInfo workspace, string id) => ForgetForRoot(workspace.Root, id);

    public bool ForgetForRoot(string root, string id)
    {
        lock (ProcessGate)
        using (CrossProcessLock.Acquire(root))
        {
            var facts = Load(root);
            var removed = facts.RemoveAll(f => f.Id.Equals(id, StringComparison.Ordinal)
                || f.Content.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            Save(root, facts);
            return true;
        }
    }

    /// <summary>按词条重合度排序的简单检索（事实层量小，无需全文索引）。</summary>
    public IReadOnlyList<FactEntry> Search(WorkspaceInfo workspace, string query, int top = 5) => SearchForRoot(workspace.Root, query, top);

    public IReadOnlyList<FactEntry> SearchForRoot(string root, string query, int top = 5)
    {
        var terms = Tokenize(query);
        if (terms.Count == 0) return [];
        return [.. ListActiveForRoot(root)
            .Select(fact =>
            {
                var haystack = Tokenize($"{fact.Content} {fact.Topic}");
                var overlap = terms.Count(haystack.Contains);
                return (Fact: fact, Score: overlap / (double)terms.Count);
            })
            .Where(pair => pair.Score > 0)
            .OrderByDescending(pair => pair.Score)
            .ThenByDescending(pair => pair.Fact.Confidence)
            .Take(Math.Clamp(top, 1, 20))
            .Select(pair => pair.Fact)];
    }

    /// <summary>删除已过期事实，返回清除数量（由保存/反思等时机顺带调用）。</summary>
    public int PurgeExpired(WorkspaceInfo workspace) => PurgeExpiredForRoot(workspace.Root);

    public int PurgeExpiredForRoot(string root)
    {
        lock (ProcessGate)
        using (CrossProcessLock.Acquire(root))
        {
            var now = DateTimeOffset.UtcNow;
            var facts = Load(root);
            var removed = facts.RemoveAll(f => f.ExpiresAt is { } at && at <= now);
            if (removed > 0) Save(root, facts);
            return removed;
        }
    }

    internal static IReadOnlyList<string> Tokenize(string text) =>
        text.ToLowerInvariant()
            .Split([.. new[] { ' ', '\t', '\n', '\r', ',', '，', '。', '、', ';', '；', ':', '：', '(', ')', '"', '\'' }],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 0)
            .Distinct()
            .ToList();

    private static List<FactEntry> Load(string root)
    {
        var file = FactsFileForRoot(root);
        if (!File.Exists(file)) return [];
        try
        {
            var json = File.ReadAllText(file);
            var facts = JsonSerializer.Deserialize(json, HaoyueJsonContext.Default.ListFactEntry);
            return facts ?? [];
        }
        catch (JsonException)
        {
            // 损坏的事实文件从空开始重建，不阻断回合。
            return [];
        }
    }

    private static void Save(string root, List<FactEntry> facts)
    {
        var file = FactsFileForRoot(root);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var json = JsonSerializer.Serialize(facts, HaoyueJsonContext.Default.ListFactEntry);
        // 原子写：同目录临时文件 + 替换，崩溃不产生半截文件。
        var temp = file + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>
    /// 跨进程互斥：对 <c>&lt;root&gt;/.haoyue/facts.lock</c> 独占打开 + 短重试。
    /// 文件锁是全平台可移植的（命名 Mutex 在部分平台不可用），获取失败按超时处理，
    /// 让调用方以「读-改-写最后一次胜出」的旧行为继续——锁是尽力而为的增强而非硬门槛。
    /// </summary>
    private sealed class CrossProcessLock : IDisposable
    {
        private const int MaxAttempts = 10;
        private static readonly TimeSpan AttemptInterval = TimeSpan.FromMilliseconds(200);

        private readonly FileStream? _stream;

        private CrossProcessLock(FileStream? stream) => _stream = stream;

        public static CrossProcessLock Acquire(string root)
        {
            var lockFile = Path.Combine(root, ".haoyue", "facts.lock");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
                for (var attempt = 0; attempt < MaxAttempts; attempt++)
                {
                    try
                    {
                        return new CrossProcessLock(new FileStream(
                            lockFile, FileMode.OpenOrCreate,
                            FileAccess.ReadWrite, FileShare.None));
                    }
                    catch (IOException) when (attempt < MaxAttempts - 1)
                    {
                        Thread.Sleep(AttemptInterval);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return new CrossProcessLock(null);
        }

        public void Dispose()
        {
            try { _stream?.Dispose(); } catch (IOException) { }
        }
    }
}
