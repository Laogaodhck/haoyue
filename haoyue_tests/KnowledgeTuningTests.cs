using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Haoyue.Runtime;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Daemon;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>
/// KnowledgeTuning hot-reload: user synonym file entries merge over the built-in
/// table, timestamp changes trigger reloads, malformed lines are ignored, and
/// normalization applies to file content exactly like to queries. Uses keys that
/// do not collide with the built-in table so parallel evaluation tests are safe.
/// </summary>
[Collection("KnowledgeTuningSerial")]
public class KnowledgeTuningTests : IDisposable
{
    private readonly string _file;

    public KnowledgeTuningTests()
    {
        _file = Path.Combine(Path.GetTempPath(), "haoyue-kt-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        KnowledgeTuning.ConfigurePaths(_file);
    }

    public void Dispose()
    {
        File.Delete(_file);
        KnowledgeTuning.ResetForTests();
    }

    [Fact]
    public void UserFile_ExtendsBuiltinTable()
    {
        File.WriteAllLines(_file, ["# 注释行", "", "芥末 = wasabi, 绿芥", "乱码 = 编码, mojibake"]);
        var synonyms = KnowledgeTuning.Synonyms;

        Assert.Equal(["wasabi", "绿芥"], synonyms["芥末"]); // new key added
        Assert.Contains("mojibake", synonyms["乱码"]);     // builtin key extended
        Assert.Equal("编码", synonyms["乱码"][0]);          // builtin values kept first
        Assert.True(synonyms.ContainsKey("部署"));          // builtin table intact
    }

    [Fact]
    public void FileChange_IsPickedUpWithoutRestart()
    {
        File.WriteAllLines(_file, ["芥末 = wasabi"]);
        Assert.Equal(["wasabi"], KnowledgeTuning.Synonyms["芥末"]);

        File.WriteAllLines(_file, ["芥末 = horseradish"]);
        Assert.Equal(["horseradish"], KnowledgeTuning.Synonyms["芥末"]);
    }

    /// <summary>
    /// 等长改写也要立刻生效：长度不变时「时间 + 长度」指纹无法区分新旧内容，
    /// 且两次写入很可能落在同一个时间戳桶里（NTFS ≈15.6 ms，共享目录更粗）。
    /// 这是外部编辑器改表的真实形态，不是测试造出来的边界。
    /// </summary>
    [Fact]
    public void SameLengthRewrite_IsPickedUp()
    {
        File.WriteAllLines(_file, ["芥末 = wasabi"]);
        Assert.Equal(["wasabi"], KnowledgeTuning.Synonyms["芥末"]);

        File.WriteAllLines(_file, ["芥末 = durian"]); // wasabi → durian：等长
        Assert.Equal(["durian"], KnowledgeTuning.Synonyms["芥末"]);
    }

    /// <summary>
    /// daemon 保存同义词后，紧接着的检索必须看到新内容（真实保存路径：
    /// knowledge.synonyms.save → 内存表失效 → KnowledgeSearchRanker.BuildTokens）。
    /// 刻意用等长内容，避免靠「长度变了」偶然通过。
    /// </summary>
    [Fact]
    public async Task DaemonSave_IsVisibleToNextSearch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "haoyue-kt-daemon-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var store = new ConfigStore(Path.Combine(dir, "config.json"), Path.Combine(dir, "state.json"));
            var workspace = new WorkspaceManager().CreateGlobal(Path.Combine(dir, "global"));
            await using var runtime = HaoyueRuntime.CreateIsolated(workspace, configureServices: services =>
            {
                services.AddSingleton<IConfigStore>(store);
                services.AddSingleton(new HaoyueDatabase(Path.Combine(dir, "state.db")));
                services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
            });
            var admin = new DaemonAdminApi(runtime, workspace, new FileLockCoordinator());

            admin.SaveKnowledgeSynonyms(new JsonObject { ["content"] = "芥末 = wasabi\n" });
            Assert.Equal(["wasabi"], KnowledgeTuning.Synonyms["芥末"]);
            Assert.Contains(KnowledgeSearchRanker.BuildTokens("芥末"),
                token => token.ExactVariants.Contains("wasabi"));

            admin.SaveKnowledgeSynonyms(new JsonObject { ["content"] = "芥末 = durian\n" });
            Assert.Equal(["durian"], KnowledgeTuning.Synonyms["芥末"]);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MissingFile_FallsBackToBuiltinTable()
    {
        Assert.False(KnowledgeTuning.Synonyms.ContainsKey("芥末"));
        Assert.True(KnowledgeTuning.Synonyms.ContainsKey("部署"));
    }

    [Fact]
    public void Normalize_AppliesToFileEntries()
    {
        File.WriteAllLines(_file, ["ＰＹＴＨＯＮ ＴＩＰＳ = python"]); // full-width key normalizes to "python tips"
        var synonyms = KnowledgeTuning.Synonyms;
        Assert.True(synonyms.ContainsKey("python tips"));
        Assert.DoesNotContain(synonyms.Keys, k => k.Contains('Ｐ'));
    }

    [Fact]
    public void Tokens_UseTuningEntries()
    {
        File.WriteAllLines(_file, ["芥末 = wasabi"]);
        var tokens = KnowledgeSearchRanker.BuildTokens("芥末");
        var token = Assert.Single(tokens);
        Assert.Contains("wasabi", token.ExactVariants);
    }

    [Fact]
    public void Weights_AreNamedConstants()
    {
        // Guard the documented scoring: title 3, content 2, tags 2.
        Assert.Equal(3, KnowledgeTuning.TitleWeight);
        Assert.Equal(2, KnowledgeTuning.ContentWeight);
        Assert.Equal(2, KnowledgeTuning.TagsWeight);
        Assert.Equal(1, KnowledgeTuning.FuzzyShortWordDistance);
        Assert.Equal(2, KnowledgeTuning.FuzzyLongWordDistance);
    }
}
