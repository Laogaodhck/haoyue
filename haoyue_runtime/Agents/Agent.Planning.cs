namespace Haoyue.Runtime.Agents;

/// <summary>Static helpers for runtime-notice composition shared by the agent loop.</summary>
internal static class AgentNotices
{
    /// <summary>
    /// One-line execution-ledger summary appended to the step-budget wrap-up notice, so
    /// the forced conclusion names how many tool steps ran, how they fared, and which
    /// tools kept failing. Empty when the turn never executed a tool.
    /// </summary>
    public static string RenderLedgerSummary(IReadOnlyList<TurnStepRecord> steps)
    {
        if (steps.Count == 0) return "";
        var failed = steps.Where(s => !s.Success).ToList();
        var summary = $"本回合执行台账：共 {steps.Count} 个步骤（成功 {steps.Count - failed.Count}，失败 {failed.Count}）";
        if (failed.Count > 0)
            summary += $";失败工具：{string.Join("、", failed.Select(s => s.Tool).Distinct())}";
        return summary;
    }
}

/// <summary>A corrective notice plus the user-facing warning to publish alongside it.</summary>
internal sealed record PlanNudge(string Notice, string Warning);

/// <summary>
/// Per-turn plan-discipline guard: tracks how many tool calls ran since the last
/// update_plan and which corrective nudges already fired, so the agent loop can inject
/// one focused notice per situation — at most once per turn each.
/// </summary>
internal sealed class PlanNudgeGuard
{
    /// <summary>Tool calls without a plan update tolerated before a staleness nudge fires.</summary>
    public const int StaleToolCallThreshold = 4;

    private int _versionSeen;
    private int _toolCallsSinceUpdate;
    private bool _finishedNudged;
    private bool _staleNudged;

    /// <summary>
    /// Call once per step that executed tools. A plan-version change (update_plan ran)
    /// resets the staleness counter; otherwise the step's tool calls accumulate.
    /// </summary>
    /// <param name="plan">The turn's plan state.</param>
    /// <param name="toolCallsThisStep">All tool calls executed this step.</param>
    /// <param name="planToolCallsThisStep">How many of those were update_plan itself.</param>
    public PlanNudge? Evaluate(TurnPlan plan, int toolCallsThisStep, int planToolCallsThisStep)
    {
        var versionChanged = plan.Version != _versionSeen;
        if (versionChanged)
        {
            _versionSeen = plan.Version;
            _toolCallsSinceUpdate = 0;
        }
        else
        {
            _toolCallsSinceUpdate += toolCallsThisStep;
        }

        if (plan.HasPlan && plan.AllCompleted)
        {
            // The declaring update_plan call itself is not "still working": only nudge
            // when other tools ran after the plan reached its completed state.
            var otherCalls = versionChanged ? toolCallsThisStep - planToolCallsThisStep : _toolCallsSinceUpdate;
            if (otherCalls <= 0 || _finishedNudged) return null;
            _finishedNudged = true;
            return new PlanNudge(
                ">>> [plan finished] 计划中的所有步骤已标记完成，但本回合仍在调用工具。请判断工作是否真正收尾：若还有遗留事项，先用 update_plan 扩展计划再继续执行；若确已完成，直接输出面向用户的最终回答，不要再调用工具。",
                "计划已全部标记完成但仍在调用工具，已注入收尾纠偏提示。");
        }

        if (plan.HasPlan && _toolCallsSinceUpdate >= StaleToolCallThreshold)
        {
            if (_staleNudged) return null;
            _staleNudged = true;
            return new PlanNudge(
                $">>> [plan stale] 已连续执行 {_toolCallsSinceUpdate} 次工具调用而计划未同步。请调用 update_plan 刷新各步骤状态（pending/in_progress/completed）或调整计划，使其反映当前真实进展。",
                $"计划状态已 {_toolCallsSinceUpdate} 次工具调用未同步，已注入计划刷新提示。");
        }

        return null;
    }
}
