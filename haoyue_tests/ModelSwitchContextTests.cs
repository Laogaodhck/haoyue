using System.Runtime.CompilerServices;
using Haoyue.Runtime;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace Haoyue.Tests;

/// <summary>
/// 模型切换保持上下文连通：回合 1 用模型 A 完成后，把活动模型切到 B（等价于
/// daemon 的 model.switch 改 config），回合 2 的请求必须携带回合 1 的全部历史
/// （用户消息 + 助手消息，且助手消息带 A 的 ModelRef），并且请求本身落在 B 上。
/// 该行为是「切换模型后对话不断线」的回归护栏。
/// </summary>
public sealed class ModelSwitchContextTests
{
    private sealed class SwitchHarness : IDisposable
    {
        public HaoyueRuntime Runtime { get; }
        public AgentSession Session { get; }
        public WorkspaceInfo Workspace { get; }
        public ConfigStore Store { get; }
        public ScriptedClientFactory Capture { get; }

        private SwitchHarness(HaoyueRuntime runtime, AgentSession session, WorkspaceInfo workspace,
            ConfigStore store, ScriptedClientFactory capture)
        {
            Runtime = runtime;
            Session = session;
            Workspace = workspace;
            Store = store;
            Capture = capture;
        }

        public async Task<AgentTurnResult> RunAsync(string input) =>
            await Runtime.Agent.RunTurnAsync(Session, Workspace, input, CancellationToken.None);

        public void Dispose()
        {
            Runtime.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Workspace.Root)) Directory.Delete(Workspace.Root, true);
        }

        public static SwitchHarness CreateAsync(List<LlmCompletion> steps)
        {
            var dir = Path.Combine(Path.GetTempPath(), "haoyue-switch-context", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var store = new ConfigStore(Path.Combine(dir, "config.json"), Path.Combine(dir, "state.json"));
            store.Config.Providers.Clear();
            // 两个 provider / 模型：A 是回合 1 的活动模型，B 是切换目标。
            store.Config.Providers.Add(new ProviderConfig
            {
                Id = "openai",
                Kind = "openai",
                BaseUrl = "https://test-a.local/v1",
                Models = [new ModelConfig { Id = "gpt-test", ContextWindow = 128_000, MaxOutput = 512 }],
            });
            store.Config.Providers.Add(new ProviderConfig
            {
                Id = "second",
                Kind = "openai",
                BaseUrl = "https://test-b.local/v1",
                Models = [new ModelConfig { Id = "glm-test", ContextWindow = 128_000, MaxOutput = 512 }],
            });
            store.Config.Provider = "openai";
            store.Config.Model = "gpt-test";

            var capture = new ScriptedClientFactory(steps);
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
            return new SwitchHarness(runtime, session, workspace, store, capture);
        }
    }

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

    [Fact]
    public async Task SwitchingActiveModel_KeepsPriorTurnHistoryInNextRequest()
    {
        using var harness = SwitchHarness.CreateAsync(
        [
            new LlmCompletion { Text = "我是模型 A 的回答", FinishReason = "stop" },
            new LlmCompletion { Text = "我是模型 B 的回答", FinishReason = "stop" },
        ]);

        // 回合 1：活动模型 A（openai/gpt-test）
        var turn1 = await harness.RunAsync("第一轮：请记住一个事实，秘钥是 7-3-2-8。");
        Assert.False(turn1.Cancelled);
        Assert.Equal("我是模型 A 的回答", turn1.Text);
        Assert.Single(harness.Capture.Requests);

        // 等价于 model.switch：把活动模型切到 B（second/glm-test）
        harness.Store.Config.Provider = "second";
        harness.Store.Config.Model = "glm-test";
        harness.Store.Save();

        // 回合 2：活动模型 B——请求必须携带回合 1 的历史，且落在 B 上
        var turn2 = await harness.RunAsync("第二轮：我们之前约定了什么秘钥？");
        Assert.False(turn2.Cancelled);
        Assert.Equal(2, harness.Capture.Requests.Count);
        var second = harness.Capture.Requests[1];
        Assert.Equal("second", second.Provider.Id);
        Assert.Equal("glm-test", second.Model.Id);

        // 上下文连通：回合 1 的用户消息与助手消息都在回合 2 的请求里
        var texts = second.Messages.Select(m => $"{m.Role}:{m.Text}").ToList();
        Assert.Contains(texts, t => t.Contains("第一轮：请记住一个事实", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("我是模型 A 的回答", StringComparison.Ordinal));

        // 助手消息保留产生它的模型引用（跨模型历史可溯源）
        var assistant = second.Messages.First(m => m.Role == ChatRole.Assistant && m.Text.Contains("我是模型 A 的回答", StringComparison.Ordinal));
        Assert.Equal("openai/gpt-test", assistant.ModelRef);
    }

    [Fact]
    public async Task SwitchingBack_AfterTwoMoves_StillCarriesFullHistory()
    {
        using var harness = SwitchHarness.CreateAsync(
        [
            new LlmCompletion { Text = "A 的回合", FinishReason = "stop" },
            new LlmCompletion { Text = "B 的回合", FinishReason = "stop" },
            new LlmCompletion { Text = "回到 A", FinishReason = "stop" },
        ]);

        await harness.RunAsync("第一轮：说点什么。");
        harness.Store.Config.Provider = "second";
        harness.Store.Config.Model = "glm-test";
        await harness.RunAsync("第二轮：接着聊。");
        harness.Store.Config.Provider = "openai";
        harness.Store.Config.Model = "gpt-test";
        await harness.RunAsync("第三轮：回到最初的模型。");

        Assert.Equal(3, harness.Capture.Requests.Count);
        var third = harness.Capture.Requests[2];
        Assert.Equal("openai", third.Provider.Id);
        Assert.Equal("gpt-test", third.Model.Id);

        // 三次切换/切回后，第一、二轮的消息仍在第三轮请求里
        var texts = third.Messages.Select(m => $"{m.Role}:{m.Text}").ToList();
        Assert.Contains(texts, t => t.Contains("第一轮：说点什么。", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("A 的回合", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("第二轮：接着聊。", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("B 的回合", StringComparison.Ordinal));
    }
}
