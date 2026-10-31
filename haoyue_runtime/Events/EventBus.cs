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
    /// when a subscriber cannot keep up, its oldest events are dropped instead of the
    /// queue growing without limit. Only live-view events are carried here — durable
    /// events are already in the SQLite journal, and the terminal turn envelope carries
    /// the authoritative result, so dropping stale deltas cannot lose the outcome.
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

    private readonly Lock _gate = new();
    private readonly List<Subscription> _subscriptions = [];

    public void Publish(RuntimeEvent evt)
    {
        // 回合身份在出口统一盖章：生产者无需知道自己在哪个回合里。
        var stamped = TurnStamp.Apply(evt);
        lock (_gate)
        {
            foreach (var sub in _subscriptions)
                sub.Channel.Writer.TryWrite(stamped);
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
        public Channel<RuntimeEvent> Channel { get; } =
            System.Threading.Channels.Channel.CreateBounded<RuntimeEvent>(
                new BoundedChannelOptions(capacity)
                {
                    SingleReader = true,
                    // DropOldest keeps the freshest view: a stalled renderer resumes at
                    // the current state instead of replaying minutes-old deltas.
                    FullMode = BoundedChannelFullMode.DropOldest,
                });

        public ChannelReader<RuntimeEvent> Reader => Channel.Reader;

        public void Dispose()
        {
            owner.Remove(this);
            Channel.Writer.TryComplete();
        }
    }
}
