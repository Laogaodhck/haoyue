using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Haoyue.Runtime;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Daemon;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Workspaces;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// 局域网接入面：监听启停、HMAC 挑战应答与配置校验。
/// 这些用例直接驱动 <see cref="LanAccessServer"/>（不启动进程级管道循环），
/// 端口固定绑定回环，避免与并行测试争抢命名管道 / socket。
/// </summary>
public sealed class LanAccessTests : IAsyncDisposable
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "haoyue-lan-tests", Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly DaemonFileLog _log;

    public LanAccessTests()
    {
        Directory.CreateDirectory(_tempDir);
        _log = DaemonFileLog.Open(Path.Combine(_tempDir, "lan.log"));
    }

    [Fact]
    public async Task LanAccess_HmacProofAuthenticates_AndServesRequests()
    {
        var (lan, _, configStore) = await StartLanAsync(enabled: true);
        using var client = await ConnectAsync(configStore.Config.Daemon.Lan.Port);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        var challenge = JsonNode.Parse(await ReadLineAsync(reader))!.AsObject();
        Assert.Equal("challenge", challenge["event"]!.GetValue<string>());
        var nonce = challenge["data"]!.GetValue<string>();

        // 正确令牌只用于计算证明，不上线；服务端用同一 nonce 复算。
        var proof = LanAccessServer.ComputeProof(Token, nonce);
        await writer.WriteLineAsync(new JsonObject
        {
            ["id"] = 0,
            ["method"] = "handshake",
            ["params"] = new JsonObject
            {
                ["proof"] = proof,
                ["protocolVersion"] = DaemonServer.ProtocolVersion,
            },
        }.ToJsonString());
        var authenticated = JsonNode.Parse(await ReadLineAsync(reader))!.AsObject();
        Assert.Equal("result", authenticated["event"]!.GetValue<string>());
        var info = JsonNode.Parse(authenticated["data"]!.GetValue<string>())!.AsObject();
        Assert.Equal(DaemonServer.ProtocolVersion, info["version"]!.GetValue<string>());

        await writer.WriteLineAsync(new JsonObject { ["id"] = 1, ["method"] = "ping" }.ToJsonString());
        var pong = JsonNode.Parse(await ReadLineAsync(reader))!.AsObject();
        Assert.Equal(1, pong["id"]!.GetValue<long>());
        Assert.Equal("pong", pong["event"]!.GetValue<string>());

        Assert.True(lan.Status()["running"]!.GetValue<bool>());
        Assert.True(lan.Status()["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task LanAccess_WrongProof_IsRejectedWithoutLeakingToken()
    {
        var (_, _, configStore) = await StartLanAsync(enabled: true);
        using var client = await ConnectAsync(configStore.Config.Daemon.Lan.Port);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        var challenge = JsonNode.Parse(await ReadLineAsync(reader))!.AsObject();
        var nonce = challenge["data"]!.GetValue<string>();
        // 旧式直接发令牌的握手在局域网路径上必须无效。
        await writer.WriteLineAsync(new JsonObject
        {
            ["id"] = 0,
            ["method"] = "handshake",
            ["params"] = new JsonObject { ["token"] = Token, ["protocolVersion"] = DaemonServer.ProtocolVersion },
        }.ToJsonString());

        var rejected = JsonNode.Parse(await ReadLineAsync(reader))!.AsObject();
        Assert.Equal("error", rejected["event"]!.GetValue<string>());
        Assert.Equal("unauthorized", rejected["details"]!["code"]!.GetValue<string>());
        Assert.DoesNotContain(Token, rejected.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LanAccess_ApplyPersistsAndValidatesSettings()
    {
        var (lan, _, configStore) = await StartLanAsync(enabled: false);
        var port = GetFreePort();

        var status = JsonNode.Parse(lan.Apply(new JsonObject
        {
            ["enabled"] = true,
            ["bindAddress"] = "127.0.0.1",
            ["port"] = port,
        }))!.AsObject();
        Assert.True(status["enabled"]!.GetValue<bool>());
        Assert.Equal(port, status["port"]!.GetValue<int>());
        // 热生效：daemon.lan.set 之后监听循环按新配置重新绑定，无需重启进程。
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!lan.Status()["running"]!.GetValue<bool>() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.True(lan.Status()["running"]!.GetValue<bool>(), "listener did not rebind after daemon.lan.set");

        var persisted = File.ReadAllText(Path.Combine(_tempDir, "config.json"));
        Assert.Contains("\"daemon\"", persisted, StringComparison.Ordinal);
        Assert.Contains("\"lan\"", persisted, StringComparison.Ordinal);
        Assert.Contains(port.ToString(), persisted, StringComparison.Ordinal);

        var badPort = Assert.Throws<DaemonRequestException>(() => lan.Apply(new JsonObject { ["port"] = 80 }));
        Assert.Equal(DaemonErrorCode.InvalidParams, badPort.Code);
        var badBind = Assert.Throws<DaemonRequestException>(() => lan.Apply(new JsonObject { ["bindAddress"] = "example.com" }));
        Assert.Equal(DaemonErrorCode.InvalidParams, badBind.Code);
        // 一个请求里既有合法字段又有非法字段：整批拒绝，合法字段也不能只改一半。
        Assert.Throws<DaemonRequestException>(() =>
            lan.Apply(new JsonObject { ["enabled"] = false, ["port"] = 80 }));
        Assert.True(configStore.Config.Daemon.Lan.Enabled);
        Assert.Equal(port, configStore.Config.Daemon.Lan.Port);
    }

    [Fact]
    public async Task LanAccess_EnableRequiresHandshakeToken()
    {
        var configStore = new ConfigStore(
            Path.Combine(_tempDir, "tokenless-config.json"),
            Path.Combine(_tempDir, "tokenless-state.json"));
        using var log = DaemonFileLog.Open(Path.Combine(_tempDir, "tokenless.log"));
        await using var lan = new LanAccessServer(configStore, log, null, (_, _) => Task.CompletedTask);
        var error = Assert.Throws<DaemonRequestException>(() =>
            lan.Apply(new JsonObject { ["enabled"] = true }));
        Assert.Equal(DaemonErrorCode.Conflict, error.Code);
        Assert.False(configStore.Config.Daemon.Lan.Enabled);
    }

    private async Task<(LanAccessServer Lan, DaemonServer Server, ConfigStore Store)> StartLanAsync(bool enabled)
    {
        var workspace = Path.Combine(_tempDir, "workspace");
        Directory.CreateDirectory(workspace);
        var configStore = new ConfigStore(
            Path.Combine(_tempDir, "config.json"),
            Path.Combine(_tempDir, "state.json"));
        var runtime = HaoyueRuntime.Create(
            workspace, configStore, Path.Combine(_tempDir, "haoyue.db"));
        var server = new DaemonServer(
            runtime,
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)),
            new WorkspaceManager().CreateGlobal(Path.Combine(_tempDir, "global-state")),
            Token,
            daemonLogFile: Path.Combine(_tempDir, "daemon.log"));
        _disposables.Add(server);

        configStore.Config.Daemon.Lan.Enabled = enabled;
        configStore.Config.Daemon.Lan.BindAddress = "127.0.0.1";
        configStore.Config.Daemon.Lan.Port = GetFreePort();

        var lan = new LanAccessServer(
            configStore, _log, Token,
            (stream, ct) => server.HandleConnectionAsync(stream, ct, authenticate: false));
        _disposables.Add(lan);
        _ = Task.Run(() => lan.RunAsync(_cts.Token), _cts.Token);
        if (enabled)
        {
            // 监听启动是异步的：先确认真正进入监听态，再让测试连端口。
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!lan.Status()["running"]!.GetValue<bool>() && DateTime.UtcNow < deadline)
                await Task.Delay(25);
            Assert.True(lan.Status()["running"]!.GetValue<bool>(), "LAN listener did not start in time");
        }
        return (lan, server, configStore);
    }

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(2));
                return client;
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException)
            {
                client.Dispose();
                if (DateTime.UtcNow >= deadline) throw;
                await Task.Delay(50);
            }
        }
    }

    private static async Task<string> ReadLineAsync(StreamReader reader)
    {
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(string.IsNullOrWhiteSpace(line));
        return line!;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            try { await disposable.DisposeAsync(); }
            catch (Exception) { /* best effort cleanup */ }
        }
        _cts.Dispose();
        _log.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }
}
