using Haoyue.Runtime.Providers;

namespace Haoyue.Runtime.Agents;

/// <summary>
/// Turn-level accuracy metrics computed as a pure function of the persisted turn
/// messages plus the final answer. No events, no I/O — evaluations and tests can score
/// any recorded turn deterministically, which makes precision/hit-rate improvements
/// measurable and continuously iterable.
/// </summary>
public sealed record AgentTurnReport(
    int AssistantSteps,
    int ToolCalls,
    int ToolFailures,
    int MaxConsecutiveSameToolFailures,
    int FinalAnswerChars,
    bool FinalAnswerEmpty)
{
    /// <summary>Captures metrics for a turn slice of messages (from the turn's first message on).</summary>
    public static AgentTurnReport Capture(IReadOnlyList<ChatMessage> messages, int turnMessageIndex, string finalText)
        => Capture([.. messages.Skip(turnMessageIndex)], finalText);

    /// <summary>Captures metrics from an explicit message list (already the turn slice).</summary>
    public static AgentTurnReport Capture(IReadOnlyList<ChatMessage> turnMessages, string finalText)
    {
        var steps = 0;
        var calls = 0;
        var failures = 0;
        var currentStreak = 0;
        var maxStreak = 0;
        foreach (var message in turnMessages)
        {
            if (message.Role == ChatRole.Assistant)
            {
                steps++;
                calls += message.ToolCalls?.Count ?? 0;
                continue;
            }
            if (message.Role != ChatRole.Tool) continue;
            if (message.ToolSuccess)
            {
                currentStreak = 0;
                continue;
            }
            failures++;
            currentStreak++;
            if (currentStreak > maxStreak) maxStreak = currentStreak;
        }

        return new AgentTurnReport(
            steps, calls, failures, maxStreak,
            finalText.Length, finalText.Length == 0);
    }

    /// <summary>Share of requested tool calls that ended in a failed execution (0 when no calls were made).</summary>
    public double ToolFailureRate => ToolCalls == 0 ? 0 : Math.Round((double)ToolFailures / ToolCalls, 4);

    /// <summary>High when the turn hit its step budget and never produced an answer — the clearest miss signal.</summary>
    public bool TurnEndedWithoutAnswer => FinalAnswerEmpty;

    public string RenderSummary() =>
        $"步骤 {AssistantSteps} · 工具调用 {ToolCalls}（失败 {ToolFailures}，失败率 {ToolFailureRate:P1}）"
        + $" · 最长连续同类失败 {MaxConsecutiveSameToolFailures}"
        + $" · 最终回答 {FinalAnswerChars} 字符{(FinalAnswerEmpty ? "（空回答）" : "")}";
}
