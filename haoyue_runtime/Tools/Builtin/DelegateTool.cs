using System.Text.Json.Nodes;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Providers;

namespace Haoyue.Runtime.Tools.Builtin;

/// <summary>
/// Delegates a self-contained subtask to a fresh sub-agent and returns its
/// final answer. Marked mutating on purpose: sub-tasks may edit files, and the
/// mutating flag keeps delegation out of read-only parallel batches, which
/// serializes sub-agent execution.
/// </summary>
public sealed class DelegateTool(IPromptProvider prompts, IAgentDelegator delegator) : ITool
{
    public string Name => AgentDelegator.DelegateToolName;

    public string Description =>
        prompts.TryGet("tool/delegate_task")
        ?? "Delegate a self-contained subtask to a fresh sub-agent and return only its final answer.";

    public JsonObject ParameterSchema => ToolSchema.Object(
        ("task", ToolSchema.String("完整、自包含的子任务描述。子代理看不到当前对话历史，描述必须包含它需要的全部信息（文件、约束、期望产出）。"), true));

    public bool Mutating => true;

    public string StatusLabel => "Delegating subtask";

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var task = arguments["task"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(task))
            return ToolResult.Fail("delegate_task 需要 task 参数：描述要委派的完整子任务。");

        try
        {
            var result = await delegator.RunSubTaskAsync(context.Workspace, task, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result.Error))
                return ToolResult.Fail($"子任务失败：{result.Error}");
            if (result.Cancelled)
                return ToolResult.Fail("子任务已取消。");
            var output = result.Text.Length > 0
                ? context.Truncate(result.Text, "subtask output")
                : "（子任务完成，但没有返回文本。）";
            return ToolResult.Ok(output, "subtask completed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the whole turn is being cancelled — let the agent loop unwind
        }
        catch (LlmException ex)
        {
            return ToolResult.Fail($"子任务执行失败：{ex.Message}");
        }
    }
}
