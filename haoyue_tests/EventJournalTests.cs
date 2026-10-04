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
