using System.Text.Json;
using Haoyue.Runtime.Configuration;

namespace Haoyue.Runtime.Teams;

/// <summary>一条「在跑团体」索引：runId → 工作区根，供 daemon 重启时跨工作区对账。</summary>
public sealed record TeamRunIndexEntry(string RunId, string Workspace, DateTimeOffset StartedAt);

/// <summary>
/// 进程级在跑团体索引（用户目录 <c>team-runs-index.json</c>）。工作区轨迹文件各自独立，
/// 启动对账需要知道「上次进程在哪些工作区还有 running 轨迹」，因此单独维护这份轻量索引。
/// 原子写、损坏按空集处理。
/// </summary>
public static class TeamRunIndex
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(HaoyuePaths.Home, "team-runs-index.json");

    public static void Add(string runId, string workspace)
    {
        lock (Gate)
        {
            var entries = Load();
            entries.RemoveAll(entry => entry.RunId.Equals(runId, StringComparison.Ordinal));
            entries.Add(new TeamRunIndexEntry(runId, workspace, DateTimeOffset.UtcNow));
            Save(entries);
        }
    }

    public static void Remove(string runId)
    {
        lock (Gate)
        {
            var entries = Load();
            if (entries.RemoveAll(entry => entry.RunId.Equals(runId, StringComparison.Ordinal)) == 0) return;
            Save(entries);
        }
    }

    /// <summary>当前索引里的工作区根（去重，忽略空白与不存在的目录）。</summary>
    public static IReadOnlyList<string> ReadRoots()
    {
        lock (Gate)
        {
            return [.. Load()
                .Select(entry => entry.Workspace)
                .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 索引清理失败不影响启动；下次对账最多重复处理已收敛的记录。
            }
        }
    }

    private static List<TeamRunIndexEntry> Load()
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllText(FilePath), HaoyueJsonContext.Default.ListTeamRunIndexEntry) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void Save(List<TeamRunIndexEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(
                entries, HaoyueJsonContext.Default.ListTeamRunIndexEntry));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 索引是启动对账的增强；写失败时本轮运行仍可正常结束并清理轨迹。
        }
    }
}
