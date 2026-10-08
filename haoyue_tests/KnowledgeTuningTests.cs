using Haoyue.Runtime.Data;

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
