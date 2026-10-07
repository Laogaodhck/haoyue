using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Mcp;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Workspaces;
using Xunit;

namespace Haoyue.Tests;

public class McpManagerTests : IDisposable
{
    private static readonly JsonObject MethodNotFound = new()
    {
        ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "method not found" },
    };

    private static readonly JsonObject InitializeResult = new()
    {
        ["protocolVersion"] = "2024-11-05",
        ["capabilities"] = new JsonObject(),
    };

    private static PromptRenderContext RenderContext() => new() { Variables = new Dictionary<string, string>() };

    private (McpManager Manager, PromptRegistry Prompts) CreateManager(FakeMcpTransport transport)
    {
        var configStore = new ConfigStore(
            Path.Combine(_dir, $"{Guid.NewGuid():N}-config.json"),
            Path.Combine(_dir, $"{Guid.NewGuid():N}-state.json"));
        configStore.Config.Mcp.Servers["demo"] = new McpServerConfig
        {
            Transport = "stdio",
            Command = "fake-command",
            Enabled = true,
        };

        var promptRegistry = new PromptRegistry();
        var manager = new McpManager(configStore, new ToolRegistry(), promptRegistry,
            transportFactory: (_, _) => transport);
        return (manager, promptRegistry);
    }

    [Fact]
    public async Task ConnectAllAsync_RegistersPromptAndResourceContributionsWithWorkingResolvers()
    {
        var workspace = new WorkspaceInfo { Root = Path.Combine(_dir, "contrib-ws"), ProjectKinds = [] };
        Directory.CreateDirectory(workspace.Root);

        var transport = new FakeMcpTransport
        {
            Responder = message =>
            {
                var method = message["method"]?.GetValue<string>();
                var uri = (message["params"] as JsonObject)?["uri"]?.GetValue<string>();
                return method switch
                {
                    "initialize" => InitializeResult,
                    "tools/list" => new JsonObject { ["tools"] = new JsonArray() },
                    "prompts/list" => new JsonObject
                    {
                        ["prompts"] = new JsonArray(new JsonObject { ["name"] = "greet" }),
                    },
                    "prompts/get" => new JsonObject
                    {
                        ["messages"] = new JsonArray(new JsonObject
                        {
                            ["content"] = new JsonObject { ["type"] = "text", ["text"] = "hello from mcp" },
                        }),
                    },
                    "resources/list" => new JsonObject
                    {
                        ["resources"] = new JsonArray(
                            new JsonObject { ["uri"] = "demo://docs/a", ["name"] = "Alpha" },
                            new JsonObject { ["uri"] = "demo://docs/b", ["name"] = "Beta" }),
                    },
                    "resources/read" when uri == "demo://docs/a" => new JsonObject
                    {
                        ["contents"] = new JsonArray(new JsonObject
                        {
                            ["uri"] = "demo://docs/a", ["text"] = "resource alpha body",
                        }),
                    },
                    _ => MethodNotFound,
                };
            }
        };

        var (manager, prompts) = CreateManager(transport);
        await using var _ = manager;

        var statuses = await manager.ConnectAllAsync(workspace, CancellationToken.None);

        var status = statuses.Single(s => s.Name == "demo");
        Assert.True(status.Connected, $"error: {status.Error}");
        Assert.Equal(3, prompts.All.Count);
        Assert.Contains(prompts.All, c => c.Id == "mcp:demo:greet");
        Assert.Contains(prompts.All, c => c.Id == "mcp:demo:resource:demo://docs/a");
        Assert.Contains(prompts.All, c => c.Id == "mcp:demo:resource:demo://docs/b");

        var prompt = prompts.All.Single(c => c.Id == "mcp:demo:greet");
        // Third-party text is wrapped in a trust boundary (data, not instructions) with
        // the original payload intact.
        var promptText = await prompt.Resolver(RenderContext(), CancellationToken.None);
        Assert.NotNull(promptText);
        Assert.Contains("hello from mcp", promptText);
        Assert.Contains("EXTERNAL CONTENT BEGIN", promptText);
        Assert.Contains("MCP server 'demo' prompt 'greet'", promptText);

        // Alpha resolves through resources/read; Beta falls back to the
        // method-not-found branch and must degrade to null.
        var alpha = prompts.All.Single(c => c.Id == "mcp:demo:resource:demo://docs/a");
        var alphaText = await alpha.Resolver(RenderContext(), CancellationToken.None);
        Assert.NotNull(alphaText);
        Assert.Contains("resource alpha body", alphaText);
        Assert.Contains("EXTERNAL CONTENT BEGIN", alphaText);

        var beta = prompts.All.Single(c => c.Id == "mcp:demo:resource:demo://docs/b");
        Assert.Null(await beta.Resolver(RenderContext(), CancellationToken.None));
    }

    [Fact]
    public async Task ConnectAllAsync_FitsOversizedResourceContentToTokenBudget()
    {
        var workspace = new WorkspaceInfo { Root = Path.Combine(_dir, "huge-ws"), ProjectKinds = [] };
        Directory.CreateDirectory(workspace.Root);

        var oversized = new string('x', 60_000); // ≫ 10_000-token fragment budget
        var transport = new FakeMcpTransport
        {
            Responder = message => message["method"]?.GetValue<string>() switch
            {
                "initialize" => InitializeResult,
                "tools/list" => new JsonObject { ["tools"] = new JsonArray() },
                "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
                "resources/list" => new JsonObject
                {
                    ["resources"] = new JsonArray(new JsonObject { ["uri"] = "demo://huge", ["name"] = "Huge" }),
                },
                "resources/read" => new JsonObject
                {
                    ["contents"] = new JsonArray(new JsonObject { ["uri"] = "demo://huge", ["text"] = oversized }),
                },
                _ => MethodNotFound,
            }
        };

        var (manager, prompts) = CreateManager(transport);
        await using var _ = manager;

        await manager.ConnectAllAsync(workspace, CancellationToken.None);

        var huge = prompts.All.Single(c => c.Id == "mcp:demo:resource:demo://huge");
        var text = await huge.Resolver(RenderContext(), CancellationToken.None);

        Assert.NotNull(text);
        Assert.Contains("middle section trimmed", text);
        Assert.True(text!.Length < oversized.Length);
    }

    [Fact]
    public async Task ConnectAllAsync_CapsResourceContributionsPerServer()
    {
        var workspace = new WorkspaceInfo { Root = Path.Combine(_dir, "cap-ws"), ProjectKinds = [] };
        Directory.CreateDirectory(workspace.Root);

        var resources = new JsonArray();
        for (var i = 0; i < 20; i++)
            resources.Add(new JsonObject { ["uri"] = $"demo://file-{i:00}", ["name"] = $"File {i}" });

        var transport = new FakeMcpTransport
        {
            Responder = message => message["method"]?.GetValue<string>() switch
            {
                "initialize" => InitializeResult,
                "tools/list" => new JsonObject { ["tools"] = new JsonArray() },
                "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
                "resources/list" => new JsonObject { ["resources"] = resources },
                _ => MethodNotFound,
            }
        };

        var (manager, prompts) = CreateManager(transport);
        await using var _ = manager;

        var statuses = await manager.ConnectAllAsync(workspace, CancellationToken.None);
        var capStatus = statuses.Single(s => s.Name == "demo");
        Assert.True(capStatus.Connected, $"error: {capStatus.Error}");

        Assert.Equal(16, prompts.All.Count); // MaxResourcesPerServer
        Assert.All(prompts.All, c => Assert.StartsWith("mcp:demo:resource:", c.Id));
        Assert.DoesNotContain(prompts.All, c => c.Id.Contains("file-16"));
    }
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-mcp-tests", Guid.NewGuid().ToString("N"));

    public McpManagerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task ConnectAllAsync_ReportsFailuresWithoutAbortingTheReload()
    {
        var workspace = new WorkspaceInfo { Root = Path.Combine(_dir, "ws"), ProjectKinds = [] };
        Directory.CreateDirectory(workspace.Root);

        var configStore = new ConfigStore(
            Path.Combine(_dir, "mcp-config.json"),
            Path.Combine(_dir, "mcp-state.json"));

        // A stdio server whose executable does not exist used to escape the exception
        // filter and abort the whole reload, hiding every other server.
        configStore.Config.Mcp.Servers["broken-stdio"] = new McpServerConfig
        {
            Transport = "stdio",
            Command = "haoyue-command-that-does-not-exist",
            Enabled = true,
        };
        configStore.Config.Mcp.Servers["broken-remote"] = new McpServerConfig
        {
            Transport = "sse",
            Url = "http://127.0.0.1:9/mcp",
            Enabled = true,
        };
        configStore.Config.Mcp.Servers["broken-streamable"] = new McpServerConfig
        {
            Transport = "http",
            Url = "http://127.0.0.1:9/mcp",
            Enabled = true,
        };
        configStore.Config.Mcp.Servers["off"] = new McpServerConfig
        {
            Transport = "stdio",
            Command = "haoyue-command-that-does-not-exist",
            Enabled = false,
        };

        await using var manager = new McpManager(configStore, new ToolRegistry(), new PromptRegistry());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var statuses = await manager.ConnectAllAsync(workspace, cts.Token);

        Assert.Equal(4, statuses.Count);
        Assert.All(statuses, status => Assert.False(status.Connected));
        Assert.Equal("disabled", statuses.Single(status => status.Name == "off").Error);
        Assert.False(string.IsNullOrWhiteSpace(statuses.Single(status => status.Name == "broken-stdio").Error));
        Assert.False(string.IsNullOrWhiteSpace(statuses.Single(status => status.Name == "broken-remote").Error));
        Assert.False(string.IsNullOrWhiteSpace(statuses.Single(status => status.Name == "broken-streamable").Error));
    }

    [Fact]
    public void MarkConnecting_ShowsEnabledServersAsPending()
    {
        var workspace = new WorkspaceInfo { Root = Path.Combine(_dir, "pending-ws"), ProjectKinds = [] };
        Directory.CreateDirectory(workspace.Root);

        var configStore = new ConfigStore(
            Path.Combine(_dir, "pending-config.json"),
            Path.Combine(_dir, "pending-state.json"));
        configStore.Config.Mcp.Servers["on"] = new McpServerConfig
        {
            Transport = "sse",
            Url = "http://127.0.0.1:9/mcp",
            Enabled = true,
        };
        configStore.Config.Mcp.Servers["off"] = new McpServerConfig
        {
            Transport = "stdio",
            Command = "npx",
            Enabled = false,
        };

        var manager = new McpManager(configStore, new ToolRegistry(), new PromptRegistry());

        manager.MarkConnecting(workspace);

        var statuses = manager.Status;
        Assert.Equal(2, statuses.Count);
        var pending = statuses.Single(status => status.Name == "on");
        Assert.True(pending.Connecting);
        Assert.False(pending.Connected);
        Assert.Null(pending.Error);
        var disabled = statuses.Single(status => status.Name == "off");
        Assert.False(disabled.Connecting);
        Assert.Equal("disabled", disabled.Error);
    }

    [Fact]
    public async Task ConnectAllAsync_CapsPromptsPerServer_AndReportsWarnings()
    {
        var workspace = new WorkspaceInfo { Root = Path.Combine(_dir, "cap-ws"), ProjectKinds = [] };
        Directory.CreateDirectory(workspace.Root);

        var transport = new FakeMcpTransport { Responder = PromptListResponder(30) };
        var (manager, prompts) = CreateManager(transport);
        await using var _ = manager;

        var statuses = await manager.ConnectAllAsync(workspace, CancellationToken.None);

        // B3 per-server cap: only the first 24 prompts become contributions, and the
        // truncated remainder is reported on the status instead of vanishing silently.
        Assert.Equal(24, prompts.All.Count(c => c.Id.StartsWith("mcp:demo:", StringComparison.Ordinal)));
        var status = statuses.Single(s => s.Name == "demo");
        Assert.True(status.Connected, $"error: {status.Error}");
        Assert.NotNull(status.Warnings);
        Assert.Equal(6, status.Warnings.Count);
        Assert.Contains(status.Warnings, w => w.Contains("prompt 'p25'"));
    }

    [Fact]
    public async Task ConnectAllAsync_EnforcesGlobalPromptCapAcrossServers()
    {
        var workspace = new WorkspaceInfo { Root = Path.Combine(_dir, "global-cap-ws"), ProjectKinds = [] };
        Directory.CreateDirectory(workspace.Root);

        var configStore = new ConfigStore(
            Path.Combine(_dir, $"{Guid.NewGuid():N}-config.json"),
            Path.Combine(_dir, $"{Guid.NewGuid():N}-state.json"));
        foreach (var name in (string[])["alpha", "beta"])
            configStore.Config.Mcp.Servers[name] = new McpServerConfig
            {
                Transport = "stdio",
                Command = "fake-command",
                Enabled = true,
            };

        var promptRegistry = new PromptRegistry();
        var manager = new McpManager(configStore, new ToolRegistry(), promptRegistry,
            transportFactory: (_, _) => new FakeMcpTransport { Responder = PromptListResponder(30) });
        await using var _ = manager;

        var statuses = await manager.ConnectAllAsync(workspace, CancellationToken.None);

        // B3 global cap: 24 from the first server, only 24 more from the second
        // (total 48) — the rest of beta's list is truncated with warnings.
        Assert.Equal(48, promptRegistry.All.Count(c => c.Id.StartsWith("mcp:", StringComparison.Ordinal)));
        var beta = statuses.Single(s => s.Name == "beta");
        Assert.NotNull(beta.Warnings);
        Assert.True(beta.Warnings.Count > 0);
    }

    private static Func<JsonObject, JsonObject> PromptListResponder(int promptCount) => message =>
    {
        var method = message["method"]?.GetValue<string>();
        return method switch
        {
            "initialize" => InitializeResult,
            "tools/list" => new JsonObject { ["tools"] = new JsonArray() },
            "prompts/list" => new JsonObject
            {
                ["prompts"] = new JsonArray(Enumerable.Range(1, promptCount)
                    .Select(i => (JsonNode)new JsonObject { ["name"] = $"p{i}" })
                    .ToArray()),
            },
            _ => MethodNotFound,
        };
    };
}
