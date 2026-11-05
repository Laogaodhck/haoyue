using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Teams;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

public sealed class TeamRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "haoyue-team-run-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private WorkspaceInfo Workspace(string? root = null) => new()
    {
        Root = root ?? _root,
        ProjectKinds = [],
        Config = new WorkspaceConfig(),
    };

    private static TeamDefinition Team(
        string mode = "sequential",
        int maxRounds = 3,
        int timeoutSeconds = 30,
        int maxRetries = 0,
        string failureStrategy = "stop",
        string? coordinatorId = null)
    {
        var members = new List<TeamMember>
        {
            new() { Id = "arch", Name = "架构师", Role = "架构师", Responsibility = "评估方案" },
            new() { Id = "dev", Name = "实现者", Role = "实现者", Responsibility = "落地实现" },
        };
        return new TeamDefinition
        {
            Id = "team-test",
            Name = "测试小队",
            Mode = mode,
            CoordinatorId = coordinatorId,
            MaxRounds = maxRounds,
            TimeoutSeconds = timeoutSeconds,
            MaxRetries = maxRetries,
            FailureStrategy = failureStrategy,
            Members = members,
        };
    }

    private static AgentTurnResult Text(string text) => new(text, Cancelled: false, Error: null);

    private sealed class StubDelegator : IAgentDelegator
    {
        private readonly Func<string, CancellationToken, Task<AgentTurnResult>> _handler;
        private readonly List<string> _prompts = [];
        private readonly List<AgentInvocation> _invocations = [];
        private int _concurrent;
        private int _maxConcurrent;

        public StubDelegator(Func<string, CancellationToken, Task<AgentTurnResult>> handler) => _handler = handler;

        public IReadOnlyList<string> Prompts
        {
            get { lock (_prompts) return [.. _prompts]; }
        }

        public IReadOnlyList<AgentInvocation> Invocations
        {
            get { lock (_prompts) return [.. _invocations]; }
        }

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public Task<AgentTurnResult> RunSubTaskAsync(WorkspaceInfo workspace, string task, CancellationToken ct) =>
            RunAsync(task, new AgentInvocation(), ct);

        public async Task<AgentTurnResult> RunSubTaskAsync(
            WorkspaceInfo workspace, string task, AgentInvocation invocation,
            TurnExecutionScope? parentScope, CancellationToken ct) =>
            await RunAsync(task, invocation, ct);

        private async Task<AgentTurnResult> RunAsync(string prompt, AgentInvocation invocation, CancellationToken ct)
        {
            lock (_prompts)
            {
                _prompts.Add(prompt);
                _invocations.Add(invocation);
            }
            var current = Interlocked.Increment(ref _concurrent);
            try
            {
                lock (_prompts)
                    _maxConcurrent = Math.Max(_maxConcurrent, current);
                return await _handler(prompt, ct);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }
    }

    [Fact]
    public async Task Sequential_ChainsUpstreamOutputsInOrder()
    {
        var stub = new StubDelegator((prompt, _) => Task.FromResult(
            Text(prompt.Contains("成员：架构师") ? "方案 A" : "实现 A")));

        var run = await new TeamRunner(stub).RunAsync(
            Workspace(), Team(), "发布 1.0", sessionId: null, parentScope: null, CancellationToken.None);

        Assert.Equal("succeeded", run.Status);
        var prompts = stub.Prompts;
        Assert.Contains("成员：架构师", prompts[0]);
        Assert.Contains("成员：实现者", prompts[1]);
        // 第二棒能看到上游结论。
        Assert.Contains("方案 A", prompts[1]);
        Assert.Contains("方案 A", run.Result);
        Assert.Contains("实现 A", run.Result);
        Assert.Equal(1, run.Rounds);
        Assert.All(run.Members, member => Assert.Equal("succeeded", member.Status));
    }

    [Fact]
    public async Task Parallel_RunsMembersConcurrently()
    {
        var stub = new StubDelegator(async (prompt, ct) =>
        {
            await Task.Delay(80, ct);
            return Text(prompt.Contains("成员：架构师") ? "并行-架构" : "并行-实现");
        });

        var run = await new TeamRunner(stub).RunAsync(
            Workspace(), Team("parallel"), "并行任务", null, null, CancellationToken.None);

        Assert.Equal("succeeded", run.Status);
        Assert.True(stub.MaxConcurrent >= 2, $"实际最大并发 {stub.MaxConcurrent}");
        Assert.Contains("并行-架构", run.Result);
        Assert.Contains("并行-实现", run.Result);
    }

    [Fact]
    public async Task Supervisor_PlansThenSummarizes_AndStopsOnDone()
    {
        var stub = new StubDelegator((prompt, _) =>
        {
            if (prompt.Contains("轮分派")) return Task.FromResult(Text("计划：架构师评估，实现者落地"));
            if (prompt.Contains("轮汇总")) return Task.FromResult(Text("最终答复\nDONE"));
            return Task.FromResult(Text("成员产出"));
        });

        var run = await new TeamRunner(stub).RunAsync(
            Workspace(), Team("supervisor", coordinatorId: "arch"), "主管任务", null, null, CancellationToken.None);

        Assert.Equal("succeeded", run.Status);
        Assert.Equal(1, run.Rounds);
        Assert.Contains("最终答复", run.Result);
        Assert.Contains(stub.Prompts, prompt => prompt.Contains("轮分派"));
        Assert.Contains(stub.Prompts, prompt => prompt.Contains("轮汇总"));
    }

    [Fact]
    public async Task Supervisor_RespectsRoundLimit_WhenCoordinatorKeepsGoing()
    {
        var stub = new StubDelegator((prompt, _) =>
        {
            if (prompt.Contains("轮分派")) return Task.FromResult(Text("计划"));
            if (prompt.Contains("轮汇总")) return Task.FromResult(Text("还没完\nCONTINUE"));
            return Task.FromResult(Text("产出"));
        });

        var run = await new TeamRunner(stub).RunAsync(
            Workspace(), Team("supervisor", maxRounds: 2, coordinatorId: "arch"),
            "主管任务", null, null, CancellationToken.None);

        Assert.Equal("succeeded", run.Status);
        Assert.Equal(2, run.Rounds);
        Assert.Equal(2, stub.Prompts.Count(prompt => prompt.Contains("轮汇总")));
    }

    [Fact]
    public async Task Review_RetriesUntilApproved_AndCarriesFeedback()
    {
        var reviews = 0;
        var stub = new StubDelegator((prompt, _) =>
        {
            if (prompt.Contains("的审核者"))
            {
                reviews++;
                return Task.FromResult(Text(reviews == 1 ? "缺少回滚方案\nREJECT" : "通过\nAPPROVE"));
            }
            return Task.FromResult(Text(prompt.Contains("回滚") ? "补充回滚" : "初版"));
        });

        var run = await new TeamRunner(stub).RunAsync(
            Workspace(), Team("review", maxRounds: 2, coordinatorId: "dev"),
            "评审任务", null, null, CancellationToken.None);

        Assert.Equal("succeeded", run.Status);
        Assert.Equal(2, reviews);
        Assert.Contains("补充回滚", run.Result);
        Assert.Contains("审核意见", run.Result);
        Assert.Contains(stub.Prompts, prompt => prompt.Contains("缺少回滚方案"));
    }

    [Fact]
    public async Task Review_UnapprovedAtLimit_MarksPartial()
    {
        var stub = new StubDelegator((prompt, _) => Task.FromResult(
            prompt.Contains("的审核者") ? Text("不行\nREJECT") : Text("产出")));

        var run = await new TeamRunner(stub).RunAsync(
            Workspace(), Team("review", maxRounds: 2, coordinatorId: "dev"),
            "评审任务", null, null, CancellationToken.None);

        Assert.Equal("partial", run.Status);
        Assert.Equal(2, run.Rounds);
    }

    [Fact]
    public async Task Retry_SucceedsAfterTransientFailures()
    {
        var attempts = 0;
        var stub = new StubDelegator((prompt, _) =>
        {
            if (!prompt.Contains("实现者")) return Task.FromResult(Text("架构完成"));
            attempts++;
            if (attempts < 3) throw new InvalidOperationException("临时失败");
            return Task.FromResult(Text("实现完成"));
        });

        var run = await new TeamRunner(stub).RunAsync(
            Workspace(), Team(maxRetries: 2), "重试任务", null, null, CancellationToken.None);

        Assert.Equal("succeeded", run.Status);
        Assert.Equal(3, attempts);
        Assert.Contains("实现完成", run.Result);
    }

    [Fact]
    public async Task FailureStrategy_StopAborts_ContinueMarksPartial()
    {
        var failing = new StubDelegator((prompt, _) => throw new InvalidOperationException("模型不可用"));

        var stopped = await new TeamRunner(failing).RunAsync(
            Workspace(), Team(failureStrategy: "stop"), "失败任务", null, null, CancellationToken.None);
        Assert.Equal("failed", stopped.Status);
        Assert.Contains("模型不可用", stopped.Error);
        Assert.Equal("failed", stopped.Members[0].Status);

        var continued = await new TeamRunner(failing).RunAsync(
            Workspace(), Team(failureStrategy: "continue"), "失败任务", null, null, CancellationToken.None);
        Assert.Equal("partial", continued.Status);
        Assert.All(continued.Members, member => Assert.Equal("failed", member.Status));
    }

    [Fact]
    public async Task Timeout_StopsRun_AndKeepsTrajectory()
    {
        var stub = new StubDelegator((_, ct) => Task.Delay(Timeout.Infinite, ct)
            .ContinueWith(_ => Text("不应返回"), ct));

        var run = await new TeamRunner(stub).RunAsync(
            Workspace(), Team(timeoutSeconds: 1), "超时任务", null, null, CancellationToken.None);

        Assert.Equal("cancelled", run.Status);
        Assert.Contains("超过", run.Error);
        Assert.True(run.EndedAt is not null);
        Assert.NotEmpty(new TeamRunStore().List(Workspace()));
    }

    [Fact]
    public async Task Cancellation_ConvergesToCancelled()
    {
        var stub = new StubDelegator(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Text("不应返回");
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));

        var run = await new TeamRunner(stub).RunAsync(
            Workspace(), Team(timeoutSeconds: 30), "取消任务", null, null, cts.Token);

        Assert.Equal("cancelled", run.Status);
        Assert.Contains("已取消", run.Error);
        Assert.Contains(run.Members, member => member.Status == "running");
    }

    [Fact]
    public async Task RunTrajectory_IsPersistedPerWorkspace()
    {
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        var stub = new StubDelegator((prompt, _) => Task.FromResult(
            Text(prompt.Contains("成员：架构师") ? "甲" : "乙")));
        var runner = new TeamRunner(stub);

        var first = await runner.RunAsync(Workspace(), Team(), "任务一", null, null, CancellationToken.None);
        var second = await runner.RunAsync(Workspace(other), Team(), "任务二", null, null, CancellationToken.None);

        var store = new TeamRunStore();
        var firstRuns = store.List(Workspace());
        var secondRuns = store.List(Workspace(other));
        Assert.Single(firstRuns);
        Assert.Single(secondRuns);
        Assert.Equal(first.RunId, firstRuns[0].RunId);
        Assert.Equal(_root, firstRuns[0].WorkspaceRoot);
        Assert.Equal(Path.GetFullPath(other), Path.GetFullPath(secondRuns[0].WorkspaceRoot));
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.All(firstRuns[0].Members, member => Assert.True(member.DurationMs >= 0));
    }

    [Fact]
    public async Task MemberInvocation_CarriesModelAndToolScope()
    {
        var stub = new StubDelegator((_, _) => Task.FromResult(Text("ok")));
        var team = Team();
        team.Members[0].Model = "openai/gpt-5";
        team.Members[0].ExpertId = "reviewer";
        team.Members[0].Tools = ["read_file", "grep"];

        await new TeamRunner(stub).RunAsync(Workspace(), team, "任务", null, null, CancellationToken.None);

        var invocation = stub.Invocations[0];
        Assert.Equal("openai/gpt-5", invocation.Model);
        Assert.Equal("reviewer", invocation.RolePreset);
        Assert.Equal(["read_file", "grep"], invocation.AllowedTools);
    }
}
