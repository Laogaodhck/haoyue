using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;

namespace Haoyue.Runtime.Daemon;

/// <summary>
/// Opt-in TCP listener that lets desktops on the same LAN drive this daemon.
///
/// 安全模型：局域网连接不发送握手令牌本身，而是由服务端下发一次性 nonce，客户端用
/// HMAC-SHA256(token, "haoyue-lan-v1:" + nonce) 应答。被动抓包只能得到 nonce 与证明，
/// 无法在别的连接上重放；令牌也不进入网络。认证失败统一延迟后断开，避免快速枚举。
/// 监听默认关闭，只有显式开启且 daemon 持握手令牌时才绑定端口。
/// </summary>
internal sealed class LanAccessServer : IAsyncDisposable
{
    /// <summary>证明计算的上下文前缀，防止该 HMAC 被复用到其他协议。</summary>
    internal const string MacContext = "haoyue-lan-v1:";

    private const int MaxConnections = 16;
    private const int MaxHandshakeBytes = 4096;
    private const int HandshakeTimeoutSeconds = 15;
    private const int AuthFailureDelayMs = 300;

    private readonly IConfigStore _config;
    private readonly DaemonFileLog _log;
    private readonly string? _token;
    private readonly Func<Stream, CancellationToken, Task> _handleConnection;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _loopExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _runCts;
    private int _runStarted;
    private volatile string? _error;

    public LanAccessServer(
        IConfigStore config,
        DaemonFileLog log,
        string? handshakeToken,
        Func<Stream, CancellationToken, Task> handleConnection)
    {
        _config = config;
        _log = log;
        _token = handshakeToken;
        _handleConnection = handleConnection;
    }

    /// <summary>当前状态（配置开关、监听是否在跑、可达地址与上一次监听错误）。</summary>
    public JsonObject Status()
    {
        var lan = _config.Config.Daemon.Lan;
        bool running;
        lock (_gate) running = _listener is not null;
        var status = new JsonObject
        {
            ["enabled"] = lan.Enabled,
            ["running"] = running,
            ["bindAddress"] = lan.BindAddress,
            ["port"] = lan.Port,
            ["addresses"] = new JsonArray(LocalAddresses().Select(address => (JsonNode)address).ToArray()),
        };
        if (_error is { Length: > 0 } error) status["error"] = error;
        return status;
    }

    /// <summary>
    /// 校验并应用 daemon.lan.set 的参数，持久化配置后立即重启监听（不需要重启进程）。
    /// </summary>
    public string Apply(JsonObject parameters)
    {
        var lan = _config.Config.Daemon.Lan;
        // 先把三个字段全部校验完再落配置：任一字段非法时内存状态不能只改一半。
        bool? enabled = null;
        string? bindAddress = null;
        int? port = null;

        if (parameters["enabled"] is JsonValue enabledValue)
        {
            if (!enabledValue.TryGetValue<bool>(out var parsedEnabled))
                throw new DaemonRequestException("params.enabled must be a boolean");
            if (parsedEnabled && _token is null)
                throw new DaemonRequestException(
                    "daemon 未配置握手令牌，无法开启局域网访问", DaemonErrorCode.Conflict);
            enabled = parsedEnabled;
        }

        if (parameters["bindAddress"] is JsonValue bindValue)
        {
            if (!bindValue.TryGetValue<string>(out var bind) || !TryNormalizeBindAddress(bind, out var normalized))
                throw new DaemonRequestException(
                    "params.bindAddress must be a local IPv4 literal (0.0.0.0 / 127.0.0.1 / this host's address)");
            bindAddress = normalized;
        }

        if (parameters["port"] is JsonValue portValue)
        {
            if (!portValue.TryGetValue<int>(out var parsedPort) || parsedPort is < 1024 or > 65535)
                throw new DaemonRequestException("params.port must be an integer between 1024 and 65535");
            port = parsedPort;
        }

        if (enabled is not null) lan.Enabled = enabled.Value;
        if (bindAddress is not null) lan.BindAddress = bindAddress;
        if (port is not null) lan.Port = port.Value;

        _config.Save();
        RequestRestart();
        return Status().ToJsonString();
    }

    /// <summary>配置变更后唤醒监听循环：停掉旧 listener，下一次循环按新配置重新绑定。</summary>
    public void RequestRestart()
    {
        lock (_gate) { _listener?.Stop(); }
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _runStarted, 1) == 1)
            throw new InvalidOperationException("LanAccessServer.RunAsync can only be started once");
        // 连接任务与接受循环共用同一个 CTS：它只在 DisposeAsync 等所有在途连接退出后才释放，
        // 避免「令牌源已释放、连接仍在用令牌注册回调」的竞态。
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var runCt = _runCts.Token;
        try
        {
            while (!runCt.IsCancellationRequested)
            {
                var lan = Snapshot();
                if (!lan.Enabled)
                {
                    _error = null;
                    if (!await WaitForSignalAsync(runCt, Timeout.InfiniteTimeSpan).ConfigureAwait(false)) return;
                    continue;
                }
                if (_token is null)
                {
                    _error = "daemon 未配置握手令牌";
                    if (!await WaitForSignalAsync(runCt, Timeout.InfiniteTimeSpan).ConfigureAwait(false)) return;
                    continue;
                }
                var restart = await ListenUntilChangedAsync(lan, runCt).ConfigureAwait(false);
                if (!restart && !runCt.IsCancellationRequested)
                    await WaitForSignalAsync(runCt, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
        }
        finally
        {
            _loopExited.TrySetResult();
        }
    }

    private async Task<bool> ListenUntilChangedAsync(LanAccessConfig lan, CancellationToken ct)
    {
        TcpListener? listener = null;
        var restartRequested = false;
        try
        {
            listener = new TcpListener(IPAddress.Parse(lan.BindAddress), lan.Port);
            listener.Start();
            lock (_gate) _listener = listener;
            _error = null;
            _log.Info($"lan access listening on {lan.BindAddress}:{lan.Port}");
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
                {
                    restartRequested = true;
                    break;
                }
                catch (SocketException ex)
                {
                    _error = ex.Message;
                    _log.Warn($"lan accept failed: {ex.Message}");
                    break;
                }
                _ = Task.Run(() => ServeClientAsync(client, ct), CancellationToken.None);
            }
        }
        catch (SocketException ex)
        {
            _error = ex.Message;
            _log.Warn($"lan listener failed on {lan.BindAddress}:{lan.Port}: {ex.Message}");
        }
        catch (FormatException ex)
        {
            _error = ex.Message;
            _log.Warn($"lan listener bind address invalid: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_listener, listener)) _listener = null;
            }
            listener?.Stop();
        }
        return restartRequested;
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken ct)
    {
        var admitted = false;
        try
        {
            admitted = await _slots.WaitAsync(0, ct).ConfigureAwait(false);
            if (!admitted) return; // 并发上限：多余的连接直接拒绝，避免拖垮 daemon

            using (client)
            await using (var stream = client.GetStream())
            {
                if (!await AuthenticateAsync(stream, ct).ConfigureAwait(false))
                {
                    await Task.Delay(AuthFailureDelayMs, ct).ConfigureAwait(false);
                    return;
                }
                await _handleConnection(stream, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Warn($"lan connection failed: {ex.Message}");
        }
        finally
        {
            if (admitted) _slots.Release();
            else client.Dispose();
        }
    }

    /// <summary>
    /// 下发 nonce 并校验客户端证明。读取手写逐字节进行（握手行 ≤4KB），这样认证后
    /// 管道里剩下的字节仍留在 socket 中，交给共享连接处理器自己的读取器，不会被预读吞掉。
    /// </summary>
    private async Task<bool> AuthenticateAsync(NetworkStream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(HandshakeTimeoutSeconds));
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await writer.WriteLineAsync(
            new JsonObject { ["id"] = 0, ["event"] = "challenge", ["data"] = nonce }.ToJsonString().AsMemory(),
            timeout.Token).ConfigureAwait(false);

        string? line;
        try
        {
            line = await ReadLineAsync(stream, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        if (line is null) return false;

        JsonObject? request;
        try { request = JsonNode.Parse(line) as JsonObject; }
        catch (System.Text.Json.JsonException) { request = null; }
        if (request is null)
        {
            await WriteErrorAsync(writer, 0, "authentication failed: invalid handshake message",
                DaemonErrorCode.Unauthorized, timeout.Token).ConfigureAwait(false);
            return false;
        }

        long id = 0;
        string? method = null;
        string? proof = null;
        string? clientVersion = null;
        try
        {
            id = request["id"]?.GetValue<long>() ?? 0;
            method = request["method"]?.GetValue<string>();
            proof = request["params"]?["proof"]?.GetValue<string>();
            clientVersion = request["params"]?["protocolVersion"]?.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
        }

        var expected = ComputeProof(_token!, nonce);
        if (method != "handshake" || proof is null || !ProofEquals(expected, proof))
        {
            await WriteErrorAsync(writer, id, "authentication failed: missing or invalid handshake proof",
                DaemonErrorCode.Unauthorized, timeout.Token).ConfigureAwait(false);
            return false;
        }

        string? versionWarning = null;
        if (clientVersion is not null
            && !DaemonContract.IsProtocolCompatible(DaemonServer.ProtocolVersion, clientVersion, out versionWarning))
        {
            await WriteErrorAsync(writer, id,
                $"protocol version mismatch: daemon={DaemonServer.ProtocolVersion}, client={clientVersion}. " +
                "Please upgrade the haoyue client or daemon to a compatible version.",
                DaemonErrorCode.VersionMismatch, timeout.Token).ConfigureAwait(false);
            return false;
        }

        await writer.WriteLineAsync(
            new JsonObject { ["id"] = id, ["event"] = "result", ["data"] = DaemonProtocol.Info(versionWarning) }
                .ToJsonString().AsMemory(),
            timeout.Token).ConfigureAwait(false);
        return true;
    }

    internal static string ComputeProof(string token, string nonce) =>
        Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(token),
            Encoding.UTF8.GetBytes(MacContext + nonce))).ToLowerInvariant();

    private static bool ProofEquals(string expected, string provided)
    {
        var left = Encoding.UTF8.GetBytes(expected.ToLowerInvariant());
        var right = Encoding.UTF8.GetBytes(provided.ToLowerInvariant());
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHandshakeBytes];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count, 1), ct).ConfigureAwait(false);
            if (read == 0) return null;
            if (buffer[count] == (byte)'\n')
                return Encoding.UTF8.GetString(buffer, 0, count).TrimEnd('\r');
            count++;
        }
        return null; // 超长握手行：未认证连接不值得处理
    }

    private static async Task WriteErrorAsync(
        StreamWriter writer, long id, string message, DaemonErrorCode code, CancellationToken ct) =>
        await writer.WriteLineAsync(new JsonObject
        {
            ["id"] = id,
            ["event"] = "error",
            ["data"] = message,
            ["details"] = new JsonObject { ["code"] = code.ToWire() },
        }.ToJsonString().AsMemory(), ct).ConfigureAwait(false);

    /// <summary>等待配置变更信号或超时；返回 false 表示调用方应结束循环（已取消）。</summary>
    private async Task<bool> WaitForSignalAsync(CancellationToken ct, TimeSpan timeout)
    {
        try
        {
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                await _wake.WaitAsync(ct).ConfigureAwait(false);
                return true;
            }
            // 有限超时只是重试节拍：返回 false 表示可以继续循环。
            await _wake.WaitAsync(timeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private LanAccessConfig Snapshot()
    {
        var lan = _config.Config.Daemon.Lan;
        return new LanAccessConfig
        {
            Enabled = lan.Enabled,
            BindAddress = lan.BindAddress,
            Port = lan.Port,
        };
    }

    /// <summary>只接受本机可用的 IPv4 字面量：任播（0.0.0.0）、回环或本机网卡地址。</summary>
    private static bool TryNormalizeBindAddress(string? raw, out string normalized)
    {
        normalized = "";
        if (!IPAddress.TryParse(raw?.Trim(), out var address)) return false;
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        if (address.Equals(IPAddress.Any) || IPAddress.IsLoopback(address))
        {
            normalized = address.ToString();
            return true;
        }
        if (!LocalAddresses().Contains(address.ToString(), StringComparer.Ordinal)) return false;
        normalized = address.ToString();
        return true;
    }

    private static List<string> LocalAddresses()
    {
        var result = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var text = unicast.Address.ToString();
                    // 169.254/16 是链路本地地址，通常无法从其他设备到达，不作为配对提示。
                    if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    if (!result.Contains(text, StringComparer.Ordinal)) result.Add(text);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // 无法枚举网卡时保持空列表：UI 会提示手动填写地址。
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        // 先停监听、唤醒等待配置信号的循环，再等接受循环与在途连接退出，
        // 最后才释放信号量 / 令牌源——否则后台循环会撞上已释放对象。
        _stop.Cancel();
        RequestRestart();
        if (Volatile.Read(ref _runStarted) == 1)
        {
            try
            {
                await _loopExited.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            var deadline = DateTime.UtcNow.AddSeconds(5);
            for (var i = 0; i < MaxConnections; i++)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                if (!await _slots.WaitAsync(remaining).ConfigureAwait(false)) break;
            }
        }
        _runCts?.Dispose();
        _stop.Dispose();
        _wake.Dispose();
        _slots.Dispose();
    }
}
