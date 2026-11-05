using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Daemon;

namespace Haoyue.Tests;

public sealed class HookRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-hooks-" + Guid.NewGuid().ToString("N")[..8]);

    public HookRunnerTests() => HookRunner.ResetForTests();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private ConfigStore Store(params HookConfig[] hooks)
    {
        Directory.CreateDirectory(_dir);
        var store = new ConfigStore(
            Path.Combine(_dir, "config.json"), Path.Combine(_dir, "state.json"));
        foreach (var hook in hooks) store.Config.Hooks.Hooks.Add(hook);
        return store;
    }

    private JsonObject Payload(string target, string? workspace = null) => new()
    {
        ["target"] = target,
        ["workspace"] = workspace ?? _dir,
        ["sessionId"] = "session-1",
    };

    [Theory]
    [InlineData("bash*", "bash_run", true)]
    [InlineData("bash*,read*", "read_file", true)]
    [InlineData("bash", "bash_run", false)]
    [InlineData("?", "x", true)]
    [InlineData("", "anything", true)]
    public void MatchesTarget_SupportsWildcards(string match, string target, bool expected) =>
        Assert.Equal(expected, HookRunner.MatchesTarget(match, target));

    [Fact]
    public void Ledger_ArchivesOverflowBeforeTrimming()
    {
        const int max = HookLedger.MinMaxRecords;
        for (var index = 0; index < max + 3; index++)
        {
            HookLedger.Append(_dir, new HookRunRecord(
                DateTimeOffset.Now.AddSeconds(-index), "tool.after", $"hook-{index}", "bash_run",
                true, 1, null, null), max);
        }

        var kept = HookLedger.Read(_dir, 500, max);
        Assert.Equal(max, kept.Count);
        Assert.Equal("tool.after", kept[0].Trigger);

        var archived = Directory.GetFiles(HookLedger.ArchiveDir(_dir))
            .Sum(file => File.ReadAllLines(file).Length);
        Assert.Equal(3, archived);
    }

    [Fact]
    public void Ledger_CapacityIsClampedToSafeRange()
    {
        for (var index = 0; index < HookLedger.MinMaxRecords + 2; index++)
        {
            HookLedger.Append(_dir, new HookRunRecord(
                DateTimeOffset.Now, "task.completed", $"h-{index}", "", true, 0, null, null),
                maxRecords: 10);
        }

        Assert.Equal(HookLedger.MinMaxRecords, HookLedger.ClampMax(10));
        Assert.Equal(HookLedger.DefaultMaxRecords, HookLedger.ClampMax(0));
        Assert.Equal(HookLedger.MinMaxRecords, HookLedger.Read(_dir, 500).Count);
    }

    [Fact]
    public async Task TriggerAsync_WaitModeTimeoutDoesNotBlockOrThrow()
    {
        var store = Store(new HookConfig
        {
            Name = "slow-after",
            Trigger = "task.completed",
            Action = "command",
            Command = "cmd /c ping -n 6 127.0.0.1",
            WaitForCompletion = true,
            Enabled = true,
            TimeoutSeconds = 1,
            FailurePolicy = "warn",
        });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await HookRunner.TriggerAsync(
            "task.completed", Payload("session-1"), store, CancellationToken.None);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"等待模式阻塞了 {watch.Elapsed}");
        var record = Assert.Single(HookLedger.Read(_dir));
        Assert.False(record.Ok);
        Assert.Equal("task.completed", record.Trigger);
    }

    [Fact]
    public async Task ToolBefore_BlockPolicy_BlocksOnFailure()
    {
        var store = Store(new HookConfig
        {
            Name = "deny-danger",
            Trigger = "tool.before",
            Action = "command",
            Command = "cmd /c exit 3",
            Match = "bash*",
            FailurePolicy = "block",
            TimeoutSeconds = 5,
        });

        var decision = await HookRunner.EvaluateAsync(
            "tool.before", Payload("bash_run"), store, CancellationToken.None);

        Assert.True(decision.Blocked);
        Assert.Contains("deny-danger", decision.Reason);
        var ledger = HookLedger.Read(_dir);
        var record = Assert.Single(ledger);
        Assert.False(record.Ok);
        Assert.Equal("tool.before", record.Trigger);
        Assert.Equal("bash_run", record.Target);
    }

    [Fact]
    public async Task ToolBefore_WarnPolicy_LetsCallThrough()
    {
        var store = Store(new HookConfig
        {
            Name = "warn-only",
            Trigger = "tool.before",
            Action = "command",
            Command = "cmd /c exit 2",
            FailurePolicy = "warn",
            TimeoutSeconds = 5,
        });

        var decision = await HookRunner.EvaluateAsync(
            "tool.before", Payload("write_file"), store, CancellationToken.None);

        Assert.False(decision.Blocked);
        Assert.Single(decision.Outcomes);
        Assert.False(decision.Outcomes[0].Ok);
        Assert.Single(HookLedger.Read(_dir));
    }

    [Fact]
    public async Task Match_FiltersOutNonMatchingTools()
    {
        var store = Store(new HookConfig
        {
            Name = "bash-only",
            Trigger = "tool.before",
            Action = "command",
            Command = "cmd /c exit 3",
            Match = "bash*",
            FailurePolicy = "block",
        });

        var decision = await HookRunner.EvaluateAsync(
            "tool.before", Payload("read_file"), store, CancellationToken.None);

        Assert.False(decision.Blocked);
        Assert.Empty(decision.Outcomes);
        Assert.Empty(HookLedger.Read(_dir));
    }

    [Fact]
    public async Task Timeout_IsEnforced_AndDoesNotBlockUnderWarn()
    {
        var store = Store(new HookConfig
        {
            Name = "slow",
            Trigger = "tool.before",
            Action = "command",
            Command = "powershell -NoProfile -Command \"Start-Sleep -Seconds 5\"",
            FailurePolicy = "warn",
            TimeoutSeconds = 1,
        });

        var started = DateTimeOffset.UtcNow;
        var decision = await HookRunner.EvaluateAsync(
            "tool.before", Payload("bash_run"), store, CancellationToken.None);

        Assert.False(decision.Blocked);
        Assert.Single(decision.Outcomes);
        Assert.False(decision.Outcomes[0].Ok);
        Assert.Contains("超时", decision.Outcomes[0].Error);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task SessionStart_NeverBlocks_EvenWithBlockPolicy()
    {
        var store = Store(new HookConfig
        {
            Name = "startup",
            Trigger = "session.start",
            Action = "command",
            Command = "cmd /c exit 3",
            FailurePolicy = "block",
        });

        var decision = await HookRunner.EvaluateAsync(
            "session.start", Payload("session-1"), store, CancellationToken.None);

        Assert.False(decision.Blocked);
    }

    [Fact]
    public async Task SuccessfulCommand_ReturnsOutput_AndRecordsLedger()
    {
        var store = Store(new HookConfig
        {
            Name = "echo",
            Trigger = "tool.after",
            Action = "command",
            Command = "cmd /c echo archived",
            FailurePolicy = "warn",
            TimeoutSeconds = 5,
        });

        var decision = await HookRunner.EvaluateAsync(
            "tool.after", Payload("write_file"), store, CancellationToken.None);

        var outcome = Assert.Single(decision.Outcomes);
        Assert.True(outcome.Ok);
        Assert.Contains("archived", outcome.Output);
        var record = Assert.Single(HookLedger.Read(_dir));
        Assert.True(record.Ok);
        Assert.Contains("archived", record.Output);
    }

    [Fact]
    public void Ledger_IsIsolatedPerWorkspace_AndTrimsBadLines()
    {
        var other = Path.Combine(_dir, "other");
        HookLedger.Append(_dir, new HookRunRecord(
            DateTimeOffset.UtcNow, "tool.after", "a", "x", true, 1, null, null));
        HookLedger.Append(other, new HookRunRecord(
            DateTimeOffset.UtcNow, "tool.after", "b", "y", true, 1, null, null));

        Assert.Equal("a", Assert.Single(HookLedger.Read(_dir)).Hook);
        Assert.Equal("b", Assert.Single(HookLedger.Read(other)).Hook);

        File.WriteAllText(HookLedger.LedgerFile(_dir), "not-json\n");
        Assert.Empty(HookLedger.Read(_dir));
    }

    [Fact]
    public void Validate_RejectsUnsupportedTriggerPolicyAndTimeout()
    {
        Assert.NotNull(HookRunner.Validate(new HookConfig
        {
            Name = "x", Trigger = "cron", Action = "command", Command = "cmd /c exit 0",
        }));
        Assert.NotNull(HookRunner.Validate(new HookConfig
        {
            Name = "x", Trigger = "tool.before", Action = "command", Command = "x",
            FailurePolicy = "explode",
        }));
        Assert.NotNull(HookRunner.Validate(new HookConfig
        {
            Name = "x", Trigger = "tool.before", Action = "command", Command = "x",
            TimeoutSeconds = 0,
        }));
        Assert.Null(HookRunner.Validate(new HookConfig
        {
            Name = "x", Trigger = "tool.before", Action = "command", Command = "cmd /c exit 0",
        }));
    }
}
