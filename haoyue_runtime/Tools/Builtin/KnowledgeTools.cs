using System.Text;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Prompts;

namespace Haoyue.Runtime.Tools.Builtin;

public sealed class KnowledgeSearchTool(KnowledgeStore knowledge, IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override bool RequiresWorkspace => false;
    public override string Name => "knowledge_search";
    public override string StatusLabel => "Searching knowledge";

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("query", ToolSchema.String("Search terms (Chinese or English). Entries containing any term are returned, best matches first."), true),
        ("limit", ToolSchema.Integer("Maximum entries to return, default 8"), false));

    public override Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var query = GetString(arguments, "query");
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult(ToolResult.Fail("query is required."));

        var limit = Math.Clamp(GetInt(arguments, "limit") ?? 8, 1, 20);
        var scope = HaoyueDatabase.ScopeKey(context.Workspace);
        var matches = knowledge.Search(scope, query, limit);
        if (matches.Count == 0)
            return Task.FromResult(ToolResult.Ok(
                "No knowledge entries matched. Use knowledge_save to record useful findings for future sessions.",
                "No knowledge matches"));

        var sb = new StringBuilder();
        foreach (var entry in matches)
        {
            sb.Append('#').Append(entry.Id).Append(' ').AppendLine(entry.Title);
            if (!string.IsNullOrEmpty(entry.Tags))
                sb.Append("   Tags: ").AppendLine(entry.Tags);
            var content = entry.Content.Length > 600 ? entry.Content[..600] + "…" : entry.Content;
            sb.Append("   ").AppendLine(content);
        }
        return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd(), $"{matches.Count} knowledge entries matched"));
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

        if (content.Length > 8000)
            return Task.FromResult(ToolResult.Fail($"content is too long ({content.Length} chars); keep entries under 8000 characters."));

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
