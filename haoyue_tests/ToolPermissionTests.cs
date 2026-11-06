using System.Text.Json.Nodes;
using Haoyue.Runtime;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Daemon;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Workspaces;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// MCP/内置工具的细粒度权限：workspace 白名单与硬禁用、角色/成员交集、
/// 通配符匹配、fail-closed 降级与拒绝台账。
/// </summary>
public sealed class ToolPermissionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-tool-perm", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---------------------------------------------------------------- 纯策略

    [Fact]
    public void Matches_SupportsExactAndMcpWildcards()
    {
        Assert.True(ToolPermissionPolicy.Matches("read_file", "read_file"));
        Assert.True(ToolPermissionPolicy.Matches("READ_FILE", "read_file"));
        Assert.False(ToolPermissionPolicy.Matches("read_file", "write_file"));

        Assert.True(ToolPermissionPolicy.Matches("*", "mcp__github__create_issue"));
        Assert.True(ToolPermissionPolicy.Matches("github/*", "mcp__github__create_issue"));
        Assert.False(ToolPermissionPolicy.Matches("github/*", "mcp__gitlab__create_issue"));
        Assert.True(ToolPermissionPolicy.Matches("*/create_issue", "mcp__github__create_issue"));
        Assert.True(ToolPermissionPolicy.Matches("mcp__my-server__*", "mcp__my-server__read_file"));
        Assert.False(ToolPermissionPolicy.Matches("", "read_file"));
    }

    [Fact]
    public void Matches_GithubCliConnector_AndDenyOverridesAllow()
    {
        Assert.True(ToolPermissionPolicy.Matches("github-cli/*", "mcp__github-cli__gh_issue_create"));
        Assert.True(ToolPermissionPolicy.Matches("github-cli/gh_issue_create", "mcp__github-cli__gh_issue_create"));
        Assert.True(ToolPermissionPolicy.Matches("mcp__github-cli__*", "mcp__github-cli__gh_repo_view"));
        Assert.False(ToolPermissionPolicy.Matches("github-cli/*", "mcp__github__create_issue"));

        var config = new WorkspaceConfig
        {
            AllowedTools = ["github-cli/*"],
            DisabledTools = ["github-cli/gh_issue_create"],
        };
        Assert.False(ToolPermissionPolicy.TryResolveWorkspace(
            config, "mcp__github-cli__gh_issue_create", out var reason));
        Assert.Contains("disabledTools", reason);
        Assert.True(ToolPermissionPolicy.TryResolveWorkspace(
            config, "mcp__github-cli__gh_repo_view", out _));
    }

    [Fact]
    public void Matches_ServerSlashTool_MapsToLocalMcpName()
    {
        Assert.True(ToolPermissionPolicy.Matches("github/create_issue", "mcp__github__create_issue"));
        Assert.True(ToolPermissionPolicy.Matches("GitHub/Create_Issue", "mcp__github__create_issue"));
        Assert.False(ToolPermissionPolicy.Matches("github/create_issue", "mcp__github__list_issues"));
        // 只拆第一个斜杠，工具名里允许再出现斜杠。
        Assert.True(ToolPermissionPolicy.Matches("fs/read/file", "mcp__fs__read/file"));
    }

    [Fact]
    public void AllowAndDeny_EmptyListsMeanUnrestricted()
    {
        Assert.True(ToolPermissionPolicy.IsAllowed(null, "bash"));
        Assert.True(ToolPermissionPolicy.IsAllowed([], "bash"));
        Assert.False(ToolPermissionPolicy.IsDenied(null, "bash"));
        Assert.False(ToolPermissionPolicy.IsDenied([], "bash"));

        Assert.True(ToolPermissionPolicy.IsDenied(["bash"], "bash"));
        Assert.False(ToolPermissionPolicy.IsAllowed(["read_file"], "bash"));
    }

    [Fact]
    public void TryResolveWorkspace_DenyWinsOverAllow_AndReportsReasons()
    {
        var allowedThenDenied = new WorkspaceConfig
        {
            AllowedTools = ["bash"],
            DisabledTools = ["bash"],
        };
        Assert.False(ToolPermissionPolicy.TryResolveWorkspace(allowedThenDenied, "bash", out var denyReason));
        Assert.Contains("disabledTools", denyReason);

        var allowOnly = new WorkspaceConfig { AllowedTools = ["read_file"] };
        Assert.False(ToolPermissionPolicy.TryResolveWorkspace(allowOnly, "bash", out var allowReason));
        Assert.Contains("allowedTools", allowReason);

        var permitted = new WorkspaceConfig { AllowedTools = ["read_file"] };
        Assert.True(ToolPermissionPolicy.TryResolveWorkspace(permitted, "read_file", out var none));
        Assert.Null(none);

        Assert.True(ToolPermissionPolicy.TryResolveWorkspace(null, "bash", out _));
    }

    [Fact]
    public void IsValidPattern_RejectsMalformedEntries()
    {
        Assert.True(ToolPermissionPolicy.IsValidPattern("read_file", out var none));
        Assert.Null(none);
        Assert.True(ToolPermissionPolicy.IsValidPattern("github/create_issue", out _));
        Assert.True(ToolPermissionPolicy.IsValidPattern("github/*", out _));
        Assert.True(ToolPermissionPolicy.IsValidPattern("*/create_issue", out _));
        Assert.True(ToolPermissionPolicy.IsValidPattern("*", out _));
        Assert.True(ToolPermissionPolicy.IsValidPattern("mcp__server__tool", out _));

        Assert.False(ToolPermissionPolicy.IsValidPattern("", out _));
        Assert.False(ToolPermissionPolicy.IsValidPattern("read file", out _));
        Assert.False(ToolPermissionPolicy.IsValidPattern(" github/issue", out _));
        Assert.False(ToolPermissionPolicy.IsValidPattern("github/**", out _));
        Assert.False(ToolPermissionPolicy.IsValidPattern("/issue", out _));
        Assert.False(ToolPermissionPolicy.IsValidPattern("github//issue", out _));
        Assert.False(ToolPermissionPolicy.IsValidPattern("github?/issue", out _));
        Assert.False(ToolPermissionPolicy.IsValidPattern(new string('a', ToolPermissionPolicy.MaxPatternLength + 1), out _));
    }

    // ---------------------------------------------------------------- 视图层

    [Fact]
    public void AllowListRegistry_SupportsWildcards_AndReportsDenialReason()
    {
        var inner = RegistryOf("read_file", "mcp__github__create_issue", "mcp__gitlab__create_issue");
        var view = new AllowListToolRegistry(inner, ["read_file", "github/*"], "成员");

        Assert.NotNull(view.Resolve("READ_FILE"));
        Assert.NotNull(view.Resolve("mcp__github__create_issue"));
        Assert.Null(view.Resolve("mcp__gitlab__create_issue"));
        Assert.Equal(2, view.All.Count);
        Assert.Contains("成员", view.DenialReason("mcp__gitlab__create_issue"));
        // 真正未知的名字不冒充「越权」，留给未知工具提示。
        Assert.Null(view.DenialReason("nonexistent_tool"));
    }

    [Fact]
    public void ToolPermissionRegistry_DenyOverridesAllow_AndDistinguishesReasons()
    {
        var inner = RegistryOf("read_file", "write_file");
        var view = new ToolPermissionRegistry(
            inner,
            allowedTools: ["read_file", "write_file"],
            disabledTools: ["write_file"],
            "工作区");

        Assert.NotNull(view.Resolve("read_file"));
        Assert.Null(view.Resolve("write_file"));
        Assert.Equal("read_file", Assert.Single(view.All).Name);
        Assert.Contains("硬禁用", view.DenialReason("write_file"));

        var allowListed = new ToolPermissionRegistry(
            inner, allowedTools: ["read_file"], disabledTools: null, "工作区");
        Assert.Contains("白名单", allowListed.DenialReason("write_file"));
    }

    // ---------------------------------------------------------------- 委派组装

    [Fact]
    public void ComposeToolView_AppliesWorkspaceRoleAndMemberLayers_AsIntersection()
    {
        var inner = RegistryOf("read_file", "write_file", "bash", "mcp__github__create_issue");
        var config = new WorkspaceConfig
        {
            AllowedTools = ["read_file", "write_file", "bash", "github/create_issue"],
            DisabledTools = ["write_file"],
        };

        var view = AgentDelegator.ComposeToolView(
            inner, config,
            roleTools: ["read_file", "write_file", "github/*"],
            invocationTools: ["read_file", "github/create_issue", "bash"]);

        // workspace ∩ 角色 ∩ 成员，且 write_file 被硬禁用。
        Assert.Equal(["mcp__github__create_issue", "read_file"],
            view.All.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Null(view.Resolve("bash"));
        Assert.Null(view.Resolve("write_file"));

        // 未配置 workspace 策略时保持既有行为（角色/成员照常生效）。
        var legacy = AgentDelegator.ComposeToolView(inner, null, roleTools: ["read_file"], invocationTools: null);
        Assert.Equal("read_file", Assert.Single(legacy.All).Name);
    }

    [Fact]
    public void ComposeToolView_FailsClosed_UnlessFallbackEnabled()
    {
        var inner = RegistryOf("read_file", "write_file");
        var strict = AgentDelegator.ComposeToolView(
            inner, new WorkspaceConfig { AllowedTools = ["read_file", "write_file"] },
            roleTools: ["bash"], invocationTools: null);
        Assert.Empty(strict.All);
        Assert.Contains("角色预置", strict.DenialReason("read_file"));

        var fallback = AgentDelegator.ComposeToolView(
            inner,
            new WorkspaceConfig
            {
                AllowedTools = ["read_file", "write_file"],
                AllowToolFallbackToGlobal = true,
            },
            roleTools: ["bash"], invocationTools: null);
        Assert.Equal(2, fallback.All.Count);

        // 降级也不能越过 workspace 硬禁用。
        var deniedInFallback = AgentDelegator.ComposeToolView(
            inner,
            new WorkspaceConfig { DisabledTools = ["write_file"], AllowToolFallbackToGlobal = true },
            roleTools: ["bash"], invocationTools: null);
        Assert.Equal("read_file", Assert.Single(deniedInFallback.All).Name);
    }

    // ---------------------------------------------------------------- 执行闸门

    [Fact]
    public async Task Agent_WorkspacePolicy_DeniesToolButKeepsTurnRunning_AndWritesLedger()
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
        store.Config.Provider = "openai";
        store.Config.Model = "gpt-test";
        store.Config.Routing.Fallback = ["openai/gpt-test"];
        store.Config.Agent.AutoVerify = false;

        var root = Path.Combine(_dir, "project");
        Directory.CreateDirectory(root);
        var workspace = new WorkspaceInfo
        {
            Root = root,
            ProjectKinds = [],
            Config = new WorkspaceConfig { AllowedTools = ["read_file"] },
            IsGlobal = false,
        };

        var capture = new ScriptedClientFactory(
        [
            new LlmCompletion { ToolCalls = [new ToolCallRequest("c1", "probe", "{}")] },
            new LlmCompletion { Text = "done" },
        ]);
        await using var runtime = HaoyueRuntime.CreateIsolated(workspace, configureServices: services =>
        {
            services.AddSingleton<IConfigStore>(store);
            services.AddSingleton(new HaoyueDatabase(Path.Combine(_dir, "state.db")));
            services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
            services.AddSingleton<ILlmClientFactory>(capture);
            services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
        });

        var probe = new StubProbeTool();
        using var _ = runtime.Tools.Register(probe);
        var session = runtime.Sessions.Create(workspace, networkEnabled: false);

        using var subscription = runtime.Events.Subscribe();
        var events = new List<RuntimeEvent>();
        var pump = Task.Run(async () =>
        {
            await foreach (var evt in subscription.Reader.ReadAllAsync().ConfigureAwait(false))
                lock (events) events.Add(evt);
        });
        AgentTurnResult result;
        try
        {
            result = await runtime.Agent.RunTurnAsync(session, workspace, "call probe", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            subscription.Dispose();
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
        }

        // 越权只降级为「该工具不可用」：回合正常结束、工具未执行、模型拿到明确的拒绝。
        Assert.Null(result.Error);
        Assert.Equal("done", result.Text);
        Assert.Empty(probe.Executions);
        lock (events)
        {
            Assert.Contains(events, evt => evt is ToolCallCompletedEvent completed
                && completed.ToolName == "probe"
                && !completed.Success
                && completed.ResultSummary!.Contains("denied"));
        }

        var ledger = HookLedger.Read(root);
        Assert.Contains(ledger, record =>
            record.Hook == "tool-permission" && record.Target == "probe" && !record.Ok);
    }

    // ---------------------------------------------------------------- 测试替身

    private static ToolRegistry RegistryOf(params string[] names)
    {
        var registry = new ToolRegistry();
        foreach (var name in names) registry.Register(new StubTool(name));
        return registry;
    }

    private sealed class StubTool(string name) : ITool
    {
        public string Name => name;
        public string Description => "stub";
        public JsonObject ParameterSchema => new() { ["type"] = "object" };
        public bool Mutating => false;
        public bool RequiresWorkspace => false;
        public string StatusLabel => "stub";
        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok("ok"));
    }

    private sealed class StubProbeTool : ITool
    {
        public List<string> Executions { get; } = [];
        public string Name => "probe";
        public string Description => "stub";
        public JsonObject ParameterSchema => new() { ["type"] = "object" };
        public bool Mutating => false;
        public bool RequiresWorkspace => false;
        public string StatusLabel => "probe";

        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
        {
            Executions.Add("ran");
            return Task.FromResult(ToolResult.Ok("ok"));
        }
    }

    private sealed class ScriptedClientFactory(List<LlmCompletion> steps) : ILlmClientFactory
    {
        private readonly List<LlmCompletion> _steps = steps;
        private int _index;

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
                await Task.Yield();
                var index = Math.Min(owner._steps.Count - 1, owner._index++);
                yield return new LlmCompleted(owner._steps[index]);
            }
        }
    }
}
