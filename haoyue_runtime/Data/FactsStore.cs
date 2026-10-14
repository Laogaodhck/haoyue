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
    /// <summary>取代发生的时间：墓碑只保留到审计价值耗尽（见 <see cref="FactsStore.TombstoneRetentionDays"/>）。</summary>
    public DateTimeOffset? SupersededAt { get; set; }
    /// <summary>最近一次随系统提示注入的时间；长期未被注入触发置信度衰减。</summary>
    public DateTimeOffset? LastUsedAt { get; set; }
    /// <summary>被注入过多少次——「记忆只进不出」里的使用侧信号。</summary>
    public int UseCount { get; set; }
    /// <summary>被否认过多少次（用户纠正、或 fact_forget 传 countAsWrong）；每次降置信度。</summary>
    public int DenialCount { get; set; }
}

/// <summary>
/// 记忆分层中的「项目事实层」：介于 MEMORY.md（用户策展的长期记忆）与知识库
/// （大容量文档检索）之间，存放带来源与置信度的短句事实，按工作区隔离持久化
/// （<c>&lt;workspace&gt;/.haoyue/facts.json</c>），到期自动失效。写入为原子替换。
/// </summary>
public sealed class FactsStore
{
    /// <summary>单工作区<b>活跃</b>事实条数上限：事实层是提示注入的一部分，必须保持精炼。</summary>
    public const int CapacityPerWorkspace = 100;

    /// <summary>
    /// 墓碑（被取代事实）保留天数。留痕是为了审计与「同类错误」反思，但它们不计入
    /// <see cref="CapacityPerWorkspace"/>，到期即物理删除——否则墓碑会挤掉活跃事实。
    /// </summary>
    public const int TombstoneRetentionDays = 30;

    /// <summary>未被注入达该天数即降一档置信度：记忆要能被遗忘，不能只进不出。</summary>
    public const int UnusedDecayDays = 45;

    /// <summary>每次衰减/下限值：降到下限的事实会在容量淘汰里最先出局。</summary>
    public const double UnusedDecayStep = 0.1;

    public const double ConfidenceFloor = 0.2;

    /// <summary>一次否认扣减的置信度：一次纠正不足以抹掉事实，但要让它退到候选末尾。</summary>
    public const double DenialDemotion = 0.4;

    /// <summary>注入时保底带上的「用户明确给出」事实条数：相关性筛选不能把用户指令筛掉。</summary>
    public const int MaxPinnedFacts = 8;

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
                target.SupersededAt = DateTimeOffset.UtcNow;
            }

            facts.Add(entry);
            EnforceCapacity(facts);
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

    /// <summary>
    /// 按词条重合度排序的简单检索（事实层量小，无需全文索引）。命中即计一次使用，
    /// 让「被取用过」与「被注入过」都进入衰减判定——只写不改的置信度是假的置信度。
    /// </summary>
    public IReadOnlyList<FactEntry> Search(WorkspaceInfo workspace, string query, int top = 5) => SearchForRoot(workspace.Root, query, top);

    public IReadOnlyList<FactEntry> SearchForRoot(string root, string query, int top = 5)
    {
        var terms = Tokenize(query);
        if (terms.Count == 0) return [];
        var limit = Math.Clamp(top, 1, 20);
        lock (ProcessGate)
        using (CrossProcessLock.Acquire(root))
        {
            var now = DateTimeOffset.UtcNow;
            var facts = Load(root);
            var hits = ActiveIn(facts, now)
                .Select(fact =>
                {
                    var haystack = Tokenize($"{fact.Content} {fact.Topic}");
                    var overlap = terms.Count(haystack.Contains);
                    return (Fact: fact, Score: overlap / (double)terms.Count);
                })
                .Where(pair => pair.Score > 0)
                .OrderByDescending(pair => pair.Score)
                .ThenByDescending(pair => pair.Fact.Confidence)
                .Take(limit)
                .Select(pair => pair.Fact)
                .ToList();
            foreach (var fact in hits)
            {
                fact.UseCount += 1;
                fact.LastUsedAt = now;
            }
            if (hits.Count > 0) Save(root, facts);
            return hits;
        }
    }

    /// <summary>活跃集（未取代且未过期），保留原顺序供回写。</summary>
    private static List<FactEntry> ActiveIn(List<FactEntry> facts, DateTimeOffset now) =>
        [.. facts.Where(f => f.SupersededBy is null && (f.ExpiresAt is null || f.ExpiresAt > now))];

    /// <summary>
    /// 记忆生命周期清理：删过期、删保留期已尽的墓碑、按「长期未用」降置信度、
    /// 只对<b>活跃</b>事实做容量淘汰。返回删除行数（仅在有变化时落盘）。
    /// 由注入/固化等时机顺带调用，让「只进不出」的事实层能自己瘦下来。
    /// </summary>
    public int Compact(WorkspaceInfo workspace) => CompactForRoot(workspace.Root);

    /// <summary>清理并衰减，返回删除行数。写路径内部（Add）也会走同一套容量规则。</summary>
    public int CompactForRoot(string root)
    {
        lock (ProcessGate)
        using (CrossProcessLock.Acquire(root))
        {
            var now = DateTimeOffset.UtcNow;
            var facts = Load(root);
            var changed = DecayUnused(facts, now);
            var removed = facts.RemoveAll(f => IsRetired(f, now));
            changed |= EnforceCapacity(facts);
            if (removed > 0 || changed) Save(root, facts);
            return removed;
        }
    }

    /// <summary>过期事实，以及保留期已尽的墓碑——墓碑只用于审计，到期即物理删除。</summary>
    private static bool IsRetired(FactEntry fact, DateTimeOffset now)
    {
        if (fact.SupersededBy is not null)
        {
            var since = fact.SupersededAt ?? fact.CreatedAt;
            return since.AddDays(TombstoneRetentionDays) <= now;
        }
        return fact.ExpiresAt is { } at && at <= now;
    }

    /// <summary>
    /// 长期未被注入的事实逐档降置信度（用户明确给出的事实豁免：那是指令不是记忆）。
    /// 判定基准是最近一次注入时间，从未注入过的新事实按创建时间起算。
    /// </summary>
    private static bool DecayUnused(List<FactEntry> facts, DateTimeOffset now)
    {
        var changed = false;
        foreach (var fact in facts.Where(f => f.SupersededBy is null && f.Source != "user"))
        {
            var lastTouched = fact.LastUsedAt ?? fact.CreatedAt;
            if (lastTouched.AddDays(UnusedDecayDays) > now) continue;
            if (fact.Confidence <= ConfidenceFloor + UnusedDecayStep / 2) continue;
            fact.Confidence = Math.Round(Math.Max(ConfidenceFloor, fact.Confidence - UnusedDecayStep), 2);
            // 衰减后重新计时，避免每个回合都再扣一档（衰减是「按周期」而非「按回合」的）。
            fact.LastUsedAt = now;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// 容量淘汰只看活跃事实：墓碑不计入上限，也绝不因为置信度高而把活跃事实挤掉。
    /// 淘汰顺序为低置信优先、其次最旧创建。返回是否有变化。
    /// </summary>
    private static bool EnforceCapacity(List<FactEntry> facts)
    {
        var active = facts.Where(f => f.SupersededBy is null).ToList();
        if (active.Count <= CapacityPerWorkspace) return false;
        var doomed = active
            .OrderBy(f => f.Confidence)
            .ThenBy(f => f.CreatedAt)
            .Take(active.Count - CapacityPerWorkspace)
            .Select(f => f.Id)
            .ToHashSet(StringComparer.Ordinal);
        facts.RemoveAll(f => doomed.Contains(f.Id));
        return true;
    }

    /// <summary>
    /// 否认一条事实（用户纠正说「这条不对」时的落点）：降置信度并记次，而不是删除。
    /// 留着是为了让「同类错误」在进化反思里可见；降到下限后会被容量淘汰自然出局。
    /// 命中返回被降级的事实，未命中返回 null。
    /// </summary>
    public FactEntry? Demote(string root, string idOrContent, string? reason = null)
    {
        lock (ProcessGate)
        using (CrossProcessLock.Acquire(root))
        {
            var facts = Load(root);
            var target = facts.FirstOrDefault(f => f.SupersededBy is null && MatchesFact(f, idOrContent));
            if (target is null) return null;
            target.Confidence = Math.Round(
                Math.Max(ConfidenceFloor, target.Confidence - DenialDemotion), 2);
            target.DenialCount += 1;
            if (!string.IsNullOrWhiteSpace(reason)) target.Topic = target.Topic ?? reason!.Trim();
            Save(root, facts);
            return target;
        }
    }

    /// <summary>
    /// 本回合要注入的事实：先从活跃集里按相关性取 top-K，再保底带上若干条用户明确给出的事实，
    /// 并把最终选中的事实标记为「已使用」。返回的 <see cref="FactSelection"/> 带上活跃总数，
    /// 让注入文本能如实说明「还有 N 条没带进来，可用 fact_search 取」。
    /// </summary>
    public FactSelection SelectForInjection(string root, string? query, int topK)
    {
        topK = Math.Clamp(topK, 1, 50);
        lock (ProcessGate)
        using (CrossProcessLock.Acquire(root))
        {
            var now = DateTimeOffset.UtcNow;
            var facts = Load(root);
            var changed = DecayUnused(facts, now);
            changed |= facts.RemoveAll(f => IsRetired(f, now)) > 0;
            changed |= EnforceCapacity(facts);

            var active = facts
                .Where(f => f.SupersededBy is null && (f.ExpiresAt is null || f.ExpiresAt > now))
                .ToList();

            IReadOnlyList<string> queryTerms = string.IsNullOrWhiteSpace(query) ? [] : Tokenize(query!);
            var terms = new HashSet<string>(queryTerms, StringComparer.OrdinalIgnoreCase);
            var ranked = active
                .Select(f => (Fact: f, Score: Overlap(terms, Tokenize($"{f.Content} {f.Topic}"))))
                .OrderByDescending(pair => pair.Score)
                .ThenByDescending(pair => pair.Fact.Confidence)
                .ThenByDescending(pair => pair.Fact.CreatedAt)
                .ToList();

            var picked = ranked.Where(pair => pair.Score > 0).Take(topK).Select(pair => pair.Fact).ToList();
            // 相关命中不足 K 条时按置信度补位：小库不该因为措辞不同就一条都不注入。
            if (picked.Count < topK)
            {
                var chosen = picked.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
                picked.AddRange(ranked
                    .Where(pair => !chosen.Contains(pair.Fact.Id))
                    .Take(topK - picked.Count)
                    .Select(pair => pair.Fact));
            }
            // 用户明确给出的事实是指令而非记忆：相关性为 0 也要在场，但只保底带上限条数。
            var pinned = active
                .Where(f => f.Source == "user" && !picked.Contains(f))
                .OrderByDescending(f => f.Confidence)
                .ThenByDescending(f => f.CreatedAt)
                .Take(MaxPinnedFacts);
            picked.AddRange(pinned);

            foreach (var fact in picked)
            {
                fact.UseCount += 1;
                fact.LastUsedAt = now;
                changed = true;
            }
            if (changed) Save(root, facts);
            return new FactSelection(picked, active.Count, topK);
        }
    }

    /// <summary>查询词条在事实文本里的覆盖比例（0..1）。与 Search 的重合度口径一致。</summary>
    private static double Overlap(HashSet<string> terms, IReadOnlyList<string> haystack)
        => terms.Count == 0 ? 0 : terms.Count(haystack.Contains) / (double)terms.Count;

    /// <summary>注入用的事实选择结果：选中集、活跃总数与本批上限。</summary>
    public sealed record FactSelection(IReadOnlyList<FactEntry> Facts, int ActiveTotal, int TopK);

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
