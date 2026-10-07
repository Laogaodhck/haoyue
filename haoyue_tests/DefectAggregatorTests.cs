using Haoyue.Runtime.Data;
using Haoyue.Runtime.Evolution;
using Haoyue.Runtime.Events;
using Microsoft.Data.Sqlite;

namespace Haoyue.Tests;

public sealed class DefectAggregatorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "haoyue-defect-aggregator-tests", Guid.NewGuid().ToString("N"));
    private readonly HaoyueDatabase _database;

    public DefectAggregatorTests()
    {
        Directory.CreateDirectory(_tempDir);
        _database = new HaoyueDatabase(Path.Combine(_tempDir, "haoyue.db"));
    }

    private void Append(RuntimeEvent evt, DateTimeOffset? timestamp = null) =>
        _database.AppendEvent(
            timestamp ?? evt.Timestamp,
            evt.GetType().Name,
            EventJournal.Serialize(evt));

    [Fact]
    public void ToolFailureCluster_DetectedWithinWindow_AndAttributedToSession()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        Append(new TurnStartedEvent("s1", "运行任务"), start);
        for (var i = 0; i < 3; i++)
            Append(new ToolCallCompletedEvent($"c{i}", "bash", false,
                "error: command failed with exit code 1", TimeSpan.FromSeconds(1)),
                start.AddMinutes(i));

        var reports = new DefectAggregator(_database).Aggregate();

        var cluster = Assert.Single(reports);
        Assert.Equal(DefectKind.ToolFailureCluster, cluster.Kind);
        Assert.Equal("bash", cluster.ToolName);
        Assert.Equal(3, cluster.Occurrences);
        Assert.Equal("s1", cluster.SessionId);
        Assert.Equal(16, cluster.Fingerprint.Length);
    }

    [Fact]
    public void ToolFailureCluster_SpreadBeyondWindow_DoesNotReport()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-2);
        // 0 / 20 / 40 minutes: no 30-minute span contains three failures.
        for (var i = 0; i < 3; i++)
            Append(new ToolCallCompletedEvent($"c{i}", "bash", false, "boom", TimeSpan.Zero),
                start.AddMinutes(i * 20));

        Assert.Empty(new DefectAggregator(_database).Aggregate());
    }

    [Fact]
    public void DifferentErrors_DoNotMergeIntoOneCluster()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        Append(new ToolCallCompletedEvent("c1", "bash", false, "error: missing file", TimeSpan.Zero), start);
        Append(new ToolCallCompletedEvent("c2", "bash", false, "error: permission denied", TimeSpan.Zero), start.AddMinutes(1));
        Append(new ToolCallCompletedEvent("c3", "edit", false, "error: missing file", TimeSpan.Zero), start.AddMinutes(2));

        Assert.Empty(new DefectAggregator(_database).Aggregate());
    }

    [Fact]
    public void VerificationStruggle_ThreeConsecutiveFailures_ReportsRecoveredRun()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-15);
        Append(new VerificationCompletedEvent(false, "dotnet build failed: CS1002", 1), start);
        Append(new VerificationCompletedEvent(false, "dotnet build failed: CS1002", 2), start.AddMinutes(1));
        Append(new VerificationCompletedEvent(false, "dotnet build failed: CS1002", 3), start.AddMinutes(2));
        Append(new VerificationCompletedEvent(true, "dotnet build ok", 4), start.AddMinutes(3));

        var struggle = Assert.Single(new DefectAggregator(_database).Aggregate());

        Assert.Equal(DefectKind.VerificationStruggle, struggle.Kind);
        Assert.Equal("verify", struggle.ToolName);
        Assert.Equal(3, struggle.Occurrences);
        Assert.Contains("cs1002", struggle.ErrorSummary);
    }

    [Fact]
    public void VerificationRun_ResetsOnSuccess_SoIsolatedFailuresDoNotReport()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        Append(new VerificationCompletedEvent(false, "failed once", 1), start);
        Append(new VerificationCompletedEvent(true, "ok", 2), start.AddMinutes(1));
        Append(new VerificationCompletedEvent(false, "failed again", 1), start.AddMinutes(2));

        Assert.Empty(new DefectAggregator(_database).Aggregate());
    }

    [Fact]
    public void CapabilityGap_SingleDeclareSkillFailure_Reports()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        Append(new TurnStartedEvent("s1", "加载技能"), start);
        Append(new ToolCallCompletedEvent("c1", "declare_skill", false,
            "技能 deploy-check 不存在、未启用或不在可用技能目录中。", TimeSpan.Zero), start);

        var gap = Assert.Single(new DefectAggregator(_database).Aggregate());

        Assert.Equal(DefectKind.CapabilityGap, gap.Kind);
        Assert.Equal("declare_skill", gap.ToolName);
        Assert.Equal(1, gap.Occurrences);
        Assert.Equal("s1", gap.SessionId);
    }

    [Fact]
    public void Fingerprint_IsStableAcrossCasingAndWhitespaceNoise()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        Append(new ToolCallCompletedEvent("c1", "bash", false, "Error:   network   unreachable", TimeSpan.Zero), start);
        Append(new ToolCallCompletedEvent("c2", "bash", false, "error: network unreachable", TimeSpan.Zero), start.AddMinutes(1));
        Append(new ToolCallCompletedEvent("c3", "bash", false, "ERROR: network unreachable", TimeSpan.Zero), start.AddMinutes(2));

        var cluster = Assert.Single(new DefectAggregator(_database).Aggregate());
        Assert.Equal(3, cluster.Occurrences);
        Assert.Equal(
            DefectAggregator.NormalizeError("Error:   Network   Unreachable"),
            DefectAggregator.NormalizeError("error: network unreachable"));
    }

    [Fact]
    public void TruncatedAndForeignPayloads_AreSkippedWithoutThrowing()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        // Oversized payload collapsed by EventJournal — must not fabricate a failure.
        _database.AppendEvent(start, nameof(ToolCallCompletedEvent), """{"type":"truncated","truncated":true}""");
        // A tool success must never be counted, and malformed JSON must not crash.
        Append(new ToolCallCompletedEvent("c1", "bash", true, "ok", TimeSpan.Zero), start);
        _database.AppendEvent(start.AddMinutes(1), nameof(ToolCallCompletedEvent), "{not json");

        Assert.Empty(new DefectAggregator(_database).Aggregate());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
    }
}
