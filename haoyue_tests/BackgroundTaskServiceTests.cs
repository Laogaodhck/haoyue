using Haoyue.Runtime;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Scheduling;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>后台任务队列（BackgroundTaskService）单元测试：注入 stub 回合执行器，验证状态机与取消。</summary>
public sealed class BackgroundTaskServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-tests", "bgtask-" + Guid.NewGuid().ToString("N"));

    public BackgroundTaskServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private BackgroundTaskService NewService(BackgroundTurnRunner? runner, out HaoyueRuntime runtime)
    {
        runtime = HaoyueRuntime.Create(
            _dir,
            new ConfigStore(Path.Combine(_dir, "config.json"), Path.Combine(_dir, "state.json")),
            Path.Combine(_dir, "runtime.db"));
        return new BackgroundTaskService(
            runtime, new FileLockCoordinator(), new LlmHttpFactory(),
            new CircuitBreaker(new RetryConfig()), turnRunner: runner);
    }

    private BackgroundTaskService NewService(out HaoyueRuntime runtime) => NewService(null, out runtime);

    private static WorkspaceInfo Workspace(HaoyueRuntime runtime) => runtime.Workspaces.CreateGlobal();

    [Fact]
    public async Task Start_CompletesAndRecordsOutput_AndPublishesTerminalEvent()
    {
        var service = NewService(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("后台结果", false, null)), out var runtime);
        var events = new List<RuntimeEvent>();
        using var subscription = runtime.Events.Subscribe();
        _ = Task.Run(async () =>
        {
            await foreach (var evt in subscription.Reader.ReadAllAsync().ConfigureAwait(false))
                lock (events) events.Add(evt);
        });

        var task = service.Start(Workspace(runtime), "总结一下");
        Assert.Equal(BackgroundTaskStatus.Running, task.Status);

        var finished = await WaitUntilAsync(() => service.Get(task.Id) is { Status: not BackgroundTaskStatus.Running });
        Assert.True(finished, "后台任务未在时限内完成");
        var info = service.Get(task.Id)!;
        Assert.Equal(BackgroundTaskStatus.Completed, info.Status);
        Assert.Equal("后台结果", info.Output);
        Assert.NotNull(info.EndedAt);
        Assert.NotNull(info.SessionId);

        lock (events)
            Assert.Contains(events, e => e is BackgroundTaskEvent bte
                && bte.TaskId == task.Id && bte.Status == BackgroundTaskStatus.Completed);
    }

    [Fact]
    public async Task Cancel_TerminatesRunningTaskWithCancelledStatus()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = NewService(async (_, _, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return new AgentTurnResult("unreachable", false, null);
        }, out var runtime);

        var task = service.Start(Workspace(runtime), "长任务");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancelled = service.Cancel(task.Id);
        Assert.NotNull(cancelled);

        var finished = await WaitUntilAsync(() => service.Get(task.Id) is { Status: not BackgroundTaskStatus.Running });
        Assert.True(finished, "取消后的任务未在时限内进入终态");
        Assert.Equal(BackgroundTaskStatus.Cancelled, service.Get(task.Id)!.Status);
        // 已终态的任务再次取消返回 null。
        Assert.Null(service.Cancel(task.Id));
    }

    [Fact]
    public async Task FailedTurn_RecordsErrorAndFailedStatus()
    {
        var service = NewService(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("", false, "provider down")), out var runtime);

        var task = service.Start(Workspace(runtime), "会失败的任务");
        await WaitUntilAsync(() => service.Get(task.Id) is { Status: not BackgroundTaskStatus.Running });

        var info = service.Get(task.Id)!;
        Assert.Equal(BackgroundTaskStatus.Failed, info.Status);
        Assert.Equal("provider down", info.Error);
    }

    [Fact]
    public void Start_RejectsEmptyPrompt()
    {
        var service = NewService(out var runtime);
        Assert.Throws<ArgumentException>(() => service.Start(Workspace(runtime), "  "));
    }

    [Fact]
    public async Task List_ReturnsNewestFirst()
    {
        var service = NewService(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)), out var runtime);
        var first = service.Start(Workspace(runtime), "任务一");
        var second = service.Start(Workspace(runtime), "任务二");
        await WaitUntilAsync(() =>
            service.Get(first.Id) is { Status: not BackgroundTaskStatus.Running }
            && service.Get(second.Id) is { Status: not BackgroundTaskStatus.Running });

        var list = service.List();
        Assert.True(list.Count >= 2);
        Assert.Equal(second.Id, list[0].Id);
        Assert.Equal(first.Id, list[1].Id);
    }

    [Fact]
    public void Get_UnknownId_ReturnsNull() =>
        Assert.Null(NewService(out _).Get("bg-nope"));

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50).ConfigureAwait(false);
        }
        return condition();
    }
}
