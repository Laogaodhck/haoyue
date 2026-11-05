using System.Collections.Concurrent;

namespace Haoyue.Runtime.Teams;

/// <summary>
/// 运行中团体的取消登记表：<c>team.run</c> 开始时登记，<c>team.cancel</c> 按 runId
/// 触发对应 CTS。进程内单例，运行结束后自动移除，避免泄漏。
/// </summary>
public static class TeamRunRegistry
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> Active =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, byte> UserCancelled =
        new(StringComparer.Ordinal);

    public static IDisposable Register(string runId, CancellationTokenSource source)
    {
        Active[runId] = source;
        return new Lease(runId, source);
    }

    /// <summary>取消一次运行；返回 false 表示该 runId 已结束或不存在。</summary>
    public static bool Cancel(string runId)
    {
        if (!Active.TryGetValue(runId, out var source)) return false;
        try
        {
            UserCancelled[runId] = 0;
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public static bool IsRunning(string runId) => Active.ContainsKey(runId);

    /// <summary>该 run 是否由 <see cref="Cancel"/> 主动取消（用于区分主动取消与超时）。</summary>
    public static bool WasCancelledByUser(string runId) => UserCancelled.ContainsKey(runId);

    private sealed class Lease(string runId, CancellationTokenSource source) : IDisposable
    {
        public void Dispose()
        {
            if (Active.TryGetValue(runId, out var current) && ReferenceEquals(current, source))
                Active.TryRemove(runId, out _);
            UserCancelled.TryRemove(runId, out _);
        }
    }
}
