using System.Text.Json.Nodes;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;

namespace Haoyue.Runtime.Tools.Builtin;

/// <summary>
/// Allows the AI agent to explicitly define, update, and track high-level milestone plan steps.
/// </summary>
public sealed class PlanTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override string Name => "update_plan";
    public override bool RequiresWorkspace => false;
    public override string StatusLabel => "Updating plan";

    public override JsonObject ParameterSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["steps"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "The list of 2-5 high-level plan steps/milestones for the task.",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Short, clear title for the step (e.g. '浏览项目结构与核心模块')" },
                        ["status"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray { (JsonNode)JsonValue.Create("pending")!, (JsonNode)JsonValue.Create("in_progress")!, (JsonNode)JsonValue.Create("completed")! },
                            ["description"] = "Current status of this step"
                        },
                        ["detail"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Optional short detail/progress note for this step"
                        }
                    },
                    ["required"] = new JsonArray { (JsonNode)JsonValue.Create("title")!, (JsonNode)JsonValue.Create("status")! }
                }
            },
            ["explanation"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Optional brief explanation of why the plan was updated"
            }
        },
        ["required"] = new JsonArray { (JsonNode)JsonValue.Create("steps")! }
    };

    public override Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var stepsNode = arguments["steps"] as JsonArray;
        if (stepsNode is null || stepsNode.Count == 0)
            return Task.FromResult(ToolResult.Fail("steps must be a non-empty array"));

        var explanation = GetString(arguments, "explanation");
        var stepsJson = stepsNode.ToJsonString();

        var steps = new List<TurnPlanStep>();
        foreach (var node in stepsNode)
        {
            if (node is not JsonObject item) continue;
            var title = item["title"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(title)) continue;
            var status = item["status"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(status))
            {
                var normalized = NormalizeStatus(status);
                if (normalized is null)
                {
                    // Schema enum only advises; execution validates. Letting an arbitrary
                    // string through would permanently break plan-finished detection
                    // (TurnPlanStep.Completed only recognizes canonical values).
                    return Task.FromResult(ToolResult.Fail(
                        $"Unknown step status \"{status.Trim()}\". Allowed values: pending, in_progress, completed."));
                }
                status = normalized;
            }
            steps.Add(new TurnPlanStep(
                title.Trim(),
                string.IsNullOrWhiteSpace(status) ? "pending" : status.Trim(),
                item["detail"]?.GetValue<string>()));
        }

        context.PlanTracker?.Update(steps, explanation);
        context.Events.Publish(new PlanUpdatedEvent(stepsJson, explanation));
        var completed = steps.Count(s => s.Completed);
        var summary = $"Plan updated: {completed}/{steps.Count} steps completed";

        // Echo the runtime-observed plan state so the model sees its own progress on the
        // next step without having to reconstruct it from history.
        var output = $"Plan updated successfully with {steps.Count} steps ({completed} completed).";
        if (context.PlanTracker is not null)
            output += $"\n当前计划状态：\n{context.PlanTracker.Render()}";

        return Task.FromResult(ToolResult.Ok(output, summary));
    }

    /// <summary>
    /// Maps the common status spellings models actually emit (including trailing marks
    /// like "done ✅" or localized forms) onto the canonical enum. Returns null for
    /// genuinely unknown values so the caller can reject them with a corrective error.
    /// </summary>
    internal static string? NormalizeStatus(string status)
    {
        var trimmed = status.Trim().TrimEnd('✅', '✔', '☑', '。', '.', '!', ' ', '\t').Trim().TrimEnd('✅', '✔', '☑');
        return trimmed.ToLowerInvariant() switch
        {
            "pending" or "todo" or "not_started" or "not-started" or "未开始" or "待办" => "pending",
            "in_progress" or "in-progress" or "inprogress" or "doing" or "active" or "进行中" => "in_progress",
            "completed" or "complete" or "done" or "finished" or "resolved" or "已完成" or "完成" => "completed",
            _ => null,
        };
    }
}
