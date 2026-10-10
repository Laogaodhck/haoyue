using System.Text.Json.Nodes;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;

namespace Haoyue.Runtime.Tools.Builtin;

/// <summary>
/// Lets the model persist a short, sourced project fact (with confidence and optional
/// TTL) into the workspace facts layer. Facts are injected into every subsequent turn's
/// system prompt and auto-expire, sitting between MEMORY.md (user-curated long-term
/// memory) and the knowledge base (large document retrieval). Not Mutating, following
/// the memory_save precedent: additive metadata that stays usable in readonly modes.
/// </summary>
public sealed class FactSaveTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override string Name => "fact_save";
    public override bool RequiresWorkspace => true;
    public override string StatusLabel => "Saving fact";

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("content", ToolSchema.String("要沉淀的事实，一句话：构建命令、部署约定、环境要点、关键决定等。"), true),
        ("topic", ToolSchema.String("可选主题标签，如 build、deploy、convention"), false),
        ("confidence", ToolSchema.String("置信度 0~1：用户明确说的填 1，推断出的填 0.5~0.8", "0.9", "0.8", "0.7", "0.5"), false),
        ("ttlDays", ToolSchema.Integer("可选有效期（天）：临时事实设 1-30，长期约定不填"), false));

    public override Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var content = GetString(arguments, "content")?.Trim();
        if (string.IsNullOrEmpty(content))
            return Task.FromResult(ToolResult.Fail("content is required"));

        var topic = GetString(arguments, "topic");
        double? confidence = arguments["confidence"] is JsonValue confValue
            && confValue.TryGetValue<double>(out var conf)
            ? Math.Clamp(conf, 0, 1) : null;
        int? ttlDays = GetInt(arguments, "ttlDays");

        try
        {
            var store = new FactsStore();
            var entry = store.Add(
                context.Workspace.Root, content, topic,
                source: "agent",
                confidence: confidence ?? 0.8,
                ttlDays: ttlDays);

            var scope = entry.ExpiresAt is { } at ? $"，有效期至 {at:yyyy-MM-dd}" : "，长期有效";
            return Task.FromResult(ToolResult.Ok(
                $"事实已沉淀（{entry.Id}，置信度 {entry.Confidence:0.0}{scope}）：\n{entry.Content}\n该事实会在此工作区的后续回合自动注入系统提示；用 fact_search 可按需检索。",
                "Fact saved"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Task.FromResult(ToolResult.Fail($"无法写入事实：{ex.Message}"));
        }
    }
}
