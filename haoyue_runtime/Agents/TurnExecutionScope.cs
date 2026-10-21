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
/// <summary>3.4 黑板条目：键 + 结论 + 写入者（角色或工具名），只增改不删（回合生命周期）。</summary>
public sealed record BlackboardEntry(string Key, string Value, string Author);

public sealed class TurnExecutionScope(string sessionId, string workspaceRoot)
{
    /// <summary>Upper bound on retained compensations before they degrade to path-only records.</summary>
    public const int MaxCompensations = 50;
    /// <summary>Total retained pre-turn content bytes before further changes become uncompensable.</summary>
    public const long MaxCompensationBytes = 20 * 1024 * 1024;

    private readonly List<TurnStepRecord> _steps = [];
    private readonly List<UndoableFileChange> _changes = [];
    private readonly HashSet<string> _consentedTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private int _ordinal;
    private long _compensationBytes;
    // 3.1 委派用量与配额：共享父台账的子代理把 token/费用记回这里，父回合的预算
    // 闸门与最终用量随之可见——否则子代理是「逃逸预算」，烧多少父回合都不知道。
    private long _delegatedInputTokens;
    private long _delegatedOutputTokens;
    private decimal _delegatedCost;
    private int _delegationCount;
    // 3.4 轻量共享黑板：委派子代理间的「共享发现」台账（键值对，同键覆盖）。
    // 刻意不做成图引擎或消息总线——TurnExecutionScope 的定位是回合内的执行台账，
    // 黑板只是它多出的一块可读写白板，生命周期随回合结束而清空。
    private readonly List<BlackboardEntry> _blackboard = [];

    public string SessionId => sessionId;
    public string WorkspaceRoot => workspaceRoot;
    public bool CompensationTruncated { get; private set; }
    public IReadOnlyList<TurnStepRecord> Steps { get { lock (_gate) return [.. _steps]; } }
    public IReadOnlyList<UndoableFileChange> Changes { get { lock (_gate) return [.. _changes]; } }

    /// <summary>崩溃对账观察者（daemon 逐步骤重写 active-turns 日志）。放在台账上而不是 Agent 实例上，
    /// 是为了让共享父台账的子代理步骤同样进日志：否则一次崩溃会留下「文件已改但日志里没有」的
    /// 子任务痕迹，对账报告不完整。</summary>
    public Action<TurnStepRecord>? MutatingStepObserved { get; set; }

    /// <summary>
    /// 3.1 子代理用量入账：委派工具在每个子任务完成后调用，父回合随后的预算检查
    /// （token/费用闸门）立即看到这笔开销。
    /// </summary>
    public void AddDelegatedUsage(long inputTokens, long outputTokens, decimal cost)
    {
        lock (_gate)
        {
            _delegatedInputTokens += inputTokens;
            _delegatedOutputTokens += outputTokens;
            _delegatedCost += cost;
        }
    }

    /// <summary>取走并清零累计的委派用量（供父回合并入自身的 token/费用计数）。</summary>
    public (long InputTokens, long OutputTokens, decimal Cost) TakeDelegatedUsage()
    {
        lock (_gate)
        {
            var result = (_delegatedInputTokens, _delegatedOutputTokens, _delegatedCost);
            _delegatedInputTokens = 0;
            _delegatedOutputTokens = 0;
            _delegatedCost = 0m;
            return result;
        }
    }

    /// <summary>
    /// 3.1 委派配额：每回合允许的子任务总数（delegate_task 一次占一席，delegate_tasks
    /// 每个子任务各占一席）。超出上限返回 false——调用方显式报错，让模型把多余子任务
    /// 留到下一轮，而不是静默放行 6 路并行各烧满配额。
    /// </summary>
    public bool TryConsumeDelegationSlot(int maxPerTurn)
    {
        lock (_gate)
        {
            if (_delegationCount >= maxPerTurn) return false;
            _delegationCount++;
            return true;
        }
    }

    public int DelegationCount { get { lock (_gate) return _delegationCount; } }

    /// <summary>黑板上限：超过后拒绝写入（防止单回合黑板被刷成第二份会话历史）。</summary>
    public const int MaxBlackboardEntries = 64;
    /// <summary>单条值上限（字符）：黑板放结论与发现，不放全文摘抄。</summary>
    public const int MaxBlackboardValueChars = 4000;

    /// <summary>
    /// 3.4 写黑板：同键覆盖（保留首任作者之外的更新者）。返回 null 表示成功；
    /// 非null 为拒绝原因（超限/超长）。并发安全（与执行台账同一把锁）。
    /// </summary>
    public string? WriteBlackboard(string key, string value, string author)
    {
        var trimmedKey = key.Trim();
        var trimmedValue = value.Trim();
        if (trimmedKey.Length is 0 or > 120) return "key 必须为 1-120 个字符。";
        if (trimmedValue.Length is 0 or > MaxBlackboardValueChars)
            return $"value 必须为 1-{MaxBlackboardValueChars} 个字符（黑板放结论，不放全文）。";
        lock (_gate)
        {
            var existing = _blackboard.FindIndex(entry =>
                string.Equals(entry.Key, trimmedKey, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                _blackboard[existing] = new BlackboardEntry(trimmedKey, trimmedValue, author);
                return null;
            }
            if (_blackboard.Count >= MaxBlackboardEntries)
                return $"黑板已满（{MaxBlackboardEntries} 条）：请合并或覆盖既有条目，而不是继续新增。";
            _blackboard.Add(new BlackboardEntry(trimmedKey, trimmedValue, author));
            return null;
        }
    }

    /// <summary>读黑板：key 为空读全部，否则按键取单条（找不到返回 null 值元组）。</summary>
    public IReadOnlyList<BlackboardEntry> ReadBlackboard(string? key = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(key)) return [.. _blackboard];
            return _blackboard
                .Where(entry => string.Equals(entry.Key, key.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    /// <summary>
    /// 本回合已获用户批准的高危工具名（按工具一次性生效，不按 callId）：交互式
    /// 计算机使用场景下同一回合批准 computer_exec 一次后放行后续调用，避免每一步
    /// 都弹审批卡片把可用性磨没。批准仅在本回合有效——下一个回合重新征求同意。
    /// </summary>
    public bool IsConsented(string toolName)
    {
        lock (_gate) return _consentedTools.Contains(toolName);
    }

    /// <summary>记录用户对本回合某工具的批准。</summary>
    public void GrantConsent(string toolName)
    {
        lock (_gate) _consentedTools.Add(toolName);
    }

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
