using Microsoft.Data.Sqlite;

namespace Haoyue.Runtime.Data;

public sealed record KnowledgeEntry(long Id, string Title, string Content, string? Tags, string CreatedAt, string UpdatedAt);

public sealed record KnowledgeSaveResult(KnowledgeEntry Entry, bool Created);

/// <summary>
/// Workspace-scoped knowledge base the agent extends through the knowledge_* tools.
/// Entries match by substring instead of FTS: SQLite tokenizers do not segment CJK
/// text, so LIKE search is what makes mixed Chinese/English queries actually work.
/// Knowledge bases are small (tens to hundreds of entries per workspace), so a
/// scanned in-memory score stays fast and correct. Query preprocessing (normalization,
/// CJK bigram slicing, synonyms, typo fallback) lives in KnowledgeSearchRanker.
/// </summary>
public sealed class KnowledgeStore(HaoyueDatabase database)
{
    public KnowledgeSaveResult Save(string scope, string title, string content, string? tags)
    {
        var trimmedTitle = title.Trim();
        var trimmedContent = content.Trim();
        var trimmedTags = string.IsNullOrWhiteSpace(tags) ? null : tags.Trim();
        var now = DateTime.UtcNow.ToString("o");

        using var connection = database.OpenConnection();
        long? existingId = null;
        string? existingCreatedAt = null;
        using (var find = connection.CreateCommand())
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

        if (existingId is long id)
        {
            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE knowledge
                SET content = $content, tags = $tags, updated_at = $now
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$content", trimmedContent);
            update.Parameters.AddWithValue("$tags", (object?)trimmedTags ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
            return new KnowledgeSaveResult(
                new KnowledgeEntry(id, trimmedTitle, trimmedContent, trimmedTags, existingCreatedAt ?? now, now),
                Created: false);
        }

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO knowledge (scope, title, content, tags, created_at, updated_at)
                VALUES ($scope, $title, $content, $tags, $now, $now);
                """;
            insert.Parameters.AddWithValue("$scope", scope);
            insert.Parameters.AddWithValue("$title", trimmedTitle);
            insert.Parameters.AddWithValue("$content", trimmedContent);
            insert.Parameters.AddWithValue("$tags", (object?)trimmedTags ?? DBNull.Value);
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();
        }

        using var lastId = connection.CreateCommand();
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

    public IReadOnlyList<KnowledgeEntry> Search(string scope, string query, int limit = 8)
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

    public int Count(string scope)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM knowledge WHERE scope = $scope;";
        command.Parameters.AddWithValue("$scope", scope);
        return (int)(long)(command.ExecuteScalar() ?? 0L);
    }
}
