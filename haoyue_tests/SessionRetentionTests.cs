using System.Text;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Workspaces;
using Microsoft.Data.Sqlite;

namespace Haoyue.Tests;

/// <summary>
/// L-D 保留策略：消息图片引用化（内容寻址缓存）、过期会话归档、孤儿图片清理、
/// 每日 VACUUM 闸门。图片字节不再进 DB payload，读时新旧格式自动兼容。
/// </summary>
public sealed class SessionRetentionTests : IDisposable
{
    private readonly string _root;
    private readonly SessionStore _store;
    private readonly WorkspaceInfo _workspace;
    private readonly string _dbPath;

    public SessionRetentionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "haoyue-retention-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "haoyue.db");
        _store = new SessionStore(_dbPath);
        _workspace = new WorkspaceInfo { Root = _root, ProjectKinds = [] };
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string CacheDir => Path.Combine(_root, "session-images");

    private static readonly byte[] ImageBytes = [1, 2, 3, 4, 5];
    private static string ImageBase64 => Convert.ToBase64String(ImageBytes);

    private ChatMessage ImageMessage(string text = "看看这张图") =>
        ChatMessage.User(text, [new ChatImageAttachment("img-1", "shot.png", "image/png", ImageBase64, ImageBytes.Length)]);

    private string NewSession() => _store.Create(_workspace).Header.Id;

    private string ReadPayload(string sessionId)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM messages WHERE session_id = $id ORDER BY id;";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        var sb = new StringBuilder();
        while (reader.Read()) sb.Append(reader.GetString(0));
        return sb.ToString();
    }

    private void BackdateSession(string sessionId, TimeSpan age)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET updated_at = $ts WHERE scope = $scope AND id = $id;";
        command.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.Subtract(age).ToString("O"));
        command.Parameters.AddWithValue("$scope", HaoyueDatabase.ScopeKey(_workspace));
        command.Parameters.AddWithValue("$id", sessionId);
        command.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- 图片引用化

    [Fact]
    public void Append_WithImage_PayloadKeepsOnlyReference_AndCacheRoundTrips()
    {
        var id = NewSession();
        _store.Append(_store.Load(_workspace, id)!, ImageMessage());

        var reloaded = _store.Load(_workspace, id)!;
        var image = Assert.Single(reloaded.Messages[0].Images!);
        Assert.Equal(ImageBase64, image.Data);
        Assert.Equal("shot.png", image.Name);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(ImageBytes.Length, image.SizeBytes);

        // payload 里只有引用，没有 base64 字节。
        var payload = ReadPayload(id);
        Assert.DoesNotContain(ImageBase64, payload, StringComparison.Ordinal);
        Assert.Contains("\"path\":\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"sha256\":\"", payload, StringComparison.Ordinal);

        // 内容寻址缓存：一个文件，名为 sha256.png，内容与原图一致。
        var files = Assert.Single(Directory.GetFiles(CacheDir));
        var expectedHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(ImageBytes));
        Assert.Equal($"{expectedHash}.png", Path.GetFileName(files));
        Assert.True(File.ReadAllBytes(files).SequenceEqual(ImageBytes));
    }

    [Fact]
    public void Append_SameImageTwice_ReusesSingleCacheFile()
    {
        var id = NewSession();
        var session = _store.Load(_workspace, id)!;
        _store.Append(session, ImageMessage("第一张"));
        _store.Append(session, ImageMessage("同一张"));

        var file = Assert.Single(Directory.GetFiles(CacheDir));
        var reloaded = _store.Load(_workspace, id)!;
        Assert.All(reloaded.Messages, m => Assert.Equal(ImageBase64, Assert.Single(m.Images!).Data));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Load_LegacyInlineBase64_StillResolves()
    {
        var id = NewSession();
        // 手工插入一条升级前的旧格式 payload：图片数据内联，无 path/sha256 字段。
        var legacy = new JsonObject
        {
            ["role"] = "user",
            ["text"] = "旧记录里的图",
            ["images"] = new JsonArray(new JsonObject
            {
                ["id"] = "legacy",
                ["name"] = "old.png",
                ["mediaType"] = "image/png",
                ["data"] = ImageBase64,
                ["sizeBytes"] = ImageBytes.Length,
            }),
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
        }.ToJsonString();
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO messages(scope, session_id, payload_json, timestamp)
                VALUES($scope, $id, $payload, $ts);
                """;
            command.Parameters.AddWithValue("$scope", HaoyueDatabase.ScopeKey(_workspace));
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$payload", legacy);
            command.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        var reloaded = _store.Load(_workspace, id)!;
        var image = Assert.Single(reloaded.Messages[0].Images!);
        Assert.Equal(ImageBase64, image.Data);
        // 旧记录不产生缓存文件（数据本来就在 payload 里）。
        Assert.False(Directory.Exists(CacheDir));
    }

    [Fact]
    public void Load_MissingCacheFile_DegradesGracefully()
    {
        var id = NewSession();
        _store.Append(_store.Load(_workspace, id)!, ImageMessage());
        Assert.Single(_store.Load(_workspace, id)!.Messages[0].Images!);

        // 缓存被外部清掉（用户手动清理/磁盘故障）：该图降级剔除，文本与消息结构完好。
        Directory.Delete(CacheDir, recursive: true);
        var reloaded = _store.Load(_workspace, id)!;
        Assert.Equal("看看这张图", reloaded.Messages[0].Text);
        Assert.True(reloaded.Messages[0].Images is null or { Count: 0 });
    }

    // ---------------------------------------------------------------- 归档与清理

    [Fact]
    public void RunMaintenance_ArchivesStaleSessions_KeepsFreshOnes()
    {
        var staleId = NewSession();
        var freshId = NewSession();
        BackdateSession(staleId, TimeSpan.FromDays(40));

        var result = _store.RunMaintenance(30);

        Assert.Equal(1, result.ArchivedSessions);
        var visible = _store.List(_workspace).Select(h => h.Id);
        Assert.Contains(freshId, visible);
        Assert.DoesNotContain(staleId, visible);
        var all = _store.List(_workspace, includeArchived: true).Select(h => h.Id);
        Assert.Contains(staleId, all); // 归档不删数据，仍可找回
    }

    [Fact]
    public void RunMaintenance_ZeroRetention_SkipsArchiving()
    {
        var staleId = NewSession();
        BackdateSession(staleId, TimeSpan.FromDays(400));

        var result = _store.RunMaintenance(0);

        Assert.Equal(0, result.ArchivedSessions);
        Assert.Contains(staleId, _store.List(_workspace).Select(h => h.Id));
    }

    [Fact]
    public void RunMaintenance_PurgesOrphanImages_KeepsReferencedAndFresh()
    {
        var id = NewSession();
        _store.Append(_store.Load(_workspace, id)!, ImageMessage());
        Directory.CreateDirectory(CacheDir);
        var referenced = Assert.Single(Directory.GetFiles(CacheDir));

        // 旧孤儿（无引用且超过保留期）应被清；新孤儿（mtime 在保留期内）受保护；
        // 被引用的缓存文件必须保留。
        var oldOrphan = Path.Combine(CacheDir, "deadbeef.png");
        File.WriteAllBytes(oldOrphan, [9, 9, 9]);
        File.SetLastWriteTimeUtc(oldOrphan, DateTime.UtcNow.AddDays(-40));
        var freshOrphan = Path.Combine(CacheDir, "cafe.png");
        File.WriteAllBytes(freshOrphan, [8, 8, 8]);

        var result = _store.RunMaintenance(30);

        Assert.Equal(1, result.PurgedImages);
        Assert.False(File.Exists(oldOrphan));
        Assert.True(File.Exists(freshOrphan));
        Assert.True(File.Exists(referenced));
    }

    [Fact]
    public void RunMaintenance_VacuumOncePerDay()
    {
        var first = _store.RunMaintenance(90);
        Assert.True(first.Vacuumed);

        var second = _store.RunMaintenance(90);
        Assert.False(second.Vacuumed);
    }

    [Fact]
    public void RunMaintenance_ToleratesEmptyDatabase()
    {
        var result = _store.RunMaintenance(90);
        Assert.Equal(0, result.ArchivedSessions);
        Assert.Equal(0, result.PurgedImages);
        Assert.True(result.Vacuumed);
    }
}
