using Microsoft.Data.Sqlite;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Data;

/// <summary>Shared SQLite storage for durable user data. Configuration remains file based.</summary>
public sealed class HaoyueDatabase
{
    /// <summary>One journaled event row restored from the database.</summary>
    public sealed record PersistedEvent(long Id, string Timestamp, string Type, string Payload);

    private static readonly object InitializationGate = new();
    private static readonly HashSet<string> InitializedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _connectionString;

    public HaoyueDatabase(string? filePath = null)
    {
        FilePath = Path.GetFullPath(filePath ?? HaoyuePaths.DatabaseFile);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 10,
        }.ToString();
        EnsureInitialized();
    }

    public string FilePath { get; }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;";
        command.ExecuteNonQuery();
        return connection;
    }

    /// <summary>
    /// Drops and recreates all durable tables in place. Existing store objects keep
    /// pointing at the same database file, so the daemon can continue using the same
    /// runtime after a factory reset without restarting.
    /// </summary>
    public void Rebuild()
    {
        lock (InitializationGate)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            DropSchema(connection);
            InitializeSchema(connection);
            InitializedFiles.Add(FilePath);
        }
    }

    /// <summary>
    /// Appends one durable event row and prunes the journal down to
    /// <see cref="Events.EventJournal.RetainedEvents"/> entries.
    /// </summary>
    public void AppendEvent(DateTimeOffset timestamp, string type, string payloadJson)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO events (timestamp, type, payload) VALUES ($ts, $type, $payload);";
            insert.Parameters.AddWithValue("$ts", timestamp.ToString("O"));
            insert.Parameters.AddWithValue("$type", type);
            insert.Parameters.AddWithValue("$payload", payloadJson);
            insert.ExecuteNonQuery();
        }
        using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText =
                "DELETE FROM events WHERE id <= (SELECT COALESCE(MAX(id), 0) FROM events) - $keep;";
            prune.Parameters.AddWithValue("$keep", EventJournal.RetainedEvents);
            prune.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>
    /// Most recent journal entries, oldest first, ready for replay.
    /// <paramref name="afterId"/> is the reconnect cursor: only rows with a larger id
    /// are returned, so a client that dropped its socket can ask for exactly what it missed.
    /// </summary>
    public IReadOnlyList<PersistedEvent> RecentEvents(int limit = 100, long afterId = 0)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, timestamp, type, payload FROM events "
            + "WHERE id > $afterId ORDER BY id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, EventJournal.RetainedEvents));
        command.Parameters.AddWithValue("$afterId", Math.Max(0, afterId));
        var events = new List<PersistedEvent>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                events.Add(new PersistedEvent(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        events.Reverse();
        return events;
    }

    public static string ScopeKey(WorkspaceInfo workspace) =>
        (workspace.IsGlobal ? "global|" : "workspace|") + PathKey(workspace.Root);

    public static string PathKey(string path)
    {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
    }

    private void EnsureInitialized()
    {
        lock (InitializationGate)
        {
            if (InitializedFiles.Contains(FilePath) && File.Exists(FilePath)) return;

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            InitializeSchema(connection);
            InitializedFiles.Add(FilePath);
        }
    }

    private static void DropSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=OFF;
            PRAGMA busy_timeout=10000;

            DROP TRIGGER IF EXISTS messages_fts_ai;
            DROP TRIGGER IF EXISTS messages_fts_ad;
            DROP TABLE IF EXISTS messages_fts;
            DROP TABLE IF EXISTS messages;
            DROP TABLE IF EXISTS sessions;
            DROP TABLE IF EXISTS projects;
            DROP TABLE IF EXISTS scheduled_tasks;
            DROP TABLE IF EXISTS migrations;
            DROP TABLE IF EXISTS kb_sources;
            DROP TABLE IF EXISTS kb_notebooks;
            DROP TABLE IF EXISTS knowledge_vectors;
            DROP TABLE IF EXISTS knowledge;
            DROP TABLE IF EXISTS events;
            DROP TABLE IF EXISTS evolution_log;
            DROP TABLE IF EXISTS evolution_runs;
            DROP TABLE IF EXISTS model_token_stats;
            DROP TABLE IF EXISTS maintenance_state;
            """;
        command.ExecuteNonQuery();
    }

    private static void InitializeSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=10000;

            CREATE TABLE IF NOT EXISTS projects (
                id TEXT NOT NULL PRIMARY KEY,
                path TEXT NOT NULL,
                path_key TEXT NOT NULL UNIQUE,
                name TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sessions (
                scope TEXT NOT NULL,
                id TEXT NOT NULL,
                workspace TEXT NULL,
                title TEXT NULL,
                archived INTEGER NOT NULL DEFAULT 0,
                reasoning_level INTEGER NOT NULL,
                network_enabled INTEGER NOT NULL DEFAULT 1,
                llm_rounds INTEGER NOT NULL DEFAULT 0,
                execution_steps INTEGER NOT NULL DEFAULT 0,
                input_tokens INTEGER NOT NULL DEFAULT 0,
                total_input_tokens INTEGER NOT NULL DEFAULT 0,
                cached_input_tokens INTEGER NOT NULL DEFAULT 0,
                output_tokens INTEGER NOT NULL DEFAULT 0,
                output_elapsed_ms INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (scope, id)
            );

            CREATE INDEX IF NOT EXISTS ix_sessions_scope_updated
                ON sessions(scope, archived, updated_at DESC);

            CREATE TABLE IF NOT EXISTS messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                scope TEXT NOT NULL,
                session_id TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                FOREIGN KEY (scope, session_id)
                    REFERENCES sessions(scope, id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_messages_session
                ON messages(scope, session_id, id);

            CREATE TABLE IF NOT EXISTS scheduled_tasks (
                id TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                workspace TEXT NULL,
                prompt TEXT NOT NULL,
                cron TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1,
                last_run_at TEXT NULL,
                next_run_at TEXT NULL,
                last_status TEXT NULL,
                last_error TEXT NULL,
                last_output TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_scheduled_tasks_enabled
                ON scheduled_tasks(enabled, next_run_at);

            CREATE TABLE IF NOT EXISTS migrations (
                scope TEXT NOT NULL PRIMARY KEY,
                source_dir TEXT NOT NULL,
                imported_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS knowledge (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                scope TEXT NOT NULL,
                title TEXT NOT NULL,
                content TEXT NOT NULL,
                tags TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_knowledge_scope_updated
                ON knowledge(scope, updated_at DESC);
            CREATE INDEX IF NOT EXISTS ix_knowledge_scope_title
                ON knowledge(scope, title);

            CREATE TABLE IF NOT EXISTS knowledge_vectors (
                scope TEXT NOT NULL,
                entry_id INTEGER NOT NULL,
                model TEXT NOT NULL,
                dim INTEGER NOT NULL,
                vector BLOB NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (scope, entry_id, model)
            );

            CREATE TABLE IF NOT EXISTS kb_notebooks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                scope TEXT NOT NULL,
                name TEXT NOT NULL,
                description TEXT NULL,
                is_default INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE (scope, name)
            );

            CREATE TABLE IF NOT EXISTS kb_sources (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                notebook_id INTEGER NOT NULL
                    REFERENCES kb_notebooks(id) ON DELETE CASCADE,
                kind TEXT NOT NULL,
                title TEXT NOT NULL,
                locator TEXT NULL,
                content TEXT NOT NULL,
                content_hash TEXT NULL,
                chunk_count INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE (notebook_id, kind, locator)
            );

            CREATE INDEX IF NOT EXISTS ix_kb_sources_notebook
                ON kb_sources(notebook_id, updated_at DESC);

            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                type TEXT NOT NULL,
                payload TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS evolution_log (
                fingerprint TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                status TEXT NOT NULL,
                candidate_dir TEXT NULL,
                session_id TEXT NULL,
                summary TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_evolution_status ON evolution_log(status);

            CREATE TABLE IF NOT EXISTS evolution_runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NULL,
                trigger TEXT NOT NULL,
                processed INTEGER NOT NULL DEFAULT 0,
                candidates INTEGER NOT NULL DEFAULT 0,
                no_action INTEGER NOT NULL DEFAULT 0,
                skipped INTEGER NOT NULL DEFAULT 0,
                failed INTEGER NOT NULL DEFAULT 0,
                error TEXT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS model_token_stats (
                provider_id TEXT NOT NULL,
                model_id TEXT NOT NULL,
                ratio REAL NOT NULL,
                samples INTEGER NOT NULL DEFAULT 0,
                last_input_tokens INTEGER NOT NULL DEFAULT 0,
                last_estimated_tokens INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (provider_id, model_id)
            );

            -- L-D 保留策略维护的跨进程状态键值（当前仅记录上次 VACUUM 时间，
            -- 保证压缩每天至多执行一次，CLI 与 daemon 并存时也不会重复）。
            CREATE TABLE IF NOT EXISTS maintenance_state (
                key TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        EnsureColumn(connection, "sessions", "network_enabled", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "sessions", "llm_rounds", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "sessions", "execution_steps", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "sessions", "input_tokens", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "sessions", "total_input_tokens", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "sessions", "cached_input_tokens", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "sessions", "output_tokens", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "sessions", "output_elapsed_ms", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "sessions", "expert_id", "TEXT NULL");

        // 知识中心（笔记本 + 来源）：老库只加列不重建，回填在 KnowledgeStore 首次访问时进行。
        EnsureColumn(connection, "knowledge", "notebook_id", "INTEGER NULL");
        EnsureColumn(connection, "knowledge", "source_id", "INTEGER NULL");
        EnsureColumn(connection, "knowledge", "chunk_ordinal", "INTEGER NULL");
        using (var indexCommand = connection.CreateCommand())
        {
            indexCommand.CommandText = """
                CREATE INDEX IF NOT EXISTS ix_knowledge_scope_notebook ON knowledge(scope, notebook_id);
                CREATE INDEX IF NOT EXISTS ix_knowledge_source ON knowledge(source_id);
                """;
            indexCommand.ExecuteNonQuery();
        }

        EnsureSessionFts(connection);
    }

    /// <summary>
    /// L2 会话全文检索：FTS5 trigram 虚表 + 消息表触发器同步。trigram 分词让中文等
    /// CJK 文本按子串命中（unicode61 对 CJK 整段成 token，无法做短语匹配）。虚表由
    /// AFTER INSERT / AFTER DELETE 触发器与 messages 保持一致（ReplaceHistory/Truncate/
    /// 级联删除全部走这两条路径，无需额外同步点）；存量消息的回填在 SessionStore
    /// 首次搜索时进行。FTS5/trigram/json1 不可用时整体跳过——SessionStore.Search
    /// 自动退回 LIKE 扫描，行为与旧版一致。
    /// </summary>
    private static void EnsureSessionFts(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
                    text,
                    msg_id UNINDEXED,
                    scope UNINDEXED,
                    session_id UNINDEXED,
                    tokenize = 'trigram'
                );

                CREATE TRIGGER IF NOT EXISTS messages_fts_ai AFTER INSERT ON messages BEGIN
                    INSERT INTO messages_fts(msg_id, scope, session_id, text)
                    VALUES (new.id, new.scope, new.session_id,
                            coalesce(json_extract(new.payload_json, '$.text'), '')
                            || ' ' || coalesce(json_extract(new.payload_json, '$.thinking'), ''));
                END;

                CREATE TRIGGER IF NOT EXISTS messages_fts_ad AFTER DELETE ON messages BEGIN
                    DELETE FROM messages_fts WHERE msg_id = old.id;
                END;
                """;
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // 虚表可能已建而触发器未建成功（索引会失去同步）——整体丢弃，统一走 LIKE。
            try
            {
                using var cleanup = connection.CreateCommand();
                cleanup.CommandText = """
                    DROP TRIGGER IF EXISTS messages_fts_ai;
                    DROP TRIGGER IF EXISTS messages_fts_ad;
                    DROP TABLE IF EXISTS messages_fts;
                    """;
                cleanup.ExecuteNonQuery();
            }
            catch (SqliteException) { }
        }
    }

    private static void EnsureColumn(
        SqliteConnection connection,
        string table,
        string column,
        string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';";
        if (Convert.ToInt64(check.ExecuteScalar()) != 0) return;

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }
}
