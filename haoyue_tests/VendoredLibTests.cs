using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace haoyue_tests;

/// <summary>
/// wwwroot/lib 下的前端库是手工 vendored 副本，没有包管理器兜底版本与完整性。
/// 这里把清单当作唯一事实来源：哈希防篡改、版本标记防“换了文件忘改版本”、
/// 引用覆盖防新增库绕过清单。CI 随 dotnet test 一起执行。
/// </summary>
public sealed class VendoredLibTests
{
    /// <summary>已审计过的下限版本：低于此值说明回退了安全更新。</summary>
    private static readonly Dictionary<string, string> MinimumVersions = new(StringComparer.Ordinal)
    {
        ["Bootstrap"] = "5.3.3",
        ["Bootstrap Icons"] = "1.11.3",
        ["highlight.js"] = "11.9.0",
        ["Mermaid"] = "11.17.0",
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "haoyue_webserver", "wwwroot", "lib", "manifest.json")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string LibRoot() => Path.Combine(RepoRoot(), "haoyue_webserver", "wwwroot", "lib");

    private static JsonDocument LoadManifest() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(LibRoot(), "manifest.json")));

    [Fact]
    public void Manifest_RecordsEveryFileOnce_WithMatchingHash()
    {
        using var manifest = LoadManifest();
        var libs = manifest.RootElement.GetProperty("libraries").EnumerateArray().ToList();

        Assert.NotEmpty(libs);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lib in libs)
        {
            var name = lib.GetProperty("name").GetString();
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.False(string.IsNullOrWhiteSpace(lib.GetProperty("version").GetString()), $"{name} 缺少版本");
            Assert.StartsWith("https://", lib.GetProperty("source").GetString(), StringComparison.Ordinal);

            foreach (var file in lib.GetProperty("files").EnumerateObject())
            {
                Assert.True(seen.Add(file.Name), $"{file.Name} 在清单中重复");
                var full = Path.Combine(LibRoot(), file.Name);
                Assert.True(File.Exists(full), $"{name} 声明的文件不存在：{file.Name}");
                var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))).ToLowerInvariant();
                Assert.Equal(file.Value.GetString(), actual);
            }
        }
    }

    [Fact]
    public void Manifest_VersionsAreNotBelowAuditedBaseline_AndMatchFileBanners()
    {
        using var manifest = LoadManifest();

        foreach (var lib in manifest.RootElement.GetProperty("libraries").EnumerateArray())
        {
            var name = lib.GetProperty("name").GetString()!;
            var version = lib.GetProperty("version").GetString()!;
            Assert.True(MinimumVersions.TryGetValue(name, out var minimum), $"{name} 未登记版本下限");
            Assert.True(CompareVersions(version, minimum!) >= 0, $"{name} 版本 {version} 低于下限 {minimum}");

            // 版本标记必须真的写在这些文件里，防止只改清单不改内容。
            var marker = lib.GetProperty("versionMarker").GetString()!;
            var files = lib.GetProperty("files").EnumerateObject().Select(f => f.Name).ToList();
            Assert.Contains(files, relative =>
                File.ReadAllText(Path.Combine(LibRoot(), relative)).Contains(marker, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Manifest_CoversEveryReferencedVendoredAsset()
    {
        using var manifest = LoadManifest();
        var listed = manifest.RootElement.GetProperty("libraries").EnumerateArray()
            .SelectMany(lib => lib.GetProperty("files").EnumerateObject().Select(f => f.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var root = RepoRoot();
        var sources = Directory.EnumerateFiles(Path.Combine(root, "haoyue_webserver", "Components"), "*.razor", SearchOption.AllDirectories)
            .Append(Path.Combine(root, "haoyue_webserver", "wwwroot", "js", "site.js"));

        var referenced = sources
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), "[\"'](?:/)?lib/([^\"']+)[\"']")
                .Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.NotEmpty(referenced);
        Assert.All(referenced, path =>
            Assert.Contains(path, listed));
    }

    private static int CompareVersions(string left, string right)
    {
        static int[] Parse(string value) => value.Split('.').Select(part =>
            int.TryParse(new string(part.TakeWhile(char.IsDigit).ToArray()), out var number) ? number : 0).ToArray();

        var a = Parse(left);
        var b = Parse(right);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var diff = (i < a.Length ? a[i] : 0) - (i < b.Length ? b[i] : 0);
            if (diff != 0) return diff;
        }

        return 0;
    }
}
