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
///
/// 3.1/3.2：每个子任务占用回合级委派配额（agent.delegationMaxPerTurn，默认 8，
/// 超限显式报错），子任务消耗的 token/费用经共享台账汇入父回合预算；可选
/// timeout 参数给子任务独立超时——单个卡死子任务只损失自己的额度，不再拖死
/// 父回合的全部剩余预算。
/// </summary>
public sealed class DelegateTool(IPromptProvider prompts, IAgentDelegator delegator) : ITool
{
    public const int MinTimeoutSeconds = 60;
    public const int MaxTimeoutSeconds = 1800;

    public string Name => AgentDelegator.DelegateToolName;

    public string Description =>
        prompts.TryGet("tool/delegate_task")
        ?? "Delegate a self-contained subtask to a fresh sub-agent and return only its final answer.";

    public JsonObject ParameterSchema => ToolSchema.Object(
        ("task", ToolSchema.String("完整、自包含的子任务描述。子代理看不到当前对话历史，描述必须包含它需要的全部信息（文件、约束、期望产出）。"), true),
        ("role", ToolSchema.String($"可选角色预设：{AgentRole.ValidRoles}。命中时子代理注入角色纪律提示词并收敛工具白名单（如 reviewer 只读）。"), false),
        ("timeout_seconds", ToolSchema.Integer($"可选子任务独立超时（{MinTimeoutSeconds}-{MaxTimeoutSeconds} 秒）。缺省时跟随父回合的取消信号——单个可能卡死的子任务建议显式设置，超时只损失该子任务。"), false));

    public bool Mutating => true;

    public string StatusLabel => "Delegating subtask";

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var task = arguments["task"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(task))
            return ToolResult.Fail("delegate_task 需要 task 参数：描述要委派的完整子任务。");
        var role = arguments["role"]?.GetValue<string>()?.Trim();
        if (role is { Length: > 0 } && !AgentRole.Builtins.ContainsKey(role))
            return ToolResult.Fail($"未知角色「{role}」。可选：{AgentRole.ValidRoles}。");

        var scope = context.TurnScope;
        var quota = context.Agent.DelegationMaxPerTurn;
        if (scope is not null && !scope.TryConsumeDelegationSlot(quota))
            return ToolResult.Fail(
                $"本回合委派配额已用尽（agent.delegationMaxPerTurn = {quota}，已发起 {scope.DelegationCount} 个）。"
                + "请把剩余子任务留到下一轮，或自行完成收尾。");

        // 3.2 独立超时：父回合 CT + 子任务超时双源 linked；超时只取消本子任务，
        // 父回合照常继续（未显式设置时保持现行为——只跟随父回合）。
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeoutSeconds = arguments["timeout_seconds"]?.GetValue<int?>();
        if (timeoutSeconds is { } requested)
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(requested, MinTimeoutSeconds, MaxTimeoutSeconds)));

        try
        {
            // context.TurnScope 把父回合的执行台账交给子代理共享：子任务写下的文件因此
            // 进入父回合的补偿栈，agent.undo 能一次撤销整回合。
            var result = await delegator.RunSubTaskAsync(
                context.Workspace, task, role, context.TurnScope, timeoutCts.Token).ConfigureAwait(false);
            // 子任务消耗回流入父回合预算（用量在 Agent 主循环并入 token/费用计数）。
            scope?.AddDelegatedUsage(result.InputTokens, result.OutputTokens, result.Cost);
            if (!string.IsNullOrWhiteSpace(result.Error))
                return ToolResult.Fail($"子任务失败：{result.Error}");
            if (result.Cancelled)
                return ToolResult.Fail("子任务已取消。");
            var output = result.Text.Length > 0
                ? context.TruncateTailPrioritized(result.Text, "subtask output", tailShare: 0.7)
                : "（子任务完成，但没有返回文本。）";
            return ToolResult.Ok(output, "subtask completed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the whole turn is being cancelled — let the agent loop unwind
        }
        catch (OperationCanceledException)
        {
            // 3.2 独立超时（父回合仍在运行）：可诊断失败而不是静默拖死。
            return ToolResult.Fail(
                $"子任务超时（{Math.Clamp(timeoutSeconds ?? 0, MinTimeoutSeconds, MaxTimeoutSeconds)} 秒）已终止："
                + "该子任务可能陷入循环或等待外部资源。父回合可继续；若需重试，请拆小任务或调大 timeout_seconds。");
        }
        catch (LlmException ex)
        {
            return ToolResult.Fail($"子任务执行失败：{ex.Message}");
        }
    }
}
