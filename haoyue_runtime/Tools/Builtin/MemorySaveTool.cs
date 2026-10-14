using System.Text.Json.Nodes;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;

namespace Haoyue.Runtime.Tools.Builtin;

/// <summary>
/// Lets the model persist durable facts and preferences to the workspace memory file
/// (MEMORY.md), which is injected into every turn's system prompt. Appends a dated
/// bullet instead of overwriting — the same file is user-curated through the desktop
/// memory editor, so blind replacement would destroy hand-written content.
/// Deliberately not Mutating: memory growth is additive, so the tool stays outside
/// the undo ledger and remains usable in readonly modes.
/// </summary>
public sealed class MemorySaveTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override string Name => "memory_save";
    public override bool RequiresWorkspace => true;
    public override string StatusLabel => "Saving memory";

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("content", ToolSchema.String("要记住的内容：用户偏好、项目约定、环境要点或重要决定，一句话即可。"), true),
        ("topic", ToolSchema.String("可选主题标签，如 build、convention、preference"), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var content = GetString(arguments, "content")?.Trim();
        if (string.IsNullOrEmpty(content))
            return ToolResult.Fail("content is required");
        var topic = GetString(arguments, "topic")?.Trim();

        var append = await MemoryFileWriter.AppendAsync(
            context.Workspace.MemoryFile, content, topic, ct).ConfigureAwait(false);
        if (append.Result == MemoryFileWriter.Status.Rejected)
            return ToolResult.Fail(append.Message ?? "无法写入工作区记忆");
        if (append.Result == MemoryFileWriter.Status.Duplicate)
            return ToolResult.Ok($"{append.Message}：\n{append.Line}", "Memory already recorded");

        var summary = $"已写入工作区记忆（MEMORY.md）：\n{append.Line}\n该记忆会在此工作区的后续回合自动注入系统提示。";
        if (append.Message is { } warning) summary += $"\n{warning}";
        return ToolResult.Ok(summary, "Memory saved");
    }
}
