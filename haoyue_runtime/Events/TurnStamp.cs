namespace Haoyue.Runtime.Events;

/// <summary>
/// 回合级环境身份（AsyncLocal）。宿主（daemon 回合任务 / CLI 聊天循环 / Agent 自身）
/// 在回合执行体外沿 <see cref="BeginScope"/> 设定身份，期间事件总线上发布的所有事件
/// 都会带上 <see cref="RuntimeEvent.TurnId"/>——事件记录不必逐个改签名，SQLite 台账
/// （<see cref="EventJournal"/>）与 events.recent 重连回放也能按回合归组，与实时帧的
/// 请求 id 同源。
/// 语义：
/// - AsyncLocal 随 ExecutionContext 流动：并发回合各自独立，互不污染；
/// - 已在外层作用域内时 <see cref="BeginScope"/> 为空操作（宿主身份优先，委派子代理
///   的事件因此归到父回合，而不是各起一个 id）；
/// - 无作用域时发布的事件 TurnId 为 null（定时任务、启动广播等非回合事件）。
/// </summary>
public static class TurnStamp
{
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>当前异步流上的回合身份；无回合时为 null。</summary>
    public static string? CurrentTurnId => Current.Value;

    /// <summary>给事件盖当前回合身份（已有 TurnId 的事件原样返回）。</summary>
    public static RuntimeEvent Apply(RuntimeEvent evt) =>
        evt.TurnId is null && Current.Value is { } turn ? evt with { TurnId = turn } : evt;

    /// <summary>
    /// 进入一个回合作用域。外层已有身份时返回空作用域（不覆盖宿主设定）。
    /// 返回的 <see cref="IDisposable"/> 必须 Dispose（using）以退出作用域。
    /// </summary>
    public static IDisposable BeginScope(string turnId)
    {
        if (Current.Value is not null) return NoopScope.Instance;
        var previous = Current.Value; // null
        Current.Value = turnId;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Current.Value = previous;
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();

        public void Dispose()
        {
            // 外层作用域仍然有效：什么都不做。
        }
    }
}
