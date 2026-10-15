using System.Collections.Concurrent;

namespace Haoyue.Runtime.Agents;

/// <summary>
/// 「本次允许」审批登记簿（P1-2.3）。内置高危守卫（bash/computer_exec 的破坏性命令
/// 防护）拒绝执行时，不是让用户去改配置文件，而是挂起该工具调用、向桌面推送审批
/// 请求；用户点「本次允许」后放行这一次调用（按 callId 一次性生效），拒绝或超时
/// 则维持拒绝。用户自定义 allow/deny 规则不进入审批——那是显式配置，必须改配置。
/// </summary>
public sealed class ApprovalRegistry
{
    /// <summary>审批请求的默认等待时长：超时按拒绝处理，回合不会无限挂起。</summary>
    public const int DefaultTimeoutSeconds = 120;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new(StringComparer.Ordinal);

    /// <summary>当前挂起中的审批数量（诊断/测试用）。</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// 登记一次审批并等待响应。同一 callId 重复登记返回既有等待（不会覆盖）。
    /// 取消（回合中止）时按拒绝收场。
    /// </summary>
    public Task<bool> RequestAsync(string callId, int timeoutSeconds = DefaultTimeoutSeconds, CancellationToken ct = default)
    {
        var tcs = _pending.GetOrAdd(callId, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

        // 超时与回合取消都按拒绝处理：审批不是执行许可的必须环节。
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 600)));
        _ = Task.Delay(Timeout.Infinite, timeoutCts.Token).ContinueWith(
            _ => tcs.TrySetResult(false),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        _ = ct.Register(() => tcs.TrySetResult(false));

        return tcs.Task;
    }

    /// <summary>
    /// 响应对应 callId 的审批。未知或已超时清理的 callId 返回 false（调用方按拒绝处理）。
    /// </summary>
    public bool Respond(string callId, bool approve)
    {
        if (!_pending.TryRemove(callId, out var tcs)) return false;
        return tcs.TrySetResult(approve);
    }

    /// <summary>回合结束时清理残留（取消的等待会自行按拒绝收场，这里只放内存）。</summary>
    public void Clear() => _pending.Clear();
}
