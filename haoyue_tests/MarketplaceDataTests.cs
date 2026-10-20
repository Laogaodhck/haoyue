using System.Text.Json;
using System.Text.Json.Serialization;
using Haoyue.Runtime.Skills;

namespace Haoyue.Tests;

/// <summary>
/// 技能市场站点数据治理：marketplace/data 下的 skills.json / connectors.json 是
/// OfficialSkillCatalog 与 connectors/ 目录的导出快照，供纯静态站点（GitHub Pages）
/// 消费。与 daemon-contract.json 同一套治理模式——提交的快照必须与代码单源一致，
/// 漂移即测试失败；设置 HAOYUE_EXPORT_MARKETPLACE=1 运行测试以重新生成。
/// </summary>
public class MarketplaceDataTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Haoyue.slnx")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("未能定位仓库根（缺 Haoyue.slnx）");
        return dir.FullName;
    }

    public sealed record SkillEntry(
        [property: JsonPropertyName("slug")] string Slug,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("tags")] string[] Tags,
        [property: JsonPropertyName("prompt")] string Prompt);

    public sealed record ConnectorEntry(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("manifest")] JsonElement Manifest);

    private static string ExportSkills() => JsonSerializer.Serialize(
        OfficialSkillCatalog.Entries.Select(e => new SkillEntry(
            e.Slug, e.Name, e.Description, e.Version, e.Tags, e.Prompt)),
        new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static string ExportConnectors()
    {
        var root = Path.Combine(FindRepoRoot(), "connectors");
        var entries = Directory.GetDirectories(root)
            .Select(dir => Path.Combine(dir, "connector.json"))
            .Where(File.Exists)
            .Select(file => new ConnectorEntry(
                Path.GetFileName(Path.GetDirectoryName(file))!,
                JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone()))
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
        return JsonSerializer.Serialize(entries,
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>
    /// 快照比对必须与行尾无关。
    /// </summary>
    /// <remarks>
    /// 序列化器的换行是平台相关的（Windows 上写出来是 CRLF，仓库按 <c>eol=lf</c> 检出的是 LF），
    /// 直接逐字符比较会让同一份代码在本地与 CI 判出相反结果——CI 上曾因此失败：
    /// 本地生成快照时带 CRLF，检出侧是 LF。统一归一为 <c>\n</c> 后再比。
    /// </remarks>
    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd();

    [Fact]
    public void Marketplace_Data_MatchesSingleSource()
    {
        var dataDir = Path.Combine(FindRepoRoot(), "marketplace", "data");
        var skillsPath = Path.Combine(dataDir, "skills.json");
        var connectorsPath = Path.Combine(dataDir, "connectors.json");

        if (Environment.GetEnvironmentVariable("HAOYUE_EXPORT_MARKETPLACE") == "1")
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(skillsPath, Normalize(ExportSkills()) + "\n");
            File.WriteAllText(connectorsPath, Normalize(ExportConnectors()) + "\n");
            return;
        }

        Assert.True(File.Exists(skillsPath),
            $"marketplace/data/skills.json 缺失：设置 HAOYUE_EXPORT_MARKETPLACE=1 运行测试以生成");
        Assert.True(File.Exists(connectorsPath),
            $"marketplace/data/connectors.json 缺失：设置 HAOYUE_EXPORT_MARKETPLACE=1 运行测试以生成");

        var skillsActual = Normalize(ExportSkills());
        var skillsCommitted = Normalize(File.ReadAllText(skillsPath));
        Assert.True(skillsActual == skillsCommitted,
            "官方技能目录与 marketplace/data/skills.json 不一致：" +
            "请设置 HAOYUE_EXPORT_MARKETPLACE=1 重新生成快照并提交。");

        var connectorsActual = Normalize(ExportConnectors());
        var connectorsCommitted = Normalize(File.ReadAllText(connectorsPath));
        Assert.True(connectorsActual == connectorsCommitted,
            "连接器目录与 marketplace/data/connectors.json 不一致：" +
            "请设置 HAOYUE_EXPORT_MARKETPLACE=1 重新生成快照并提交。");
    }
}
