using System.Text.Json.Nodes;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Events;

namespace Haoyue.Tests;

/// <summary>
/// 审批决策留痕（P0）：批准/拒绝/超时/取消都必须落成可回放的 ApprovalDecisionEvent，
/// 记录「谁、何时、对哪个回合、对什么工具与参数、结果」。
/// </summary>
public sealed class ApprovalAuditTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "haoyue-approval-audit-tests", Guid.NewGuid().ToString("N"));
    private readonly HaoyueDatabase _database;

    public ApprovalAuditTests()
    {
        Directory.CreateDirectory(_dir);
        _database = new HaoyueDatabase(Path.Combine(_dir, "audit.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private ApprovalRegistry NewRegistry() =>
        new(new JournaledEventBus(new EventBus(), _database));

    private JsonObject? FindDecision(string callId) => _database.RecentEvents()
        .Where(row => row.Type == nameof(ApprovalDecisionEvent))
        .Select(row => JsonNode.Parse(row.Payload)!.AsObject())
        .FirstOrDefault(obj => obj["CallId"]?.GetValue<string>() == callId);

    private static ApprovalRequestContext Context(string tool = "bash") =>
        new(tool, "rm -rf build", "内置高危守卫", "/repo");

    [Fact]
    public async Task ApprovedDecision_IsJournaledWithActorToolAndTurn()
    {
        var registry = NewRegistry();
        using var scope = TurnStamp.BeginScope("turn-approval");
        var pending = registry.RequestOutcomeAsync("call-approved", Context(), 120);

        Assert.True(registry.Respond("call-approved", approve: true));
        Assert.Equal(ApprovalOutcome.Approved, await pending.WaitAsync(TimeSpan.FromSeconds(5)));

        var row = FindDecision("call-approved");
        Assert.NotNull(row);
        Assert.Equal("bash", row!["ToolName"]!.GetValue<string>());
        Assert.Equal("rm -rf build", row["Summary"]!.GetValue<string>());
        Assert.Equal("内置高危守卫", row["Reason"]!.GetValue<string>());
        Assert.True(row["Approved"]!.GetValue<bool>());
        Assert.Equal("approved", row["Outcome"]!.GetValue<string>());
        Assert.Equal("turn-approval", row["TurnId"]!.GetValue<string>());
        Assert.Equal("/repo", row["WorkspaceRoot"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(row["Actor"]!.GetValue<string>()));
    }

    [Fact]
    public async Task DeniedDecision_IsJournaledOnce()
    {
        var registry = NewRegistry();
        var pending = registry.RequestOutcomeAsync("call-denied", Context("computer_exec"), 120);

        Assert.True(registry.Respond("call-denied", approve: false));
        // 第二个响应者面对已裁决的请求必须失败，且不会写出第二条审计。
        Assert.False(registry.Respond("call-denied", approve: true));
        Assert.Equal(ApprovalOutcome.Denied, await pending.WaitAsync(TimeSpan.FromSeconds(5)));

        var rows = _database.RecentEvents().Count(row => row.Type == nameof(ApprovalDecisionEvent));
        Assert.Equal(1, rows);
        var row = FindDecision("call-denied")!;
        Assert.False(row["Approved"]!.GetValue<bool>());
        Assert.Equal("denied", row["Outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task CancelledTurn_IsJournaledAsCancelled()
    {
        var registry = NewRegistry();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcome = await registry.RequestOutcomeAsync(
            "call-cancelled", Context(), timeoutSeconds: 120, ct: cts.Token);
        Assert.Equal(ApprovalOutcome.Cancelled, outcome);
        Assert.Equal(0, registry.PendingCount);

        var row = FindDecision("call-cancelled")!;
        Assert.False(row["Approved"]!.GetValue<bool>());
        Assert.Equal("cancelled", row["Outcome"]!.GetValue<string>());
        Assert.Equal("turn-cancelled", row["Actor"]!.GetValue<string>());
    }

    [Fact]
    public async Task Timeout_IsJournaledAsTimedOut()
    {
        // 回归：旧实现对 timeout CTS 用了 using，方法一返回就 Dispose，超时回调可能
        // 永远不触发（审批挂死）。这里 1 秒超时必须真的收敛并落一条 timed-out。
        var registry = NewRegistry();
        var outcome = await registry.RequestOutcomeAsync("call-timeout", Context(), timeoutSeconds: 1)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ApprovalOutcome.TimedOut, outcome);
        var row = FindDecision("call-timeout")!;
        Assert.False(row["Approved"]!.GetValue<bool>());
        Assert.Equal("timed-out", row["Outcome"]!.GetValue<string>());
        Assert.Equal("timeout", row["Actor"]!.GetValue<string>());
    }
}
