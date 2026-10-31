using System.Text.Json.Nodes;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Events;
using Microsoft.Data.Sqlite;

namespace Haoyue.Tests;

public sealed class EventJournalTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "haoyue-event-journal-tests", Guid.NewGuid().ToString("N"));
    private readonly HaoyueDatabase _database;

    public EventJournalTests()
    {
        Directory.CreateDirectory(_tempDir);
        _database = new HaoyueDatabase(Path.Combine(_tempDir, "haoyue.db"));
    }

    [Fact]
    public void IsPersistent_KeepsLifecycleAndDiagnostics_StreamsStreamingNoise()
    {
        Assert.True(EventJournal.IsPersistent(new TurnStartedEvent("s1", "hi")));
        Assert.True(EventJournal.IsPersistent(new TurnCompletedEvent("s1", false, null)));
        Assert.True(EventJournal.IsPersistent(new ToolCallCompletedEvent(
            "c1", "bash", true, "ok", TimeSpan.FromSeconds(1))));
        Assert.True(EventJournal.IsPersistent(new ErrorEvent("boom")));
        Assert.True(EventJournal.IsPersistent(new ScheduledTaskCompletedEvent("t1", "任务", null, "success")));

        Assert.False(EventJournal.IsPersistent(new StatusEvent("Thinking")));
        Assert.False(EventJournal.IsPersistent(new AssistantTextDeltaEvent("hello")));
        Assert.False(EventJournal.IsPersistent(new ThinkingDeltaEvent("deep")));
        Assert.False(EventJournal.IsPersistent(new WorkflowEvent(1, "think", "Thinking")));
        Assert.False(EventJournal.IsPersistent(new ComputerActionEvent("click", 1, 2, null, true, null, "BASE64")));
    }

    [Fact]
    public void Serialize_ProducesTypedJson_AndCollapsesOversizedPayloads()
    {
        var completed = new TurnCompletedEvent("s1", false, null);
        var json = JsonNode.Parse(EventJournal.Serialize(completed))!;
        Assert.Equal("s1", json["SessionId"]!.GetValue<string>());

        var huge = new ToolCallCompletedEvent(
            "c2", "bash", true, new string('x', EventJournal.MaxPayloadChars + 1), TimeSpan.Zero);
        var truncated = JsonNode.Parse(EventJournal.Serialize(huge))!;
        Assert.True(truncated["truncated"]!.GetValue<bool>());
        Assert.Null(truncated["CallId"]);
    }

    [Fact]
    public void Serialize_EveryEventType_HasSourceGeneratedMetadata()
    {
        // 台账按运行时类型序列化：任何新增事件忘了进 EventJsonContext 都会静默
        // 退化成 {"type","truncated"} 占位符。这个测试把该漂移挡在 CI。
        var eventTypes = typeof(RuntimeEvent).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsClass: true } && typeof(RuntimeEvent).IsAssignableFrom(type))
            .ToList();

        Assert.NotEmpty(eventTypes);
        foreach (var type in eventTypes)
            Assert.NotNull(EventJsonContext.Default.GetTypeInfo(type));
    }

    [Fact]
    public void JournaledEventBus_PersistsKeyEvents_AndForwardsEverything()
    {
        var bus = new JournaledEventBus(new EventBus(), _database);
        using var subscription = bus.Subscribe();

        bus.Publish(new StatusEvent("Thinking"));               // memory only
        bus.Publish(new TurnCompletedEvent("s1", false, null)); // journaled
        bus.Publish(new ErrorEvent("boom", "detail"));          // journaled

        var journal = _database.RecentEvents();
        Assert.Equal(2, journal.Count);
        Assert.Equal("TurnCompletedEvent", journal[0].Type);
        Assert.Equal("ErrorEvent", journal[1].Type);
        Assert.Contains("boom", journal[1].Payload);

        // Subscribers still see every event, including non-persistent ones.
        var seen = new List<string>();
        for (var i = 0; i < 3; i++)
            seen.Add(subscription.Reader.ReadAsync().AsTask().GetAwaiter().GetResult().GetType().Name);
        Assert.Equal(["StatusEvent", "TurnCompletedEvent", "ErrorEvent"], seen);
    }

    [Fact]
    public void JournaledEventBus_SurvivesStorageFailures_AndStillDelivers()
    {
        var locked = new HaoyueDatabase(Path.Combine(_tempDir, "broken.db"));
        // Corrupt the database file after initialization (and drop pooled handles)
        // so every later append fails with "file is not a database".
        SqliteConnection.ClearAllPools();
        File.WriteAllText(locked.FilePath, "not a database");

        var bus = new JournaledEventBus(new EventBus(), locked);
        using var subscription = bus.Subscribe();

        bus.Publish(new ErrorEvent("before failure"));
        var delivered = subscription.Reader.ReadAsync().AsTask().GetAwaiter().GetResult();
        Assert.IsType<ErrorEvent>(delivered);
    }

    [Fact]
    public void Project_UsesStableWireNames_CamelPayload_AndSessionId()
    {
        var completed = new TurnCompletedEvent("s-7", false, null, ["a.cs"], "stop");
        var row = EventJournal.Project(new HaoyueDatabase.PersistedEvent(
            3, DateTimeOffset.UtcNow.ToString("O"), completed.GetType().Name, EventJournal.Serialize(completed)));

        Assert.Equal(3, row["id"]!.GetValue<long>());
        Assert.Equal("turn_completed", row["event"]!.GetValue<string>());
        Assert.Equal("s-7", row["sessionId"]!.GetValue<string>());
        var payload = row["payload"]!.AsObject();
        Assert.False(payload.ContainsKey("SessionId"));
        Assert.Equal("stop", payload["finishReason"]!.GetValue<string>());
        Assert.Equal("a.cs", payload["undoableFiles"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Project_KeepsTheRowWhenThePayloadIsCorrupt()
    {
        var row = EventJournal.Project(new HaoyueDatabase.PersistedEvent(9, "2026-01-01T00:00:00Z", "UsageRecordedEvent", "{oops"));
        Assert.Equal("usage_recorded", row["event"]!.GetValue<string>());
        Assert.Null(row["payload"]);
        Assert.False(row.ContainsKey("sessionId"));
    }

    [Theory]
    [InlineData("TurnStartedEvent", "turn_started")]
    [InlineData("MemoryConsolidationCompletedEvent", "memory_consolidation_completed")]
    [InlineData("UsageRecordedEvent", "usage_recorded")]
    [InlineData("RetrievalMissEvent", "retrieval_miss")]
    [InlineData("MCPUpdatedEvent", "mcp_updated")]
    public void WireName_MatchesTheStableSnakeCase(string typeName, string expected) =>
        Assert.Equal(expected, EventJournal.WireName(typeName));

    [Fact]
    public void RecentEvents_AfterCursor_ReplaysOnlyNewerRows_OldestFirst()
    {
        for (var i = 1; i <= 5; i++)
            _database.AppendEvent(DateTimeOffset.UtcNow, "TestEvent", $"{{\"index\":{i}}}");

        var all = _database.RecentEvents(limit: 100);
        Assert.Equal(5, all.Count);
        var firstId = all[0].Id;

        var tail = _database.RecentEvents(limit: 100, afterId: firstId);
        Assert.Equal(4, tail.Count);
        Assert.All(tail, item => Assert.True(item.Id > firstId));
        // 游标推进后重复请求应为空集：客户端不会二次收到同一条终态。
        Assert.Empty(_database.RecentEvents(limit: 100, afterId: tail[^1].Id));
    }

    [Fact]
    public void AppendEvent_PrunesTheJournalToTheRetentionWindow()
    {
        for (var i = 0; i < EventJournal.RetainedEvents + 10; i++)
            _database.AppendEvent(DateTimeOffset.UtcNow, "TestEvent", "{}");

        var all = _database.RecentEvents(EventJournal.RetainedEvents);
        Assert.Equal(EventJournal.RetainedEvents, all.Count);
        Assert.Equal(11, all[0].Id); // first row inside the retention window

        var recent = _database.RecentEvents(1000);
        Assert.Equal(1000, recent.Count);
        Assert.Equal(EventJournal.RetainedEvents + 10, recent[^1].Id); // newest row
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
    }
}
