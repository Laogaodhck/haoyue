using System.Text.Json.Nodes;
using Haoyue.Runtime;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Extensions;
using Haoyue.Runtime.NetworkOps;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Workspaces;
using Xunit;

namespace Haoyue.Tests;

public sealed class NetworkOpsTests : IAsyncDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"haoyue_netops_test_{Guid.NewGuid():N}");

    public NetworkOpsTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask DisposeAsync()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        return ValueTask.CompletedTask;
    }

    // ---------- 配置与发现 ----------

    [Fact]
    public void DefaultConfig_NetworkOpsIsDisabled()
    {
        var config = new HaoyueConfig();
        Assert.NotNull(config.NetworkOps);
        Assert.False(config.NetworkOps.Enabled);
        Assert.True(config.NetworkOps.AllowMutating);
        Assert.Equal(60, config.NetworkOps.TimeoutSeconds);
    }

    [Fact]
    public void ExtensionManager_DiscoversNetworkOpsExtension()
    {
        var manager = ExtensionManager.CreateDefault();
        Assert.NotNull(manager.Get("network_ops"));
        Assert.NotNull(manager.Get("computer_use"));
    }

    [Fact]
    public void Runtime_DisabledByDefault_RegistersNoNetworkTools()
    {
        var store = NewStore("disabled");
        using var runtime = HaoyueRuntime.Create(_tempDir, store);

        Assert.Null(runtime.Tools.Resolve("network_cmd"));
        Assert.Null(runtime.Tools.Resolve("network_diagnose"));
    }

    [Fact]
    public void Runtime_Enabled_RegistersNetworkTools()
    {
        var store = NewStore("enabled");
        store.Config.NetworkOps.Enabled = true;
        using var runtime = HaoyueRuntime.Create(_tempDir, store);

        Assert.NotNull(runtime.Tools.Resolve("network_cmd"));
        Assert.NotNull(runtime.Tools.Resolve("network_diagnose"));
        Assert.True(runtime.Tools.Resolve("network_diagnose") is { Mutating: false });
        Assert.True(runtime.Tools.Resolve("network_cmd") is { Mutating: true });
    }

    // ---------- 命令目录 ----------

    [Fact]
    public void Catalog_CoversCoreCommands()
    {
        var names = NetworkOpsCatalog.Commands.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[]
                 {
                     "ping", "traceroute", "interfaces", "routes", "connections",
                     "arp", "dns_lookup", "dns_flush", "getmac", "netsh", "ip", "nmcli", "ethtool",
                 })
        {
            Assert.Contains(expected, names);
        }
        Assert.Equal(13, NetworkOpsCatalog.Commands.Count);
    }

    [Fact]
    public void Catalog_MutatingDetection()
    {
        var dnsFlush = NetworkOpsCatalog.Resolve("dns_flush")!;
        var ping = NetworkOpsCatalog.Resolve("ping")!;
        var netsh = NetworkOpsCatalog.Resolve("netsh")!;
        var interfaces = NetworkOpsCatalog.Resolve("interfaces")!;

        Assert.True(NetworkOpsCatalog.IsMutating(dnsFlush, []));
        Assert.False(NetworkOpsCatalog.IsMutating(ping, ["-n", "4"]));
        Assert.False(NetworkOpsCatalog.IsMutating(interfaces, []));

        Assert.False(NetworkOpsCatalog.IsMutating(netsh, ["wlan", "show", "profiles"]));
        Assert.True(NetworkOpsCatalog.IsMutating(netsh, ["wlan", "set", "hostednetwork"]));
        Assert.True(NetworkOpsCatalog.IsMutating(netsh, ["interface", "set", "interface", "eth0"]));
    }

    [Fact]
    public void Catalog_ValidateArgs_RejectsControlCharsAndOverflow()
    {
        Assert.Null(NetworkOpsCatalog.ValidateArgs(["-n", "4"]));
        Assert.NotNull(NetworkOpsCatalog.ValidateArgs(["bad\narg"]));
        Assert.NotNull(NetworkOpsCatalog.ValidateArgs(["\0"]));
        Assert.NotNull(NetworkOpsCatalog.ValidateArgs([new string('a', 300)]));
        Assert.NotNull(NetworkOpsCatalog.ValidateArgs(Enumerable.Repeat("x", 33).ToArray()));
    }

    // ---------- 适配器 ----------

    [Fact]
    public void WindowsAdapter_FiltersLinuxOnlyCommands()
    {
        var adapter = new WindowsNetworkAdapter();
        Assert.Contains(adapter.SupportedCommands, c => c.Name == "netsh");
        Assert.DoesNotContain(adapter.SupportedCommands, c => c.Name == "ip");
        Assert.DoesNotContain(adapter.SupportedCommands, c => c.Name == "ethtool");
    }

    [Fact]
    public void LinuxAdapter_FiltersWindowsOnlyCommands()
    {
        var adapter = new LinuxNetworkAdapter();
        Assert.Contains(adapter.SupportedCommands, c => c.Name == "ip");
        Assert.Contains(adapter.SupportedCommands, c => c.Name == "nmcli");
        Assert.DoesNotContain(adapter.SupportedCommands, c => c.Name == "getmac");
        Assert.DoesNotContain(adapter.SupportedCommands, c => c.Name == "netsh");
    }

    [Fact]
    public async Task Adapter_UnknownCommand_DegradesInBand()
    {
        var adapter = NetworkOpsAdapterFactory.Create();
        var outcome = await adapter.ExecuteAsync("no_such_command", null, [], 10, CancellationToken.None);
        Assert.Equal(-1, outcome.ExitCode);
        Assert.Contains("不支持", outcome.Output);
    }

    [Fact]
    public async Task Adapter_PingWithoutTarget_FailsWithGuidance()
    {
        var adapter = NetworkOpsAdapterFactory.Create();
        var outcome = await adapter.ExecuteAsync("ping", null, [], 10, CancellationToken.None);
        Assert.Equal(-1, outcome.ExitCode);
        Assert.Contains("target", outcome.Output);
    }

    [Fact]
    public async Task Adapter_PingLoopback_EndToEnd()
    {
        var adapter = NetworkOpsAdapterFactory.Create();
        var outcome = await adapter.ExecuteAsync("ping", "127.0.0.1", [], 30, CancellationToken.None);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains("127.0.0.1", outcome.Output);
    }

    // ---------- 工具 ----------

    [Fact]
    public async Task NetworkCmd_UnknownCommand_FailsWithAvailableList()
    {
        var tool = new NetworkCommandTool(new MockPromptProvider(), NetworkOpsAdapterFactory.Create(), new NetworkOpsConfig());
        var result = await tool.ExecuteAsync(new JsonObject { ["command"] = "frobnicate" }, CreateToolContext(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("可用命令", result.Output);
    }

    [Fact]
    public async Task NetworkCmd_MissingTarget_Fails()
    {
        var tool = new NetworkCommandTool(new MockPromptProvider(), NetworkOpsAdapterFactory.Create(), new NetworkOpsConfig());
        var result = await tool.ExecuteAsync(new JsonObject { ["command"] = "ping" }, CreateToolContext(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("target", result.Output);
    }

    [Fact]
    public async Task NetworkCmd_MutatingBlockedWhenDisallowed()
    {
        var tool = new NetworkCommandTool(
            new MockPromptProvider(), NetworkOpsAdapterFactory.Create(),
            new NetworkOpsConfig { AllowMutating = false });
        var result = await tool.ExecuteAsync(
            new JsonObject { ["command"] = "dns_flush" }, CreateToolContext(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("allowMutating", result.Output);
    }

    [Fact]
    public async Task NetworkCmd_VerboseNetshTreatedAsMutating_WhenDisallowed()
    {
        var tool = new NetworkCommandTool(
            new MockPromptProvider(), NetworkOpsAdapterFactory.Create(),
            new NetworkOpsConfig { AllowMutating = false });
        var result = await tool.ExecuteAsync(new JsonObject
        {
            ["command"] = "netsh",
            ["args"] = new JsonArray("interface", "set", "interface", "eth0"),
        }, CreateToolContext(), CancellationToken.None);
        if (OperatingSystem.IsWindows())
        {
            Assert.False(result.Success);
            Assert.Contains("变更类", result.Output);
        }
        else
        {
            Assert.False(result.Success);
            Assert.Contains("不支持", result.Output);
        }
    }

    [Fact]
    public async Task NetworkCmd_PingLoopback_Succeeds()
    {
        var tool = new NetworkCommandTool(new MockPromptProvider(), NetworkOpsAdapterFactory.Create(), new NetworkOpsConfig());
        var result = await tool.ExecuteAsync(new JsonObject
        {
            ["command"] = "ping",
            ["target"] = "127.0.0.1",
        }, CreateToolContext(), CancellationToken.None);
        Assert.True(result.Success);
        Assert.Contains("127.0.0.1", result.Output);
    }

    [Fact]
    public async Task Diagnose_SingleSection_ReturnsLabeledBundle()
    {
        var tool = new NetworkDiagnoseTool(new MockPromptProvider(), NetworkOpsAdapterFactory.Create());
        var result = await tool.ExecuteAsync(
            new JsonObject { ["section"] = "interfaces" }, CreateToolContext(), CancellationToken.None);
        Assert.True(result.Success);
        Assert.Contains("网络接口与 IP", result.Output);
    }

    [Fact]
    public async Task Diagnose_UnknownSection_Fails()
    {
        var tool = new NetworkDiagnoseTool(new MockPromptProvider(), NetworkOpsAdapterFactory.Create());
        var result = await tool.ExecuteAsync(
            new JsonObject { ["section"] = "wifi" }, CreateToolContext(), CancellationToken.None);
        Assert.False(result.Success);
    }

    // ---------- 扩展生命周期隔离 ----------

    [Fact]
    public async Task ExtensionManager_DisablingNetworkOps_KeepsOtherExtensions()
    {
        var store = NewStore("isolation");
        store.Config.NetworkOps.Enabled = true;
        store.Config.ComputerUse.Enabled = true;
        store.Config.ComputerUse.Driver = "vision";
        using var runtime = HaoyueRuntime.Create(_tempDir, store);
        Assert.NotNull(runtime.Tools.Resolve("network_cmd"));

        await runtime.Extensions.DisableAsync("network_ops");

        Assert.Null(runtime.Tools.Resolve("network_cmd"));
        Assert.Null(runtime.Tools.Resolve("network_diagnose"));
        // 其他扩展不受牵连
        Assert.NotNull(runtime.Tools.Resolve("computer"));
    }

    [Fact]
    public async Task ExtensionManager_ToggleNetworkOps_ReregistersCleanly()
    {
        var store = NewStore("toggle");
        store.Config.NetworkOps.Enabled = false;
        using var runtime = HaoyueRuntime.Create(_tempDir, store);

        store.Config.NetworkOps.Enabled = true;
        await runtime.Extensions.EnableAsync(runtime, "network_ops");
        Assert.NotNull(runtime.Tools.Resolve("network_cmd"));

        // 再次启用不得重复注册（ToolRegistry 重名会抛异常）
        await runtime.Extensions.EnableAsync(runtime, "network_ops");
        Assert.NotNull(runtime.Tools.Resolve("network_cmd"));

        store.Config.NetworkOps.Enabled = false;
        await runtime.Extensions.DisableAsync("network_ops");
        Assert.Null(runtime.Tools.Resolve("network_cmd"));
    }

    private ConfigStore NewStore(string tag)
    {
        var store = new ConfigStore(
            Path.Combine(_tempDir, $"config_{tag}.json"),
            Path.Combine(_tempDir, $"state_{tag}.json"));
        return store;
    }

    private ToolContext CreateToolContext()
    {
        var bus = new Haoyue.Runtime.Events.EventBus();
        var ws = new WorkspaceManager().Detect(_tempDir);
        return new ToolContext
        {
            Workspace = ws,
            Events = bus,
            Agent = new AgentConfig()
        };
    }

    private sealed class MockPromptProvider : IPromptProvider
    {
        public string? TryGet(string key) => null;
        public string Get(string key) => "";
        public string Render(string template, IReadOnlyDictionary<string, string> variables) => template;
        public string? GetRendered(string key, IReadOnlyDictionary<string, string> variables) => null;
        public void SetWorkspaceRoot(string? promptsDir) { }
    }
}
