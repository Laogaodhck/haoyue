using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Haoyue.Runtime.Tools.HeadlessBrowser;

/// <summary>
/// 极简 Chrome DevTools Protocol 客户端（WebSocket + flatten 模式）：一次连接承载
/// browser 级与 page 级会话消息，按 id 关联响应、按 method 缓存事件。只实现 browser
/// 工具所需的调用面（Call / WaitForEvent），不追求协议全量覆盖。
/// </summary>
internal sealed class CdpClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly ConcurrentQueue<(string Method, string? SessionId, JsonObject Params)> _events = new();
    private readonly SemaphoreSlim _eventSignal = new(0, int.MaxValue);
    private long _nextId;
    private Task? _receiveLoop;

    /// <summary>Single devtools event surfaced for tests/diagnostics.</summary>
    public bool IsConnected => _ws.State == WebSocketState.Open;

    public async Task ConnectAsync(string wsEndpoint, CancellationToken ct)
    {
        await _ws.ConnectAsync(new Uri(wsEndpoint), ct).ConfigureAwait(false);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(CancellationToken.None));
    }

    /// <summary>Sends a command and awaits its matching response. Throws TimeoutException on deadline.</summary>
    public async Task<JsonNode?> CallAsync(string method, JsonObject? parameters, string? sessionId, TimeSpan timeout, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, tcs))
            throw new InvalidOperationException("CDP message id collision.");

        var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? [] };
        if (sessionId is not null) message["sessionId"] = sessionId;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        using var registration = timeoutCts.Token.Register(() => tcs.TrySetException(new TimeoutException($"CDP call '{method}' timed out after {timeout.TotalSeconds:0}s.")));

        await SendAsync(message, CancellationToken.None).ConfigureAwait(false);
        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Awaits one occurrence of an event (newest buffered first), or registers no
    /// waiter and falls back to the signaled queue — avoids lost events between the
    /// queue check and the wait. Throws TimeoutException on deadline.
    /// </summary>
    public async Task<JsonObject> WaitForEventAsync(string method, string? sessionId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            if (TryTakeEvent(method, sessionId, out var match)) return match;

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"CDP event '{method}' not received within {timeout.TotalSeconds:0}s.");

            var signaled = await _eventSignal.WaitAsync(remaining, ct).ConfigureAwait(false);
            if (!signaled) continue; // re-check deadline on next pass
        }
    }

    private bool TryTakeEvent(string method, string? sessionId, out JsonObject match)
    {
        match = null!;
        var kept = new List<(string, string?, JsonObject)>();
        var found = false;
        while (_events.TryDequeue(out var evt))
        {
            if (!found && evt.Method == method && (sessionId is null || evt.SessionId == sessionId))
            {
                match = evt.Params;
                found = true;
                continue;
            }
            kept.Add(evt);
        }
        foreach (var evt in kept) _events.Enqueue(evt);
        return found;
    }

    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                while (true)
                {
                    var result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
                        break;
                    }
                    message.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage) break;
                }
                if (_ws.State != WebSocketState.Open) break;

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(Encoding.UTF8.GetString(message.ToArray()));
                }
                catch (System.Text.Json.JsonException)
                {
                    continue; // malformed frame — skip, keep the loop alive
                }
                if (node is not JsonObject obj) continue;

                if (obj["id"] is { } idNode)
                {
                    var id = idNode.GetValue<long>();
                    if (_pending.TryRemove(id, out var tcs))
                        tcs.TrySetResult(obj["result"] ?? obj["error"]);
                }
                else if (obj["method"] is { } methodNode)
                {
                    var evt = (
                        methodNode.GetValue<string>(),
                        obj["sessionId"]?.GetValue<string>(),
                        obj["params"] as JsonObject ?? []);
                    _events.Enqueue(evt);
                    _eventSignal.Release();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }

        // 断连：唤醒所有等待者，避免调用方挂在超时上。
        foreach (var tcs in _pending.Values)
            tcs.TrySetException(new InvalidOperationException("CDP connection closed."));
        _pending.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        _ws.Dispose();
        foreach (var tcs in _pending.Values)
            tcs.TrySetException(new InvalidOperationException("CDP connection closed."));
        _pending.Clear();
    }
}
