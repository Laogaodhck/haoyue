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
[Collection(ProcessEnvironmentCollection.Name)]
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

    /// <summary>
    /// L-4：可选配置未提供时，占位符不得以 <c>${KEY}</c> 字面量落盘（旧行为会把字面量交给子进程），
    /// 并且未填项要随安装结果回报给调用方。
    /// </summary>
    [Fact]
    public void Install_OptionalInputsMissing_DropsPlaceholdersAndReports()
    {
        var package = new ConnectorPackage
        {
            SchemaVersion = 1,
            Id = "optional-conn",
            Name = "可选配置",
            Description = "测试",
            Category = "data",
            Server = new McpServerConfig
            {
                Transport = "stdio",
                Command = "npx",
                Args = ["-y", "@example/server", "--host", "${SMTP_HOST}", "--user", "${SMTP_USER}", "--flag"],
                Env = new Dictionary<string, string>
                {
                    ["SMTP_HOST"] = "${SMTP_HOST}",
                    ["SMTP_PORT"] = "${SMTP_PORT}",
                    ["STATIC"] = "fixed",
                },
            },
            Inputs =
            [
                new ConnectorInput { Key = "SMTP_HOST", Title = "主机", Required = true },
                new ConnectorInput { Key = "SMTP_USER", Title = "用户", Secret = true },
                new ConnectorInput { Key = "SMTP_PORT", Title = "端口" },
            ],
        };

        var store = NewStore();
        var result = ConnectorInstaller.Install(store, package, new Dictionary<string, string>
        {
            ["SMTP_HOST"] = "smtp.example.com",
        });

        var installed = store.Config.Mcp.Servers["optional-conn"];
        var env = installed.Env!;
        // 已提供的占位符照常替换。
        Assert.Equal("smtp.example.com", env["SMTP_HOST"]);
        Assert.Equal("fixed", env["STATIC"]);
        // 未提供的 env 键整键剔除，不留 ${...} 字面量。
        Assert.False(env.ContainsKey("SMTP_PORT"), "未填写的可选 env 键必须剔除");
        Assert.DoesNotContain("${", string.Join('|', env.Values));
        // 未提供的参数连同紧邻的 --user 开关一起丢弃，不留悬空开关。
        Assert.Equal(new[] { "-y", "@example/server", "--host", "smtp.example.com", "--flag" }, installed.Args);
        // 未填项按 key 排序回报，并带可读标题。
        Assert.Equal(new[] { "SMTP_PORT（端口）", "SMTP_USER（用户）" }, result.UnfilledInputs);
        Assert.False(result.IsComplete);

        var reloaded = NewStore();
        Assert.DoesNotContain("${", reloaded.Config.Mcp.Servers["optional-conn"].Args is { } args
            ? string.Join('|', args) : string.Empty);
    }

    /// <summary>L-4：命令/URL 是启动必需项，占位符取不到值时抛可读错误且不写配置。</summary>
    [Fact]
    public void Install_UnresolvedCommandPlaceholder_ThrowsWithoutWriting()
    {
        var package = new ConnectorPackage
        {
            SchemaVersion = 1,
            Id = "needs-binary",
            Name = "需要路径",
            Description = "测试",
            Category = "dev",
            Server = new McpServerConfig { Transport = "stdio", Command = "${BIN_PATH}" },
            Inputs = [new ConnectorInput { Key = "BIN_PATH", Title = "可执行文件" }],
        };

        var store = NewStore();
        var ex = Assert.Throws<ConnectorException>(() => ConnectorInstaller.Install(store, package));
        Assert.Contains("BIN_PATH", ex.Message);
        Assert.False(store.Config.Mcp.Servers.ContainsKey("needs-binary"), "失败的安装不得写入配置");
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
/// 连接器供应链完整性护栏（P2）：仓库 SHA256SUMS 清单与目录内容自洽、
/// Verify 对未篡改包放行、对篡改清单/内置服务器抛可读异常、无清单目录跳过。
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ConnectorIntegrityTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"haoyue-conn-int-{Guid.NewGuid():N}");

    public ConnectorIntegrityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>仓库 SHA256SUMS 的每一行都与目录实际内容一致（清单自洽，CI 同时有 python --check）。</summary>
    [Fact]
    public void RepoSums_MatchDirectoryContents()
    {
        var root = Path.Combine(FindRepoRoot(), "connectors");
        var sums = File.ReadAllLines(Path.Combine(root, "SHA256SUMS"));
        Assert.True(sums.Length >= 12, $"SHA256SUMS 覆盖不足：{sums.Length} 行");
        foreach (var line in sums)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var hash = line[..64];
            var relative = line[66..];
            var file = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), $"SHA256SUMS 引用了不存在的文件：{relative}");
            using var stream = File.OpenRead(file);
            Assert.Equal(hash, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)));
        }
    }

    /// <summary>未篡改的官方包应通过校验（清单 + 内置服务器一并比对）。</summary>
    [Fact]
    public void Verify_PassesForUntamperedBundledPackage()
    {
        var root = Path.Combine(FindRepoRoot(), "connectors");
        ConnectorIntegrity.Verify(Path.Combine(root, "email", "connector.json"));
    }

    /// <summary>清单哈希不匹配（被篡改）时抛可读异常。</summary>
    [Fact]
    public void Verify_TamperedManifest_Throws()
    {
        var root = Path.Combine(_dir, "connectors");
        Directory.CreateDirectory(Path.Combine(root, "evil"));
        var manifest = Path.Combine(root, "evil", "connector.json");
        File.WriteAllText(manifest, """{"schemaVersion":1,"id":"evil","name":"x","description":"x","category":"x"}""");
        File.WriteAllLines(Path.Combine(root, "SHA256SUMS"),
            [new string('0', 64) + "  evil/connector.json"]);

        var ex = Assert.Throws<ConnectorException>(() => ConnectorIntegrity.Verify(manifest));
        Assert.Contains("完整性校验失败", ex.Message);
    }

    /// <summary>内置本地服务器被改动且在清单覆盖范围内时拒绝（它是直接执行的代码面）。</summary>
    [Fact]
    public void Verify_TamperedBundledServer_Throws()
    {
        var root = Path.Combine(_dir, "connectors");
        Directory.CreateDirectory(Path.Combine(root, "servers"));
        Directory.CreateDirectory(Path.Combine(root, "someconn"));
        var manifest = Path.Combine(root, "someconn", "connector.json");
        File.WriteAllText(manifest, """{"schemaVersion":1,"id":"someconn","name":"x","description":"x","category":"x"}""");
        var server = Path.Combine(root, "servers", "haoyue_local_server.py");
        File.WriteAllText(server, "print('tampered')");
        // 清单登记该包（哈希正确）但把内置服务器哈希写错 → 必须拦截服务器。
        File.WriteAllLines(Path.Combine(root, "SHA256SUMS"),
        [
            $"{Sha256Of(manifest)}  someconn/connector.json",
            $"{new string('a', 64)}  servers/haoyue_local_server.py",
        ]);

        var ex = Assert.Throws<ConnectorException>(() => ConnectorIntegrity.Verify(manifest));
        Assert.Contains("内置本地 MCP 服务器", ex.Message);
    }

    /// <summary>
    /// L-7：清单是「已登记包」白名单。受管目录里多出一个未登记的连接器包时默认拒绝，
    /// 只有显式设置 HAOYUE_CONNECTORS_ALLOW_UNSIGNED=1 才放行自建包。
    /// </summary>
    [Fact]
    public void Verify_UnlistedPackage_ThrowsUnlessExplicitlyAllowed()
    {
        var root = Path.Combine(_dir, "connectors");
        Directory.CreateDirectory(Path.Combine(root, "known"));
        Directory.CreateDirectory(Path.Combine(root, "rogue"));
        var known = Path.Combine(root, "known", "connector.json");
        var rogue = Path.Combine(root, "rogue", "connector.json");
        File.WriteAllText(known, """{"schemaVersion":1,"id":"known","name":"x","description":"x","category":"x"}""");
        File.WriteAllText(rogue, """{"schemaVersion":1,"id":"rogue","name":"x","description":"x","category":"x"}""");
        File.WriteAllLines(Path.Combine(root, "SHA256SUMS"), [$"{Sha256Of(known)}  known/connector.json"]);

        ConnectorIntegrity.Verify(known); // 已登记包放行

        var ex = Assert.Throws<ConnectorException>(() => ConnectorIntegrity.Verify(rogue));
        Assert.Contains("未登记", ex.Message);

        Environment.SetEnvironmentVariable(ConnectorIntegrity.AllowUnsignedEnvVar, "1");
        try
        {
            ConnectorIntegrity.Verify(rogue); // 显式放行自建包
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConnectorIntegrity.AllowUnsignedEnvVar, null);
        }
    }

    /// <summary>目录无 SHA256SUMS（第三方/自定义目录）时跳过，不阻断安装。</summary>
    [Fact]
    public void Verify_SkipsWithoutSumsFile()
    {
        var root = Path.Combine(_dir, "custom-connectors");
        Directory.CreateDirectory(Path.Combine(root, "third"));
        var manifest = Path.Combine(root, "third", "connector.json");
        File.WriteAllText(manifest, """{"schemaVersion":1,"id":"third","name":"x","description":"x","category":"x"}""");
        ConnectorIntegrity.Verify(manifest); // 不抛即通过
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
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

/// <summary>
/// 连接器 daemon RPC 护栏：ListConnectors 的目录/安装状态合成、Install 的
/// 占位符与加密落盘、卸载闭环与未知 id 拒绝。走真实 DaemonAdminApi。
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
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

    /// <summary>
    /// L-4：可选配置未填时，daemon 安装回包必须列出「未填写项」，且配置里不得残留
    /// <c>${KEY}</c> 字面量（桌面端据此提示功能降级）。
    /// </summary>
    [Fact]
    public async Task Install_OptionalSecretsMissing_ReportedInPayloadAndNotPersisted()
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

            // email 的 SMTP_USER / SMTP_PASSWORD 是可选 secret：不填则从配置剔除并回报。
            var payload = JsonNode.Parse(await admin.InstallConnectorAsync(new JsonObject
            {
                ["id"] = "email",
                ["values"] = new JsonObject
                {
                    ["IMAP_HOST"] = "imap.example.com",
                    ["IMAP_USER"] = "user@example.com",
                    ["IMAP_PASSWORD"] = "imap-secret",
                    ["SMTP_HOST"] = "smtp.example.com",
                },
            }, CancellationToken.None))!.AsObject();

            var unfilled = payload["unfilledInputs"]!.AsArray()
                .Select(node => node!.GetValue<string>()).ToArray();
            Assert.Contains(unfilled, key => key.StartsWith("SMTP_USER", StringComparison.Ordinal));
            Assert.Contains(unfilled, key => key.StartsWith("SMTP_PASSWORD", StringComparison.Ordinal));

            var env = store.Config.Mcp.Servers["email"].Env!;
            Assert.DoesNotContain("${", string.Join('|', env.Values));
            Assert.False(env.ContainsKey("SMTP_USER"), "未填写的可选 env 键必须剔除");
            Assert.Equal("993", env["IMAP_PORT"]); // 有默认值的不受影响
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
