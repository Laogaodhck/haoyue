using System.Text.Json;
using Haoyue.Runtime.Configuration;

namespace Haoyue.Runtime.Daemon;

/// <summary>One in-flight agent turn recorded in the crash-marker journal.</summary>
public sealed record ActiveTurnRecord(
    string SessionId,
    string Scope,
    string WorkspaceRoot,
    bool IsGlobal,
    DateTimeOffset StartedAt);

/// <summary>
/// Crash marker for in-flight agent turns. The daemon rewrites the file whenever a
/// turn starts or ends; a residue after a process death tells the next start which
/// sessions were mid-turn and deserve an interruption notice. Best-effort by design:
/// any I/O failure degrades to "no recovery semantics", never breaks the turn.
/// </summary>
internal static class ActiveTurnJournal
{
    public static void Write(string path, IReadOnlyList<ActiveTurnRecord> records)
    {
        try
        {
            if (records.Count == 0)
            {
                Clear(path);
                return;
            }
            var payload = JsonSerializer.Serialize(records, HaoyueJsonContext.Compact.ListActiveTurnRecord);
            var temp = path + ".tmp";
            File.WriteAllText(temp, payload);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The journal is an optimization over clean startup, never a requirement.
        }
    }

    public static List<ActiveTurnRecord> Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return [];
            return JsonSerializer.Deserialize(json, HaoyueJsonContext.Compact.ListActiveTurnRecord) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public static void Clear(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
