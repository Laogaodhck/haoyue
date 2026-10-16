using System.Text.Json;
using System.Text.Json.Nodes;

namespace Haoyue.Runtime.Daemon;

/// <summary>
/// Workspace file listing for the desktop composer's @-mention picker. Split out of
/// DaemonAdminApi.cs to keep that file inside its line budget (ADR-20261015).
/// </summary>
internal sealed partial class DaemonAdminApi
{
    /// <summary>
    /// Relative file paths inside one workspace for the composer's @-mention picker.
    /// Shares the tool-side ignore rules (VCS, build output, .haoyue) and only ever
    /// reads directory entries — file contents are never touched or returned.
    /// </summary>
    public string ListWorkspaceFiles(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var query = OptionalString(parameters, "query")?.ToLowerInvariant() ?? "";
        var limit = Math.Clamp(parameters["limit"]?.GetValue<int?>() ?? 30, 1, 100);
        var root = workspace.Root;
        // root 的每一段都按父目录判定（probe 只是占位叶子）：~/.ssh、~/.aws 这类目录
        // 不给列举。选中的文件会进 prompt，也就等于给模型指路，凭证一律挡在前面。
        if (Tools.PathGuard.IsCredentialPath(Path.Combine(root, "probe")))
            throw new DaemonRequestException($"Workspace not listable: {root}");
        var matches = new List<(string Path, int Score, DateTime Modified)>();

        foreach (var file in Tools.FileWalker.EnumerateFiles(root, maxFiles: 20_000))
        {
            if (Tools.PathGuard.IsCredentialPath(file)) continue;
            string relative;
            try
            {
                relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            }
            catch (ArgumentException)
            {
                continue;
            }

            var score = MentionScore(relative, query);
            if (score < 0) continue;
            DateTime modified;
            try { modified = File.GetLastWriteTimeUtc(file); }
            catch { modified = DateTime.MinValue; }
            matches.Add((relative, score, modified));
        }

        return JsonSerializer.Serialize(matches
            .OrderBy(match => match.Score)
            .ThenByDescending(match => match.Modified)
            .ThenBy(match => match.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(match => match.Path)
            .ToList());
    }

    /// <summary>
    /// Picker ranking: file name beats path, prefix beats substring. Negative means no
    /// match. An empty query keeps every file so the picker can browse from scratch.
    /// </summary>
    private static int MentionScore(string relativePath, string query)
    {
        if (query.Length == 0) return 0;
        var name = Path.GetFileName(relativePath).ToLowerInvariant();
        var path = relativePath.ToLowerInvariant();
        if (name == query) return 0;
        if (name.StartsWith(query, StringComparison.Ordinal)) return 1;
        if (name.Contains(query, StringComparison.Ordinal)) return 2;
        return path.Contains(query, StringComparison.Ordinal) ? 3 : -1;
    }
}
