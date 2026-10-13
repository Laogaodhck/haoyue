using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Haoyue.Runtime;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>
/// H-D / M-B：把"软提示"升级为"硬动作"的两条机制。
/// H-D：同签名失败达到换策略阈值后，工具加入回合级隔离名单，后续调用在执行前被拒——
/// 模型无法用"再试一次同样的命令"烧完整个步数预算。
/// M-B：steering abort（显式 opt-in）——用户喊"停"后，在途工具批次中 pending 的
/// mutating 调用不再执行，回合以"被用户中止"收尾；默认"下一回合生效"语义不变。
/// </summary>
public sealed class AgentHardMechanismTests
{
    [Fact]
    public async Task SameSignatureFailures_QuarantineToolForRestOfTurn()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"haoyue-quarantine-missing-{Guid.NewGuid():N}.txt");
        var args = $$"""{"path":"{{missing.Replace("\\", "\\\\")}}"}""";
        var listArgs = $$"""{"path":"."}""";
        using var harness = await HardHarness.CreateAsync(ScriptedSteps(
            ToolCall("c1", "read_file", args),
            ToolCall("c2", "read_file", args),
            ToolCall("c3", "read_file", args),
            ToolCall("c4", "read_file", args),
            ToolCall("c5", "read_file", args),   // 第 4 次失败后已被隔离：不得再执行
            ToolCall("c6", "list_dir", listArgs), // 换用其他工具照常放行
            new LlmCompletion { Text = "换路径后完成" }));

        var result = await harness.RunAsync("反复读取一个不存在的文件");

        Assert.Equal("换路径后完成", result.Text);

        var quarantined = harness.Session.Messages.Single(m => m.Role == ChatRole.Tool && m.ToolCallId == "c5");
        Assert.Contains("quarantined", quarantined.Text);

        var listResult = harness.Session.Messages.Single(m => m.Role == ChatRole.Tool && m.ToolCallId == "c6");
        Assert.True(listResult.ToolSuccess, listResult.Text);

        // 前 4 次是真实执行的真实失败；隔离从第 5 次起生效。
        var report = AgentTurnReport.Capture(harness.Session.Messages, 0, result.Text);
        Assert.Equal(5, report.ToolFailures); // 4 次真实失败 + 1 次隔离拒绝
        Assert.Equal(6, report.ToolCalls);
    }

    [Fact]
    public async Task SteeringAbort_StopsPendingMutatingCalls_InFlightBatch()
    {
        using var harness = await HardHarness.CreateAsync(ScriptedSteps(
            // 单条 completion 内含两个 mutating 调用 = 一个在途工具批次。
            new LlmCompletion
            {
                ToolCalls =
                [
                    new ToolCallRequest("c1", "abort_probe", "{}"),
                    new ToolCallRequest("c2", "abort_probe", "{}"),
                ],
            },
            new LlmCompletion { Text = "不应该到达这一句" }));
        var steering = new AgentSteeringQueue();
        harness.RegisterAbortProbe(steering);

        var result = await harness.RunAsync("执行两个探针调用", steering);

        Assert.True(result.Cancelled);
        // 第一个探针执行时即请求 abort：批次中 pending 的第二个调用不得执行。
        Assert.Equal(1, harness.AbortProbeExecutions);
        Assert.DoesNotContain(harness.Session.Messages, m => m.ToolCallId == "c2");
        Assert.Contains(harness.Session.Messages, m => m.Text.Contains("[turn interrupted]"));
    }

    [Fact]
    public async Task SteeringAbort_TurnAlreadyFinished_ReturnsFalse()
    {
        using var harness = await HardHarness.CreateAsync(ScriptedSteps(new LlmCompletion { Text = "完成" }));
        var steering = new AgentSteeringQueue();
        await harness.RunAsync("直接完成", steering);
        Assert.False(steering.RequestAbort());
    }

    // ---------------------------------------------------------------- harness

    private static List<LlmCompletion> ScriptedSteps(params LlmCompletion[] steps) => [.. steps];

    private static LlmCompletion ToolCall(string id, string name, string args) => new()
    {
        ToolCalls = [new ToolCallRequest(id, name, args)],
    };

    private sealed class ScriptedClientFactory : ILlmClientFactory
    {
        private readonly List<LlmCompletion> _steps;
        public ScriptedClientFactory(List<LlmCompletion> steps) => _steps = steps;
        public ILlmClient GetClient(string kind) => new ScriptedClient(this);

        private sealed class ScriptedClient(ScriptedClientFactory owner) : ILlmClient
        {
            public string Kind => "openai";

            public Task<EmbeddingResult?> EmbedAsync(
                ProviderConfig provider, IReadOnlyList<string> inputs, string? model = null, CancellationToken ct = default)
                => Task.FromResult<EmbeddingResult?>(null);

            public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
                LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
            {
                owner.Requests.Add(request);
                await Task.Yield();
                var index = Math.Min(owner.Requests.Count - 1, owner._steps.Count - 1);
                yield return new LlmCompleted(owner._steps[index]);
            }
        }

        public List<LlmRequest> Requests { get; } = [];
    }

    /// <summary>确定性 abort 探针：每次执行即请求 steering abort，供 M-B 测试在批次
    /// 中途触发"用户喊停"，不依赖时序猜测。</summary>
    private sealed class AbortProbeTool(AgentSteeringQueue steering, Action onExecute) : ITool
    {
        public string Name => "abort_probe";
        public string Description => "Test probe tool that requests a steering abort on execution.";
        public JsonObject ParameterSchema => ToolSchema.Object();
        public bool Mutating => true;
        public bool RequiresWorkspace => false;
        public bool RequiresNetwork => false;
        public string StatusLabel => "Probing";

        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
        {
            onExecute();
            steering.RequestAbort();
            return Task.FromResult(ToolResult.Ok("probe ok"));
        }
    }

    private sealed class HardHarness : IDisposable
    {
        private readonly List<string> _abortProbeLog = [];
        private AgentSteeringQueue? _steering;

        public HaoyueRuntime Runtime { get; }
        public AgentSession Session { get; }
        public WorkspaceInfo Workspace { get; }
        public int AbortProbeExecutions => _abortProbeLog.Count;

        private HardHarness(HaoyueRuntime runtime, AgentSession session, WorkspaceInfo workspace)
        {
            Runtime = runtime;
            Session = session;
            Workspace = workspace;
        }

        public void RegisterAbortProbe(AgentSteeringQueue steering)
        {
            _steering = steering;
            Runtime.Tools.Register(new AbortProbeTool(steering, () => _abortProbeLog.Add("executed")));
        }

        public async Task<AgentTurnResult> RunAsync(string input, AgentSteeringQueue? steering = null) =>
            await Runtime.Agent.RunTurnAsync(Session, Workspace, input, CancellationToken.None, steering: steering);

        public void Dispose()
        {
            _steering?.Complete();
            Runtime.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Workspace.Root)) Directory.Delete(Workspace.Root, true);
        }

        public static async Task<HardHarness> CreateAsync(List<LlmCompletion> steps)
        {
            var dir = Path.Combine(Path.GetTempPath(), "haoyue-hard-mechanism", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var store = new ConfigStore(Path.Combine(dir, "config.json"), Path.Combine(dir, "state.json"));
            store.Config.Providers.Clear();
            store.Config.Providers.Add(new ProviderConfig
            {
                Id = "openai",
                Kind = "openai",
                BaseUrl = "https://test.local/v1",
                Models = [new ModelConfig { Id = "gpt-test", ContextWindow = 128_000 }],
            });
            var capture = new ScriptedClientFactory(steps);
            // 非 global 工作区：写边界 = workspace.Root，探针调用落在测试目录内。
            var workspace = new WorkspaceInfo { Root = Path.Combine(dir, "ws"), ProjectKinds = [] };
            Directory.CreateDirectory(workspace.Root);
            var runtime = HaoyueRuntime.CreateIsolated(workspace, configureServices: services =>
            {
                services.AddSingleton<IConfigStore>(store);
                services.AddSingleton(new HaoyueDatabase(Path.Combine(dir, "state.db")));
                services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
                services.AddSingleton<ILlmClientFactory>(capture);
                services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
            });
            var session = runtime.Sessions.Create(workspace, networkEnabled: false);
            await Task.CompletedTask;
            return new HardHarness(runtime, session, workspace);
        }
    }
}
