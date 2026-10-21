using System.Text.Json.Nodes;
using Haoyue.Runtime.Connectors;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// 6.3 清单信任分级：source 字段（official/verified/community/local）+ 完整性清单校验
/// ——official 声明必须有 SHA256SUMS 登记为证，社区包自填不能生效；旧包缺省 local
/// 100% 兼容。
/// </summary>
public sealed class ConnectorSourceTrustTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-connector-source-" + Guid.NewGuid().ToString("N"));

    public ConnectorSourceTrustTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Root(string tag)
    {
        var root = Path.Combine(_dir, tag);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WritePackage(string root, string id, JsonObject extra)
    {
        var packageDir = Path.Combine(root, id);
        Directory.CreateDirectory(packageDir);
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = id,
            ["name"] = id,
            ["description"] = "test package",
            ["category"] = "dev",
            ["server"] = new JsonObject { ["transport"] = "stdio", ["command"] = "fake" },
        };
        foreach (var (key, value) in extra) manifest[key] = value?.DeepClone();
        File.WriteAllText(Path.Combine(packageDir, "connector.json"), manifest.ToJsonString());
    }

    /// <summary>清单行：只要求条目存在（IsListed 不比对 hash，校验由 Verify 负责）。</summary>
    private static void WriteSums(string root, params string[] relativePaths)
    {
        var lines = relativePaths.Select(path => new string('a', 64) + "  " + path);
        File.WriteAllText(Path.Combine(root, ConnectorIntegrity.SumsFileName), string.Join('\n', lines));
    }

    [Fact]
    public void LegacyPackage_WithoutSourceField_LoadsAsLocal()
    {
        // 旧包（无 source）：100% 兼容加载，来源按最低可信度 local。
        var root = Root("legacy");
        WritePackage(root, "legacy-connector", new JsonObject());

        var package = ConnectorCatalog.LoadAll(root).Single();
        Assert.Equal("local", package.EffectiveSource);
        Assert.Null(package.SourceWarning);
    }

    [Fact]
    public void SelfDeclaredOfficial_WithoutSumsEntry_IsDowngradedToCommunity()
    {
        // 社区包自填 official 撒谎：未登记在 SHA256SUMS → 降级 community 并给出警告。
        var root = Root("liar");
        WritePackage(root, "liar-connector", new JsonObject { ["source"] = "official" });
        WriteSums(root, "other-connector/connector.json"); // 清单存在，但没登记本包

        var package = ConnectorCatalog.LoadAll(root).Single();
        Assert.Equal("community", package.EffectiveSource);
        Assert.NotNull(package.SourceWarning);
        Assert.Contains("未登记", package.SourceWarning!, StringComparison.Ordinal);
    }

    [Fact]
    public void OfficialDeclaration_WithSumsEntry_IsHonored()
    {
        // 受管分发目录：包清单登记在完整性清单 → official 声明成立。
        var root = Root("official");
        WritePackage(root, "official-connector", new JsonObject { ["source"] = "official" });
        WriteSums(root, "official-connector/connector.json");

        var package = ConnectorCatalog.LoadAll(root).Single();
        Assert.Equal("official", package.EffectiveSource);
        Assert.Null(package.SourceWarning);
    }

    [Fact]
    public void OfficialDeclaration_InDirectoryWithoutSums_IsDowngraded()
    {
        // 自建目录（无清单）：official 无从证明 → 同样降级。
        var root = Root("nosums");
        WritePackage(root, "nosums-connector", new JsonObject { ["source"] = "official" });

        var package = ConnectorCatalog.LoadAll(root).Single();
        Assert.Equal("community", package.EffectiveSource);
        Assert.NotNull(package.SourceWarning);
    }

    [Fact]
    public void UnknownSourceValue_IsTreatedAsCommunity()
    {
        // 未知取值不静默信任：按最低可信度处理。
        var root = Root("unknown");
        WritePackage(root, "unknown-connector", new JsonObject { ["source"] = "super-trusted" });

        Assert.Equal("community", ConnectorCatalog.LoadAll(root).Single().EffectiveSource);
    }

    [Fact]
    public void VerifiedAndCommunityDeclarations_PassThroughUnchanged()
    {
        var root = Root("plain");
        WritePackage(root, "verified-connector", new JsonObject { ["source"] = "verified" });
        WritePackage(root, "community-connector", new JsonObject { ["source"] = "community" });

        var packages = ConnectorCatalog.LoadAll(root);
        Assert.Equal("verified", packages.Single(p => p.Id == "verified-connector").EffectiveSource);
        Assert.Equal("community", packages.Single(p => p.Id == "community-connector").EffectiveSource);
        Assert.All(packages, package => Assert.Null(package.SourceWarning));
    }

    [Fact]
    public void MinRuntimeVersion_IsParsedThrough()
    {
        var root = Root("minver");
        WritePackage(root, "minver-connector", new JsonObject { ["minRuntimeVersion"] = "1.3.20" });

        Assert.Equal("1.3.20", ConnectorCatalog.LoadAll(root).Single().MinRuntimeVersion);
    }
}
