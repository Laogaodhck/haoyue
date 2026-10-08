using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Haoyue.Runtime.Mcp;

/// <summary>A bidirectional JSON-RPC message pipe to an MCP server.</summary>
public interface IMcpTransport : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct);
    Task SendAsync(JsonObject message, CancellationToken ct);
    ChannelReader<JsonObject> Incoming { get; }
}

/// <summary>stdio transport: spawns the server process, newline-delimited JSON-RPC over stdin/stdout.</summary>
public sealed class StdioMcpTransport(string command, IReadOnlyList<string>? args, IReadOnlyDictionary<string, string>? env)
    : IMcpTransport
{
    // Encoding.UTF8 carries a BOM preamble that StreamWriter emits before the first
    // line; Node-based MCP servers fail JSON.parse on "\uFEFF{...}" and silently
    // drop every request — the pipe must be strictly BOM-less UTF-8.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Channel<JsonObject> _incoming = Channel.CreateUnbounded<JsonObject>();
    private readonly object _stderrGate = new();
    private readonly Queue<string> _stderrLines = new();
    private bool _killRequested;
    private Process? _process;
    private Task? _readLoop;
    private Task? _stderrPump;

    public ChannelReader<JsonObject> Incoming => _incoming.Reader;

    /// <summary>
    /// Human-readable reason for an unplanned child exit (exit code + stderr tail),
    /// or null when shutdown was requested by us or the process is still running.
    /// Lets the client turn a bare "disconnected" into a diagnosable message.
    /// Waits briefly for the stderr pump so the final error lines are captured.
    /// </summary>
    internal async Task<string?> DescribeUnexpectedExitAsync()
    {
        if (_killRequested || _process is null) return null;
        bool exited;
        int code;
        try { exited = _process.HasExited; code = exited ? _process.ExitCode : 0; }
        catch (InvalidOperationException) { return null; }
        if (!exited) return null;

        if (_stderrPump is not null)
        {
            try { await _stderrPump.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            catch { }
        }

        string stderr;
        lock (_stderrGate)
            stderr = string.Join("\n", _stderrLines).TrimEnd();
        return string.IsNullOrWhiteSpace(stderr)
            ? $"子进程已退出（exit code {code}），且未输出任何 stderr 信息"
            : $"子进程已退出（exit code {code}）：{stderr}";
    }

    public Task StartAsync(CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardInputEncoding = Utf8NoBom,
        };

        if (OperatingSystem.IsWindows())
        {
            ApplyWindowsCommand(startInfo, command, args ?? []);
        }
        else
        {
            startInfo.FileName = command;
            foreach (var arg in args ?? []) startInfo.ArgumentList.Add(arg);
        }

        if (env is not null)
            foreach (var (key, value) in env)
                startInfo.Environment[key] = value;

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start MCP server process: {command}");

        // Drain stderr so the child never blocks on a full pipe, keeping a short
        // tail for diagnostics when the process dies unexpectedly.
        _stderrPump = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    lock (_stderrGate)
                    {
                        _stderrLines.Enqueue(line);
                        while (_stderrLines.Count > 15) _stderrLines.Dequeue();
                    }
                }
            }
            catch { }
        }, CancellationToken.None);

        _readLoop = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var cleaned = line.TrimStart('\uFEFF');
                    try
                    {
                        if (JsonNode.Parse(cleaned) is JsonObject obj)
                            _incoming.Writer.TryWrite(obj);
                    }
                    catch (System.Text.Json.JsonException) { }
                }
            }
            catch (IOException) { }
            finally { _incoming.Writer.TryComplete(); }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <summary>
    /// CreateProcess only finds .exe files on PATH, and modern .NET refuses to run
    /// .cmd/.bat shims under UseShellExecute=false — so bare commands like `npx`
    /// (actually npx.cmd) fail with "file not found". Resolve the real shim first;
    /// batch shims launch through cmd.exe, which keeps stdin/stdout redirection intact.
    /// With /s, cmd strips the outer quote pair, so the whole command line is wrapped
    /// in one and inner quotes keep a spaced path like "C:\Program Files\..." intact.
    /// </summary>
    private static void ApplyWindowsCommand(ProcessStartInfo startInfo, string command, IReadOnlyList<string> args)
    {
        var resolved = ResolveOnPath(command);
        if (resolved is null && !command.Contains('\\') && !command.Contains('/') && !Path.HasExtension(command))
            throw new McpException(NotInstalledHint(command));

        if (resolved is not null &&
            (resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
             resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            startInfo.FileName = "cmd.exe";
            var inner = QuoteForCmd(resolved) + string.Concat(args.Select(a => " " + QuoteForCmd(a)));
            startInfo.Arguments = $"/d /s /c \"{inner}\"";
        }
        else
        {
            startInfo.FileName = resolved ?? command;
            foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        }
    }

    private static readonly char[] CmdSpecialChars = [' ', '\t', '"', '&', '<', '>', '(', ')', '^', '|'];

    private static string QuoteForCmd(string value) =>
        value.IndexOfAny(CmdSpecialChars) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    /// <summary>Full path of the command shim on PATH, or null when not found.</summary>
    internal static string? ResolveOnPath(string command)
    {
        // A command carrying a directory part is used verbatim — the OS reports a
        // precise error when it does not exist.
        if (command.Contains('\\') || command.Contains('/')) return command;

        var searchExtensions = Path.HasExtension(command)
            ? [string.Empty]
            : new[] { ".exe", ".cmd", ".bat", ".com" };
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var rawEntry in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = Environment.ExpandEnvironmentVariables(rawEntry.Trim('"'));
            if (directory.Length == 0) continue;
            foreach (var extension in searchExtensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static string NotInstalledHint(string command) => command.ToLowerInvariant() switch
    {
        "npx" or "npm" or "node" =>
            $"未在本机找到 '{command}'：尚未安装 Node.js。请从 https://nodejs.org 安装（自带 npx），安装后重启 Haoyue 再试。",
        "uvx" or "uv" or "pipx" =>
            $"未在本机找到 '{command}'：尚未安装 Python 工具链。请从 https://docs.astral.sh/uv/ 安装 uv，安装后重启 Haoyue 再试。",
        _ =>
            $"未在 PATH 中找到 '{command}'。请确认该命令已安装并可在终端直接运行，或在高级选项中改用完整路径。",
    };

    public async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        if (_process is null) throw new InvalidOperationException("Transport not started.");
        await _process.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _killRequested = true;
        _incoming.Writer.TryComplete();
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.StandardInput.Close();
                    if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) { }
            _process.Dispose();
        }
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { }
        }
    }
}

/// <summary>
/// HTTP / SSE transport: supports both Streamable HTTP (MCP 2024-11/2025 specification)
/// where JSON-RPC requests are sent via POST and the response is streamed or returned as JSON,
/// and legacy SSE transport where a long-lived GET stream receives responses.
/// </summary>
public class HttpMcpTransport : IMcpTransport
{
    private readonly string _url;
    private readonly IReadOnlyDictionary<string, string>? _headers;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly Channel<JsonObject> _incoming = Channel.CreateUnbounded<JsonObject>();
    private readonly TaskCompletionSource<string> _endpoint = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private string? _sessionId;
    private Task? _readLoop;

    /// <param name="url">Remote server base URL.</param>
    /// <param name="httpClient">Optional externally-owned client (tests).</param>
    /// <param name="headers">
    /// Static headers applied to every request (GET probe and POST). Resolved
    /// credentials such as Authorization land here; values are never logged.
    /// </param>
    /// <param name="connectTimeout">Connect timeout; defaults to 10s.</param>
    public HttpMcpTransport(
        string url,
        HttpClient? httpClient = null,
        IReadOnlyDictionary<string, string>? headers = null,
        TimeSpan? connectTimeout = null)
    {
        _url = url;
        _headers = headers;
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? CreateDefaultClient(connectTimeout ?? TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// SSE streams stay open for the whole session, so the overall timeout must
    /// remain infinite. The connect timeout is what stops an unreachable host from
    /// stalling the whole MCP reload, since a black-holed address otherwise hangs
    /// until the OS gives up.
    /// </summary>
    private static HttpClient CreateDefaultClient(TimeSpan connectTimeout)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = connectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public ChannelReader<JsonObject> Incoming => _incoming.Reader;

    public virtual async Task StartAsync(CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var token = linkedCts.Token;

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, _url);
            request.Headers.TryAddWithoutValidation("Accept", "text/event-stream, application/json");
            ApplyHeaders(request);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            ThrowIfUnauthorized(response);

            CaptureSessionId(response);

            if (response.IsSuccessStatusCode)
            {
                var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
                if (mediaType.Contains("event-stream"))
                {
                    // Server returned an SSE stream.
                    var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    _readLoop = Task.Run(async () =>
                    {
                        try
                        {
                            await foreach (var sse in Providers.SseReader.ReadAsync(stream, _lifetime.Token).ConfigureAwait(false))
                            {
                                if (sse.Event == "endpoint")
                                {
                                    _endpoint.TrySetResult(new Uri(new Uri(_url), sse.Data).ToString());
                                }
                                else
                                {
                                    DispatchSseData(sse.Data);
                                }
                            }
                        }
                        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException) { }
                        // NOTE: Do not complete _incoming here because Streamable HTTP servers may finish GET immediately.
                    }, CancellationToken.None);

                    // Wait briefly for endpoint announcement if server announces endpoint via legacy SSE event.
                    var completed = await Task.WhenAny(_endpoint.Task, Task.Delay(3000, token)).ConfigureAwait(false);
                    if (completed != _endpoint.Task) _endpoint.TrySetResult(_url);
                    return;
                }
                else if (mediaType.Contains("json"))
                {
                    // Some servers return a JSON greeting with endpoint metadata (e.g. {"endpoint": "POST /mcp"})
                    var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (JsonNode.Parse(body) is JsonObject json)
                        {
                            if (json["endpoint"]?.GetValue<string>() is { } epStr)
                            {
                                var resolvedEndpoint = epStr.StartsWith("POST ", StringComparison.OrdinalIgnoreCase)
                                    ? epStr[5..].Trim()
                                    : epStr.Trim();
                                _endpoint.TrySetResult(new Uri(new Uri(_url), resolvedEndpoint).ToString());
                            }
                        }
                    }
                    catch { }
                }
            }
        }
        catch (McpUnauthorizedException)
        {
            // The server demanded authorization on the GET probe: do not swallow it
            // into the POST fallback — the OAuth assistant needs the exception.
            throw;
        }
        catch (Exception)
        {
            // Server might only accept POST /mcp and reject GET with 405/400.
            // Fall back directly to POSTing to _url.
        }

        _endpoint.TrySetResult(_url);
    }

    public virtual async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var token = linkedCts.Token;

        var endpoint = await _endpoint.Task.WaitAsync(token).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        ApplyHeaders(request);
        if (!string.IsNullOrWhiteSpace(_sessionId))
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        ThrowIfUnauthorized(response);
        response.EnsureSuccessStatusCode();

        CaptureSessionId(response);

        var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        if (mediaType.Contains("event-stream"))
        {
            // Streamable HTTP: response is an SSE stream (e.g. StarLife, MCP 2024-11/2025 spec)
            var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await foreach (var sse in Providers.SseReader.ReadAsync(stream, token).ConfigureAwait(false))
            {
                if (sse.Event == "endpoint")
                {
                    _endpoint.TrySetResult(new Uri(new Uri(_url), sse.Data).ToString());
                }
                else
                {
                    DispatchSseData(sse.Data);
                }
            }
        }
        else if (mediaType.Contains("json"))
        {
            // Direct HTTP POST: response is a JSON-RPC message
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(body))
            {
                DispatchJson(body);
            }
        }
    }

    private void ApplyHeaders(HttpRequestMessage request)
    {
        if (_headers is null) return;
        foreach (var (key, value) in _headers)
        {
            if (!string.IsNullOrWhiteSpace(key))
                request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    private void ThrowIfUnauthorized(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.Unauthorized) return;
        var challenge = response.Headers.WwwAuthenticate.FirstOrDefault()?.ToString();
        throw new McpUnauthorizedException(_url, challenge);
    }

    private void CaptureSessionId(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
        {
            var id = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(id)) _sessionId = id;
        }
    }

    private void DispatchSseData(string data)
    {
        if (string.IsNullOrWhiteSpace(data)) return;
        DispatchJson(data);
    }

    private void DispatchJson(string text)
    {
        try
        {
            var node = JsonNode.Parse(text);
            if (node is JsonObject obj)
            {
                _incoming.Writer.TryWrite(obj);
            }
            else if (node is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is JsonObject itemObj)
                        _incoming.Writer.TryWrite(itemObj);
                }
            }
        }
        catch (System.Text.Json.JsonException) { }
    }

    private int _disposed;

    public virtual async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        _incoming.Writer.TryComplete();
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { }
        }
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
        _lifetime.Dispose();
    }
}

/// <summary>
/// Backwards-compatible SSE transport alias for HttpMcpTransport.
/// </summary>
public sealed class SseMcpTransport(string url) : HttpMcpTransport(url);
