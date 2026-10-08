namespace Haoyue.Runtime.Workspaces;

/// <summary>One hierarchical AGENTS.md rule file discovered under a workspace root.</summary>
public sealed record WorkspaceRuleFile(string Path, bool IsRoot, string Content);

/// <summary>
/// Shared AGENTS.md rule-file management used by every front end (daemon admin API and
/// CLI alike): discovery over the workspace root plus two directory levels — skipping
/// dependency and build-output folders — and validated writes that can neither rename
/// the file nor escape the workspace root.
/// </summary>
public static class WorkspaceRules
{
    private const string RuleFileName = "AGENTS.md";

    /// <summary>
    /// Lists the hierarchical AGENTS.md rule files under the workspace root
    /// (root plus two directory levels). Global workspaces carry no rules.
    /// Unreadable files are skipped instead of failing the whole listing.
    /// </summary>
    public static IReadOnlyList<WorkspaceRuleFile> List(WorkspaceInfo workspace)
    {
        if (workspace.IsGlobal) return [];

        var files = new List<WorkspaceRuleFile>();
        var rootPath = Path.GetFullPath(workspace.Root);
        foreach (var candidate in Candidates(rootPath))
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                files.Add(new WorkspaceRuleFile(
                    Path.GetRelativePath(rootPath, candidate).Replace('\\', '/'),
                    IsRootFile(candidate, rootPath),
                    File.ReadAllText(candidate)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return files;
    }

    /// <summary>
    /// Writes one rule file. <paramref name="relativePath"/> must either be null (the
    /// workspace-root AGENTS.md) or end with AGENTS.md and stay inside the workspace.
    /// Returns the normalized relative path actually written. Throws
    /// <see cref="InvalidOperationException"/> for global workspaces and
    /// <see cref="ArgumentException"/> for invalid names or escaping paths.
    /// </summary>
    public static string Save(WorkspaceInfo workspace, string? relativePath, string content)
    {
        var fullPath = ResolveWithinWorkspace(workspace, relativePath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"无法写入规则文件：{ex.Message}", ex);
        }
        return Path.GetRelativePath(Path.GetFullPath(workspace.Root), fullPath).Replace('\\', '/');
    }

    /// <summary>
    /// Deletes one rule file with the same validation as <see cref="Save"/>.
    /// Returns the normalized relative path actually removed. The root AGENTS.md is
    /// deletable too (recreated on the next save); a missing file is not an error.
    /// </summary>
    public static string Delete(WorkspaceInfo workspace, string? relativePath)
    {
        var fullPath = ResolveWithinWorkspace(workspace, relativePath);
        try
        {
            if (File.Exists(fullPath)) File.Delete(fullPath);
            var directory = Path.GetDirectoryName(fullPath);
            // 清理删除后变空的子目录，保持工作区整洁；根目录从不删除。
            if (!string.Equals(directory, Path.GetFullPath(workspace.Root), StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"无法删除规则文件：{ex.Message}", ex);
        }
        return Path.GetRelativePath(Path.GetFullPath(workspace.Root), fullPath).Replace('\\', '/');
    }

    /// <summary>
    /// Shared validation for rule-file paths: global workspaces carry no rules, the
    /// name must end with AGENTS.md, and the resolved path must stay inside the
    /// workspace root.
    /// </summary>
    private static string ResolveWithinWorkspace(WorkspaceInfo workspace, string? relativePath)
    {
        if (workspace.IsGlobal)
            throw new InvalidOperationException("全局会话不加载规则文件，请选择工作区后保存。");

        var relative = (relativePath ?? RuleFileName).Trim().Replace('\\', '/');
        if (relative.Length == 0 || !relative.EndsWith(RuleFileName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("规则文件必须命名为 AGENTS.md");

        var rootPath = Path.GetFullPath(workspace.Root);
        string fullPath;
        try { fullPath = Path.GetFullPath(Path.Combine(rootPath, relative)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"Invalid rules path: {ex.Message}", nameof(relativePath));
        }
        if (!fullPath.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("规则路径越出了工作区根目录");
        return fullPath;
    }

    private static bool IsRootFile(string candidate, string rootPath)
        => string.Equals(Path.GetDirectoryName(candidate), rootPath, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Candidates(string rootPath)
    {
        yield return Path.Combine(rootPath, RuleFileName);
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", ".git", ".haoyue", ".session", ".cache", ".venv", "venv",
            "bin", "obj", "dist", "build", "out", "logs", "__pycache__", ".next", ".nuxt", "coverage",
        };
        foreach (var level1 in SafeDirectories(rootPath, skipped))
        {
            yield return Path.Combine(level1, RuleFileName);
            foreach (var level2 in SafeDirectories(level1, skipped))
                yield return Path.Combine(level2, RuleFileName);
        }
    }

    private static IEnumerable<string> SafeDirectories(string directory, HashSet<string> skipped)
    {
        string[] directories;
        try { directories = Directory.GetDirectories(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var dir in directories.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if (!skipped.Contains(Path.GetFileName(dir)))
                yield return dir;
        }
    }
}
