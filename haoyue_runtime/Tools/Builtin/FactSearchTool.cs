using System.Text.Json.Nodes;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;

namespace Haoyue.Runtime.Tools.Builtin;

/// <summary>
/// On-demand retrieval over the workspace facts layer. Complements the every-turn
/// injection: when the injected subset does not answer the current question, the
/// model can search all active facts (including low-confidence ones) here. Results
/// carry the #id so a wrong fact can be removed via fact_forget. Not Mutating —
/// pure read.
/// </summary>
public sealed class FactSearchTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override string Name => "fact_search";
    public override bool RequiresWorkspace => true;
    public override string StatusLabel => "Searching facts";

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("query", ToolSchema.String("检索关键词，空格分词；命中按重合度与置信度排序"), true),
        ("top", ToolSchema.Integer("返回条数上限（默认 5，最大 20）"), false));

    public override Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var query = GetString(arguments, "query")?.Trim();
        if (string.IsNullOrEmpty(query))
            return Task.FromResult(ToolResult.Fail("query is required"));
        var top = Math.Clamp(GetInt(arguments, "top") ?? 5, 1, 20);

        try
        {
            var store = new FactsStore();
            var hits = store.SearchForRoot(context.Workspace.Root, query, top);
            if (hits.Count == 0)
                return Task.FromResult(ToolResult.Ok(
                    $"没有命中任何事实（query: {query}）。可用 fact_save 沉淀新事实。",
                    "No facts matched"));

            var lines = hits.Select(f =>
                $"- #{f.Id} [{f.Topic ?? "general"}] ({f.Source}, 置信度 {f.Confidence:0.0}{(f.ExpiresAt is { } at ? $"，过期 {at:yyyy-MM-dd}" : "")}) {f.Content}");
            var output = $"命中 {hits.Count} 条事实：\n" + string.Join("\n", lines)
                + "\n用 fact_forget 并传入 #id 可撤销过期或错误的事实。";
            return Task.FromResult(ToolResult.Ok(output, $"Found {hits.Count} facts"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ToolResult.Fail($"无法读取事实文件：{ex.Message}"));
        }
    }
}

/// <summary>
/// Removes a wrong or outdated fact by its #id (from fact_search) or by exact content.
/// Fills the gap where the model could write facts but never retract them — cleanup
/// previously depended on TTL expiry or capacity eviction. Not Mutating, following
/// the fact_save/memory_save precedent: facts-layer metadata management stays usable
/// in readonly modes.
/// </summary>
public sealed class FactForgetTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override string Name => "fact_forget";
    public override bool RequiresWorkspace => true;
    public override string StatusLabel => "Removing fact";

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("id", ToolSchema.String("要删除的事实 #id（来自 fact_search），或与事实内容完全一致的原文"), true));

    public override Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var id = GetString(arguments, "id")?.Trim();
        if (string.IsNullOrEmpty(id))
            return Task.FromResult(ToolResult.Fail("id is required"));

        try
        {
            var store = new FactsStore();
            return Task.FromResult(store.ForgetForRoot(context.Workspace.Root, id)
                ? ToolResult.Ok($"事实已删除：{id}", "Fact removed")
                : ToolResult.Fail(
                    $"没有找到该事实（{id}）。请先用 fact_search 确认 #id；删除按完整 #id 或完整内容原文匹配。"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ToolResult.Fail($"无法写入事实文件：{ex.Message}"));
        }
    }
}
