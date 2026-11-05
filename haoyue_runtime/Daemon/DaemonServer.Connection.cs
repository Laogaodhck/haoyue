using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Evolution;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Experts;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Scheduling;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Daemon;

/// <summary>DaemonServer 的按域分部类（成员仅搬运自 DaemonServer.cs，逻辑未改动）。</summary>
public sealed partial class DaemonServer
{
    private async Task ServeNamedPipeAsync(CancellationToken ct)
    {
        var pipe = CreatePipeServer();
        await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
        _ = Task.Run(() => RunPipeConnectionAsync(pipe, ct));
    }

    internal static string PipeUserSuffix() => SanitizePipeUser(Environment.GetEnvironmentVariable("USERNAME"));

    /// <summary>Keeps only characters that are legal in a pipe name; empty input falls back to "local".</summary>
    internal static string SanitizePipeUser(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "local";
        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw)
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '-');
        return builder.ToString();
    }

    /// <summary>
    /// Named pipes default to an ACL that lets Everyone connect. Restrict access to
    /// the current user and SYSTEM so another local account cannot open our endpoint;
    /// the handshake token stays the actual authentication on top of this.
    /// </summary>
    internal static NamedPipeServerStream CreatePipeServer(string? pipeName = null)
    {
        pipeName ??= PipeName;
        if (!OperatingSystem.IsWindows())
        {
            return new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }

        var security = new PipeSecurity();
        // CreateNewInstance 是并发第二个服务端实例的前提（MaxAllowedServerInstances），
        // 缺了它会在已有一条活跃连接时抛 UnauthorizedAccessException（1.3.15 回归）。
        security.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: 0, security);
    }

    private async Task ServeUnixSocketLoopAsync(CancellationToken ct)
    {
        var socketDir = Path.GetDirectoryName(SocketPath);
        if (!string.IsNullOrEmpty(socketDir))
        {
            if (!Directory.Exists(socketDir)) Directory.CreateDirectory(socketDir);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(socketDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        if (File.Exists(SocketPath))
        {
            try { File.Delete(SocketPath); } catch { }
        }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        // 0600：端点只允许当前用户连接（目录 0700 是第二道闸）。
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(128);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var socket = await listener.AcceptAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => RunSocketConnectionAsync(socket, ct), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                Console.Error.WriteLine($"Daemon socket accept error: {ex.Message}");
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }

        if (File.Exists(SocketPath))
        {
            try { File.Delete(SocketPath); } catch { }
        }
    }

    private async Task RunPipeConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            try { await HandleConnectionAsync(pipe, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { Console.Error.WriteLine($"Daemon connection failed: {ex.Message}"); }
        }
    }

    private async Task RunSocketConnectionAsync(Socket socket, CancellationToken ct)
    {
        using (socket)
        await using (var stream = new NetworkStream(socket, ownsSocket: false))
        {
            try { await HandleConnectionAsync(stream, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { Console.Error.WriteLine($"Daemon connection failed: {ex.Message}"); }
        }
    }

    private sealed class ConnectionContext(
        StreamWriter writer,
        SemaphoreSlim writerGate,
        CancellationToken connectionCt)
    {
        public StreamWriter Writer { get; } = writer;
        public SemaphoreSlim WriterGate { get; } = writerGate;
        public CancellationToken ConnectionCt { get; } = connectionCt;
        public ConcurrentDictionary<long, ActiveTurn> ActiveTurns { get; } = new();
        public AgentSession? LegacySession;
    }

    internal async Task HandleConnectionAsync(Stream stream, CancellationToken ct, bool authenticate = true)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        // 握手与常规请求共用同一个读取器：它内部会预读，拆成两个实例会丢字符。
        var lines = new BoundedLineReader(reader);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 64 * 1024, leaveOpen: true)
        {
            AutoFlush = true,
        };
        using var writerGate = new SemaphoreSlim(1, 1);

        // Handshake gate: when a token is configured, the very first message must
        // authenticate. Unauthenticated connections never receive broadcasts and
        // cannot dispatch any method; failures drop the link immediately.
        // 局域网连接在 LanAccessServer 里已完成 HMAC 挑战应答，这里不再重复令牌握手。
        if (authenticate
            && _handshakeToken is not null
            && !await AuthenticateConnectionAsync(lines, writer, writerGate, ct).ConfigureAwait(false))
        {
            return;
        }

        var clientId = RegisterClient(writer, writerGate);
        var context = new ConnectionContext(writer, writerGate, ct);
        _connections[clientId] = context;

        try
        {
            while (true)
            {
                var read = await lines.ReadLineAsync(MaxRequestLineLength, ct).ConfigureAwait(false);
                if (read.Line is not { } line)
                {
                    if (read.TooLong)
                    {
                        await WriteAsync(writer, writerGate, 0, "error",
                            $"Request exceeds the {MaxRequestLineLength / 1024 / 1024} MB line limit",
                            ct, code: DaemonErrorCode.InvalidRequest).ConfigureAwait(false);
                    }
                    break;
                }

                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonObject? request;
                try { request = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { request = null; }

                if (request is null)
                {
                    await WriteAsync(writer, writerGate, 0, "error", "Invalid JSON request", ct, code: DaemonErrorCode.InvalidRequest).ConfigureAwait(false);
                    continue;
                }

                long id;
                string method;
                try
                {
                    id = request["id"]?.GetValue<long>() ?? 0;
                    method = request["method"]?.GetValue<string>() ?? "";
                }
                catch (InvalidOperationException)
                {
                    await WriteAsync(writer, writerGate, 0, "error", "Request id and method have invalid types", ct, code: DaemonErrorCode.InvalidRequest).ConfigureAwait(false);
                    continue;
                }

                // Periodically clean up completed turns
                foreach (var (turnId, turn) in context.ActiveTurns)
                {
                    if (turn.Task?.IsCompleted == true)
                    {
                        if (turn.Task is not null) await ObserveAsync(turn.Task).ConfigureAwait(false);
                        turn.Cancellation.Dispose();
                        context.ActiveTurns.TryRemove(turnId, out _);
                    }
                }

                // Non-blocking concurrent dispatch: reader immediately loops back to read the next line!
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await DispatchMethodAsync(request, id, method, context).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // 方法名命中契约、但执行期抛错：固定回 internalError，并在 details.method
                        // 带上方法名。客户端据此区分「版本不匹配（unknownMethod）」与「实现故障」，
                        // 不再只能对 data 文本做字符串匹配。
                        await WriteAsync(context.Writer, context.WriterGate, id, "error", FailureText(method, ex), context.ConnectionCt,
                            details: new JsonObject
                            {
                                ["code"] = DaemonErrorCode.InternalError.ToWire(),
                                ["method"] = method,
                            }).ConfigureAwait(false);
                    }
                }, ct);
            }
        }
        finally
        {
            UnregisterClient(clientId);
            _connections.TryRemove(clientId, out _);
            foreach (var turn in context.ActiveTurns.Values) turn.Cancellation.Cancel();
            foreach (var turn in context.ActiveTurns.Values)
                if (turn.Task is not null) await ObserveAsync(turn.Task).ConfigureAwait(false);
            foreach (var turn in context.ActiveTurns.Values) turn.Cancellation.Dispose();
            context.ActiveTurns.Clear();
        }
    }
}
