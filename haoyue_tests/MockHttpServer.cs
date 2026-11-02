using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Haoyue.Tests;

/// <summary>
/// 顺序应答的本地 mock HTTP 服务器：每个请求按队列依次取一个响应
/// （视频：创建任务 JSON → 轮询 JSON → MP4 下载；图像/语音：单发）。
/// 记录请求行/头/体，供断言路径与关键字段。
/// </summary>
internal sealed class MockHttpServer : IDisposable
{
    public sealed record MockResponse(string ContentType, byte[] Body)
    {
        public static MockResponse Json(JsonObject payload) =>
            new("application/json", Encoding.UTF8.GetBytes(payload.ToJsonString()));

        public static MockResponse Bytes(byte[] body, string contentType = "video/mp4") =>
            new(contentType, body);
    }

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Queue<MockResponse> _responses = new();
    private readonly object _logGate = new();
    private readonly List<string> _requestLines = [];
    private readonly List<string> _requestHeaders = [];
    private readonly List<string> _requestBodies = [];
    private int _handled;

    public int Port { get; }

    /// <summary>形如 "POST /v1/contents/generations/tasks HTTP/1.1" 的请求行。</summary>
    public IReadOnlyList<string> RequestLines
    {
        get { lock (_logGate) return _requestLines.ToArray(); }
    }

    /// <summary>完整请求头（含 Authorization / X-Api-Key 等，供断言凭据头）。</summary>
    public IReadOnlyList<string> RequestHeaders
    {
        get { lock (_logGate) return _requestHeaders.ToArray(); }
    }

    /// <summary>请求体（仅 POST；GET 为空串）。</summary>
    public IReadOnlyList<string> RequestBodies
    {
        get { lock (_logGate) return _requestBodies.ToArray(); }
    }

    public MockHttpServer(params MockResponse[] responses)
    {
        foreach (var response in responses) _responses.Enqueue(response);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    /// <summary>追加响应（服务器启动后仍可入队；端口此时已确定，可引用 Port）。</summary>
    public void Enqueue(MockResponse response)
    {
        lock (_responses)
        {
            _responses.Enqueue(response);
        }
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            var (headerText, body) = await ReadRequest(stream);
            var lines = headerText.Split("\r\n");
            var requestLine = lines.Length > 0 ? lines[0] : "";
            var contentLength = 0;
            foreach (var line in lines.Skip(1))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line[(line.IndexOf(':') + 1)..].Trim(), out contentLength);
            }
            if (contentLength > 0)
            {
                var buffer = new byte[contentLength];
                var read = 0;
                while (read < contentLength)
                    read += await stream.ReadAsync(buffer.AsMemory(read, contentLength - read));
                body = Encoding.UTF8.GetString(buffer, 0, read);
            }

            lock (_logGate)
            {
                _requestLines.Add(requestLine);
                _requestHeaders.Add(headerText);
                _requestBodies.Add(body);
            }

            var index = Interlocked.Increment(ref _handled) - 1;
            MockResponse response;
            lock (_responses)
            {
                response = index < _responses.Count
                    ? _responses.ElementAt(index)
                    : MockResponse.Json(new JsonObject());
            }
            var header = $"HTTP/1.1 200 OK\r\nContent-Type: {response.ContentType}\r\n" +
                         $"Content-Length: {response.Body.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(header));
            await stream.WriteAsync(response.Body);
        }
        catch (Exception)
        {
            // 客户端提前断开等：忽略。
        }
        finally
        {
            client.Dispose();
        }
    }

    private static async Task<(string Headers, string Body)> ReadRequest(NetworkStream stream)
    {
        var builder = new StringBuilder();
        var buffer = new byte[1];
        while (builder.Length < 64 * 1024)
        {
            var n = await stream.ReadAsync(buffer.AsMemory());
            if (n == 0) break;
            builder.Append((char)buffer[0]);
            if (builder.ToString().EndsWith("\r\n\r\n")) break;
        }
        var text = builder.ToString();
        var separator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return separator < 0
            ? (text, "")
            : (text[..separator], text[(separator + 4)..]);
    }

    public void Dispose() => _cts.Cancel();
}
