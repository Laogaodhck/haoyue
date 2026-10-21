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

        Assert.True(result.Error is null, result.Error ?? "ok");
        Assert.Equal("done", result.Text);

        // Call 1 = main agent step 1 (delegation tool call), 2 = sub-agent turn, 3 = main agent step 2.
        Assert.Equal(3, capture.Requests.Count);

        var mainTools = capture.Requests[0].Tools.Select(tool => tool.Name).ToList();
        var subTools = capture.Requests[1].Tools.Select(tool => tool.Name).ToList();
        Assert.Contains("delegate_task", mainTools);
        Assert.Contains("delegate_tasks", mainTools);
        // Depth limit: the sub-agent must not see either delegation tool.
        Assert.DoesNotContain("delegate_task", subTools);
        Assert.DoesNotContain("delegate_tasks", subTools);
        Assert.Equal(mainTools.Count - 2, subTools.Count);

        // Clean context: the sub-agent's history is the delegated task alone.
        Assert.Contains("总结知识库的部署说明", capture.Requests[1].Messages.Single(m => m.Role == ChatRole.User).Text);

        // The sub-agent's answer flows back into the main agent as a tool result.
        var mainHistoryText = string.Join("\n", capture.Requests[2].Messages.Select(m => m.Text));
        Assert.Contains("sub-answer", mainHistoryText);
    }

    // ---------------------------------------------------------------- 台账继承（B4）

    [Fact]
    public async Task Delegation_SubAgentWrite_LandsInParentLedgerAndUndo()
    {
        // 子代理写文件过去只进它自己那次「回合」的台账，而子会话无人可撤销——
        // agent.undo 只能回滚主回合自己的改动，整条子任务链留下的文件成为孤儿。
        var workspace = ProjectWorkspace("ledger-one");
        var target = Path.Combine(workspace.Root, "sub-note.md");
        var store = LedgerStore();
        var capture = new ScriptedTurnsFactory();
        capture.Completions.AddRange(
        [
            ToolStep("delegate_task", new JsonObject { ["task"] = "把部署说明写入 sub-note.md" }),
            ToolStep("write_file", new JsonObject { ["path"] = target, ["content"] = "部署说明" }),
            new LlmCompletion { Text = "已写入 sub-note.md", FinishReason = "stop" },
            new LlmCompletion { Text = "子任务完成，sub-note.md 已生成", FinishReason = "stop" },
        ]);

        await using var runtime = BuildLedgerRuntime(workspace, store, capture);
        var session = runtime.Sessions.Create(workspace);
        using var subscription = runtime.Events.Subscribe();

        var result = await runtime.Agent.RunTurnAsync(session, workspace, "委派子任务写部署说明", CancellationToken.None);
        Assert.True(result.Error is null, result.Error ?? "ok");
        Assert.True(File.Exists(target));

        var ledger = runtime.UndoRegistry.Peek(session.Header.Id);
        Assert.NotNull(ledger);
        var change = Assert.Single(ledger!.Changes, c => c.AbsolutePath == target);
        Assert.Null(change.PreviousContent); // 新建文件：撤销即删除

        // 同一批改动只能有一个撤销入口：子会话既不落账，也不在终态事件里报可撤销文件。
        var terminals = Terminals(subscription);
        Assert.Equal(2, terminals.Count);
        Assert.Single(terminals, t => t.UndoableFiles is { Count: > 0 });
        Assert.Null(Assert.Single(terminals, t => t.SessionId != session.Header.Id).UndoableFiles);

        var (restored, failed) = await ledger.ApplyAsync();
        Assert.Empty(failed);
        Assert.Single(restored);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Delegation_NestedSubAgents_GrandchildWrite_ReachesRootLedger()
    {
        // 第二层以下同样要转发台账：ChildDelegator 漏掉 parentScope 的话，孙子代理写的文件
        // 又会回到「无人可撤销」的状态，而且父回合的崩溃日志里也看不到这一步。
        var workspace = ProjectWorkspace("ledger-two");
        var target = Path.Combine(workspace.Root, "nested.md");
        var store = LedgerStore();
        store.Config.Agent.DelegationMaxDepth = 2;
        var capture = new ScriptedTurnsFactory();
        capture.Completions.AddRange(
        [
            ToolStep("delegate_task", new JsonObject { ["task"] = "再委派一层，把结论写入 nested.md" }),
            ToolStep("delegate_task", new JsonObject { ["task"] = "把结论写入 nested.md" }),
            ToolStep("write_file", new JsonObject { ["path"] = target, ["content"] = "结论" }),
            new LlmCompletion { Text = "已写入 nested.md", FinishReason = "stop" },
            new LlmCompletion { Text = "子任务完成：nested.md 已写好", FinishReason = "stop" },
            new LlmCompletion { Text = "全部完成，见 nested.md", FinishReason = "stop" },
        ]);

        await using var runtime = BuildLedgerRuntime(workspace, store, capture);
        var session = runtime.Sessions.Create(workspace);

        var result = await runtime.Agent.RunTurnAsync(session, workspace, "两层委派写文件", CancellationToken.None);
        Assert.True(result.Error is null, result.Error ?? "ok");

        var ledger = runtime.UndoRegistry.Peek(session.Header.Id);
        Assert.NotNull(ledger);
        Assert.Contains(target, ledger!.Changes.Select(c => c.AbsolutePath));

        // 三层模型请求：主 → 子（仍可委派）→ 孙（深度耗尽，看不到委派工具）。
        Assert.Equal(6, capture.Requests.Count);
        Assert.Contains("delegate_task", capture.Requests[1].Tools.Select(tool => tool.Name));
        Assert.DoesNotContain("delegate_task", capture.Requests[2].Tools.Select(tool => tool.Name));
        Assert.Contains("把结论写入 nested.md", capture.Requests[2].Messages.Single(m => m.Role == ChatRole.User).Text);

        await ledger.ApplyAsync();
        Assert.False(File.Exists(target));
    }

    // ---------------------------------------------------------------- parallel delegation

    [Fact]
    public void OverlayToolRegistry_LayersDepthToolsOverSharedRegistry()
    {
        var inner = new ToolRegistry();
        using var keep = inner.Register(new StubTool("read_file"));
        using var sharedDelegate = inner.Register(new StubTool("delegate_task"));
        var overlay = new OverlayToolRegistry(
            inner,
            [new StubTool("delegate_task"), new StubTool("delegate_tasks")],
            "delegate_task", "delegate_tasks");

        // Overlay wins for delegation tools; other tools pass through.
        Assert.NotNull(overlay.Resolve("delegate_task"));
        Assert.NotNull(overlay.Resolve("delegate_tasks"));
        Assert.NotNull(overlay.Resolve("read_file"));
        // Registrations forward to the inner registry (MCP re-connect visibility).
        using var added = overlay.Register(new StubTool("grep"));
        Assert.NotNull(inner.Resolve("grep"));
        Assert.Equal(["delegate_task", "delegate_tasks", "grep", "read_file"],
            overlay.All.Select(tool => tool.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public async Task DelegateTasksTool_RequiresAtLeastTwoTasks()
    {
        var tool = new DelegateTasksTool(new FilePromptProvider(), new StubDelegator());
        var one = await tool.ExecuteAsync(
            new JsonObject { ["tasks"] = new JsonArray("只有一个任务") },
            Context(), CancellationToken.None);
        Assert.False(one.Success);
        Assert.Contains("至少需要 2 个", one.Output);

        var empty = await tool.ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.False(empty.Success);
        Assert.Contains("tasks", empty.Output);
    }

    [Fact]
    public async Task DelegateTasksTool_AggregatesPerTaskReports_WithoutFailingTheBatch()
    {
        var delegator = new BatchDelegator
        {
            Outcomes =
            {
                ["调研模块 A"] = new AgentTurnResult("A 的结论", false, null),
                ["调研模块 B"] = new AgentTurnResult("", false, "provider down"),
            },
        };
        var tool = new DelegateTasksTool(new FilePromptProvider(), delegator);
        var result = await tool.ExecuteAsync(
            new JsonObject { ["tasks"] = new JsonArray("调研模块 A", "调研模块 B") },
            Context(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("## 子任务 1 — 完成", result.Output);
        Assert.Contains("A 的结论", result.Output);
        Assert.Contains("## 子任务 2 — 失败", result.Output);
        Assert.Contains("provider down", result.Output);
        Assert.Contains("2 个并行子任务：1 完成，1 失败/取消", result.Summary);
        Assert.Equal(["调研模块 A", "调研模块 B"], delegator.ReceivedTasks);
    }

    [Fact]
    public async Task DelegateTasksTool_RejectsBatchBeyondSixTasks()
    {
        // 2026-10-12 行为变更：>6 个子任务不再静默截断（模型会误以为全部已派出），
        // 改为显式报错让模型拆分批次。
        var delegator = new BatchDelegator();
        var tool = new DelegateTasksTool(new FilePromptProvider(), delegator);
        var tasks = new JsonArray();
        for (var i = 1; i <= 8; i++) tasks.Add((JsonNode)$"任务 {i}");
        var result = await tool.ExecuteAsync(new JsonObject { ["tasks"] = tasks }, Context(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("最多 6 个", result.Output);
        Assert.Empty(delegator.ReceivedTasks);
    }

    private static ToolContext Context() => new()
    {
        Workspace = new WorkspaceManager().CreateGlobal(Path.Combine(
            Path.GetTempPath(), "haoyue-delegate-tests", "ctx-" + Guid.NewGuid().ToString("N"))),
        Events = new EventBus(),
        Agent = new AgentConfig(),
    };

    private sealed class BatchDelegator : IAgentDelegator
    {
        public Dictionary<string, AgentTurnResult> Outcomes { get; } = new();
        public List<string> ReceivedTasks { get; } = [];

        public Task<AgentTurnResult> RunSubTaskAsync(WorkspaceInfo workspace, string task, CancellationToken ct)
        {
            // 3.2 起 DelegateTasksTool 逐任务调用（每任务独立超时），单任务路径同样要记录。
            ReceivedTasks.Add(task);
            return Task.FromResult(Outcomes.GetValueOrDefault(task, new AgentTurnResult("", false, null)));
        }

        public Task<IReadOnlyList<AgentTurnResult>> RunSubTasksAsync(
            WorkspaceInfo workspace, IReadOnlyList<string> tasks, CancellationToken ct)
        {
            ReceivedTasks.AddRange(tasks);
            IReadOnlyList<AgentTurnResult> results = tasks
                .Select(t => Outcomes.GetValueOrDefault(t, new AgentTurnResult("", false, null)))
                .ToList();
            return Task.FromResult(results);
        }
    }

    private WorkspaceInfo GlobalWorkspace() => new WorkspaceManager().CreateGlobal(_dir);

    /// <summary>
    /// 项目工作区：write_file 的写边界只有在非全局工作区才是工作区根（全局会话落在进程
    /// cwd），台账测试需要一个可预测的落盘目录。
    /// </summary>
    private WorkspaceInfo ProjectWorkspace(string tag)
    {
        var root = Path.Combine(_dir, tag);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), "{}");
        return new WorkspaceManager().Detect(root);
    }

    private ConfigStore LedgerStore()
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
        return store;
    }

    private HaoyueRuntime BuildLedgerRuntime(
        WorkspaceInfo workspace, ConfigStore store, ILlmClientFactory capture) =>
        HaoyueRuntime.CreateIsolated(workspace, configureServices: services =>
        {
            services.AddSingleton<IConfigStore>(store);
            services.AddSingleton(new HaoyueDatabase(Path.Combine(_dir, "state.db")));
            services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
            services.AddSingleton(capture);
            services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
        });

    private static LlmCompletion ToolStep(string tool, JsonObject arguments) => new()
    {
        ToolCalls = [new ToolCallRequest("c1", tool, arguments.ToJsonString())],
    };

    /// <summary>回合已 await 完成，事件总线是无界队列，直接排空即可拿到全部终态事件。</summary>
    private static List<TurnCompletedEvent> Terminals(IEventSubscription subscription)
    {
        var terminals = new List<TurnCompletedEvent>();
        while (subscription.Reader.TryRead(out var evt))
            if (evt is TurnCompletedEvent completed) terminals.Add(completed);
        return terminals;
    }

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

    /// <summary>
    /// 按脚本顺序逐步应答。超出脚本长度直接抛错而不是重复最后一步——静默兜底会把
    /// 「多出一整个模型回合」（比如台账补写、收尾提示）这类回归藏起来。
    /// </summary>
    private sealed class ScriptedTurnsFactory : ILlmClientFactory
    {
        public List<LlmRequest> Requests { get; } = [];
        public List<LlmCompletion> Completions { get; } = [];
        private int _call;

        public ILlmClient GetClient(string kind) => new ScriptedClient(this);

        private sealed class ScriptedClient(ScriptedTurnsFactory owner) : ILlmClient
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
                var index = Interlocked.Increment(ref owner._call) - 1;
                if (index >= owner.Completions.Count)
                    throw new InvalidOperationException(
                        $"脚本只有 {owner.Completions.Count} 步，第 {index + 1} 次模型请求没有预置应答");
                yield return new LlmCompleted(owner.Completions[index]);
            }
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
