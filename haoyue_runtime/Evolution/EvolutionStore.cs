using Haoyue.Runtime.Data;
using Microsoft.Data.Sqlite;

namespace Haoyue.Runtime.Evolution;

/// <summary>Lifecycle of one defect fingerprint in the evolution engine.</summary>
public static class EvolutionStatus
{
    /// <summary>A candidate skill draft passed experiments and awaits human review (P3).</summary>
    public const string Candidate = "candidate";
    /// <summary>Reflection concluded no skill-layer action is warranted; fingerprint is closed.</summary>
    public const string NoAction = "no-action";
    /// <summary>Human adopted the candidate into the live skills directory (P3).</summary>
    public const string Adopted = "adopted";
    /// <summary>Human discarded the candidate (P3).</summary>
    public const string Rejected = "rejected";
    /// <summary>The reflection turn itself failed; the fingerprint is retried on the next run.</summary>
    public const string Failed = "failed";

    /// <summary>Only a failed attempt lets a later reflection run retry the fingerprint.</summary>
    public static bool BlocksRetry(string status) => status != Failed;
}

/// <summary>One row of the evolution decision log.</summary>
public sealed record EvolutionRecord(
    string Fingerprint,
    string Kind,
    string Status,
    string? CandidateDir,
    string? SessionId,
    string? Summary,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// SQLite-backed decision log for the evolution engine. Doubles as the
/// anti-self-feeding guard: fingerprints already recorded (any status except
/// <see cref="EvolutionStatus.Failed"/>) are skipped by future reflection runs,
/// and reflection-turn session ids recorded here are excluded from defect
/// aggregation so the engine cannot chase its own failures.
/// </summary>
public sealed class EvolutionStore(HaoyueDatabase database)
{
    public EvolutionRecord? Get(string fingerprint)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT fingerprint, kind, status, candidate_dir, session_id, summary, created_at, updated_at " +
            "FROM evolution_log WHERE fingerprint = $fp;";
        command.Parameters.AddWithValue("$fp", fingerprint);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    /// <summary>Inserts or replaces the record for its fingerprint.</summary>
    public void Record(EvolutionRecord record)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO evolution_log
                (fingerprint, kind, status, candidate_dir, session_id, summary, created_at, updated_at)
            VALUES ($fp, $kind, $status, $dir, $session, $summary, $created, $updated);
            """;
        command.Parameters.AddWithValue("$fp", record.Fingerprint);
        command.Parameters.AddWithValue("$kind", record.Kind);
        command.Parameters.AddWithValue("$status", record.Status);
        command.Parameters.AddWithValue("$dir", (object?)record.CandidateDir ?? DBNull.Value);
        command.Parameters.AddWithValue("$session", (object?)record.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$summary", (object?)record.Summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", record.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", record.UpdatedAt.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>Moves a fingerprint to a decision status (P3 approval flow); returns the updated row.</summary>
    public EvolutionRecord? Decide(string fingerprint, string status, string? summary = null)
    {
        var existing = Get(fingerprint);
        if (existing is null) return null;
        var updated = existing with
        {
            Status = status,
            Summary = summary ?? existing.Summary,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        Record(updated);
        return updated;
    }

    public IReadOnlyList<EvolutionRecord> List(string? status = null)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT fingerprint, kind, status, candidate_dir, session_id, summary, created_at, updated_at " +
            "FROM evolution_log" + (status is null ? "" : " WHERE status = $status") + " ORDER BY updated_at DESC;";
        if (status is not null) command.Parameters.AddWithValue("$status", status);
        var records = new List<EvolutionRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) records.Add(ReadRow(reader));
        return records;
    }

    /// <summary>
    /// Session ids of every reflection turn ever run. The defect aggregator excludes
    /// events attributed to these sessions so reflection cannot feed on itself.
    /// </summary>
    public IReadOnlyList<string> ReflectionSessionIds()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT session_id FROM evolution_log WHERE session_id IS NOT NULL;";
        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    private static EvolutionRecord ReadRow(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        DateTimeOffset.Parse(reader.GetString(6)),
        DateTimeOffset.Parse(reader.GetString(7)));
}
