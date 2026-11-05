using System.Text.Json;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Teams;

/// <summary>
/// 团体运行轨迹存储：每个项目工作区 <c>&lt;root&gt;/.haoyue/team-runs.json</c>，
/// 只保留最近 <see cref="MaxRecords"/> 次运行。运行过程中每完成一个成员就落盘一次，
/// 因此桌面可以轮询到实时轨迹与失败原因；跨项目不可见。
/// </summary>
public sealed class TeamRunStore
{
    public const int MaxRecords = 20;

    private static readonly object Gate = new();

    public static string FileFor(WorkspaceInfo workspace) =>
        Path.Combine(workspace.HaoyueDir, "team-runs.json");

    public IReadOnlyList<TeamRun> List(WorkspaceInfo workspace, string? teamId = null, int limit = MaxRecords)
    {
        lock (Gate)
        {
            var runs = Load(workspace);
            if (!string.IsNullOrWhiteSpace(teamId))
                runs = runs.Where(run => run.TeamId.Equals(teamId, StringComparison.Ordinal)).ToList();
            return runs
                .OrderByDescending(run => run.StartedAt)
                .Take(Math.Clamp(limit, 1, MaxRecords))
                .ToList();
        }
    }

    public TeamRun? Get(WorkspaceInfo workspace, string runId)
    {
        lock (Gate)
        {
            return Load(workspace).FirstOrDefault(run => run.RunId.Equals(runId, StringComparison.Ordinal));
        }
    }

    /// <summary>把一个工作区里所有残留 running 收敛为 interrupted，返回收敛条数。</summary>
    public static int Reconcile(WorkspaceInfo workspace, string reason) =>
        ReconcileRoot(workspace.Root, reason);

    /// <summary>按工作区根路径直接对账（启动时未必有完整 WorkspaceInfo）。</summary>
    public static int ReconcileRoot(string root, string reason)
    {
        var file = Path.Combine(root, ".haoyue", "team-runs.json");
        lock (Gate)
        {
            var runs = LoadFile(file);
            var now = DateTimeOffset.UtcNow;
            var reconciled = 0;
            for (var index = 0; index < runs.Count; index++)
            {
                if (!runs[index].Status.Equals("running", StringComparison.Ordinal)) continue;
                runs[index] = runs[index] with
                {
                    Status = "interrupted",
                    EndedAt = now,
                    Error = reason,
                };
                reconciled++;
            }
            if (reconciled > 0) SaveFile(file, runs);
            return reconciled;
        }
    }

    /// <summary>按 runId 覆盖写入（运行中多次更新同一条），并裁剪到最近 N 条。</summary>
    public void Save(WorkspaceInfo workspace, TeamRun run)
    {
        lock (Gate)
        {
            var runs = Load(workspace);
            var index = runs.FindIndex(item => item.RunId.Equals(run.RunId, StringComparison.Ordinal));
            if (index >= 0) runs[index] = run;
            else runs.Add(run);
            runs = runs
                .OrderByDescending(item => item.StartedAt)
                .Take(MaxRecords)
                .ToList();
            SaveAll(workspace, runs);
        }
    }

    private static List<TeamRun> Load(WorkspaceInfo workspace) => LoadFile(FileFor(workspace));

    private static List<TeamRun> LoadFile(string file)
    {
        if (!File.Exists(file)) return [];
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllText(file), HaoyueJsonContext.Default.ListTeamRun) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void SaveAll(WorkspaceInfo workspace, List<TeamRun> runs) =>
        SaveFile(FileFor(workspace), runs);

    private static void SaveFile(string file, List<TeamRun> runs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(runs, HaoyueJsonContext.Default.ListTeamRun));
        File.Move(temp, file, overwrite: true);
    }
}
