using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Haoyue.Runtime;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Connectors;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Daemon;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Secrets;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>
/// S5 连接器生态护栏：目录加载完整性、包规范校验、安装器占位符/加密落盘、
/// 卸载闭环。全部在临时目录进行，不触碰真实 ~/.haoyue/config.json。
/// </summary>
public sealed class ConnectorCatalogTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"haoyue-connectors-{Guid.NewGuid():N}");

    public ConnectorCatalogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>仓库内置目录应包含全部 11 个连接器包且 id/字段合法。</summary>
    [Fact]
    public void RepoCatalog_LoadsAllPackages()
    {
        var repoRoot = FindRepoRoot();
        var packages = ConnectorCatalog.LoadAll(Path.Combine(repoRoot, "connectors"));

        // 用户对标清单要求 ≥10；仓库当前 11 个（git 与 github 各一包）。
        Assert.True(packages.Count >= 10, $"首批连接器应 ≥10 个，实际 {packages.Count}");

        foreach (var package in packages)
        {
            Assert.Equal(1, package.SchemaVersion);
            Assert.False(string.IsNullOrWhiteSpace(package.Name), $"{package.Id} name");
            Assert.False(string.IsNullOrWhiteSpace(package.Description), $"{package.Id} description");
            Assert.False(string.IsNullOrWhiteSpace(package.Category), $"{package.Id} category");
            Assert.False(string.IsNullOrWhiteSpace(package.Server.Transport), $"{package.Id} transport");
        }

        var ids = packages.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[]
                 {
                     "browser", "git", "github", "email", "calendar", "rss",
                     "clipboard", "notify", "http-api", "sqlite", "notion",
                 })
            Assert.True(ids.Contains(expected), $"缺少连接器 {expected}");
    }

    /// <summary>内置本地服务器应可解析且被 offline 连接器引用。</summary>
    [Fact]
    public void BundledServer_ExistsAndReferenced()
    {
        var repoRoot = FindRepoRoot();
        var root = Path.Combine(repoRoot, "connectors");
        Assert.NotNull(ConnectorCatalog.ResolveBundledServer(root));

        var offlineIds = ConnectorCatalog.LoadAll(root)
            .Where(p => p.Offline == true)
            .Select(p => p.Id)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("clipboard", offlineIds);
        Assert.Contains("email", offlineIds);
    }

    /// <summary>安装：占位符替换、默认值回填、secret 加密、写入 mcp.servers 并持久化。</summary>
    [Fact]
    public void Install_ResolvesPlaceholdersAndEncryptsSecrets()
    {
        var package = new ConnectorPackage
        {
            SchemaVersion = 1,
            Id = "test-conn",
            Name = "测试连接器",
            Description = "测试",
            Category = "data",
            Server = new McpServerConfig
            {
                Transport = "stdio",
                Command = "npx",
                Args = ["-y", "@example/server", "--token", "${API_TOKEN}", "--root", "${ROOT_DIR}"],
                Env = new Dictionary<string, string> { ["API_TOKEN"] = "${API_TOKEN}" },
            },
            Inputs =
            [
                new ConnectorInput { Key = "API_TOKEN", Title = "令牌", Required = true, Secret = true },
                new ConnectorInput { Key = "ROOT_DIR", Title = "目录", Required = false, Default = "." },
            ],
        };

        var store = NewStore();
        ConnectorInstaller.Install(store, package, new Dictionary<string, string>
        {
            ["API_TOKEN"] = "plain-secret-value",
        });

        var installed = store.Config.Mcp.Servers["test-conn"];
        Assert.Equal("npx", installed.Command);
        Assert.Equal(".", installed.Args![5]);        // 默认值应回填
        Assert.Equal("plain-secret-value", installed.Args![3]); // 占位符应被替换
        // secret 环境变量落盘必须是 secret: 加密形态，且可被 SecretResolver 还原。
        Assert.StartsWith("secret:", installed.Env!["API_TOKEN"]);
        Assert.Equal("plain-secret-value", SecretResolver.Resolve(installed.Env["API_TOKEN"]));

        // 持久化往返：重开配置仍可读。
        var reloaded = NewStore();
        Assert.True(reloaded.Config.Mcp.Servers.ContainsKey("test-conn"));
    }

    /// <summary>安装缺必填项时应抛可读异常且不写入配置。</summary>
    [Fact]
    public void Install_MissingRequiredInput_Fails()
    {
        var package = new ConnectorPackage
        {
            SchemaVersion = 1,
            Id = "needs-token",
            Name = "需要令牌",
            Description = "测试",
            Category = "dev",
            Server = new McpServerConfig { Transport = "stdio", Command = "npx" },
            Inputs = [new ConnectorInput { Key = "TOKEN", Title = "令牌", Required = true, Secret = true }],
        };

        var store = NewStore();
        var ex = Assert.Throws<ConnectorException>(
            () => ConnectorInstaller.Install(store, package));
        Assert.Contains("TOKEN", ex.Message);
        Assert.False(store.Config.Mcp.Servers.ContainsKey("needs-token"), "失败安装不得写入");
    }

    /// <summary>卸载闭环：安装后可卸载，重复卸载返回 false。</summary>
    [Fact]
    public void Uninstall_RemovesAndReports()
    {
        var package = new ConnectorPackage
        {
            SchemaVersion = 1,
            Id = "temp-conn",
            Name = "临时",
            Description = "测试",
            Category = "system",
            Server = new McpServerConfig { Transport = "stdio", Command = "python" },
        };
        var store = NewStore();
        ConnectorInstaller.Install(store, package);
        Assert.True(ConnectorInstaller.IsInstalled(store, "temp-conn"));

        Assert.True(ConnectorInstaller.Uninstall(store, "temp-conn"));
        Assert.False(ConnectorInstaller.IsInstalled(store, "temp-conn"));
        Assert.False(ConnectorInstaller.Uninstall(store, "temp-conn"), "重复卸载应返回 false");
    }

    private ConfigStore NewStore() => new(
        Path.Combine(_dir, "config.json"), Path.Combine(_dir, "state.json"));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Haoyue.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

/// <summary>
/// 连接器 daemon RPC 护栏：ListConnectors 的目录/安装状态合成、Install 的
/// 占位符与加密落盘、卸载闭环与未知 id 拒绝。走真实 DaemonAdminApi。
/// </summary>
public sealed class ConnectorDaemonApiTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"haoyue-conn-rpc-{Guid.NewGuid():N}");

    public ConnectorDaemonApiTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task ListInstallUninstall_RoundTrip()
    {
        var repoRoot = FindRepoRoot();
        var connectorsDir = Path.Combine(repoRoot, "connectors");
        Environment.SetEnvironmentVariable("HAOYUE_CONNECTORS_DIR", connectorsDir);
        try
        {
            var store = new ConfigStore(
                Path.Combine(_dir, "config.json"), Path.Combine(_dir, "state.json"));
            var globalWorkspace = new WorkspaceManager().CreateGlobal(Path.Combine(_dir, "global"));
            await using var runtime = HaoyueRuntime.CreateIsolated(globalWorkspace, configureServices: services =>
            {
                services.AddSingleton<IConfigStore>(store);
                services.AddSingleton(new HaoyueDatabase(Path.Combine(_dir, "state.db")));
                services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
                services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
            });
            var admin = new Haoyue.Runtime.Daemon.DaemonAdminApi(runtime, globalWorkspace, new FileLockCoordinator());

            // list：目录可用、11 个包、clipboard 未安装。
            var listed = JsonNode.Parse(admin.ListConnectors())!.AsObject();
            Assert.True(listed["catalogAvailable"]!.GetValue<bool>());
            var clipboard = listed["connectors"]!.AsArray()
                .First(node => node!["id"]!.GetValue<string>() == "clipboard")!.AsObject();
            Assert.False(clipboard["installed"]!.GetValue<bool>());

            // install：一键安装 offline 连接器，LOCAL_SERVER 自动解析。
            var afterInstall = JsonNode.Parse((await admin.InstallConnectorAsync(
                new JsonObject { ["id"] = "clipboard" }, CancellationToken.None))!)!.AsObject();
            var installedEntry = afterInstall["connectors"]!.AsArray()
                .First(node => node!["id"]!.GetValue<string>() == "clipboard")!.AsObject();
            Assert.True(installedEntry["installed"]!.GetValue<bool>());
            Assert.True(installedEntry["enabled"]!.GetValue<bool>());

            var server = store.Config.Mcp.Servers["clipboard"];
            Assert.False(string.IsNullOrWhiteSpace(server.Command), "command 应已实例化");
            Assert.DoesNotContain("${LOCAL_SERVER}", string.Join(' ', server.Args ?? []));

            // uninstall：卸载后状态回落，重复卸载拒绝。
            var afterUninstall = JsonNode.Parse((await admin.UninstallConnectorAsync(
                new JsonObject { ["id"] = "clipboard" }, CancellationToken.None))!)!.AsObject();
            var removed = afterUninstall["connectors"]!.AsArray()
                .First(node => node!["id"]!.GetValue<string>() == "clipboard")!.AsObject();
            Assert.False(removed["installed"]!.GetValue<bool>());
            Assert.False(store.Config.Mcp.Servers.ContainsKey("clipboard"));

            await Assert.ThrowsAsync<Haoyue.Runtime.Daemon.DaemonRequestException>(() =>
                admin.UninstallConnectorAsync(new JsonObject { ["id"] = "clipboard" }, CancellationToken.None));
            await Assert.ThrowsAsync<Haoyue.Runtime.Daemon.DaemonRequestException>(() =>
                admin.InstallConnectorAsync(new JsonObject { ["id"] = "no-such-connector" }, CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HAOYUE_CONNECTORS_DIR", null);
        }
    }

    /// <summary>必填 inputs 缺失时安装报可读错误且不写配置。</summary>
    [Fact]
    public async Task Install_MissingRequired_ReturnsReadableError()
    {
        var repoRoot = FindRepoRoot();
        Environment.SetEnvironmentVariable("HAOYUE_CONNECTORS_DIR", Path.Combine(repoRoot, "connectors"));
        try
        {
            var store = new ConfigStore(
                Path.Combine(_dir, "config.json"), Path.Combine(_dir, "state.json"));
            var globalWorkspace = new WorkspaceManager().CreateGlobal(Path.Combine(_dir, "global"));
            await using var runtime = HaoyueRuntime.CreateIsolated(globalWorkspace, configureServices: services =>
            {
                services.AddSingleton<IConfigStore>(store);
                services.AddSingleton(new HaoyueDatabase(Path.Combine(_dir, "state.db")));
                services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
                services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
            });
            var admin = new Haoyue.Runtime.Daemon.DaemonAdminApi(runtime, globalWorkspace, new FileLockCoordinator());

            // sqlite 连接器要求 DB_PATH（无默认值）。
            var ex = await Assert.ThrowsAsync<Haoyue.Runtime.Daemon.DaemonRequestException>(() =>
                admin.InstallConnectorAsync(new JsonObject { ["id"] = "sqlite" }, CancellationToken.None));
            Assert.Contains("DB_PATH", ex.Message);
            Assert.False(store.Config.Mcp.Servers.ContainsKey("sqlite"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HAOYUE_CONNECTORS_DIR", null);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Haoyue.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
