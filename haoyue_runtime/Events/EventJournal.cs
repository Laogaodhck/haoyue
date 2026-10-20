using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        nameof(RetrievalMissEvent),
        nameof(ModelInvocationStartedEvent), nameof(ProviderRetryEvent), nameof(ProviderSwitchedEvent),
        nameof(UsageRecordedEvent),
        nameof(VerificationStartedEvent), nameof(VerificationCompletedEvent),
        nameof(PlanUpdatedEvent),
        nameof(ScheduledTaskUpcomingEvent), nameof(ScheduledTaskCompletedEvent),
        nameof(EvolutionReflectionCompletedEvent), nameof(UserFeedbackEvent),
        nameof(MemoryConsolidationCompletedEvent),
        nameof(ModelLoadProgressEvent),
        nameof(WarningEvent), nameof(ErrorEvent),
    ];

    public static bool IsPersistent(RuntimeEvent evt) => PersistentTypes.Contains(evt.GetType().Name);

    /// <summary>
    /// 台账行的回放投影：稳定事件名（snake_case）+ camelCase 载荷 + 会话归属。
    /// 直接把 C# 记录名和 PascalCase 属性丢给客户端，等于要求每个客户端都复制一份
    /// 类型字典；重连回放要的只是「哪个会话的哪一回合怎么结束的」。
    /// </summary>
    public static JsonObject Project(HaoyueDatabase.PersistedEvent persisted)
    {
        JsonNode? payload = null;
        try
        {
            payload = JsonNode.Parse(persisted.Payload);
        }
        catch (JsonException)
        {
            // 载荷损坏时仍要把行本身回放出去，客户端才能推进游标、跳过坏行。
        }
        var body = payload as JsonObject;
        var projected = body is null ? payload?.DeepClone() : Camelize(body);
        var row = new JsonObject
        {
            ["id"] = persisted.Id,
            ["timestamp"] = persisted.Timestamp,
            ["type"] = persisted.Type,
            ["event"] = WireName(persisted.Type),
            ["payload"] = projected,
        };
        if (projected is JsonObject camel
            && camel.TryGetPropertyValue("sessionId", out var session)
            && session is JsonValue && session.GetValueKind() == JsonValueKind.String)
            row["sessionId"] = session.GetValue<string>();
        // 回合身份与 sessionId 同级暴露：重连回放按它把散事件归组到某一回合，
        // 与实时帧的请求 id 同源（daemon 回合 = 客户端请求 id）。
        if (projected is JsonObject camelTurn
            && camelTurn.TryGetPropertyValue("turnId", out var turn)
            && turn is JsonValue && turn.GetValueKind() == JsonValueKind.String)
            row["turnId"] = turn.GetValue<string>();
        return row;
    }

    /// <summary><c>TurnCompletedEvent</c> → <c>turn_completed</c>；连续大写按缩写词整体处理。</summary>
    public static string WireName(string typeName)
    {
        var name = typeName.EndsWith("Event", StringComparison.Ordinal)
            ? typeName[..^"Event".Length]
            : typeName;
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];
            if (char.IsUpper(current) && i > 0
                && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(current));
        }
        return builder.ToString();
    }

    private static JsonObject Camelize(JsonObject source)
    {
        var target = new JsonObject();
        foreach (var pair in source)
            target[LowerFirst(pair.Key)] = CamelizeNode(pair.Value);
        return target;
    }

    private static JsonNode? CamelizeNode(JsonNode? node) => node switch
    {
        JsonObject body => Camelize(body),
        JsonArray items => new JsonArray([.. items.Select(CamelizeNode)]),
        _ => node?.DeepClone(),
    };

    private static string LowerFirst(string key) =>
        string.IsNullOrEmpty(key) || char.IsLower(key[0]) ? key : char.ToLowerInvariant(key[0]) + key[1..];

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
        // 先盖章再落台账：否则台账行丢失回合身份，回放无法按回合归组。
        var stamped = TurnStamp.Apply(evt);
        if (EventJournal.IsPersistent(stamped))
        {
            try
            {
                database.AppendEvent(stamped.Timestamp, stamped.GetType().Name, EventJournal.Serialize(stamped));
            }
            catch (Exception ex) when (ex is SqliteException or IOException)
            {
                // Journaling must never take the agent turn down with it.
            }
        }
        inner.Publish(stamped);
    }

    public IEventSubscription Subscribe() => inner.Subscribe();
}
