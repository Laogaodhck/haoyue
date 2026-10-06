using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Haoyue.Runtime;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>
/// Agent 输出精度与命中率保障：空回答兜底、重复失败策略纠偏、步数预算收尾与回合级
/// 指标报告。全部通过脚本化 LLM 客户端离线驱动，不依赖真实模型。
/// </summary>
public sealed class AgentPrecisionTests
{
    [Fact]
    public async Task EmptyAnswer_NudgeOnce_ThenRecover()
    {
        using var harness = await AgentHarness.CreateAsync(
            ScriptedSteps(new LlmCompletion(), new LlmCompletion { Text = "最终回答：任务已完成" }));
        var result = await harness.RunAsync("做个总结");

        Assert.Equal("最终回答：任务已完成", result.Text);
        var nudge = harness.Session.Messages.SingleOrDefault(m =>
            m.Role == ChatRole.User && m.Text.Contains("[empty answer]"));
        Assert.NotNull(nudge);
    }

    [Fact]
    public async Task RepeatedIdenticalFailures_InjectStrategyGuidanceOnce()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"haoyue-missing-{Guid.NewGuid():N}.txt");
        var args = $$"""{"path":"{{missing.Replace("\\", "\\\\")}}"}""";
        using var harness = await AgentHarness.CreateAsync(ScriptedSteps(
            ToolCall("c1", "read_file", args),
            ToolCall("c2", "read_file", args),
            ToolCall("c3", "read_file", args),
            new LlmCompletion { Text = "换了一种方法后完成" }));

        var result = await harness.RunAsync("读取一个不存在的文件");

        Assert.Equal("换了一种方法后完成", result.Text);
        var nudges = harness.Session.Messages
            .Where(m => m.Role == ChatRole.User && m.Text.Contains("[repeated tool failure]"))
            .ToList();
        Assert.Single(nudges);
        Assert.Contains("read_file", nudges[0].Text);

        var report = AgentTurnReport.Capture(harness.Session.Messages, 0, result.Text);
        Assert.Equal(4, report.AssistantSteps);
        Assert.Equal(3, report.ToolCalls);
        Assert.Equal(3, report.ToolFailures);
        Assert.Equal(3, report.MaxConsecutiveSameToolFailures);
        Assert.False(report.FinalAnswerEmpty);
    }

    [Fact]
    public async Task StepBudgetExhausted_GrantsWrapUpStepAndSummarizes()
    {
        var existing = Path.Combine(Path.GetTempPath(), $"haoyue-existing-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(existing, "content");
        var args = $$"""{"path":"{{existing.Replace("\\", "\\\\")}}"}""";
        using var harness = await AgentHarness.CreateAsync(ScriptedSteps(
            ToolCall("c1", "read_file", args),
            new LlmCompletion { Text = "收尾总结：已确认文件内容" }));
        harness.Store.Config.Agent.MaxSteps = 1;
        harness.Store.Config.Agent.MaxRepairAttempts = 0;

        var result = await harness.RunAsync("多读几次文件");

        Assert.Equal("收尾总结：已确认文件内容", result.Text);
        Assert.NotNull(harness.Session.Messages.SingleOrDefault(m =>
            m.Role == ChatRole.User && m.Text.Contains("[step budget]")));
    }

    [Fact]
    public void AgentTurnReport_ComputesMetricsFromMessages()
    {
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                ToolCalls =
                [
                    new ToolCallRequest("a", "read_file", "{}"),
                    new ToolCallRequest("b", "read_file", "{}"),
                ],
            },
            new() { Role = ChatRole.Tool, ToolCallId = "a", ToolName = "read_file", ToolSuccess = false },
            new() { Role = ChatRole.Tool, ToolCallId = "b", ToolName = "read_file", ToolSuccess = true },
            new() { Role = ChatRole.Assistant, ToolCalls = [new ToolCallRequest("c", "edit_file", "{}")] },
            new() { Role = ChatRole.Tool, ToolCallId = "c", ToolName = "edit_file", ToolSuccess = false },
            ChatMessage.Assistant("结论"),
        };

        var report = AgentTurnReport.Capture(messages, finalText: "结论");

        Assert.Equal(3, report.AssistantSteps);
        Assert.Equal(3, report.ToolCalls);
        Assert.Equal(2, report.ToolFailures);
        Assert.Equal(1, report.MaxConsecutiveSameToolFailures);
        Assert.Equal(2.0 / 3, report.ToolFailureRate, precision: 4);
        Assert.False(report.FinalAnswerEmpty);
        Assert.Contains("失败率 66.7", report.RenderSummary());
    }

    [Fact]
    public void AgentTurnReport_EmptyFinalAnswerFlagsMiss()
    {
        var report = AgentTurnReport.Capture([ChatMessage.Assistant("")], finalText: "");
        Assert.True(report.FinalAnswerEmpty);
        Assert.True(report.TurnEndedWithoutAnswer);
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
        public List<LlmRequest> Requests { get; } = [];
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
    }

    private sealed class AgentHarness(
        ConfigStore store, HaoyueRuntime runtime, AgentSession session, WorkspaceInfo workspace,
        ScriptedClientFactory factory) : IDisposable
    {
        public ConfigStore Store { get; } = store;
        public AgentSession Session { get; } = session;

        public async Task<AgentTurnResult> RunAsync(string input) =>
            await runtime.Agent.RunTurnAsync(Session, workspace, input, CancellationToken.None);

        public void Dispose()
        {
            runtime.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(workspace.Root)) Directory.Delete(workspace.Root, true);
        }

        public static async Task<AgentHarness> CreateAsync(List<LlmCompletion> steps)
        {
            var dir = Path.Combine(Path.GetTempPath(), "haoyue-precision-test", Guid.NewGuid().ToString("N"));
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
            var workspace = new WorkspaceManager().CreateGlobal(Path.Combine(dir, "global"));

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
            return new AgentHarness(store, runtime, session, workspace, capture);
        }
    }
}
