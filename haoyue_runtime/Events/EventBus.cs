using System.Threading.Channels;

namespace Haoyue.Runtime.Events;

/// <summary>
/// In-process publish/subscribe bus. Producers (agent, tools, providers) publish;
/// renderers and other observers subscribe. Publishing never blocks.
/// </summary>
public interface IEventBus
{
    void Publish(RuntimeEvent evt);

    /// <summary>
    /// Creates an independent subscription with its own queue. The queue is bounded:
    /// when a subscriber cannot keep up, streaming text/thinking deltas are coalesced
    /// (never dropped mid-sentence) and other events keep their order. Only live-view
    /// events are carried here — durable events are already in the SQLite journal, and
    /// the terminal turn envelope carries the authoritative result, so a slow consumer
    /// degrades latency instead of corrupting the rendered answer.
    /// </summary>
    IEventSubscription Subscribe(int capacity = EventBus.DefaultCapacity);
}

public interface IEventSubscription : IDisposable
{
    ChannelReader<RuntimeEvent> Reader { get; }
}

public sealed class EventBus : IEventBus
{
    /// <summary>Per-subscription queue ceiling. Generous enough for a full streaming
    /// answer, small enough that a wedged consumer cannot exhaust memory.</summary>
    public const int DefaultCapacity = 8_192;

    /// <summary>Cap on overflow-delayed events (outside the channel). Deltas merge in
    /// place, so this only bounds non-streaming events during a long stall.</summary>
    internal const int MaxPendingEvents = 256;

    private readonly Lock _gate = new();
    private readonly List<Subscription> _subscriptions = [];

    public void Publish(RuntimeEvent evt)
    {
        // 回合身份在出口统一盖章：生产者无需知道自己在哪个回合里。
        var stamped = TurnStamp.Apply(evt);
        lock (_gate)
        {
            foreach (var sub in _subscriptions)
                sub.Publish(stamped);
        }
    }

    public IEventSubscription Subscribe(int capacity = DefaultCapacity)
    {
        var sub = new Subscription(this, Math.Clamp(capacity, 1, 1_000_000));
        lock (_gate)
            _subscriptions.Add(sub);
        return sub;
    }

    private void Remove(Subscription sub)
    {
        lock (_gate)
            _subscriptions.Remove(sub);
    }

    private sealed class Subscription(EventBus owner, int capacity) : IEventSubscription
    {
        // Wait mode makes saturation observable (TryWrite returns false) so overflow can
        // be handled losslessly; DropOldest silently truncated streamed text mid-answer.
        public Channel<RuntimeEvent> Channel { get; } =
            System.Threading.Channels.Channel.CreateBounded<RuntimeEvent>(
                new BoundedChannelOptions(capacity)
                {
                    SingleReader = true,
                    FullMode = BoundedChannelFullMode.Wait,
                });

        private readonly Lock _pendingGate = new();
        private readonly List<RuntimeEvent> _pending = [];
        private bool _flushing;

        public ChannelReader<RuntimeEvent> Reader => Channel.Reader;

        public void Publish(RuntimeEvent evt)
        {
            // 有序性：只要有待发或正在补发的事件，就必须排队，不能抢跑到通道里。
            lock (_pendingGate)
            {
                if (_flushing || _pending.Count > 0)
                {
                    EnqueueLocked(evt);
                    return;
                }
            }

            if (Channel.Writer.TryWrite(evt)) return;

            lock (_pendingGate)
                EnqueueLocked(evt);
        }

        private void EnqueueLocked(RuntimeEvent evt)
        {
            // 相邻同类增量合并：连续文本（或思考）片段合成一个事件，内容零丢失。
            if (_pending.Count > 0)
            {
                switch (_pending[^1], evt)
                {
                    case (AssistantTextDeltaEvent last, AssistantTextDeltaEvent next):
                        _pending[^1] = next with { Delta = last.Delta + next.Delta };
                        break;
                    case (ThinkingDeltaEvent last, ThinkingDeltaEvent next):
                        _pending[^1] = next with { Delta = last.Delta + next.Delta };
                        break;
                    default:
                        _pending.Add(evt);
                        break;
                }
            }
            else
            {
                _pending.Add(evt);
            }

            // 极端饱和：只有非流式事件会堆积；丢弃最旧的待发项，内存恒有上界。
            while (_pending.Count > MaxPendingEvents)
                _pending.RemoveAt(0);

            if (!_flushing)
            {
                _flushing = true;
                _ = Task.Run(FlushAsync);
            }
        }

        private async Task FlushAsync()
        {
            while (true)
            {
                RuntimeEvent next;
                lock (_pendingGate)
                {
                    if (_pending.Count == 0)
                    {
                        _flushing = false;
                        return;
                    }
                    next = _pending[0];
                    _pending.RemoveAt(0);
                }

                try
                {
                    // 消费者恢复后按序补齐；生产者在此期间继续把增量并入待发表。
                    await Channel.Writer.WriteAsync(next).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    lock (_pendingGate)
                    {
                        _pending.Clear();
                        _flushing = false;
                    }
                    return;
                }
            }
        }

        public void Dispose()
        {
            owner.Remove(this);
            Channel.Writer.TryComplete();
        }
    }
}
