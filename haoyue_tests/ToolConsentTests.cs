using System.Text.Json.Nodes;
using Haoyue.Runtime;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Scheduling;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace Haoyue.Tests;

/// <summary>
/// 高危工具执行前同意（agent.toolPolicy.requireConsentFor，默认 computer_exec）：
/// 无审批通道立即拒绝并指路；有通道时按 callId 审批、按工具回合内一次性生效。
/// 用桩工具（同名 computer_exec）驱动闸门，不依赖 Computer Use 驱动与真实 shell。
/// </summary>
public sealed class ToolConsentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-consent-test", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ComputerExec_NoApprovalChannel_DeniedBeforeExecution()
    {
        var stub = new StubExecTool();
        var harness = await CreateAsync(
        [
            ToolCall("c1", "computer_exec"),
            Text("done"),
        ]);
        using var _ = harness;
        harness.Runtime.Tools.Register(stub);

        var result = await harness.RunAsync("run shell");

        Assert.Equal("done", result.Text);
        var completed = harness.Events.OfType<ToolCallCompletedEvent>()
            .Single(e => e.ToolName == "computer_exec");
        Assert.False(completed.Success);
        Assert.Contains("没有审批通道", completed.ResultSummary);
        Assert.Empty(stub.Executions); // 闸门在执行前拦下，桩从未被调用
        Assert.Empty(harness.Events.OfType<ApprovalRequiredEvent>());
    }

    [Fact]
    public async Task ComputerExec_ApprovedOnce_PerTurnMemoCoversLaterCalls()
    {
        var stub = new StubExecTool();
        var approvals = new ApprovalRegistry { Interactive = true };
        var harness = await CreateAsync(
        [
            ToolCall("c1", "computer_exec"),
            ToolCall("c2", "computer_exec"),
            Text("done"),
        ], approvals: approvals);
        using var _ = harness;
        harness.Runtime.Tools.Register(stub);

        using var approvalCts = new CancellationTokenSource();
        using var approvalSub = harness.Runtime.Events.Subscribe();
        var responder = PumpApprovalsAsync(approvalSub, approvals, approvalCts.Token);
        AgentTurnResult result;
        try
        {
            result = await harness.RunAsync("run shell twice");
        }
        catch (TimeoutException)
        {
            var seen = string.Join(",", harness.Events.Select(e => e.GetType().Name));
            Assert.Fail($"回合挂起。events=[{seen}] stub={stub.Executions.Count} pending={approvals.PendingCount}");
            throw;
        }
        await approvalCts.CancelAsync();
        await responder;

        Assert.Equal("done", result.Text);
        Assert.Equal(2, stub.Executions.Count);
        Assert.All(
            harness.Events.OfType<ToolCallCompletedEvent>().Where(e => e.ToolName == "computer_exec"),
            e => Assert.True(e.Success));
        // 回合内按工具批准一次：两次调用只弹一张审批卡。
        var approval = Assert.Single(harness.Events.OfType<ApprovalRequiredEvent>());
        Assert.Equal("computer_exec", approval.ToolName);
        Assert.Contains("无沙箱", approval.Reason);
    }

    [Fact]
    public async Task ComputerExec_DeniedByUser_NotExecuted()
    {
        var stub = new StubExecTool();
        var approvals = new ApprovalRegistry { Interactive = true };
        var harness = await CreateAsync(
        [
            ToolCall("c1", "computer_exec"),
            Text("done"),
        ], approvals: approvals);
        using var _ = harness;
        harness.Runtime.Tools.Register(stub);

        using var approvalCts = new CancellationTokenSource();
        using var approvalSub = harness.Runtime.Events.Subscribe();
        var responder = PumpApprovalsAsync(approvalSub, approvals, approvalCts.Token, approve: false);
        AgentTurnResult result;
        try
        {
            result = await harness.RunAsync("run shell");
        }
        catch (TimeoutException)
        {
            var seen = string.Join(",", harness.Events.Select(e => e.GetType().Name));
            Assert.Fail($"回合挂起。events=[{seen}] stub={stub.Executions.Count} pending={approvals.PendingCount}");
            throw;
        }
        await approvalCts.CancelAsync();
        await responder;

        Assert.Equal("done", result.Text);
        Assert.Empty(stub.Executions);
        var completed = harness.Events.OfType<ToolCallCompletedEvent>()
            .Single(e => e.ToolName == "computer_exec");
        Assert.False(completed.Success);
        Assert.Contains("未获得用户同意", completed.ResultSummary);
    }

    [Fact]
    public async Task ScheduledTurn_SystemTierTool_IsGatedThroughSharedApprovalChannel()
    {
        Directory.CreateDirectory(_dir);
        var store = new ConfigStore(Path.Combine(_dir, "config-scheduled.json"), Path.Combine(_dir, "state-scheduled.json"));
        store.Config.Providers.Clear();
        store.Config.Providers.Add(new ProviderConfig
        {
            Id = "openai",
            Kind = "openai",
            BaseUrl = "https://test.local/v1",
            Models = [new ModelConfig { Id = "gpt-test", ContextWindow = 128_000 }],
        });
        store.Config.Agent.AutoVerify = false;
        // 只走「风险分级」闸门：名单为空，确保不是 requireConsentFor 那条旧路径在兜底。
        store.Config.Agent.ToolPolicy.RequireConsentFor = [];
        store.Config.Agent.ToolPolicy.ConsentRiskTiers = ["system"];

        var capture = new ScriptedClientFactory([ToolCall("call-probe", "probe"), Text("done")]);
        var workspace = new WorkspaceManager().CreateGlobal(Path.Combine(_dir, "global-scheduled"));
        var approvals = new ApprovalRegistry { Interactive = true };

        var baseRuntime = HaoyueRuntime.CreateIsolated(workspace, configureServices: services =>
        {
            services.AddSingleton<IConfigStore>(store);
            services.AddSingleton(new HaoyueDatabase(Path.Combine(_dir, "scheduled-base.db")));
            services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
            services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
        });
        var service = new ScheduleService(
            new ScheduleStore(new HaoyueDatabase(Path.Combine(_dir, "scheduled-schedules.db"))),
            baseRuntime, new FileLockCoordinator(), new LlmHttpFactory(),
            new CircuitBreaker(new RetryConfig()), approvals: approvals);

        var stub = new StubProbeTool();
        // 与 RunIsolatedTurnAsync 相同的隔离运行时构建路径：审批登记簿必须是共享交互实例。
        await using var turn = service.CreateTurnRuntime(workspace, "scheduled-test", services =>
        {
            services.AddSingleton<IConfigStore>(store);
            services.AddSingleton(new HaoyueDatabase(Path.Combine(_dir, "scheduled-turn.db")));
            services.AddSingleton<ILlmClientFactory>(capture);
        });
        turn.Tools.Register(stub);
        Assert.Same(approvals, turn.Approvals);
        Assert.True(turn.Approvals.Interactive);

        var session = turn.Sessions.Create(workspace, networkEnabled: false);
        using var subscription = turn.Events.Subscribe();
        using var approvalCts = new CancellationTokenSource();
        var events = new List<RuntimeEvent>();
        var responder = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in subscription.Reader.ReadAllAsync(approvalCts.Token).ConfigureAwait(false))
                {
                    events.Add(evt);
                    if (evt is ApprovalRequiredEvent approval)
                        approvals.Respond(approval.CallId, approve: false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        var result = await turn.Agent.RunTurnAsync(session, workspace, "probe", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));
        await approvalCts.CancelAsync();
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("done", result.Text);
        // 反向用例：无人批准时风险工具绝不执行，而不是被静默放行。
        Assert.Empty(stub.Executions);
        Assert.Contains(events, e => e is ApprovalRequiredEvent { ToolName: "probe" });
        Assert.Contains(events,
            e => e is ToolCallCompletedEvent { ToolName: "probe", Success: false });
    }

    [Fact]
    public void ConsentList_DefaultsToComputerExec_AndIsConfigurable()
    {
        var config = new ToolPolicyConfig();
        Assert.Equal(["computer_exec"], config.RequireConsentFor);

        // 大小写不敏感；未列入的工具（bash 仍只受守卫规则约束）不需要事前同意。
        Assert.True(NeedsConsent("computer_exec", config));
        Assert.True(NeedsConsent("Computer_Exec", config));
        Assert.False(NeedsConsent("bash", config));

        config.RequireConsentFor = ["terminal"];
        Assert.False(NeedsConsent("computer_exec", config));
        Assert.True(NeedsConsent("terminal", config));

        config.RequireConsentFor = [];
        Assert.False(NeedsConsent("terminal", config));
    }

    // 与 Agent.Tools.cs 闸门同一判定（工具名大小写不敏感 + 空清单关闭）。
    private static bool NeedsConsent(string toolName, ToolPolicyConfig config) =>
        config.RequireConsentFor is { Count: > 0 } list
        && list.Any(name => string.Equals(name, toolName, StringComparison.OrdinalIgnoreCase));

    // 订阅必须在校验线程启动前同步建立：否则满载线程池下泵送任务可能排到事件发布之后，
    // 审批请求永远等不到响应（表现为回合挂起）。
    private static Task PumpApprovalsAsync(
        IEventSubscription subscription, ApprovalRegistry approvals, CancellationToken ct, bool approve = true) =>
        Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in subscription.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    if (evt is ApprovalRequiredEvent approval)
                        approvals.Respond(approval.CallId, approve);
                }
            }
            catch (OperationCanceledException)
            {
                // 测试收尾：取消即止
            }
        }, CancellationToken.None);

    private async Task<ConsentHarness> CreateAsync(List<LlmCompletion> steps, ApprovalRegistry? approvals = null)
    {
        Directory.CreateDirectory(_dir);
        var store = new ConfigStore(Path.Combine(_dir, "config.json"), Path.Combine(_dir, "state.json"));
        store.Config.Providers.Clear();
        store.Config.Providers.Add(new ProviderConfig
        {
            Id = "openai",
            Kind = "openai",
            BaseUrl = "https://test.local/v1",
            Models = [new ModelConfig { Id = "gpt-test", ContextWindow = 128_000 }],
        });
        store.Config.Agent.AutoVerify = false;

        var capture = new ScriptedClientFactory(steps);
        var workspace = new WorkspaceManager().CreateGlobal(Path.Combine(_dir, "global"));
        var runtime = HaoyueRuntime.CreateIsolated(workspace, configureServices: services =>
        {
            services.AddSingleton<IConfigStore>(store);
            services.AddSingleton(new HaoyueDatabase(Path.Combine(_dir, "state.db")));
            services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
            services.AddSingleton<ILlmClientFactory>(capture);
            services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
            // 后注册覆盖 DI 默认（非交互）登记簿——与 daemon 注入方式一致。
            if (approvals is not null) services.AddSingleton(approvals);
        });
        var session = runtime.Sessions.Create(workspace, networkEnabled: false);
        return new ConsentHarness(runtime, session, workspace);
    }

    private static LlmCompletion ToolCall(string id, string tool) => new()
    {
        ToolCalls = [new ToolCallRequest(id, tool, "{}")],
    };

    private static LlmCompletion Text(string text) => new() { Text = text };

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class ConsentHarness(
        HaoyueRuntime runtime, AgentSession session, WorkspaceInfo workspace) : IDisposable
    {
        public HaoyueRuntime Runtime { get; } = runtime;
        public List<RuntimeEvent> Events { get; } = [];

        public async Task<AgentTurnResult> RunAsync(string input)
        {
            using var subscription = Runtime.Events.Subscribe();
            var pump = Task.Run(async () =>
            {
                await foreach (var evt in subscription.Reader.ReadAllAsync().ConfigureAwait(false))
                    Events.Add(evt);
            });
            try
            {
                // 快失败：回合级挂起（审批无人响应等）在 30 秒内变成断言失败而不是挂死整个测试运行。
                return await Runtime.Agent.RunTurnAsync(Session, workspace, input, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            finally
            {
                subscription.Dispose();
                await pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }

        public AgentSession Session { get; } = session;

        public void Dispose() => Runtime.Dispose();
    }

    private sealed class StubExecTool : ITool
    {
        public List<string> Executions { get; } = [];
        public string Name => "computer_exec";
        public string Description => "stub";
        public JsonObject ParameterSchema => [];
        public bool Mutating => true;
        public bool RequiresWorkspace => false;
        public string StatusLabel => "Running shell";

        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
        {
            Executions.Add("ran");
            return Task.FromResult(ToolResult.Ok("ok", "ok"));
        }
    }

    /// <summary>没有配名单、但声明了系统级风险的工具：只能由风险分级闸门拦截。</summary>
    private sealed class StubProbeTool : ITool
    {
        public List<string> Executions { get; } = [];
        public string Name => "probe";
        public string Description => "stub";
        public JsonObject ParameterSchema => [];
        public bool Mutating => false;
        public bool RequiresWorkspace => false;
        public string StatusLabel => "Probing";
        public ToolRiskTier RiskTier => ToolRiskTier.Write | ToolRiskTier.System;

        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
        {
            Executions.Add("ran");
            return Task.FromResult(ToolResult.Ok("ok", "ok"));
        }
    }

    private sealed class ScriptedClientFactory(List<LlmCompletion> steps) : ILlmClientFactory
    {
        private readonly List<LlmCompletion> _steps = steps;
        public List<LlmRequest> Requests { get; } = [];
        public ILlmClient GetClient(string kind) => new ScriptedClient(this);

        private sealed class ScriptedClient(ScriptedClientFactory owner) : ILlmClient
        {
            public string Kind => "openai";

            public Task<EmbeddingResult?> EmbedAsync(
                ProviderConfig provider, IReadOnlyList<string> inputs, string? model = null, CancellationToken ct = default) =>
                Task.FromResult<EmbeddingResult?>(null);

            public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
                LlmRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
            {
                owner.Requests.Add(request);
                await Task.Yield();
                var index = Math.Min(owner.Requests.Count - 1, owner._steps.Count - 1);
                yield return new LlmCompleted(owner._steps[index]);
            }
        }
    }
}
