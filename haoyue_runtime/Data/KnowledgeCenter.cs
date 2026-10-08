using Microsoft.Data.Sqlite;

namespace Haoyue.Runtime.Data;

/// <summary>
/// Knowledge-center half of <see cref="KnowledgeStore"/>: notebooks (collections),
/// sources (text / file / URL ingest units that own their chunk entries) and
/// attribution-aware listing/retrieval. Modeled after a QMind-style knowledge
/// center — notebooks group sources, sources chunk into entries, retrieval ranks
/// globally across the selected notebooks and cites the source of every hit.
/// Old databases backfill lazily: rows with NULL notebook_id are moved into a
/// per-scope default notebook on first touch.
/// </summary>
public sealed partial class KnowledgeStore
{
    // ───────────────────────────── notebooks ─────────────────────────────

    public IReadOnlyList<KnowledgeNotebook> ListNotebooks(string scope)
    {
        EnsureScopeReady(scope);
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.id, n.name, n.description, n.is_default, n.created_at, n.updated_at,
                (SELECT COUNT(*) FROM knowledge k WHERE k.notebook_id = n.id) AS entry_count,
                (SELECT COUNT(*) FROM kb_sources s WHERE s.notebook_id = n.id) AS source_count
            FROM kb_notebooks n
            WHERE n.scope = $scope
            ORDER BY n.is_default DESC, n.id;
            """;
        command.Parameters.AddWithValue("$scope", scope);
        using var reader = command.ExecuteReader();
        var notebooks = new List<KnowledgeNotebook>();
        while (reader.Read())
            notebooks.Add(ReadNotebook(reader));
        return notebooks;
    }

    public KnowledgeNotebook? GetNotebook(string scope, long id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.id, n.name, n.description, n.is_default, n.created_at, n.updated_at,
                (SELECT COUNT(*) FROM knowledge k WHERE k.notebook_id = n.id),
                (SELECT COUNT(*) FROM kb_sources s WHERE s.notebook_id = n.id)
            FROM kb_notebooks n
            WHERE n.scope = $scope AND n.id = $id;
            """;
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadNotebook(reader) : null;
    }

    /// <summary>Creates a notebook or renames/ redescribes an existing one. Duplicate names are rejected.</summary>
    public KnowledgeNotebook SaveNotebook(string scope, string name, string? description, long? id = null)
    {
        EnsureScopeReady(scope);
        var trimmedName = name.Trim();
        var trimmedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        var now = DateTime.UtcNow.ToString("o");

        using var connection = database.OpenConnection();
        if (id is long updateId)
        {
            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE kb_notebooks
                SET name = $name, description = $description, updated_at = $now
                WHERE scope = $scope AND id = $id;
                """;
            update.Parameters.AddWithValue("$name", trimmedName);
            update.Parameters.AddWithValue("$description", (object?)trimmedDescription ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$scope", scope);
            update.Parameters.AddWithValue("$id", updateId);
            if (update.ExecuteNonQuery() == 0)
                throw new KeyNotFoundException($"No knowledge notebook #{updateId} in this scope.");
            return GetNotebook(scope, updateId) ?? throw new InvalidOperationException("notebook vanished after update");
        }

        var duplicate = FindNotebookByName(connection, scope, trimmedName);
        if (duplicate is not null)
            throw new InvalidOperationException($"同名笔记本已存在：{trimmedName}");

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO kb_notebooks (scope, name, description, is_default, created_at, updated_at)
                VALUES ($scope, $name, $description, 0, $now, $now);
                """;
            insert.Parameters.AddWithValue("$scope", scope);
            insert.Parameters.AddWithValue("$name", trimmedName);
            insert.Parameters.AddWithValue("$description", (object?)trimmedDescription ?? DBNull.Value);
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();
        }
        var newId = LastRowId(connection);
        return GetNotebook(scope, newId) ?? throw new InvalidOperationException("notebook vanished after insert");
    }

    /// <summary>
    /// Deletes a notebook together with its sources and entries. The default
    /// notebook is permanent — it is the landing place for agent-saved knowledge.
    /// </summary>
    public void DeleteNotebook(string scope, long id)
    {
        using var connection = database.OpenConnection();
        var notebook = GetNotebook(scope, id) ?? throw new KeyNotFoundException($"No knowledge notebook #{id} in this scope.");
        if (notebook.IsDefault)
            throw new InvalidOperationException("默认笔记本不能删除——Agent 自动沉淀的知识都落在这里");

        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "DELETE FROM knowledge WHERE notebook_id = $id;", id);
        // kb_sources rows cascade via FK; knowledge.source_id rows already gone.
        Execute(connection, transaction, "DELETE FROM kb_notebooks WHERE id = $id;", id);
        transaction.Commit();
    }

    // ───────────────────────────── sources ──────────────────────────────

    public IReadOnlyList<KnowledgeSource> ListSources(string scope, long notebookId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, notebook_id, kind, title, locator, content, chunk_count, created_at, updated_at
            FROM kb_sources
            WHERE notebook_id = $notebook
            ORDER BY updated_at DESC;
            """;
        command.Parameters.AddWithValue("$notebook", notebookId);
        using var reader = command.ExecuteReader();
        var sources = new List<KnowledgeSource>();
        while (reader.Read())
            sources.Add(ReadSource(reader));
        return sources;
    }

    public KnowledgeSource? GetSource(string scope, long sourceId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.notebook_id, s.kind, s.title, s.locator, s.content, s.chunk_count, s.created_at, s.updated_at
            FROM kb_sources s
            JOIN kb_notebooks n ON n.id = s.notebook_id
            WHERE s.id = $id AND n.scope = $scope;
            """;
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$id", sourceId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSource(reader) : null;
    }

    /// <summary>
    /// Creates or updates an ingest unit. Files/URLs upsert by locator within the
    /// notebook (re-adding the same file refreshes it); text sources upsert by title.
    /// Returns the stored source plus the full text (unchanged for updates).
    /// </summary>
    public (KnowledgeSource Source, bool Created) SaveSource(
        string scope, long notebookId, string kind, string title, string? locator, string content)
    {
        EnsureScopeReady(scope);
        if (!NotebookExists(scope, notebookId))
            throw new KeyNotFoundException($"No knowledge notebook #{notebookId} in this scope.");
        var trimmedTitle = title.Trim();
        var now = DateTime.UtcNow.ToString("o");

        using var connection = database.OpenConnection();
        long? existingId = null;
        using (var find = connection.CreateCommand())
        {
            find.CommandText = locator is null
                ? """
                  SELECT s.id FROM kb_sources s
                  JOIN kb_notebooks n ON n.id = s.notebook_id
                  WHERE s.notebook_id = $notebook AND s.kind = $kind AND lower(s.title) = lower($title)
                  ORDER BY s.id LIMIT 1;
                  """
                : """
                  SELECT s.id FROM kb_sources s
                  JOIN kb_notebooks n ON n.id = s.notebook_id
                  WHERE s.notebook_id = $notebook AND s.kind = $kind AND s.locator = $locator
                  ORDER BY s.id LIMIT 1;
                  """;
            find.Parameters.AddWithValue("$notebook", notebookId);
            find.Parameters.AddWithValue("$kind", kind);
            find.Parameters.AddWithValue("$title", trimmedTitle);
            if (locator is not null) find.Parameters.AddWithValue("$locator", locator);
            using var reader = find.ExecuteReader();
            if (reader.Read()) existingId = reader.GetInt64(0);
        }

        if (existingId is long updateId)
        {
            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE kb_sources
                SET title = $title, locator = $locator, content = $content, updated_at = $now
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$title", trimmedTitle);
            update.Parameters.AddWithValue("$locator", (object?)locator ?? DBNull.Value);
            update.Parameters.AddWithValue("$content", content);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$id", updateId);
            update.ExecuteNonQuery();
            return (GetSource(scope, updateId)!, Created: false);
        }

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO kb_sources (notebook_id, kind, title, locator, content, created_at, updated_at)
                VALUES ($notebook, $kind, $title, $locator, $content, $now, $now);
                """;
            insert.Parameters.AddWithValue("$notebook", notebookId);
            insert.Parameters.AddWithValue("$kind", kind);
            insert.Parameters.AddWithValue("$title", trimmedTitle);
            insert.Parameters.AddWithValue("$locator", (object?)locator ?? DBNull.Value);
            insert.Parameters.AddWithValue("$content", content);
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();
        }
        var newId = LastRowId(connection);
        return (GetSource(scope, newId)!, Created: true);
    }

    /// <summary>Deletes a source and the chunk entries derived from it (manual entries stay).</summary>
    public void DeleteSource(string scope, long sourceId)
    {
        var source = GetSource(scope, sourceId) ?? throw new KeyNotFoundException($"No knowledge source #{sourceId} in this scope.");
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "DELETE FROM knowledge WHERE source_id = $id;", sourceId);
        Execute(connection, transaction, "DELETE FROM kb_sources WHERE id = $id;", sourceId);
        transaction.Commit();
    }

    /// <summary>
    /// Replaces the chunk entries of a source: old chunks are deleted, the new
    /// ones insert with stable 「标题 · 第i/n部分」 titles and 1-based ordinals,
    /// and the source's chunk count refreshes.
    /// </summary>
    public int ReplaceSourceChunks(string scope, long sourceId, IReadOnlyList<string> chunks, string? tags)
    {
        var source = GetSource(scope, sourceId) ?? throw new KeyNotFoundException($"No knowledge source #{sourceId} in this scope.");
        var now = DateTime.UtcNow.ToString("o");
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        Execute(connection, transaction, "DELETE FROM knowledge WHERE source_id = $id;", sourceId);
        for (var index = 0; index < chunks.Count; index++)
        {
            var title = chunks.Count == 1 ? source.Title : $"{source.Title} · 第{index + 1}/{chunks.Count}部分";
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO knowledge (scope, title, content, tags, notebook_id, source_id, chunk_ordinal, created_at, updated_at)
                VALUES ($scope, $title, $content, $tags, $notebook, $source, $ordinal, $now, $now);
                """;
            insert.Parameters.AddWithValue("$scope", scope);
            insert.Parameters.AddWithValue("$title", title);
            insert.Parameters.AddWithValue("$content", chunks[index]);
            insert.Parameters.AddWithValue("$tags", (object?)tags ?? DBNull.Value);
            insert.Parameters.AddWithValue("$notebook", source.NotebookId);
            insert.Parameters.AddWithValue("$source", sourceId);
            insert.Parameters.AddWithValue("$ordinal", index + 1);
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE kb_sources SET chunk_count = $count, updated_at = $now WHERE id = $id;";
            update.Parameters.AddWithValue("$count", chunks.Count);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$id", sourceId);
            update.ExecuteNonQuery();
        }
        transaction.Commit();
        return chunks.Count;
    }

    // ────────────────────── attribution-aware reads ─────────────────────

    public IReadOnlyList<KnowledgeEntryInfo> ListInfo(string scope, int limit = 100, long? notebookId = null, string? tag = null)
    {
        EnsureScopeReady(scope);
        var hasTagFilter = !string.IsNullOrWhiteSpace(tag);
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        // Tag filtering runs after the join (comma-split comparison), so when a tag
        // is given the SQL must not cut rows short before the filter sees them.
        command.CommandText = hasTagFilter
            ? """
                SELECT k.id, k.title, k.content, k.tags, k.created_at, k.updated_at,
                    k.notebook_id, n.name, k.source_id, s.title, s.kind, s.locator, k.chunk_ordinal
                FROM knowledge k
                LEFT JOIN kb_notebooks n ON n.id = k.notebook_id
                LEFT JOIN kb_sources s ON s.id = k.source_id
                WHERE k.scope = $scope AND ($notebook IS NULL OR k.notebook_id = $notebook)
                ORDER BY k.updated_at DESC;
                """
            : """
                SELECT k.id, k.title, k.content, k.tags, k.created_at, k.updated_at,
                    k.notebook_id, n.name, k.source_id, s.title, s.kind, s.locator, k.chunk_ordinal
                FROM knowledge k
                LEFT JOIN kb_notebooks n ON n.id = k.notebook_id
                LEFT JOIN kb_sources s ON s.id = k.source_id
                WHERE k.scope = $scope AND ($notebook IS NULL OR k.notebook_id = $notebook)
                ORDER BY k.updated_at DESC LIMIT $limit;
                """;
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$notebook", (object?)notebookId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        var infos = ReadInfos(command);
        if (hasTagFilter)
            infos = infos.Where(info => HasTag(info.Entry.Tags, tag!)).Take(limit).ToList();
        return infos;
    }

    /// <summary>
    /// Ranked search restricted to the given notebooks (or sources, or the whole
    /// scope when neither is given). Returns hits with their notebook/source
    /// attribution so the UI can cite where each piece of knowledge came from.
    /// </summary>
    public IReadOnlyList<KnowledgeEntryInfo> RetrieveInfo(
        string scope, string query, IReadOnlyList<long>? notebookIds, IReadOnlyList<long>? sourceIds, int limit, string? tag = null)
    {
        EnsureScopeReady(scope);
        var filterSql = (notebookIds, sourceIds) switch
        {
            ({ Count: > 0 } nb, { Count: > 0 } src) =>
                $"AND (k.notebook_id IN ({string.Join(",", nb)}) OR k.source_id IN ({string.Join(",", src)}))",
            ({ Count: > 0 } nb, _) => $"AND k.notebook_id IN ({string.Join(",", nb)})",
            (_, { Count: > 0 } src) => $"AND k.source_id IN ({string.Join(",", src)})",
            _ => "",
        };

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT k.id, k.title, k.content, k.tags, k.created_at, k.updated_at,
                k.notebook_id, n.name, k.source_id, s.title, s.kind, s.locator, k.chunk_ordinal
            FROM knowledge k
            LEFT JOIN kb_notebooks n ON n.id = k.notebook_id
            LEFT JOIN kb_sources s ON s.id = k.source_id
            WHERE k.scope = $scope {filterSql};
            """;
        command.Parameters.AddWithValue("$scope", scope);
        var infos = ReadInfos(command);

        if (!string.IsNullOrWhiteSpace(tag))
            infos = infos.Where(info => HasTag(info.Entry.Tags, tag)).ToList();

        var ranked = KnowledgeSearchRanker.Rank(infos.Select(info => info.Entry).ToList(), query, limit);
        var byId = infos.ToDictionary(info => info.Entry.Id, info => info);
        return ranked.Select(entry => byId[entry.Id]).ToList();
    }

    // ─────────────────────────── shared helpers ─────────────────────────

    /// <summary>
    /// One-time per-scope backfill: rows saved before the knowledge center existed
    /// carry notebook_id NULL — move them into the scope's default notebook.
    /// </summary>
    private void EnsureScopeReady(string scope)
    {
        using var connection = database.OpenConnection();
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM knowledge WHERE scope = $scope AND notebook_id IS NULL;";
        check.Parameters.AddWithValue("$scope", scope);
        if ((long)(check.ExecuteScalar() ?? 0L) == 0) return;

        var defaultId = EnsureDefaultNotebook(scope);
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE knowledge SET notebook_id = $notebook WHERE scope = $scope AND notebook_id IS NULL;";
        update.Parameters.AddWithValue("$notebook", defaultId);
        update.Parameters.AddWithValue("$scope", scope);
        update.ExecuteNonQuery();
    }

    /// <summary>Returns the scope's default notebook, creating it on first use.</summary>
    public long EnsureDefaultNotebook(string scope)
    {
        using var connection = database.OpenConnection();
        using var find = connection.CreateCommand();
        find.CommandText = "SELECT id FROM kb_notebooks WHERE scope = $scope AND is_default = 1 LIMIT 1;";
        find.Parameters.AddWithValue("$scope", scope);
        if (find.ExecuteScalar() is long existing) return existing;

        var now = DateTime.UtcNow.ToString("o");
        // UNIQUE(scope, name) may trip on a user notebook called 默认笔记本 — adopt it instead.
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO kb_notebooks (scope, name, description, is_default, created_at, updated_at)
                VALUES ($scope, '默认笔记本', NULL, 1, $now, $now)
                ON CONFLICT(scope, name) DO UPDATE SET is_default = 1, updated_at = $now;
                """;
            insert.Parameters.AddWithValue("$scope", scope);
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();
        }
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT id FROM kb_notebooks WHERE scope = $scope AND is_default = 1 LIMIT 1;";
        select.Parameters.AddWithValue("$scope", scope);
        return (long)(select.ExecuteScalar() ?? 0L);
    }

    private bool NotebookExists(string scope, long id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM kb_notebooks WHERE scope = $scope AND id = $id;";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$id", id);
        return (long)(command.ExecuteScalar() ?? 0L) > 0;
    }

    private static long? FindNotebookByName(SqliteConnection connection, string scope, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM kb_notebooks WHERE scope = $scope AND lower(name) = lower($name) LIMIT 1;";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is long id ? id : null;
    }

    private static long LastRowId(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_insert_rowid();";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, long id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static KnowledgeNotebook ReadNotebook(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetInt64(3) != 0,
        (int)reader.GetInt64(6),
        (int)reader.GetInt64(7),
        reader.GetString(4),
        reader.GetString(5));

    private static KnowledgeSource ReadSource(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetString(5),
        (int)reader.GetInt64(6),
        reader.GetString(7),
        reader.GetString(8));

    private static List<KnowledgeEntryInfo> ReadInfos(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var infos = new List<KnowledgeEntryInfo>();
        while (reader.Read())
        {
            infos.Add(new KnowledgeEntryInfo(
                new KnowledgeEntry(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5)),
                reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? default(int?) : (int)reader.GetInt64(12)));
        }
        return infos;
    }
}
