using System.Text.Json;
using Haoyue.Runtime.Data;
using Microsoft.Data.Sqlite;

namespace Haoyue.Runtime.Events;

/// <summary>
/// Decides which runtime events are durable. Streaming noise (text deltas,
/// status ticks, workflow steps, screenshots) is deliberately excluded: it is
/// high-volume and only meaningful live. Persistent events survive restarts in
/// the SQLite journal so clients can query and replay recent activity.
/// </summary>
public static class EventJournal
{
    /// <summary>Maximum rows kept in the journal; older rows are pruned on append.</summary>
    public const int RetainedEvents = 5000;

    /// <summary>Serialized payloads above this size are replaced by a placeholder.</summary>
    public const int MaxPayloadChars = 65_536;

    private static readonly HashSet<string> PersistentTypes =
    [
        nameof(TurnStartedEvent), nameof(TurnCompletedEvent), nameof(UserSteerEvent),
        nameof(ToolCallStartedEvent), nameof(ToolCallCompletedEvent), nameof(FileDiffEvent),
        nameof(ModelInvocationStartedEvent), nameof(ProviderRetryEvent), nameof(ProviderSwitchedEvent),
        nameof(UsageRecordedEvent),
        nameof(VerificationStartedEvent), nameof(VerificationCompletedEvent),
        nameof(PlanUpdatedEvent),
        nameof(ScheduledTaskUpcomingEvent), nameof(ScheduledTaskCompletedEvent),
        nameof(EvolutionReflectionCompletedEvent), nameof(UserFeedbackEvent),
        nameof(WarningEvent), nameof(ErrorEvent),
    ];

    public static bool IsPersistent(RuntimeEvent evt) => PersistentTypes.Contains(evt.GetType().Name);

    /// <summary>
    /// Serializes the concrete event record. Oversized payloads (for example a
    /// tool summary that hit the 30k output cap) collapse to a placeholder so a
    /// single row cannot bloat the journal.
    /// </summary>
    public static string Serialize(RuntimeEvent evt)
    {
        var type = evt.GetType().Name;
        var payload = JsonSerializer.Serialize(evt, evt.GetType());
        return payload.Length <= MaxPayloadChars
            ? payload
            : JsonSerializer.Serialize(new { type, truncated = true });
    }
}

/// <summary>
/// Decorator that mirrors persistent events into the SQLite journal before
/// fanning them out to subscribers. Journaling is best-effort: a storage
/// failure never breaks live event delivery.
/// </summary>
public sealed class JournaledEventBus(IEventBus inner, HaoyueDatabase database) : IEventBus
{
    public void Publish(RuntimeEvent evt)
    {
        if (EventJournal.IsPersistent(evt))
        {
            try
            {
                database.AppendEvent(evt.Timestamp, evt.GetType().Name, EventJournal.Serialize(evt));
            }
            catch (Exception ex) when (ex is SqliteException or IOException)
            {
                // Journaling must never take the agent turn down with it.
            }
        }
        inner.Publish(evt);
    }

    public IEventSubscription Subscribe() => inner.Subscribe();
}
