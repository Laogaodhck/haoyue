using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;

namespace Haoyue.Runtime.Daemon;

/// <summary>
/// A4 single-writer convergence for ~/.haoyue/config.json: when a CLI process wants to
/// persist a config change and the daemon is running, the change is delegated to the
/// daemon over the control pipe (<c>config.save</c> RPC carrying a recursive dirty
/// patch) so only one process ever writes the file. When the daemon is offline the CLI
/// falls back to its own ConfigStore.Save() — same file, same atomic-write/merge
/// guarantees, no behavioral regression for daemon-less usage.
///
/// 超时语义（M-7）：整条委派链路由一个 <see cref="CancellationTokenSource"/> 封顶，
/// 超时即取消连接、写帧与读帧（并主动关闭管道打断挂起的读）。旧实现用
/// <c>Task.Wait(4000)</c>：超时后调用方回落本地 Save，而遗弃的后台线程仍会继续
/// 跑到 <c>store.Reload()</c>，把刚写入的本地改动覆盖回旧快照；daemon 半死时
/// <c>ReadLine</c> 无读超时则让该线程永久挂住。
/// </summary>
public static class DaemonConfigBridge
{
    private const int ConnectTimeoutMs = 1500;
    private const int OverallTimeoutMs = 4000;

    /// <summary>
    /// Tries to delegate the pending config changes to the running daemon.
    /// Returns true when the daemon accepted the patch (the caller must NOT also
    /// write locally — the daemon's merged result is authoritative and the store
    /// has been reloaded from disk). Returns false when the daemon is unreachable,
    /// stalled, or rejected the request; the caller then saves locally as before.
    /// </summary>
    public static bool TryDelegateSave(IConfigStore store)
    {
        var patch = store.CaptureDirtyPatch();
        if (patch is null || patch.Count == 0)
        {
            // Nothing changed relative to the baseline: nothing to persist anywhere.
            return true;
        }

        using var cts = new CancellationTokenSource(OverallTimeoutMs);
        try
        {
            return DelegateOverPipeAsync(store, patch, cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or IOException or ObjectDisposedException
                or JsonException or UnauthorizedAccessException or TimeoutException)
        {
            // 委派不可用：调用方走本地保存。取消已贯穿到所有 I/O，不存在遗留线程。
            return false;
        }
    }

    private static async Task<bool> DelegateOverPipeAsync(
        IConfigStore store, JsonObject patch, CancellationToken ct)
    {
        await using var pipe = new NamedPipeClientStream(
            ".", DaemonServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        if (!await TryConnectAsync(pipe, ct).ConfigureAwait(false)) return false;

        // 兜底：即使底层流忽略 CancellationToken（某些平台/管道实现），关闭管道也能
        // 让挂起的读立刻失败，保证后台任务不会越过超时继续跑。
        using var abortOnCancel = ct.Register(static state =>
        {
            try { ((NamedPipeClientStream)state!).Dispose(); }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
        }, pipe);

        using var reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        var handshake = new JsonObject
        {
            ["id"] = 1,
            ["method"] = "handshake",
            ["params"] = new JsonObject { ["token"] = DaemonAuth.LoadOrCreateToken() },
        };
        await writer.WriteLineAsync(handshake.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
        if (!await IsResultAsync(reader, 1, ct).ConfigureAwait(false)) return false;

        var request = new JsonObject
        {
            ["id"] = 2,
            ["method"] = "config.save",
            ["params"] = new JsonObject { ["fields"] = patch },
        };
        await writer.WriteLineAsync(request.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
        if (!await IsResultAsync(reader, 2, ct).ConfigureAwait(false)) return false;

        // The daemon merged and persisted the patch; reload so the in-memory config,
        // the baseline and the file converge (no stale local snapshot left behind).
        ct.ThrowIfCancellationRequested();
        store.Reload();
        return true;
    }

    private static async Task<bool> TryConnectAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(ConnectTimeoutMs);
        try
        {
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // 仅是连接超时（daemon 不在此机器/未启动），整体超时交给上层。
        }
    }

    private static async Task<bool> IsResultAsync(StreamReader reader, long id, CancellationToken ct)
        => await ReadResponseAsync(reader, id, ct).ConfigureAwait(false) is { } response
           && response["event"]?.GetValue<string>() == "result";

    private static async Task<JsonObject?> ReadResponseAsync(StreamReader reader, long id, CancellationToken ct)
    {
        // Broadcasts from other clients may interleave; skip lines until our id answers.
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            try
            {
                if (JsonNode.Parse(line) is not JsonObject obj) continue;
                if ((obj["id"]?.GetValue<long>() ?? -1) == id) return obj;
            }
            catch (JsonException)
            {
                // Skip malformed/non-JSON lines; the protocol is line-delimited JSON.
            }
        }
        return null;
    }
}
