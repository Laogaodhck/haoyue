using Microsoft.Data.Sqlite;

namespace Haoyue.Runtime.Data;

public sealed record KnowledgeEntry(long Id, string Title, string Content, string? Tags, string CreatedAt, string UpdatedAt);

public sealed record KnowledgeSaveResult(KnowledgeEntry Entry, bool Created);

/// <summary>A notebook (collection) inside one scope — the QMind-style grouping unit.</summary>
public sealed record KnowledgeNotebook(
    long Id, string Name, string? Description, bool IsDefault, int EntryCount, int SourceCount, string CreatedAt, string UpdatedAt);

/// <summary>An ingest unit inside a notebook: a text snippet, a local file or a fetched URL.</summary>
public sealed record KnowledgeSource(
    long Id, long NotebookId, string Kind, string Title, string? Locator, string Content, int ChunkCount, string CreatedAt, string UpdatedAt);

/// <summary>An entry plus its notebook/source attribution, for knowledge-center views.</summary>
public sealed record KnowledgeEntryInfo(
    KnowledgeEntry Entry, long NotebookId, string? NotebookName, long? SourceId,
    string? SourceTitle, string? SourceKind, string? SourceLocator, int? ChunkOrdinal);

/// <summary>
/// Workspace-scoped knowledge base the agent extends through the knowledge_* tools.
/// Entries match by substring instead of FTS: SQLite tokenizers do not segment CJK
/// text, so LIKE search is what makes mixed Chinese/English queries actually work.
/// Knowledge bases are small (tens to hundreds of entries per workspace), so a
/// scanned in-memory score stays fast and correct. Query preprocessing (normalization,
/// CJK bigram slicing, synonyms, typo fallback) lives in KnowledgeSearchRanker.
/// Notebook/source management (the knowledge-center layer) lives in the partial
/// halves of this class — KnowledgeCenter.cs.
/// </summary>
public sealed partial class KnowledgeStore(HaoyueDatabase database)
{
    /// <summary>
    /// Creates an entry (same-title upsert) or, when <paramref name="id"/> is given,
    /// updates that entry in place — including its title, so renaming from the
    /// desktop editor does not silently fork the entry. New entries land in
    /// <paramref name="notebookId"/>, or the scope's default notebook when omitted.
    /// </summary>
    public KnowledgeSaveResult Save(string scope, string title, string content, string? tags, long? id = null, long? notebookId = null)
    {
        var trimmedTitle = title.Trim();
        var trimmedContent = content.Trim();
        var trimmedTags = string.IsNullOrWhiteSpace(tags) ? null : tags.Trim();
        var now = DateTime.UtcNow.ToString("o");

        if (id is long updateId)
        {
            using var connection = database.OpenConnection();
            string? createdAt;
            using (var find = connection.CreateCommand())
            {
                find.CommandText = "SELECT created_at FROM knowledge WHERE scope = $scope AND id = $id;";
                find.Parameters.AddWithValue("$scope", scope);
                find.Parameters.AddWithValue("$id", updateId);
                using var reader = find.ExecuteReader();
                if (!reader.Read())
                    throw new KeyNotFoundException($"No knowledge entry #{updateId} in this scope.");
                createdAt = reader.GetString(0);
            }
            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE knowledge
                SET title = $title, content = $content, tags = $tags, updated_at = $now
                WHERE scope = $scope AND id = $id;
                """;
            update.Parameters.AddWithValue("$title", trimmedTitle);
            update.Parameters.AddWithValue("$content", trimmedContent);
            update.Parameters.AddWithValue("$tags", (object?)trimmedTags ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$scope", scope);
            update.Parameters.AddWithValue("$id", updateId);
            update.ExecuteNonQuery();
            return new KnowledgeSaveResult(
                new KnowledgeEntry(updateId, trimmedTitle, trimmedContent, trimmedTags, createdAt, now),
                Created: false);
        }

        EnsureScopeReady(scope);
        var targetNotebook = notebookId ?? EnsureDefaultNotebook(scope);
        if (notebookId is long requested && !NotebookExists(scope, requested))
            throw new KeyNotFoundException($"No knowledge notebook #{requested} in this scope.");

        using var connection2 = database.OpenConnection();
        long? existingId = null;
        string? existingCreatedAt = null;
        using (var find = connection2.CreateCommand())
        {
            find.CommandText = """
                SELECT id, created_at FROM knowledge
                WHERE scope = $scope AND lower(title) = lower($title)
                ORDER BY id LIMIT 1;
                """;
            find.Parameters.AddWithValue("$scope", scope);
            find.Parameters.AddWithValue("$title", trimmedTitle);
            using var reader = find.ExecuteReader();
            if (reader.Read())
            {
                existingId = reader.GetInt64(0);
                existingCreatedAt = reader.GetString(1);
            }
        }

        if (existingId is long matchedId)
        {
            using var update = connection2.CreateCommand();
            update.CommandText = """
                UPDATE knowledge
                SET content = $content, tags = $tags, updated_at = $now
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$content", trimmedContent);
            update.Parameters.AddWithValue("$tags", (object?)trimmedTags ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$id", matchedId);
            update.ExecuteNonQuery();
            return new KnowledgeSaveResult(
                new KnowledgeEntry(matchedId, trimmedTitle, trimmedContent, trimmedTags, existingCreatedAt ?? now, now),
                Created: false);
        }

        using (var insert = connection2.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO knowledge (scope, title, content, tags, notebook_id, created_at, updated_at)
                VALUES ($scope, $title, $content, $tags, $notebook, $now, $now);
                """;
            insert.Parameters.AddWithValue("$scope", scope);
            insert.Parameters.AddWithValue("$title", trimmedTitle);
            insert.Parameters.AddWithValue("$content", trimmedContent);
            insert.Parameters.AddWithValue("$tags", (object?)trimmedTags ?? DBNull.Value);
            insert.Parameters.AddWithValue("$notebook", targetNotebook);
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();
        }

        using var lastId = connection2.CreateCommand();
        lastId.CommandText = "SELECT last_insert_rowid();";
        var newId = (long)(lastId.ExecuteScalar() ?? 0L);
        return new KnowledgeSaveResult(
            new KnowledgeEntry(newId, trimmedTitle, trimmedContent, trimmedTags, now, now),
            Created: true);
    }

    public KnowledgeEntry? Get(string scope, long id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, content, tags, created_at, updated_at FROM knowledge WHERE scope = $scope AND id = $id;";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new KnowledgeEntry(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5))
            : null;
    }

    public bool Delete(string scope, long id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM knowledge WHERE scope = $scope AND id = $id;";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<KnowledgeEntry> Search(string scope, string query, int limit = 8, string? tag = null)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, content, tags, created_at, updated_at FROM knowledge WHERE scope = $scope;";
        command.Parameters.AddWithValue("$scope", scope);
        var entries = new List<KnowledgeEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new KnowledgeEntry(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        }

        if (!string.IsNullOrWhiteSpace(tag))
            entries = entries.Where(entry => HasTag(entry.Tags, tag)).ToList();

        return KnowledgeSearchRanker.Rank(entries, query, limit);
    }

    public IReadOnlyList<KnowledgeEntry> List(string scope, int limit = 100)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, content, tags, created_at, updated_at FROM knowledge
            WHERE scope = $scope ORDER BY updated_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var entries = new List<KnowledgeEntry>();
        while (reader.Read())
        {
            entries.Add(new KnowledgeEntry(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        }
        return entries;
    }

    /// <summary>
    /// Entries carrying exactly the given tag (comma-split comparison, not substring —
    /// "build" must not match "buildtool"). Ordering matches <see cref="List"/>.
    /// </summary>
    public IReadOnlyList<KnowledgeEntry> ListByTag(string scope, string tag, int limit = 500)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, content, tags, created_at, updated_at FROM knowledge
            WHERE scope = $scope ORDER BY updated_at DESC;
            """;
        command.Parameters.AddWithValue("$scope", scope);
        using var reader = command.ExecuteReader();
        var entries = new List<KnowledgeEntry>();
        while (reader.Read())
        {
            var entry = new KnowledgeEntry(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5));
            if (HasTag(entry.Tags, tag)) entries.Add(entry);
        }
        return entries.Take(limit).ToList();
    }

    /// <summary>Distinct tags with occurrence counts, most used first.</summary>
    public IReadOnlyList<(string Tag, int Count)> TagCounts(string scope)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tags FROM knowledge WHERE scope = $scope AND tags IS NOT NULL;";
        command.Parameters.AddWithValue("$scope", scope);
        using var reader = command.ExecuteReader();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.Read())
        {
            foreach (var tag in SplitTags(reader.GetString(0)))
            {
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
            }
        }
        return counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => (pair.Key, pair.Value))
            .ToList();
    }

    public int Count(string scope)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM knowledge WHERE scope = $scope;";
        command.Parameters.AddWithValue("$scope", scope);
        return (int)(long)(command.ExecuteScalar() ?? 0L);
    }

    private static string[] SplitTags(string? tags) => string.IsNullOrWhiteSpace(tags)
        ? []
        : tags.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => tag.Length > 0)
            .ToArray();

    private static bool HasTag(string? tags, string tag)
    {
        var needle = tag.Trim();
        return needle.Length > 0 && SplitTags(tags).Contains(needle, StringComparer.OrdinalIgnoreCase);
    }
}
