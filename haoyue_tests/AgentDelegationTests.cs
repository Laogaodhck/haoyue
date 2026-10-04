using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Haoyue.Runtime;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;
using Haoyue.Runtime.Workspaces;
using Xunit;

namespace Haoyue.Tests;

public class AgentDelegationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-delegate-tests", Guid.NewGuid().ToString("N"));

    public AgentDelegationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---------------------------------------------------------------- unit level

    [Fact]
    public void FilteredToolRegistry_HidesExcludedToolButForwardsTheRest()
    {
        var inner = new ToolRegistry();
        var removeMe = inner.Register(new StubTool("delegate_task"));
        using var keep = inner.Register(new StubTool("read_file"));
        var view = new FilteredToolRegistry(inner, "delegate_task");

        Assert.Null(view.Resolve("delegate_task"));
        Assert.NotNull(view.Resolve("read_file"));
        Assert.Equal(["read_file"], view.All.Select(tool => tool.Name).ToArray());

        // Registrations stay forwarded so MCP re-connects remain visible to sub-agents.
        using var added = view.Register(new StubTool("grep"));
        Assert.NotNull(view.Resolve("grep"));
        removeMe.Dispose();
        Assert.Null(inner.Resolve("delegate_task"));
    }

    [Fact]
    public async Task DelegateTool_RequiresTaskArgument()
    {
        var tool = new DelegateTool(new FilePromptProvider(), new StubDelegator());
        var result = await tool.ExecuteAsync(
            new JsonObject(), new ToolContext { Workspace = GlobalWorkspace(), Events = new EventBus(), Agent = new AgentConfig() },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("task", result.Output);
    }

    [Fact]
    public async Task DelegateTool_ReturnsSubtaskAnswer_AndSurfacesFailures()
    {
        var delegator = new StubDelegator
        {
            Next = new AgentTurnResult("子任务的最终回答", false, null),
        };
        var tool = new DelegateTool(new FilePromptProvider(), delegator);
        var workspace = GlobalWorkspace();
        var context = new ToolContext { Workspace = workspace, Events = new EventBus(), Agent = new AgentConfig() };

        var ok = await tool.ExecuteAsync(
            new JsonObject { ["task"] = "梳理知识库结构" }, context, CancellationToken.None);
        Assert.True(ok.Success);
        Assert.Equal("子任务的最终回答", ok.Output);
        Assert.Equal(workspace.Root, delegator.LastWorkspace?.Root);
        Assert.Equal("梳理知识库结构", delegator.LastTask);

        delegator.Next = new AgentTurnResult("", false, "provider down");
        var failed = await tool.ExecuteAsync(
            new JsonObject { ["task"] = "第二个子任务" }, context, CancellationToken.None);
        Assert.False(failed.Success);
        Assert.Contains("provider down", failed.Output);
    }

    // ---------------------------------------------------------------- integration: one level deep

    [Fact]
    public async Task Delegation_RunsSubAgentOnCleanContext_WithoutDelegateTool()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"), Path.Combine(_dir, "state.json"));
        store.Config.Providers.Clear();
        store.Config.Providers.Add(new ProviderConfig
        {
            Id = "openai", Kind = "openai", BaseUrl = "https://test.local/v1",
            Models = [new ModelConfig { Id = "text-only", ContextWindow = 8_000, MaxOutput = 256 }],
        });
        store.Config.Provider = "openai";
        store.Config.Model = "text-only";
        store.Config.Routing.Fallback = ["openai/text-only"];

        var capture = new ScriptedClientFactory();
        var workspace = new WorkspaceManager().CreateGlobal(Path.Combine(_dir, "global"));
        await using var runtime = HaoyueRuntime.CreateIsolated(workspace, configureServices: services =>
        {
            services.AddSingleton<IConfigStore>(store);
            services.AddSingleton(new HaoyueDatabase(Path.Combine(_dir, "state.db")));
            services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
            services.AddSingleton<ILlmClientFactory>(capture);
            services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
        });

        var session = runtime.Sessions.Create(workspace);
        var result = await runtime.Agent.RunTurnAsync(session, workspace, "帮我委派一个子任务", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("done", result.Text);

        // Call 1 = main agent step 1 (delegation tool call), 2 = sub-agent turn, 3 = main agent step 2.
        Assert.Equal(3, capture.Requests.Count);

        var mainTools = capture.Requests[0].Tools.Select(tool => tool.Name).ToList();
        var subTools = capture.Requests[1].Tools.Select(tool => tool.Name).ToList();
        Assert.Contains("delegate_task", mainTools);
        // Depth limit: the sub-agent must not see the delegation tool.
        Assert.DoesNotContain("delegate_task", subTools);
        Assert.Equal(mainTools.Count - 1, subTools.Count);

        // Clean context: the sub-agent's history is the delegated task alone.
        Assert.Contains("总结知识库的部署说明", capture.Requests[1].Messages.Single(m => m.Role == ChatRole.User).Text);

        // The sub-agent's answer flows back into the main agent as a tool result.
        var mainHistoryText = string.Join("\n", capture.Requests[2].Messages.Select(m => m.Text));
        Assert.Contains("sub-answer", mainHistoryText);
    }

    private WorkspaceInfo GlobalWorkspace() => new WorkspaceManager().CreateGlobal(_dir);

    private sealed class StubTool(string name) : ITool
    {
        public string Name => name;
        public string Description => "stub";
        public JsonObject ParameterSchema => new() { ["type"] = "object" };
        public bool Mutating => false;
        public string StatusLabel => "stub";

        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok("ok"));
    }

    private sealed class StubDelegator : IAgentDelegator
    {
        public AgentTurnResult? Next { get; set; }
        public WorkspaceInfo? LastWorkspace { get; private set; }
        public string? LastTask { get; private set; }

        public Task<AgentTurnResult> RunSubTaskAsync(WorkspaceInfo workspace, string task, CancellationToken ct)
        {
            LastWorkspace = workspace;
            LastTask = task;
            return Task.FromResult(Next ?? new AgentTurnResult("", false, null));
        }
    }

    /// <summary>Answers in script order: delegate call → sub-agent reply → main continuation.</summary>
    private sealed class ScriptedClientFactory : ILlmClientFactory
    {
        public List<LlmRequest> Requests { get; } = [];
        private int _call;

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
                var call = Interlocked.Increment(ref owner._call);
                yield return call switch
                {
                    1 => new LlmCompleted(new LlmCompletion
                    {
                        ToolCalls = [new ToolCallRequest("c1", "delegate_task", """{"task":"总结知识库的部署说明"}""")],
                    }),
                    2 => new LlmCompleted(new LlmCompletion { Text = "sub-answer", FinishReason = "stop" }),
                    _ => new LlmCompleted(new LlmCompletion { Text = "done", FinishReason = "stop" }),
                };
            }
        }
    }
}
