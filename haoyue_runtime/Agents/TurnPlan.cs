namespace Haoyue.Runtime.Agents;

/// <summary>One milestone step of the per-turn plan, as declared by the model via update_plan.</summary>
public sealed record TurnPlanStep(string Title, string Status, string? Detail)
{
    public bool Completed => Status is "completed" or "done";
}

/// <summary>
/// Per-turn plan state written by update_plan through ToolContext.PlanTracker. The agent
/// loop watches Version / HasPlan / AllCompleted to inject plan-discipline nudges (plan
/// finished while tools keep running, statuses gone stale) — this makes the plan visible
/// to the runtime instead of it being a UI-only side channel.
/// </summary>
public sealed class TurnPlan
{
    private readonly Lock _gate = new();
    private readonly List<TurnPlanStep> _steps = [];

    /// <summary>Incremented on every update; 0 means no plan was declared this turn.</summary>
    public int Version { get; private set; }
    public string? Explanation { get; private set; }
    public bool HasPlan => Version > 0;

    public bool AllCompleted
    {
        get { lock (_gate) return _steps.Count > 0 && _steps.All(s => s.Completed); }
    }

    public void Update(IReadOnlyList<TurnPlanStep> steps, string? explanation)
    {
        lock (_gate)
        {
            _steps.Clear();
            _steps.AddRange(steps);
            Explanation = explanation;
            Version++;
        }
    }

    public (int Version, IReadOnlyList<TurnPlanStep> Steps) Snapshot()
    {
        lock (_gate) return (Version, [.. _steps]);
    }

    /// <summary>Human-readable plan progress, echoed back to the model in tool results.</summary>
    public string Render()
    {
        lock (_gate)
        {
            if (_steps.Count == 0) return "(尚未制定计划)";
            var lines = _steps.Select((s, i) =>
                $"  {i + 1}. [{s.Status}] {s.Title}{(string.IsNullOrEmpty(s.Detail) ? "" : $" — {s.Detail}")}");
            var completed = _steps.Count(s => s.Completed);
            return string.Join("\n", lines) + $"\n  进度：{completed}/{_steps.Count} 已完成";
        }
    }
}
