using System.Text.Json.Nodes;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Providers;

namespace Haoyue.Runtime.Tools.Builtin;

/// <summary>
/// Fans several self-contained subtasks out to parallel sub-agents and returns a
/// per-task aggregated report. One failed subtask does not fail the batch — the
/// report marks it so the caller can retry selectively.
/// </summary>
public sealed class DelegateTasksTool(IPromptProvider prompts, IAgentDelegator delegator) : ITool
{
    public const int MinTasks = 2;
    public const int MaxTasks = 6;

    public string Name => AgentDelegator.ParallelDelegateToolName;

    public string Description =>
        prompts.TryGet("tool/delegate_tasks")
        ?? "Fan out 2-6 self-contained subtasks to parallel sub-agents and return their aggregated results.";

    public JsonObject ParameterSchema => ToolSchema.Object(
        ("tasks", ToolSchema.Array(
            $"完整、自包含的子任务描述列表（{MinTasks}-{MaxTasks} 个）。每个子代理都看不到当前对话历史，描述必须自包含。",
            ToolSchema.String("子任务描述")), true));

    public bool Mutating => true;

    public string StatusLabel => "Delegating subtasks in parallel";

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        if (arguments["tasks"] is not JsonArray array || array.Count == 0)
            return ToolResult.Fail($"delegate_tasks 需要 tasks 参数：{MinTasks}-{MaxTasks} 个子任务描述的数组。");

        var tasks = array
            .Select(node => node?.GetValue<string>()?.Trim())
            .Where(task => !string.IsNullOrWhiteSpace(task))
            .Cast<string>()
            .ToList();

        if (tasks.Count < MinTasks)
            return ToolResult.Fail($"并行委派至少需要 {MinTasks} 个子任务；单个子任务请改用 delegate_task。");
        if (tasks.Count > MaxTasks)
            tasks = tasks.Take(MaxTasks).ToList();

        try
        {
            var results = await delegator.RunSubTasksAsync(context.Workspace, tasks, ct).ConfigureAwait(false);

            var report = new System.Text.StringBuilder();
            var failed = 0;
            for (var i = 0; i < tasks.Count; i++)
            {
                var result = i < results.Count ? results[i] : new AgentTurnResult("", false, "no result");
                var status = !string.IsNullOrWhiteSpace(result.Error) ? "失败" : result.Cancelled ? "已取消" : "完成";
                if (status != "完成") failed++;
                report.AppendLine($"## 子任务 {i + 1} — {status}");
                report.AppendLine(!string.IsNullOrWhiteSpace(result.Error)
                    ? $"错误：{result.Error}"
                    : string.IsNullOrWhiteSpace(result.Text) ? "（子任务完成，但没有返回文本。）" : result.Text);
                report.AppendLine();
            }
            var summary = $"{tasks.Count} 个并行子任务：{tasks.Count - failed} 完成，{failed} 失败/取消";
            return ToolResult.Ok(context.Truncate(report.ToString().TrimEnd(), "subtask output"), summary);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the whole turn is being cancelled — let the agent loop unwind
        }
        catch (LlmException ex)
        {
            return ToolResult.Fail($"并行子任务执行失败：{ex.Message}");
        }
    }
}
