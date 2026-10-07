using System.Collections.Concurrent;

namespace Haoyue.Runtime.Agents;

/// <summary>One executed step recorded in the per-turn execution ledger.</summary>
public sealed record TurnStepRecord(
    int Ordinal,
    string Kind,        // tool | subtask
    string Tool,
    string Target,
    bool Compensable,
    bool Success,
    DateTimeOffset At);

/// <summary>A file change that can be reverted to its pre-turn content.</summary>
/// <param name="PreviousContent">The file content before the change; null means the file was created by the turn.</param>
public sealed record UndoableFileChange(string AbsolutePath, string? PreviousContent, string Description);

/// <summary>The undoable footprint of one completed turn.</summary>
public sealed record TurnUndoLedger(
    string SessionId,
    string WorkspaceRoot,
    DateTimeOffset CompletedAt,
    bool TurnFailed,
    IReadOnlyList<UndoableFileChange> Changes);

/// <summary>
/// Per-turn execution scope: a retrospective step ledger plus a compensation stack.
/// Every tool execution appends a step record; file-writing tools register the pre-turn
/// content of each changed file so a failed or finished turn can be reverted on demand.
/// Deliberately NOT a prospective workflow graph: steps are recorded as they happen,
/// and only the controlled subset (builtin file tools) claims compensability — shell
/// and MCP side effects are recorded but honestly marked non-compensable.
/// </summary>
public sealed class TurnExecutionScope(string sessionId, string workspaceRoot)
{
    /// <summary>Upper bound on retained compensations before they degrade to path-only records.</summary>
    public const int MaxCompensations = 50;
    /// <summary>Total retained pre-turn content bytes before further changes become uncompensable.</summary>
    public const long MaxCompensationBytes = 20 * 1024 * 1024;

    private readonly List<TurnStepRecord> _steps = [];
    private readonly List<UndoableFileChange> _changes = [];
    private readonly Lock _gate = new();
    private int _ordinal;
    private long _compensationBytes;

    public string SessionId => sessionId;
    public string WorkspaceRoot => workspaceRoot;
    public bool CompensationTruncated { get; private set; }
    public IReadOnlyList<TurnStepRecord> Steps { get { lock (_gate) return [.. _steps]; } }
    public IReadOnlyList<UndoableFileChange> Changes { get { lock (_gate) return [.. _changes]; } }

    /// <summary>Appends one executed step to the ledger.</summary>
    public void RecordStep(string kind, string tool, string target, bool compensable, bool success)
    {
        lock (_gate)
        {
            _steps.Add(new TurnStepRecord(++_ordinal, kind, tool, target, compensable, success, DateTimeOffset.UtcNow));
        }
    }

    /// <summary>
    /// Registers the pre-turn content of a file about to be (or just) changed, so a later
    /// undo can restore it. Returns false when the compensation budget is exhausted — the
    /// change still happens and is recorded, it just cannot be auto-reverted.
    /// </summary>
    public bool TryRegisterFileChange(string absolutePath, string? previousContent, string description)
    {
        lock (_gate)
        {
            if (_changes.Count >= MaxCompensations)
            {
                CompensationTruncated = true;
                return false;
            }
            var cost = previousContent?.Length ?? 0L;
            if (_compensationBytes + cost > MaxCompensationBytes)
            {
                CompensationTruncated = true;
                return false;
            }
            _compensationBytes += cost;
            _changes.Add(new UndoableFileChange(absolutePath, previousContent, description));
            return true;
        }
    }

    /// <summary>The undoable footprint of this turn, or null when nothing was changed.</summary>
    public TurnUndoLedger? BuildLedger(bool turnFailed)
    {
        lock (_gate)
        {
            if (_changes.Count == 0) return null;
            return new TurnUndoLedger(sessionId, workspaceRoot, DateTimeOffset.UtcNow, turnFailed, [.. _changes]);
        }
    }

    /// <summary>Human-readable execution timeline for logs and interruption notices.</summary>
    public string RenderTrace()
    {
        lock (_gate)
        {
            if (_steps.Count == 0) return "(no steps executed)";
            var lines = _steps.Select(s =>
                $"  {s.Ordinal}. [{s.Kind}] {s.Tool} → {s.Target}{(s.Success ? "" : " (failed)")}");
            var footer = CompensationTruncated ? "\n  (compensation budget exhausted: later changes are not revertible)" : "";
            return string.Join("\n", lines) + footer;
        }
    }
}

/// <summary>
/// Process-wide registry of the latest completed turn's undoable changes, keyed by
/// session. Isolated per-turn runtimes share one daemon-provided instance so the
/// <c>agent.undo</c> RPC can reach the ledger after the turn's runtime is disposed.
/// Only the most recent undoable turn per session is kept.
/// </summary>
public sealed class TurnUndoRegistry
{
    private readonly ConcurrentDictionary<string, TurnUndoLedger> _latest = new(StringComparer.Ordinal);

    public void Deposit(TurnUndoLedger ledger) => _latest[ledger.SessionId] = ledger;

    /// <summary>Removes and returns the ledger — an undo is a one-shot action.</summary>
    public TurnUndoLedger? Take(string sessionId) =>
        _latest.TryRemove(sessionId, out var ledger) ? ledger : null;

    /// <summary>Returns the ledger without removing it (terminal-response enrichment).</summary>
    public TurnUndoLedger? Peek(string sessionId) =>
        _latest.TryGetValue(sessionId, out var ledger) ? ledger : null;
}

public static class TurnUndoLedgerExtensions
{
    /// <summary>
    /// Applies the ledger's compensations in LIFO order (last change reverted first).
    /// A single failure never aborts the remaining compensations — partial success is
    /// reported item by item so the user knows exactly what was and was not reverted.
    /// </summary>
    public static async Task<(IReadOnlyList<string> Restored, IReadOnlyList<string> Failed)> ApplyAsync(
        this TurnUndoLedger ledger, CancellationToken ct = default)
    {
        var restored = new List<string>();
        var failed = new List<string>();
        for (var i = ledger.Changes.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            var change = ledger.Changes[i];
            try
            {
                if (change.PreviousContent is null)
                {
                    if (File.Exists(change.AbsolutePath)) File.Delete(change.AbsolutePath);
                }
                else
                {
                    var dir = Path.GetDirectoryName(change.AbsolutePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    await File.WriteAllTextAsync(change.AbsolutePath, change.PreviousContent, ct).ConfigureAwait(false);
                }
                restored.Add(change.Description);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{change.Description}: {ex.Message}");
            }
        }
        return (restored, failed);
    }
}
