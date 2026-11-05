namespace Haoyue.Runtime.Teams;

/// <summary>
/// daemon 启动对账：上次进程异常退出时，工作区轨迹里可能残留 running。
/// 这里按 <see cref="TeamRunIndex"/> 记录的工作区逐个收敛为 interrupted，写审计原因，
/// 并清理可能残留的临时文件。
/// </summary>
public static class TeamRunReconciler
{
    public const string InterruptedReason = "Runtime 重启时该团体运行仍在 running，已标记为 interrupted。";

    public static int ReconcileOnStartup()
    {
        var reconciled = 0;
        foreach (var root in TeamRunIndex.ReadRoots())
        {
            reconciled += TeamRunStore.ReconcileRoot(root, InterruptedReason);
            TryDelete(Path.Combine(root, ".haoyue", "team-runs.json.tmp"));
        }
        TeamRunIndex.Clear();
        return reconciled;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时产物清理是尽力而为；残留不影响后续读写（写入会覆盖同名临时文件）。
        }
    }
}
