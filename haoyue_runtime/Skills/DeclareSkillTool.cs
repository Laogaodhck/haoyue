using System.Text.Json.Nodes;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;

namespace Haoyue.Runtime.Skills;

/// <summary>
/// N5 stage 1: lets the model load a trigger-gated skill that keyword matching
/// missed (措辞不含触发词时的召回兜底). Read-only by definition — it changes
/// prompt composition for the remaining steps of the current turn, never files.
/// </summary>
public sealed class DeclareSkillTool(ISkillManager skills, IPromptProvider prompts) : ITool
{
    public string Name => "declare_skill";
    public string Description =>
        prompts.TryGet("tool/declare_skill")
        ?? "Declare (load) a listed skill for the rest of this turn.";
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("name", ToolSchema.String("Skill name from the available-skills catalog"), true));
    public bool Mutating => false;
    public string StatusLabel => "加载技能";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var name = arguments["name"]?.GetValue<string>()?.Trim() ?? "";
        if (name.Length == 0)
            return Task.FromResult(ToolResult.Fail("params.name is required"));

        return Task.FromResult(skills.DeclareForTurn(context.Workspace, name)
            ? ToolResult.Ok($"技能 {name} 已加载，从下一轮对话起生效。", $"加载技能 {name}")
            : ToolResult.Fail($"技能 {name} 不存在、未启用或不在可用技能目录中。"));
    }
}
