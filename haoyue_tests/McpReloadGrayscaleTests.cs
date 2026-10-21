using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Mcp;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Workspaces;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// 6.2 mcp.reload 两段式灰度：一个坏服务器不再清空全部 MCP 工具。
/// 探测阶段（连接+发现）与切换阶段（注册+换槽）分离——失败服务器保留旧连接与
/// 旧工具集，只有从配置中消失/禁用的服务器才回收。
/// </summary>
public sealed class McpReloadGrayscaleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-mcp-gray-" + Guid.NewGuid().ToString("N"));
    /// <summary>
    /// 每个服务器名的响应器。每次连接都新建 FakeMcpTransport（真实场景同样每次重连
    /// 新 transport）：复用同一 transport 会让新旧两个 dispatch loop 竞争同一个
    /// incoming channel——新请求的响应被旧循环消费，initialize 等满 30s 超时。
    /// </summary>
    private readonly Dictionary<string, Func<JsonObject, JsonObject>> _responders = new(StringComparer.Ordinal);

    public McpReloadGrayscaleTests()
    {
        Directory.CreateDirectory(_dir);
        _responders["alpha"] = ServerResponder("tool-a1", "tool-a2");
        _responders["beta"] = ServerResponder("tool-b1");
        _responders["gamma"] = ServerResponder("tool-c1");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static Func<JsonObject, JsonObject> ServerResponder(params string[] tools) => message =>
    {
        var method = message["method"]?.GetValue<string>();
        return method switch
        {
            "initialize" => new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject
                {
                    ["tools"] = new JsonObject(),
                    ["prompts"] = new JsonObject(),
                    ["resources"] = new JsonObject(),
                },
            },
            "tools/list" => new JsonObject
            {
                ["tools"] = new JsonArray(tools.Select(name => (JsonNode)new JsonObject
                {
                    ["name"] = name,
                    ["description"] = $"{name} description",
                    ["inputSchema"] = new JsonObject { ["type"] = "object" },
                }).ToArray()),
            },
            "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
            "resources/list" => new JsonObject { ["resources"] = new JsonArray() },
            _ => new JsonObject { ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "method not found" } },
        };
    };

    private static JsonObject FailingResponder(JsonObject message) =>
        new() { ["error"] = new JsonObject { ["code"] = -32000, ["message"] = "server unreachable" } };

    private (McpManager Manager, ToolRegistry Tools, ConfigStore Config) CreateManager()
    {
        var config = new ConfigStore(
            Path.Combine(_dir, $"{Guid.NewGuid():N}-config.json"),
            Path.Combine(_dir, $"{Guid.NewGuid():N}-state.json"));
        var tools = new ToolRegistry();
        var manager = new McpManager(config, tools, new PromptRegistry(),
            transportFactory: (name, _, _) => new FakeMcpTransport
            {
                Responder = message => _responders.TryGetValue(name, out var responder)
                    ? responder(message)
                    : FailingResponder(message),
            });
        return (manager, tools, config);
    }

    private static McpServerConfig Server() => new() { Transport = "stdio", Command = "fake", Enabled = true };

    private WorkspaceInfo Workspace()
    {
        var root = Path.Combine(_dir, "ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new WorkspaceInfo { Root = root, ProjectKinds = [] };
    }

    private static void AddServer(ConfigStore config, string name) => config.Config.Mcp.Servers[name] = Server();

    [Fact]
    public async Task Reload_OneServerFails_KeepsItsPreviousToolsAndTheOtherServerTools()
    {
        var (manager, tools, config) = CreateManager();
        AddServer(config, "alpha");
        AddServer(config, "beta");
        var workspace = Workspace();
        await using var _ = manager;

        var first = await manager.ConnectAllAsync(workspace, CancellationToken.None);
        Assert.All(first, status => Assert.True(status.Connected, status.Error));
        Assert.NotNull(tools.Resolve(McpToolAdapter.BuildName("alpha", "tool-a1")));
        Assert.NotNull(tools.Resolve(McpToolAdapter.BuildName("beta", "tool-b1")));

        // beta 变成必败（配置还在、服务不可达）：两段式灰度必须保留 beta 的旧连接与旧工具集。
        _responders["beta"] = FailingResponder;
        var second = await manager.ConnectAllAsync(workspace, CancellationToken.None);

        var beta = second.Single(status => status.Name == "beta");
        Assert.False(beta.Connected);
        Assert.Contains("保留上一次成功连接的工具集", beta.Error!, StringComparison.Ordinal);
        // 核心断言：beta 失败不清空任何工具 —— alpha 与 beta 的工具集 100% 保留。
        Assert.NotNull(tools.Resolve(McpToolAdapter.BuildName("alpha", "tool-a1")));
        Assert.NotNull(tools.Resolve(McpToolAdapter.BuildName("alpha", "tool-a2")));
        Assert.NotNull(tools.Resolve(McpToolAdapter.BuildName("beta", "tool-b1")));
        Assert.Equal(3, tools.All.Count(tool => tool.Name.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Reload_NewlyAddedServerFails_ExistingServersKeepWorking()
    {
        var (manager, tools, config) = CreateManager();
        AddServer(config, "alpha");
        var workspace = Workspace();
        await using var _ = manager;
        await manager.ConnectAllAsync(workspace, CancellationToken.None);

        // 新加一个必然连不上的服务器：不能影响既有 alpha。
        _responders["gamma"] = FailingResponder;
        AddServer(config, "gamma");
        var second = await manager.ConnectAllAsync(workspace, CancellationToken.None);

        var gamma = second.Single(status => status.Name == "gamma");
        Assert.False(gamma.Connected);
        Assert.NotNull(tools.Resolve(McpToolAdapter.BuildName("alpha", "tool-a1")));
    }

    [Fact]
    public async Task Reload_ServerRemovedFromConfig_UnregistersItsTools()
    {
        var (manager, tools, config) = CreateManager();
        AddServer(config, "alpha");
        AddServer(config, "beta");
        var workspace = Workspace();
        await using var _ = manager;
        await manager.ConnectAllAsync(workspace, CancellationToken.None);
        Assert.NotNull(tools.Resolve(McpToolAdapter.BuildName("beta", "tool-b1")));

        // 从配置移除 beta：这是「应回收」——工具集随之反注册。
        config.Config.Mcp.Servers.Remove("beta");
        var third = await manager.ConnectAllAsync(workspace, CancellationToken.None);

        Assert.DoesNotContain(third, status => status.Name == "beta");
        Assert.Null(tools.Resolve(McpToolAdapter.BuildName("beta", "tool-b1")));
        Assert.NotNull(tools.Resolve(McpToolAdapter.BuildName("alpha", "tool-a1")));
    }

    [Fact]
    public async Task Reload_SameServerTwice_DoesNotDuplicateTools()
    {
        var (manager, tools, config) = CreateManager();
        AddServer(config, "alpha");
        var workspace = Workspace();
        await using var _ = manager;

        await manager.ConnectAllAsync(workspace, CancellationToken.None);
        await manager.ConnectAllAsync(workspace, CancellationToken.None);

        // 换槽时旧注册先释放：同名工具不产生双注册。
        var matches = tools.All.Count(tool =>
            tool.Name.Equals(McpToolAdapter.BuildName("alpha", "tool-a1"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, matches);
    }
}
