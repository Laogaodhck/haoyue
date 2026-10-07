using System.IO.Pipes;
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
/// </summary>
public static class DaemonConfigBridge
{
    private const int ConnectTimeoutMs = 1500;
    private const int OverallTimeoutMs = 4000;

    /// <summary>
    /// Tries to delegate the pending config changes to the running daemon.
    /// Returns true when the daemon accepted the patch (the caller must NOT also
    /// write locally — the daemon's merged result is authoritative and the store
    /// has been reloaded from disk). Returns false when the daemon is unreachable
    /// or rejected the request; the caller then saves locally as before.
    /// </summary>
    public static bool TryDelegateSave(IConfigStore store)
    {
        var patch = store.CaptureDirtyPatch();
        if (patch is null || patch.Count == 0)
        {
            // Nothing changed relative to the baseline: nothing to persist anywhere.
            return true;
        }

        try
        {
            var task = Task.Run(() => DelegateOverPipe(store, patch));
            if (!task.Wait(OverallTimeoutMs)) return false; // daemon accepted the connection but stalled
            return task.Result;
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    private static bool DelegateOverPipe(IConfigStore store, JsonObject patch)
    {
        using var pipe = new NamedPipeClientStream(".", DaemonServer.PipeName, PipeDirection.InOut);
        pipe.Connect(ConnectTimeoutMs);
        using var reader = new StreamReader(pipe);
        using var writer = new StreamWriter(pipe) { AutoFlush = true, NewLine = "\n" };

        var handshake = new JsonObject
        {
            ["id"] = 1,
            ["method"] = "handshake",
            ["params"] = new JsonObject { ["token"] = DaemonAuth.LoadOrCreateToken() },
        };
        writer.WriteLine(handshake.ToJsonString());
        if (ReadResponse(reader, 1)?["event"]?.GetValue<string>() != "result") return false;

        var request = new JsonObject
        {
            ["id"] = 2,
            ["method"] = "config.save",
            ["params"] = new JsonObject { ["fields"] = patch },
        };
        writer.WriteLine(request.ToJsonString());
        if (ReadResponse(reader, 2)?["event"]?.GetValue<string>() != "result") return false;

        // The daemon merged and persisted the patch; reload so the in-memory config,
        // the baseline and the file converge (no stale local snapshot left behind).
        store.Reload();
        return true;
    }

    private static JsonObject? ReadResponse(StreamReader reader, long id)
    {
        // Broadcasts from other clients may interleave; skip lines until our id answers.
        while (reader.ReadLine() is { } line)
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
