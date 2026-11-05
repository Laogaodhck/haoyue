using System.Diagnostics;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Teams;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>团体运行异步化、取消收敛与启动对账。</summary>
public sealed class TeamRunAsyncTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "haoyue-team-async-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        TeamRunIndex.Clear();
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { } // 后台运行可能仍在收尾写 team-runs.json：临时目录清理失败不影响结论。
    }

    private WorkspaceInfo Workspace() => new()
    {
        Root = _root,
        ProjectKinds = [],
        Config = new WorkspaceConfig(),
    };

    private static TeamDefinition Team() => new()
    {
        Id = "team-async",
        Name = "异步小队",
        Mode = "sequential",
        MaxRounds = 3,
        TimeoutSeconds = 60,
        MaxRetries = 0,
        FailureStrategy = "stop",
        Members =
        [
            new TeamMember { Id = "arch", Name = "架构师", Role = "架构师", Responsibility = "评估方案" },
            new TeamMember { Id = "dev", Name = "实现者", Role = "实现者", Responsibility = "落地实现" },
        ],
    };

    private sealed class BlockingDelegator : IAgentDelegator
    {
        public Task<AgentTurnResult> RunSubTaskAsync(
            WorkspaceInfo workspace, string task, CancellationToken ct) =>
            RunSubTaskAsync(workspace, task, new AgentInvocation(), null, ct);

        public async Task<AgentTurnResult> RunSubTaskAsync(
            WorkspaceInfo workspace, string task, AgentInvocation invocation,
            TurnExecutionScope? parentScope, CancellationToken ct)
        {
            // 模拟长耗时成员：只有取消才会结束。
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new AgentTurnResult("done", Cancelled: false, Error: null);
        }
    }

    [Fact]
    public void Start_ReturnsImmediatelyWithRunningSnapshot()
    {
        var runner = new TeamRunner(new BlockingDelegator());
        var watch = Stopwatch.StartNew();
        var run = runner.Start(Workspace(), Team(), "做一次评审", sessionId: null, parentScope: null);
        watch.Stop();

        Assert.Equal("running", run.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Start 阻塞了 {watch.Elapsed}");
        Assert.True(TeamRunRegistry.IsRunning(run.RunId));

        TeamRunRegistry.Cancel(run.RunId);
    }

    [Fact]
    public async Task Cancel_ConvergesToCancelled()
    {
        var workspace = Workspace();
        var runner = new TeamRunner(new BlockingDelegator());
        var run = runner.Start(workspace, Team(), "做一次评审", sessionId: null, parentScope: null);

        Assert.True(TeamRunRegistry.Cancel(run.RunId));
        var store = new TeamRunStore();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        TeamRun? converged = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            converged = store.Get(workspace, run.RunId);
            if (converged?.Status == "cancelled") break;
            await Task.Delay(100);
        }

        Assert.NotNull(converged);
        Assert.Equal("cancelled", converged!.Status);
        Assert.NotNull(converged.EndedAt);
        Assert.False(TeamRunRegistry.IsRunning(run.RunId));
    }

    [Fact]
    public void Reconcile_MarksResidualRunningAsInterrupted()
    {
        var workspace = Workspace();
        var store = new TeamRunStore();
        store.Save(workspace, new TeamRun(
            "team-run-residual", "team-async", "", "running",
            DateTimeOffset.UtcNow.AddMinutes(-5), null, 0, [], null, null, workspace.Root));

        var reconciled = TeamRunStore.Reconcile(workspace, TeamRunReconciler.InterruptedReason);

        Assert.Equal(1, reconciled);
        var run = store.Get(workspace, "team-run-residual");
        Assert.NotNull(run);
        Assert.Equal("interrupted", run!.Status);
        Assert.NotNull(run.EndedAt);
        Assert.Contains("interrupted", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconcileOnStartup_UsesIndexAndClearsIt()
    {
        var workspace = Workspace();
        var store = new TeamRunStore();
        store.Save(workspace, new TeamRun(
            "team-run-indexed", "team-async", "", "running",
            DateTimeOffset.UtcNow.AddMinutes(-5), null, 0, [], null, null, workspace.Root));
        TeamRunIndex.Add("team-run-indexed", workspace.Root);

        var reconciled = TeamRunReconciler.ReconcileOnStartup();

        Assert.Equal(1, reconciled);
        Assert.Equal("interrupted", store.Get(workspace, "team-run-indexed")!.Status);
        Assert.False(File.Exists(TeamRunIndex.FilePath));
    }
}
