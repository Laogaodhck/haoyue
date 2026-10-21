using System.Text;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;

namespace Haoyue.Runtime.Tools.Builtin;

/// <summary>
/// Knowledge search with an optional semantic layer: when an embedding-capable
/// provider is configured the query is answered by hybrid (lexical + cosine) ranking,
/// otherwise the plain lexical path runs unchanged.
/// </summary>
public sealed class KnowledgeSearchTool(
    KnowledgeStore knowledge,
    KnowledgeSemanticIndex semantic,
    IConfigStore configStore,
    IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override bool RequiresWorkspace => false;
    public override string Name => "knowledge_search";
    public override string StatusLabel => "Searching knowledge";

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("query", ToolSchema.String("Search terms (Chinese or English). Entries containing any term are returned, best matches first."), true),
        ("limit", ToolSchema.Integer("Maximum entries to return, default 8"), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var query = GetString(arguments, "query");
        if (string.IsNullOrWhiteSpace(query))
            return ToolResult.Fail("query is required.");

        var limit = Math.Clamp(GetInt(arguments, "limit") ?? 8, 1, 20);
        var scope = HaoyueDatabase.ScopeKey(context.Workspace);
        var (matches, outcome, corpusSize) = await SearchAsync(scope, query, limit, ct).ConfigureAwait(false);
        // 语义层失效或索引没建全都会显式写进 summary：用户和进化引擎都需要知道
        // 「这次是纯词法结果」，而不是看到一个看起来一样的答案。
        var caveat = SemanticCaveat(outcome);
        if (matches.Count == 0)
        {
            // 空结果是 Success == true，缺陷聚合器看不见——单独发一条 miss 事件，
            // 让「知识库覆盖不足」成为可被反思回合处理的信号。
            context.Events.Publish(new RetrievalMissEvent(Name, query.Trim(), corpusSize, caveat));
            return ToolResult.Ok(
                "No knowledge entries matched. Use knowledge_save to record useful findings for future sessions.",
                caveat is null ? "No knowledge matches" : $"No knowledge matches（{caveat}）");
        }

        var sb = new StringBuilder();
        // 出处标注：导入类条目带来源与分片序号，knowledge_save 直接写的条目没有来源——
        // 模型回答时可引用「来自哪份文档」，幻觉可归因、可复核。
        var attribution = knowledge.AttributionMap(scope);
        foreach (var entry in matches)
        {
            sb.Append('#').Append(entry.Id).Append(' ').AppendLine(entry.Title);
            if (attribution.TryGetValue(entry.Id, out var source)
                && !string.IsNullOrWhiteSpace(source.SourceTitle))
            {
                sb.Append("   来源：").Append(source.SourceTitle);
                if (source.ChunkOrdinal is > 0)
                    sb.Append(" · 第 ").Append(source.ChunkOrdinal).Append(" 部分");
                sb.AppendLine();
            }
            if (!string.IsNullOrEmpty(entry.Tags))
                sb.Append("   Tags: ").AppendLine(entry.Tags);
            var content = entry.Content.Length > 600 ? entry.Content[..600] + "…" : entry.Content;
            sb.Append("   ").AppendLine(content);
        }
        // H-B: 20 条 ×600 字符的硬切在 32K 窗口模型上会超预算 3 倍——最终输出收敛进
        // 自适应窗口预算，且 head+tail 保证靠后的条目不被整段丢弃。
        // 4.5 触发式遥测：语料规模随 tool_done 持久化——后续「>800 条走 FTS5 预筛」
        // 的动刀决策靠这个数判断，不需要再埋独立的统计通道。
        var summary = $"{matches.Count} knowledge entries matched (corpus {corpusSize})";
        if (caveat is not null) summary += $"（{caveat}）";
        return ToolResult.Ok(
            context.TruncateTailPrioritized(sb.ToString().TrimEnd(), "knowledge results"),
            summary);
    }

    /// <summary>语义层当前不可信的原因（供 summary 与缺陷统计消费）；语义完全正常时返回 null。</summary>
    private static string? SemanticCaveat(HybridSearchOutcome outcome)
    {
        if (outcome.EmbeddingError is { Length: > 0 } message)
            return $"语义检索本次失效，结果是纯词法排序：{message}";
        if (outcome.DisabledReason is { Length: > 0 } reason)
            return $"语义检索未启用，结果是纯词法排序：{reason}";
        if (outcome.PendingVectors > 0)
            return $"语义索引未建全，还有 {outcome.PendingVectors} 条没有向量（请在知识中心点「建立语义索引」，或调用 knowledge.semantic.build 分批补齐）";
        return null;
    }

    private async Task<(IReadOnlyList<KnowledgeEntry> Matches, HybridSearchOutcome Outcome, int CorpusSize)> SearchAsync(
        string scope, string query, int limit, CancellationToken ct)
    {
        var entries = knowledge.LoadScope(scope);
        var route = semantic.CreateRoute(configStore.Config);
        if (route is null)
            // 「没配模型」和「配了但这次坏了」要分开说：前者是配置问题，用户能自己修。
            return (KnowledgeSearchRanker.Rank(entries, query, limit),
                new HybridSearchOutcome([], DisabledReason: semantic.Status(scope, entries, configStore.Config).Reason),
                entries.Count);

        try
        {
            var outcome = await semantic.SearchHybridAsync(
                scope, entries, query, limit, route.Client, route.CacheKey, ct).ConfigureAwait(false);
            return (outcome.Entries, outcome, entries.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (KnowledgeSearchRanker.Rank(entries, query, limit),
                new HybridSearchOutcome([], EmbeddingError: ex.Message), entries.Count);
        }
    }
}

public sealed class KnowledgeSaveTool(KnowledgeStore knowledge, IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override bool RequiresWorkspace => false;
    public override string Name => "knowledge_save";
    public override string StatusLabel => "Saving knowledge";

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("title", ToolSchema.String("Short stable name for the fact, e.g. '构建命令' or 'User prefers pnpm'. Saving again with the same title updates the entry."), true),
        ("content", ToolSchema.String("The knowledge itself: facts, decisions, concrete commands/paths, pitfalls and their fixes. Keep it self-contained."), true),
        ("tags", ToolSchema.String("Optional comma-separated tags, e.g. build,preferences"), false));

    public override Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var title = GetString(arguments, "title");
        var content = GetString(arguments, "content");
        if (string.IsNullOrWhiteSpace(title)) return Task.FromResult(ToolResult.Fail("title is required."));
        if (string.IsNullOrWhiteSpace(content)) return Task.FromResult(ToolResult.Fail("content is required."));

        if (content.Length > KnowledgeStore.MaxEntryContentChars)
            return Task.FromResult(ToolResult.Fail(
                $"content is too long ({content.Length} chars); keep entries under {KnowledgeStore.MaxEntryContentChars} characters."));

        var scope = HaoyueDatabase.ScopeKey(context.Workspace);
        var (entry, created) = knowledge.Save(scope, title, content, GetString(arguments, "tags"));
        var action = created ? "created" : "updated";
        return Task.FromResult(ToolResult.Ok(
            $"Knowledge #{entry.Id} {action}: {entry.Title}",
            $"{(created ? "Saved new" : "Updated")} knowledge: {entry.Title}"));
    }
}

public sealed class KnowledgeForgetTool(KnowledgeStore knowledge, IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override bool RequiresWorkspace => false;
    public override string Name => "knowledge_forget";
    public override string StatusLabel => "Removing knowledge";

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("id", ToolSchema.Integer("Entry #id from knowledge_search"), true));

    public override Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var id = GetInt(arguments, "id");
        if (id is null) return Task.FromResult(ToolResult.Fail("id is required."));

        var scope = HaoyueDatabase.ScopeKey(context.Workspace);
        return Task.FromResult(knowledge.Delete(scope, id.Value)
            ? ToolResult.Ok($"Knowledge #{id} removed.", $"Removed knowledge #{id}")
            : ToolResult.Fail($"No knowledge entry #{id} in this workspace. Use knowledge_search to list current entries."));
    }
}
