using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Sessions;

/// <summary>A live conversation persisted in the central Haoyue SQLite database.</summary>
public sealed class AgentSession
{
    public required SessionHeader Header { get; init; }
    public List<ChatMessage> Messages { get; } = [];
    public required string FilePath { get; init; }
    internal string Scope { get; init; } = "";
}

public interface ISessionStore
{
    AgentSession Create(
        WorkspaceInfo workspace,
        ReasoningLevel reasoningLevel = ReasoningLevel.High,
        bool networkEnabled = true,
        string? expertId = null);
    AgentSession Fork(
        WorkspaceInfo workspace,
        string sourceSessionId,
        int keepMessageCount = 0,
        string? title = null);
    AgentSession? Load(WorkspaceInfo workspace, string sessionId);
    AgentSession? LoadLatest(WorkspaceInfo workspace);
    IReadOnlyList<SessionHeader> List(WorkspaceInfo workspace, bool includeArchived = false);
    /// <summary>
    /// Full-text search across session titles and message bodies. Message payloads are
    /// stored as JSON with non-ASCII characters escaped (\uXXXX), so the query is matched
    /// in both raw form (titles) and JSON-escaped form (bodies). Results are ranked by
    /// hit count, then recency.
    /// </summary>
    IReadOnlyList<SessionSearchHit> Search(
        WorkspaceInfo workspace, string query, bool includeArchived = false, int limit = 20);
    void Append(AgentSession session, ChatMessage message);
    /// <summary>Rewrites a session's persisted history (used by context compaction).</summary>
    void ReplaceHistory(AgentSession session, IReadOnlyList<ChatMessage> messages);
    /// <summary>Drops persisted messages after the first N (used by "regenerate").</summary>
    void Truncate(WorkspaceInfo workspace, string sessionId, int keepMessageCount);
    SessionHeader UpdateMetadata(
        WorkspaceInfo workspace,
        string sessionId,
        string? title = null,
        bool? archived = null,
        ReasoningLevel? reasoningLevel = null,
        bool? networkEnabled = null,
        string? expertId = null);
    SessionHeader RecordUsage(WorkspaceInfo workspace, string sessionId, SessionUsage usage);
    void Delete(WorkspaceInfo workspace, string sessionId);
    void DeleteAll(WorkspaceInfo workspace);
}

/// <summary>
/// One <see cref="SessionStore.Search"/> result. MatchCount = number of matching
/// messages (1 if only the title matched), driving the hit-count-first ranking.
/// </summary>
public sealed record SessionSearchHit(SessionHeader Header, int MatchCount);

public sealed class SessionStore : ISessionStore
{
    private static readonly ConcurrentDictionary<string, Lock> SessionGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Lock> MigrationGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HaoyueDatabase _database;

    public SessionStore() : this(new HaoyueDatabase()) { }

    public SessionStore(string databaseFile) : this(new HaoyueDatabase(databaseFile)) { }

    public SessionStore(HaoyueDatabase database) => _database = database;

    public AgentSession Create(
        WorkspaceInfo workspace,
        ReasoningLevel reasoningLevel = ReasoningLevel.High,
        bool networkEnabled = true,
        string? expertId = null)
    {
        EnsureLegacyImported(workspace);
        var id = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var now = DateTimeOffset.UtcNow;
        var header = new SessionHeader
        {
            Id = id,
            Workspace = workspace.IsGlobal ? null : Path.GetFullPath(workspace.Root),
            ReasoningLevel = reasoningLevel,
            NetworkEnabled = networkEnabled,
            ExpertId = expertId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var scope = HaoyueDatabase.ScopeKey(workspace);

        using var connection = _database.OpenConnection();
        InsertSession(connection, null, scope, header);
        return NewSession(header, scope);
    }

    public AgentSession Fork(
        WorkspaceInfo workspace,
        string sourceSessionId,
        int keepMessageCount = 0,
        string? title = null)
    {
        ValidateSessionId(sourceSessionId);
        EnsureLegacyImported(workspace);
        var scope = HaoyueDatabase.ScopeKey(workspace);

        lock (GateFor(scope, sourceSessionId))
        {
            using var connection = _database.OpenConnection();
            var sourceHeader = ReadHeader(connection, scope, sourceSessionId)
                ?? throw new FileNotFoundException($"Session not found: {sourceSessionId}", _database.FilePath);

            var newId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
            var now = DateTimeOffset.UtcNow;
            var branchTitle = string.IsNullOrWhiteSpace(title)
                ? (sourceHeader.Title != null ? $"{sourceHeader.Title} (分支)" : "分支任务")
                : title;

            var newHeader = new SessionHeader
            {
                Id = newId,
                Workspace = sourceHeader.Workspace,
                Title = branchTitle,
                Archived = false,
                ReasoningLevel = sourceHeader.ReasoningLevel,
                NetworkEnabled = sourceHeader.NetworkEnabled,
                ExpertId = sourceHeader.ExpertId,
                CreatedAt = now,
                UpdatedAt = now,
            };

            using var transaction = connection.BeginTransaction(deferred: false);
            InsertSession(connection, transaction, scope, newHeader);

            using (var copyCmd = connection.CreateCommand())
            {
                copyCmd.Transaction = transaction;
                if (keepMessageCount > 0)
                {
                    copyCmd.CommandText = """
                        INSERT INTO messages(scope, session_id, payload_json, timestamp)
                        SELECT $scope, $newSessionId, payload_json, timestamp
                        FROM messages
                        WHERE scope = $scope AND session_id = $sourceSessionId
                        ORDER BY id
                        LIMIT $limit;
                        """;
                    copyCmd.Parameters.AddWithValue("$limit", keepMessageCount);
                }
                else
                {
                    copyCmd.CommandText = """
                        INSERT INTO messages(scope, session_id, payload_json, timestamp)
                        SELECT $scope, $newSessionId, payload_json, timestamp
                        FROM messages
                        WHERE scope = $scope AND session_id = $sourceSessionId
                        ORDER BY id;
                        """;
                }
                copyCmd.Parameters.AddWithValue("$scope", scope);
                copyCmd.Parameters.AddWithValue("$sourceSessionId", sourceSessionId);
                copyCmd.Parameters.AddWithValue("$newSessionId", newId);
                copyCmd.ExecuteNonQuery();
            }

            transaction.Commit();

            var session = NewSession(newHeader, scope);
            using var readCmd = connection.CreateCommand();
            readCmd.CommandText = """
                SELECT payload_json FROM messages
                WHERE scope = $scope AND session_id = $sessionId
                ORDER BY id;
                """;
            readCmd.Parameters.AddWithValue("$scope", scope);
            readCmd.Parameters.AddWithValue("$sessionId", newId);
            using var reader = readCmd.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    var record = JsonSerializer.Deserialize(
                        reader.GetString(0), HaoyueJsonContext.Compact.SessionMessage);
                    if (record is not null) session.Messages.Add(ToMessage(record));
                }
                catch (JsonException) { }
            }
            return session;
        }
    }

    public AgentSession? Load(WorkspaceInfo workspace, string sessionId)
    {
        ValidateSessionId(sessionId);
        EnsureLegacyImported(workspace);
        var scope = HaoyueDatabase.ScopeKey(workspace);
        using var connection = _database.OpenConnection();
        var header = ReadHeader(connection, scope, sessionId);
        if (header is null) return null;

        var session = NewSession(header, scope);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json FROM messages
            WHERE scope = $scope AND session_id = $sessionId
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            try
            {
                var record = JsonSerializer.Deserialize(
                    reader.GetString(0), HaoyueJsonContext.Compact.SessionMessage);
                if (record is not null) session.Messages.Add(ToMessage(record));
            }
            catch (JsonException) { }
        }
        return session;
    }

    public AgentSession? LoadLatest(WorkspaceInfo workspace)
    {
        var latest = List(workspace).FirstOrDefault();
        return latest is null ? null : Load(workspace, latest.Id);
    }

    public IReadOnlyList<SessionHeader> List(WorkspaceInfo workspace, bool includeArchived = false)
    {
        EnsureLegacyImported(workspace);
        var scope = HaoyueDatabase.ScopeKey(workspace);
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, title, workspace, archived, reasoning_level, network_enabled,
                   llm_rounds, execution_steps, input_tokens, total_input_tokens,
                   cached_input_tokens, output_tokens, output_elapsed_ms,
                   created_at, updated_at, expert_id
            FROM sessions
            WHERE scope = $scope {(includeArchived ? "" : "AND archived = 0")}
            ORDER BY updated_at DESC;
            """;
        command.Parameters.AddWithValue("$scope", scope);
        using var reader = command.ExecuteReader();
        var headers = new List<SessionHeader>();
        while (reader.Read()) headers.Add(ReadHeader(reader));
        return headers;
    }

    public IReadOnlyList<SessionSearchHit> Search(
        WorkspaceInfo workspace, string query, bool includeArchived = false, int limit = 20)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        EnsureLegacyImported(workspace);

        var scope = HaoyueDatabase.ScopeKey(workspace);
        var rawPattern = "%" + EscapeLike(query) + "%";
        var jsonPattern = "%" + EscapeLike(ToJsonEscaped(query)) + "%";

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.id, s.title, s.workspace, s.archived, s.reasoning_level, s.network_enabled,
                   s.llm_rounds, s.execution_steps, s.input_tokens, s.total_input_tokens,
                   s.cached_input_tokens, s.output_tokens, s.output_elapsed_ms,
                   s.created_at, s.updated_at, s.expert_id,
                   (CASE WHEN s.title LIKE $rawPattern ESCAPE '\' THEN 1 ELSE 0 END) +
                   (SELECT COUNT(*) FROM messages m
                    WHERE m.scope = s.scope AND m.session_id = s.id
                      AND m.payload_json LIKE $jsonPattern ESCAPE '\') AS match_count
            FROM sessions s
            WHERE s.scope = $scope {(includeArchived ? "" : "AND s.archived = 0")}
              AND (s.title LIKE $rawPattern ESCAPE '\'
                   OR EXISTS (SELECT 1 FROM messages m
                              WHERE m.scope = s.scope AND m.session_id = s.id
                                AND m.payload_json LIKE $jsonPattern ESCAPE '\'))
            ORDER BY match_count DESC, s.updated_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$rawPattern", rawPattern);
        command.Parameters.AddWithValue("$jsonPattern", jsonPattern);
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        var hits = new List<SessionSearchHit>();
        while (reader.Read())
        {
            var header = ReadHeader(reader);
            hits.Add(new SessionSearchHit(header, reader.GetInt32(16)));
        }
        return hits;
    }

    /// <summary>Escapes LIKE wildcards so user queries match literally (pattern uses ESCAPE '\').</summary>
    private static string EscapeLike(string text) => text
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");

    /// <summary>
    /// Rewrites non-ASCII chars into the \uXXXX form used by the JSON encoder that
    /// serialized message payloads (System.Text.Json's default encoder escapes
    /// everything above ASCII). Surrogate pairs become two escapes, matching how
    /// they are stored. ASCII passes through, mirroring payload layout.
    /// </summary>
    private static string ToJsonEscaped(string text)
    {
        var asciiOnly = true;
        foreach (var ch in text)
        {
            if (ch >= 128)
            {
                asciiOnly = false;
                break;
            }
        }
        if (asciiOnly) return text;

        var sb = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            if (ch < 128) sb.Append(ch);
            else sb.Append("\\u").Append(((int)ch).ToString("x4"));
        }
        return sb.ToString();
    }

    public void Append(AgentSession session, ChatMessage message)
    {
        var record = ToRecord(message);
        var payload = JsonSerializer.Serialize(record, HaoyueJsonContext.Compact.SessionMessage);
        var now = DateTimeOffset.UtcNow;
        var suggestedTitle = record.Role == "user" && !string.IsNullOrWhiteSpace(record.Text)
            ? TitleFrom(record.Text)
            : null;

        lock (GateFor(session.Scope, session.Header.Id))
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: false);
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO messages(scope, session_id, payload_json, timestamp)
                    VALUES($scope, $sessionId, $payload, $timestamp);
                    """;
                insert.Parameters.AddWithValue("$scope", session.Scope);
                insert.Parameters.AddWithValue("$sessionId", session.Header.Id);
                insert.Parameters.AddWithValue("$payload", payload);
                insert.Parameters.AddWithValue("$timestamp", record.Timestamp.ToString("O"));
                insert.ExecuteNonQuery();
            }
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE sessions SET
                        updated_at = $updatedAt,
                        title = CASE WHEN title IS NULL AND $suggestedTitle IS NOT NULL
                            THEN $suggestedTitle ELSE title END
                    WHERE scope = $scope AND id = $sessionId;
                    """;
                update.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
                update.Parameters.AddWithValue("$suggestedTitle", (object?)suggestedTitle ?? DBNull.Value);
                update.Parameters.AddWithValue("$scope", session.Scope);
                update.Parameters.AddWithValue("$sessionId", session.Header.Id);
                if (update.ExecuteNonQuery() == 0)
                    throw new FileNotFoundException($"Session not found: {session.Header.Id}", _database.FilePath);
            }
            transaction.Commit();
            session.Messages.Add(message);
            session.Header.UpdatedAt = now;
            if (session.Header.Title is null && suggestedTitle is not null)
                session.Header.Title = suggestedTitle;
        }
    }

    public void Truncate(WorkspaceInfo workspace, string sessionId, int keepMessageCount)
    {
        ValidateSessionId(sessionId);
        var scope = HaoyueDatabase.ScopeKey(workspace);
        lock (GateFor(scope, sessionId))
        {
            using var connection = _database.OpenConnection();
            if (keepMessageCount <= 0)
            {
                using var deleteAll = connection.CreateCommand();
                deleteAll.CommandText = "DELETE FROM messages WHERE scope = $scope AND session_id = $sessionId;";
                deleteAll.Parameters.AddWithValue("$scope", scope);
                deleteAll.Parameters.AddWithValue("$sessionId", sessionId);
                deleteAll.ExecuteNonQuery();
                return;
            }

            object? keepId;
            using (var select = connection.CreateCommand())
            {
                select.CommandText = """
                    SELECT id FROM messages
                    WHERE scope = $scope AND session_id = $sessionId
                    ORDER BY id LIMIT 1 OFFSET $offset;
                    """;
                select.Parameters.AddWithValue("$scope", scope);
                select.Parameters.AddWithValue("$sessionId", sessionId);
                select.Parameters.AddWithValue("$offset", keepMessageCount - 1);
                keepId = select.ExecuteScalar();
            }
            if (keepId is null) return; // keep count beyond the history → no-op

            using var delete = connection.CreateCommand();
            delete.CommandText = """
                DELETE FROM messages
                WHERE scope = $scope AND session_id = $sessionId AND id > $keepId;
                """;
            delete.Parameters.AddWithValue("$scope", scope);
            delete.Parameters.AddWithValue("$sessionId", sessionId);
            delete.Parameters.AddWithValue("$keepId", Convert.ToInt64(keepId));
            delete.ExecuteNonQuery();
        }
    }

    public void ReplaceHistory(AgentSession session, IReadOnlyList<ChatMessage> messages)
    {
        // Snapshot before touching session.Messages: callers may pass the session's own
        // list, and the in-memory replacement below must not alias the DB payload source.
        var snapshot = messages.ToList();
        lock (GateFor(session.Scope, session.Header.Id))
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: false);
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = """
                    DELETE FROM messages WHERE scope = $scope AND session_id = $sessionId;
                    """;
                delete.Parameters.AddWithValue("$scope", session.Scope);
                delete.Parameters.AddWithValue("$sessionId", session.Header.Id);
                delete.ExecuteNonQuery();
            }

            foreach (var message in snapshot)
            {
                var record = ToRecord(message);
                var payload = JsonSerializer.Serialize(record, HaoyueJsonContext.Compact.SessionMessage);
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO messages(scope, session_id, payload_json, timestamp)
                    VALUES($scope, $sessionId, $payload, $timestamp);
                    """;
                insert.Parameters.AddWithValue("$scope", session.Scope);
                insert.Parameters.AddWithValue("$sessionId", session.Header.Id);
                insert.Parameters.AddWithValue("$payload", payload);
                insert.Parameters.AddWithValue("$timestamp", record.Timestamp.ToString("O"));
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        session.Messages.Clear();
        session.Messages.AddRange(snapshot);
    }

    public SessionHeader UpdateMetadata(
        WorkspaceInfo workspace,
        string sessionId,
        string? title = null,
        bool? archived = null,
        ReasoningLevel? reasoningLevel = null,
        bool? networkEnabled = null,
        string? expertId = null)
    {
        ValidateSessionId(sessionId);
        EnsureLegacyImported(workspace);
        var scope = HaoyueDatabase.ScopeKey(workspace);
        lock (GateFor(scope, sessionId))
        {
            using var connection = _database.OpenConnection();
            var assignments = new List<string> { "updated_at = $updatedAt" };
            using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            if (title is not null)
            {
                assignments.Add("title = $title");
                command.Parameters.AddWithValue("$title",
                    string.IsNullOrWhiteSpace(title) ? DBNull.Value : title.Trim());
            }
            if (archived is not null)
            {
                assignments.Add("archived = $archived");
                command.Parameters.AddWithValue("$archived", archived.Value ? 1 : 0);
            }
            if (reasoningLevel is not null)
            {
                assignments.Add("reasoning_level = $reasoningLevel");
                command.Parameters.AddWithValue("$reasoningLevel", (int)reasoningLevel.Value);
            }
            if (networkEnabled is not null)
            {
                assignments.Add("network_enabled = $networkEnabled");
                command.Parameters.AddWithValue("$networkEnabled", networkEnabled.Value ? 1 : 0);
            }
            // expertId uses "" as the explicit "unbind" sentinel; null leaves it untouched.
            if (expertId is not null)
            {
                assignments.Add("expert_id = $expertId");
                command.Parameters.AddWithValue("$expertId",
                    string.IsNullOrWhiteSpace(expertId) ? DBNull.Value : expertId.Trim());
            }
            command.CommandText = $"""
                UPDATE sessions SET {string.Join(", ", assignments)}
                WHERE scope = $scope AND id = $sessionId;
                """;
            command.Parameters.AddWithValue("$scope", scope);
            command.Parameters.AddWithValue("$sessionId", sessionId);
            if (command.ExecuteNonQuery() == 0)
                throw new FileNotFoundException($"Session not found: {sessionId}", _database.FilePath);
            return ReadHeader(connection, scope, sessionId)!;
        }
    }

    public SessionHeader RecordUsage(WorkspaceInfo workspace, string sessionId, SessionUsage usage)
    {
        ValidateSessionId(sessionId);
        EnsureLegacyImported(workspace);
        var scope = HaoyueDatabase.ScopeKey(workspace);
        lock (GateFor(scope, sessionId))
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE sessions SET
                    llm_rounds = llm_rounds + $llmRounds,
                    execution_steps = execution_steps + $executionSteps,
                    input_tokens = input_tokens + $inputTokens,
                    total_input_tokens = total_input_tokens + $totalInputTokens,
                    cached_input_tokens = cached_input_tokens + $cachedInputTokens,
                    output_tokens = output_tokens + $outputTokens,
                    output_elapsed_ms = output_elapsed_ms + $outputElapsedMs,
                    updated_at = $updatedAt
                WHERE scope = $scope AND id = $sessionId;
                """;
            command.Parameters.AddWithValue("$llmRounds", usage.LlmRounds);
            command.Parameters.AddWithValue("$executionSteps", usage.ExecutionSteps);
            command.Parameters.AddWithValue("$inputTokens", usage.InputTokens);
            command.Parameters.AddWithValue("$totalInputTokens", usage.TotalInputTokens);
            command.Parameters.AddWithValue("$cachedInputTokens", usage.CachedInputTokens);
            command.Parameters.AddWithValue("$outputTokens", usage.OutputTokens);
            command.Parameters.AddWithValue("$outputElapsedMs", usage.OutputElapsedMs);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$scope", scope);
            command.Parameters.AddWithValue("$sessionId", sessionId);
            if (command.ExecuteNonQuery() == 0)
                throw new FileNotFoundException($"Session not found: {sessionId}", _database.FilePath);
            return ReadHeader(connection, scope, sessionId)!;
        }
    }

    public void Delete(WorkspaceInfo workspace, string sessionId)
    {
        ValidateSessionId(sessionId);
        EnsureLegacyImported(workspace);
        var scope = HaoyueDatabase.ScopeKey(workspace);
        lock (GateFor(scope, sessionId))
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM sessions WHERE scope = $scope AND id = $sessionId;";
            command.Parameters.AddWithValue("$scope", scope);
            command.Parameters.AddWithValue("$sessionId", sessionId);
            if (command.ExecuteNonQuery() == 0)
                throw new FileNotFoundException($"Session not found: {sessionId}", _database.FilePath);
            DeleteLegacyFile(workspace, sessionId);
        }
    }

    public void DeleteAll(WorkspaceInfo workspace)
    {
        EnsureLegacyImported(workspace);
        var scope = HaoyueDatabase.ScopeKey(workspace);
        using (var connection = _database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM sessions WHERE scope = $scope;";
            command.Parameters.AddWithValue("$scope", scope);
            command.ExecuteNonQuery();
        }

        if (!Directory.Exists(workspace.SessionsDir)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(workspace.SessionsDir, "*.jsonl"))
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private AgentSession NewSession(SessionHeader header, string scope) => new()
    {
        Header = header,
        FilePath = _database.FilePath,
        Scope = scope,
    };

    private void EnsureLegacyImported(WorkspaceInfo workspace)
    {
        var scope = HaoyueDatabase.ScopeKey(workspace);
        lock (MigrationGates.GetOrAdd(_database.FilePath + "|" + scope, static _ => new Lock()))
        {
            using var connection = _database.OpenConnection();
            using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT 1 FROM migrations WHERE scope = $scope;";
                check.Parameters.AddWithValue("$scope", scope);
                if (check.ExecuteScalar() is not null) return;
            }

            using var transaction = connection.BeginTransaction(deferred: false);
            if (Directory.Exists(workspace.SessionsDir))
            {
                foreach (var file in Directory.EnumerateFiles(workspace.SessionsDir, "*.jsonl"))
                    ImportLegacyFile(connection, transaction, workspace, scope, file);
            }
            using (var mark = connection.CreateCommand())
            {
                mark.Transaction = transaction;
                mark.CommandText = """
                    INSERT OR REPLACE INTO migrations(scope, source_dir, imported_at)
                    VALUES($scope, $sourceDir, $importedAt);
                    """;
                mark.Parameters.AddWithValue("$scope", scope);
                mark.Parameters.AddWithValue("$sourceDir", Path.GetFullPath(workspace.SessionsDir));
                mark.Parameters.AddWithValue("$importedAt", DateTimeOffset.UtcNow.ToString("O"));
                mark.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    private static void ImportLegacyFile(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkspaceInfo workspace,
        string scope,
        string file)
    {
        try
        {
            using var lines = File.ReadLines(file).GetEnumerator();
            if (!lines.MoveNext()) return;
            var header = JsonSerializer.Deserialize(lines.Current, HaoyueJsonContext.Compact.SessionHeader);
            if (header is null || string.IsNullOrWhiteSpace(header.Id)) return;
            header.Workspace = workspace.IsGlobal ? null : Path.GetFullPath(workspace.Root);
            header.UpdatedAt = File.GetLastWriteTimeUtc(file);

            var records = new List<SessionMessage>();
            while (lines.MoveNext())
            {
                if (string.IsNullOrWhiteSpace(lines.Current)) continue;
                try
                {
                    var record = JsonSerializer.Deserialize(
                        lines.Current, HaoyueJsonContext.Compact.SessionMessage);
                    if (record is not null) records.Add(record);
                }
                catch (JsonException) { }
            }
            if (string.IsNullOrWhiteSpace(header.Title))
            {
                var firstUserText = records.FirstOrDefault(record =>
                    record.Role == "user" && !string.IsNullOrWhiteSpace(record.Text))?.Text;
                if (firstUserText is not null) header.Title = TitleFrom(firstUserText);
            }

            if (!InsertSession(connection, transaction, scope, header, ignoreConflict: true)) return;
            foreach (var record in records)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO messages(scope, session_id, payload_json, timestamp)
                    VALUES($scope, $sessionId, $payload, $timestamp);
                    """;
                insert.Parameters.AddWithValue("$scope", scope);
                insert.Parameters.AddWithValue("$sessionId", header.Id);
                insert.Parameters.AddWithValue("$payload",
                    JsonSerializer.Serialize(record, HaoyueJsonContext.Compact.SessionMessage));
                insert.Parameters.AddWithValue("$timestamp", record.Timestamp.ToString("O"));
                insert.ExecuteNonQuery();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private static bool InsertSession(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string scope,
        SessionHeader header,
        bool ignoreConflict = false)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT {(ignoreConflict ? "OR IGNORE" : "")} INTO sessions(
                scope, id, workspace, title, archived, reasoning_level, network_enabled,
                llm_rounds, execution_steps, input_tokens, total_input_tokens,
                cached_input_tokens, output_tokens, output_elapsed_ms, created_at, updated_at,
                expert_id)
            VALUES($scope, $id, $workspace, $title, $archived, $reasoningLevel, $networkEnabled,
                   $llmRounds, $executionSteps, $inputTokens, $totalInputTokens,
                   $cachedInputTokens, $outputTokens, $outputElapsedMs, $createdAt, $updatedAt,
                   $expertId);
            """;
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$id", header.Id);
        command.Parameters.AddWithValue("$workspace", (object?)header.Workspace ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", (object?)header.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$archived", header.Archived ? 1 : 0);
        command.Parameters.AddWithValue("$reasoningLevel", (int)header.ReasoningLevel);
        command.Parameters.AddWithValue("$networkEnabled", header.NetworkEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$llmRounds", header.LlmRounds);
        command.Parameters.AddWithValue("$executionSteps", header.ExecutionSteps);
        command.Parameters.AddWithValue("$inputTokens", header.InputTokens);
        command.Parameters.AddWithValue("$totalInputTokens", header.TotalInputTokens);
        command.Parameters.AddWithValue("$cachedInputTokens", header.CachedInputTokens);
        command.Parameters.AddWithValue("$outputTokens", header.OutputTokens);
        command.Parameters.AddWithValue("$outputElapsedMs", header.OutputElapsedMs);
        command.Parameters.AddWithValue("$createdAt", header.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", header.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$expertId", (object?)header.ExpertId ?? DBNull.Value);
        return command.ExecuteNonQuery() > 0;
    }

    private static SessionHeader? ReadHeader(SqliteConnection connection, string scope, string sessionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, workspace, archived, reasoning_level, network_enabled,
                   llm_rounds, execution_steps, input_tokens, total_input_tokens,
                   cached_input_tokens, output_tokens, output_elapsed_ms,
                   created_at, updated_at, expert_id
            FROM sessions WHERE scope = $scope AND id = $sessionId;
            """;
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadHeader(reader) : null;
    }

    // Ordinal contract: every SELECT feeding this mapper ends with expert_id at
    // ordinal 15 (Search's match_count trailing aggregate reads ordinal 16).
    private static SessionHeader ReadHeader(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Title = reader.IsDBNull(1) ? null : reader.GetString(1),
        Workspace = reader.IsDBNull(2) ? null : reader.GetString(2),
        Archived = reader.GetInt64(3) != 0,
        ReasoningLevel = (ReasoningLevel)reader.GetInt32(4),
        NetworkEnabled = reader.GetInt64(5) != 0,
        LlmRounds = reader.GetInt64(6),
        ExecutionSteps = reader.GetInt64(7),
        InputTokens = reader.GetInt64(8),
        TotalInputTokens = reader.GetInt64(9),
        CachedInputTokens = reader.GetInt64(10),
        OutputTokens = reader.GetInt64(11),
        OutputElapsedMs = reader.GetInt64(12),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(13)),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(14)),
        ExpertId = reader.IsDBNull(15) ? null : reader.GetString(15),
    };

    private static string TitleFrom(string text) =>
        text.Length > 42 ? text[..42] + "…" : text;

    private static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || sessionId.Length > 128
            || !string.Equals(Path.GetFileName(sessionId), sessionId, StringComparison.Ordinal))
            throw new ArgumentException("Invalid session id.", nameof(sessionId));
    }

    private static void DeleteLegacyFile(WorkspaceInfo workspace, string sessionId)
    {
        var file = Path.Combine(workspace.SessionsDir, sessionId + ".jsonl");
        if (File.Exists(file)) File.Delete(file);
    }

    private Lock GateFor(string scope, string sessionId) =>
        SessionGates.GetOrAdd(_database.FilePath + "|" + scope + "|" + sessionId, static _ => new Lock());

    private static SessionMessage ToRecord(ChatMessage message) => new()
    {
        Role = message.Role.ToString().ToLowerInvariant(),
        Text = message.Text.Length > 0 ? message.Text : null,
        Images = message.Images?.Select(image => new SessionImage
        {
            Id = image.Id,
            Name = image.Name,
            MediaType = image.MediaType,
            Data = image.Data,
            SizeBytes = image.SizeBytes,
        }).ToList(),
        Thinking = message.Thinking,
        ModelRef = message.ModelRef,
        ViewedImages = message.ViewedImages?.Select(image => new SessionImageReference
        {
            Id = image.Id,
            Name = image.Name,
        }).ToList(),
        ToolCalls = message.ToolCalls?.Select(call => new SessionToolCall
        {
            Id = call.Id,
            Name = call.Name,
            ArgumentsJson = call.ArgumentsJson,
        }).ToList(),
        ToolCallId = message.ToolCallId,
        ToolName = message.ToolName,
        ToolSuccess = message.Role == ChatRole.Tool ? message.ToolSuccess : null,
        ToolDiff = message.ToolDiff,
        ToolFilePath = message.ToolFilePath,
        Timestamp = message.Timestamp ?? DateTimeOffset.UtcNow,
    };

    private static ChatMessage ToMessage(SessionMessage record)
    {
        var role = record.Role switch
        {
            "assistant" => ChatRole.Assistant,
            "tool" => ChatRole.Tool,
            _ => ChatRole.User,
        };
        return new ChatMessage
        {
            Role = role,
            Text = record.Text ?? "",
            Timestamp = record.Timestamp,
            Images = record.Images?.Select(image => new ChatImageAttachment(
                image.Id, image.Name, image.MediaType, image.Data, image.SizeBytes)).ToList(),
            Thinking = record.Thinking,
            ModelRef = record.ModelRef,
            ViewedImages = record.ViewedImages?.Select(image => new ChatImageReference(
                image.Id, image.Name)).ToList(),
            ToolCalls = record.ToolCalls?.Select(call =>
                new ToolCallRequest(call.Id, call.Name, call.ArgumentsJson)).ToList(),
            ToolCallId = record.ToolCallId,
            ToolName = record.ToolName,
            ToolSuccess = record.ToolSuccess ?? true,
            ToolDiff = record.ToolDiff,
            ToolFilePath = record.ToolFilePath,
        };
    }
}
