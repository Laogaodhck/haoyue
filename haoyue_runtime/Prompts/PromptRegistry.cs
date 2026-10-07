namespace Haoyue.Runtime.Prompts;

/// <summary>Composition order of the final system prompt.</summary>
public enum PromptSlot
{
    System = 0,
    Developer = 1,
    Workspace = 2,
    Skill = 3,
    Tool = 4,
    Memory = 5,
}

/// <summary>Context handed to prompt contributors when the final prompt is assembled.</summary>
public sealed class PromptRenderContext
{
    public required IReadOnlyDictionary<string, string> Variables { get; init; }
    public string? WorkspaceRoot { get; init; }
    public IReadOnlyList<string> ProjectKinds { get; init; } = [];
    public Configuration.WorkspaceConfig? WorkspaceConfig { get; init; }
}

/// <summary>One source of prompt text (system file, skill, MCP server, memory…).</summary>
/// <param name="DegradeRank">Drop priority when the assembled prompt exceeds the token
/// budget (higher is dropped first). -1 derives the rank from the slot. Contributions
/// in the System/Developer slots are always protected regardless of this value.</param>
/// <param name="Protected">Never dropped by budget enforcement.</param>
public sealed record PromptContribution(
    string Id,
    PromptSlot Slot,
    Func<PromptRenderContext, CancellationToken, ValueTask<string?>> Resolver,
    int DegradeRank = -1,
    bool Protected = false);

/// <summary>
/// Unified registry for System / Developer / Tool / Workflow / Skill / MCP prompts.
/// Skills and MCP servers register contributions here; nothing is hard-coded.
/// </summary>
public interface IPromptRegistry
{
    IDisposable Register(PromptContribution contribution);
    IReadOnlyList<PromptContribution> All { get; }
}

public sealed class PromptRegistry : IPromptRegistry
{
    private readonly Lock _gate = new();
    private readonly List<PromptContribution> _contributions = [];

    public IReadOnlyList<PromptContribution> All
    {
        get { lock (_gate) return [.. _contributions]; }
    }

    public IDisposable Register(PromptContribution contribution)
    {
        lock (_gate) _contributions.Add(contribution);
        return new Registration(this, contribution);
    }

    private void Remove(PromptContribution contribution)
    {
        lock (_gate) _contributions.Remove(contribution);
    }

    private sealed class Registration(PromptRegistry owner, PromptContribution contribution) : IDisposable
    {
        public void Dispose() => owner.Remove(contribution);
    }
}

/// <summary>Assembles the final system prompt from all registered contributions, in slot order.</summary>
public sealed class PromptComposer(IPromptProvider prompts, IPromptRegistry registry)
{
    public async Task<string> ComposeAsync(PromptRenderContext context, CancellationToken ct = default)
    {
        var parts = new List<(PromptContribution Contribution, string Text)>();
        foreach (var contribution in registry.All.OrderBy(c => (int)c.Slot))
        {
            ct.ThrowIfCancellationRequested();
            string? text;
            try
            {
                text = await contribution.Resolver(context, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or FileNotFoundException)
            {
                continue; // a missing optional prompt never breaks the turn
            }

            if (!string.IsNullOrWhiteSpace(text))
                parts.Add((contribution, prompts.Render(text.Trim(), context.Variables)));
        }

        var total = parts.Sum(part => Agents.ContextPlanner.EstimateTokens(part.Text));
        if (total <= Agents.ContextPlanner.MaxSystemPromptTokens)
            return string.Join("\n\n", parts.Select(p => p.Text));

        // Budget enforcement (previously declared but never executed): degrade optional
        // contributions highest-rank-first until the assembled prompt fits. System and
        // Developer slots are structural and never dropped; everything else yields.
        var dropped = new List<string>();
        while (total > Agents.ContextPlanner.MaxSystemPromptTokens)
        {
            (PromptContribution Contribution, string Text)? victim = null;
            foreach (var part in parts)
            {
                if (IsProtected(part.Contribution)) continue;
                // ">=" keeps the LAST of equal-rank candidates in scan (slot) order,
                // so later-registered optional fragments yield before earlier ones.
                if (victim is null || EffectiveRank(part.Contribution) >= EffectiveRank(victim.Value.Contribution))
                    victim = part;
            }
            if (victim is null) break; // only protected contributions remain — budget cannot shrink further

            total -= Agents.ContextPlanner.EstimateTokens(victim.Value.Text);
            dropped.Add(victim.Value.Contribution.Id);
            parts.Remove(victim.Value);
        }

        if (dropped.Count > 0)
        {
            parts.Add((new PromptContribution("prompt-budget-notice", PromptSlot.Memory, (_, _) => ValueTask.FromResult<string?>(null)),
                $">>> [prompt budget] The following injected contributions were omitted because the assembled system " +
                $"prompt exceeded the {Agents.ContextPlanner.MaxSystemPromptTokens}-token budget: {string.Join(", ", dropped)}. " +
                "Skill bodies among them can be re-loaded on demand with the declare_skill tool."));
        }

        return string.Join("\n\n", parts.Select(p => p.Text));
    }

    private static bool IsProtected(PromptContribution contribution) =>
        contribution.Protected || contribution.Slot is PromptSlot.System or PromptSlot.Developer;

    private static int EffectiveRank(PromptContribution contribution) =>
        contribution.DegradeRank >= 0
            ? contribution.DegradeRank
            : contribution.Slot switch
            {
                PromptSlot.Workspace => 10,
                PromptSlot.Skill => 30,
                PromptSlot.Tool => 20,
                PromptSlot.Memory => 40,
                _ => 0,
            };
}
