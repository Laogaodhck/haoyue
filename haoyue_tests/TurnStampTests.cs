using Haoyue.Runtime.Data;
using Haoyue.Runtime.Events;

namespace Haoyue.Tests;

/// <summary>
/// 回合身份盖章（TurnStamp）：事件总线出口统一盖 TurnId、宿主身份优先、嵌套不覆盖、
/// 台账行与回放投影按回合归组。
/// </summary>
public sealed class TurnStampTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "haoyue-turn-stamp-tests", Guid.NewGuid().ToString("N"));
    private readonly HaoyueDatabase _database;

    public TurnStampTests()
    {
        Directory.CreateDirectory(_tempDir);
        _database = new HaoyueDatabase(Path.Combine(_tempDir, "haoyue.db"));
    }

    [Fact]
    public void Publish_InsideTurnScope_StampsEveryEvent()
    {
        var bus = new EventBus();
        using var subscription = bus.Subscribe();
        using (TurnStamp.BeginScope("req-42"))
        {
            bus.Publish(new StatusEvent("Thinking"));
            bus.Publish(new ToolCallCompletedEvent("c1", "bash", true, "ok", TimeSpan.Zero));
        }
        bus.Publish(new StatusEvent("after"));

        var inside = subscription.Reader.ReadAsync().AsTask().GetAwaiter().GetResult();
        Assert.Equal("req-42", inside.TurnId);
        var inside2 = subscription.Reader.ReadAsync().AsTask().GetAwaiter().GetResult();
        Assert.Equal("req-42", inside2.TurnId);
        var after = subscription.Reader.ReadAsync().AsTask().GetAwaiter().GetResult();
        Assert.Null(after.TurnId);
    }

    [Fact]
    public void BeginScope_InsideOuterScope_IsNoOp_HostIdentityWins()
    {
        var bus = new EventBus();
        using var subscription = bus.Subscribe();
        using (TurnStamp.BeginScope("outer"))
        {
            using (TurnStamp.BeginScope("inner")) // 子代理/内层不得覆盖宿主身份
                bus.Publish(new StatusEvent("nested"));
        }
        var seen = subscription.Reader.ReadAsync().AsTask().GetAwaiter().GetResult();
        Assert.Equal("outer", seen.TurnId);
    }

    [Fact]
    public void Apply_PreservesExplicitTurnId()
    {
        using var _ = TurnStamp.BeginScope("scope-id");
        var stamped = TurnStamp.Apply(new StatusEvent("x") { TurnId = "explicit" });
        Assert.Equal("explicit", stamped.TurnId);
    }

    [Fact]
    public void JournaledBus_RowsCarryTurnId_AndProjectSurfacesIt()
    {
        var bus = new JournaledEventBus(new EventBus(), _database);
        using (TurnStamp.BeginScope("req-7"))
            bus.Publish(new TurnCompletedEvent("s1", false, null));

        var row = _database.RecentEvents().Single();
        var projected = EventJournal.Project(row);
        Assert.Equal("req-7", projected["turnId"]!.GetValue<string>());
        Assert.Equal("turn_completed", projected["event"]!.GetValue<string>());
    }

    [Fact]
    public void AsyncLocal_DoesNotLeakAcrossConcurrentFlows()
    {
        var bus = new EventBus();
        using var subscription = bus.Subscribe();
        var gate = new Barrier(2);

        var a = Task.Run(() =>
        {
            using (TurnStamp.BeginScope("turn-a"))
            {
                gate.SignalAndWait();
                bus.Publish(new StatusEvent("a"));
            }
        });
        var b = Task.Run(() =>
        {
            using (TurnStamp.BeginScope("turn-b"))
            {
                gate.SignalAndWait();
                bus.Publish(new StatusEvent("b"));
            }
        });
        Task.WaitAll(a, b);

        var first = subscription.Reader.ReadAsync().AsTask().GetAwaiter().GetResult();
        var second = subscription.Reader.ReadAsync().AsTask().GetAwaiter().GetResult();
        Assert.Equal(["turn-a", "turn-b"], new[] { first.TurnId, second.TurnId }.OrderBy(t => t, StringComparer.Ordinal));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }
}
