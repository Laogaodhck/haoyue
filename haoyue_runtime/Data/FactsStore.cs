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

    public static string FactsFile(WorkspaceInfo workspace) => FactsFileForRoot(workspace.Root);

    public static string FactsFileForRoot(string root) =>
        Path.Combine(root, ".haoyue", "facts.json");

    public IReadOnlyList<FactEntry> ListActive(WorkspaceInfo workspace) => ListActiveForRoot(workspace.Root);

    public IReadOnlyList<FactEntry> ListActiveForRoot(string root)
    {
        var all = Load(root);
        var now = DateTimeOffset.UtcNow;
        return [.. all.Where(f => f.ExpiresAt is null || f.ExpiresAt > now)];
    }

    public FactEntry Add(
        string root, string content, string? topic = null,
        string source = "agent", double confidence = 0.8, int? ttlDays = null)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("事实内容不能为空。", nameof(content));
        confidence = Math.Clamp(confidence, 0, 1);

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

    public bool Forget(WorkspaceInfo workspace, string id) => ForgetForRoot(workspace.Root, id);

    public bool ForgetForRoot(string root, string id)
    {
        var facts = Load(root);
        var removed = facts.RemoveAll(f => f.Id.Equals(id, StringComparison.Ordinal)
            || f.Content.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return false;
        Save(root, facts);
        return true;
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
        var now = DateTimeOffset.UtcNow;
        var facts = Load(root);
        var removed = facts.RemoveAll(f => f.ExpiresAt is { } at && at <= now);
        if (removed > 0) Save(root, facts);
        return removed;
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
}
