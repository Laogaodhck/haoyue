using System.Collections.Concurrent;
using Haoyue.Runtime.Events;

namespace Haoyue.Runtime.Agents;

/// <summary>审批决策结果：交互响应、超时、回合取消三种收场。</summary>
public enum ApprovalOutcome
{
    Approved,
    Denied,
    TimedOut,
    Cancelled,
}

/// <summary>
/// 审批请求的工具上下文。最后会写进审计事件，用来回答「对什么任务、做什么操作」。
/// 回合身份由 <see cref="TurnStamp"/> 在登记时捕获（响应线程/定时器线程没有 AsyncLocal）。
/// </summary>
public sealed record ApprovalRequestContext(
    string ToolName,
    string Summary,
    string Reason,
    string? WorkspaceRoot = null)
{
    public static readonly ApprovalRequestContext Unknown = new("unknown", "", "");
}

/// <summary>
/// 「本次允许」审批登记簿（P1-2.3）。内置高危守卫（bash/computer_exec 的破坏性命令
/// 防护）拒绝执行时，不是让用户去改配置文件，而是挂起该工具调用、向桌面推送审批
/// 请求；用户点「本次允许」后放行这一次调用（按 callId 一次性生效），拒绝或超时
/// 则维持拒绝。用户自定义 allow/deny 规则不进入审批——那是显式配置，必须改配置。
///
/// P0：每次决策（批准/拒绝/超时/取消）都通过 <paramref name="auditBus"/> 发布
/// <see cref="ApprovalDecisionEvent"/> 持久留痕，供事后核对「谁在何时放行了什么」。
/// </summary>
public sealed class ApprovalRegistry(IEventBus? auditBus = null)
{
    /// <summary>审批请求的默认等待时长：超时按拒绝处理，回合不会无限挂起。</summary>
    public const int DefaultTimeoutSeconds = 120;

    /// <summary>
    /// 是否存在真实的响应通道（桌面审批卡片）。daemon 注入的登记簿为 true；
    /// CLI / 测试上下文没有 UI 能回传 agent.respondApproval，等待必然超时——
    /// 那些环境下调用方应按拒绝立即收场并给出可操作提示，而不是干等两分钟。
    /// </summary>
    public bool Interactive { get; init; }

    private sealed record Pending(
        string CallId,
        ApprovalRequestContext Context,
        string? TurnId,
        TaskCompletionSource<ApprovalOutcome> Completion);

    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    /// <summary>当前挂起中的审批数量（诊断/测试用）。</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// 兼容旧调用：只关心「是否批准」，不带工具上下文（审计里记为 unknown）。
    /// </summary>
    public async Task<bool> RequestAsync(
        string callId, int timeoutSeconds = DefaultTimeoutSeconds, CancellationToken ct = default)
        => await RequestOutcomeAsync(callId, null, timeoutSeconds, ct).ConfigureAwait(false)
           == ApprovalOutcome.Approved;

    /// <summary>
    /// 登记一次审批并等待响应，返回可区分的结果（批准/拒绝/超时/取消）。
    /// 同一 callId 重复登记返回既有等待（不会覆盖）。
    /// </summary>
    public async Task<ApprovalOutcome> RequestOutcomeAsync(
        string callId,
        ApprovalRequestContext? context,
        int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken ct = default)
    {
        var pending = _pending.GetOrAdd(callId, _ => new Pending(
            callId,
            context ?? ApprovalRequestContext.Unknown,
            TurnStamp.CurrentTurnId,
            new TaskCompletionSource<ApprovalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously)));

        // 三种收场竞争同一个 TrySetResult：批准/拒绝、超时、回合取消只会有一次生效，
        // 因此审计事件对每个 callId 至多一条。
        using var cancelRegistration = ct.Register(
            () => Complete(pending, ApprovalOutcome.Cancelled, "turn-cancelled"));
        using var timer = new CancellationTokenSource();
        timer.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 600)));
        using var timeoutRegistration = timer.Token.Register(
            () => Complete(pending, ApprovalOutcome.TimedOut, "timeout"));

        // 注意：timer/registration 必须在等待结束后才 Dispose，否则超时回调永远不会触发。
        return await pending.Completion.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// 响应对应 callId 的审批。未知或已超时清理的 callId 返回 false（调用方按拒绝处理）。
    /// </summary>
    public bool Respond(string callId, bool approve)
    {
        if (!_pending.TryGetValue(callId, out var pending)) return false;
        return Complete(pending, approve ? ApprovalOutcome.Approved : ApprovalOutcome.Denied, "user");
    }

    private bool Complete(Pending pending, ApprovalOutcome outcome, string decidedBy)
    {
        if (!pending.Completion.TrySetResult(outcome)) return false;
        _pending.TryRemove(pending.CallId, out _);
        PublishAudit(pending, outcome, decidedBy);
        return true;
    }

    private void PublishAudit(Pending pending, ApprovalOutcome outcome, string decidedBy)
    {
        if (auditBus is null) return;
        try
        {
            auditBus.Publish(new ApprovalDecisionEvent(
                pending.CallId,
                pending.Context.ToolName,
                pending.Context.Summary,
                pending.Context.Reason,
                outcome == ApprovalOutcome.Approved,
                OutcomeName(outcome),
                decidedBy == "user" ? Environment.UserName : decidedBy,
                pending.Context.WorkspaceRoot) with { TurnId = pending.TurnId });
        }
        catch (Exception)
        {
            // 审计写入失败不能改变审批结果，更不能把回合带崩。
        }
    }

    private static string OutcomeName(ApprovalOutcome outcome) => outcome switch
    {
        ApprovalOutcome.Approved => "approved",
        ApprovalOutcome.Denied => "denied",
        ApprovalOutcome.TimedOut => "timed-out",
        _ => "cancelled",
    };

    /// <summary>回合结束时清理残留（取消的等待会自行按拒绝收场，这里只放内存）。</summary>
    public void Clear() => _pending.Clear();
}
