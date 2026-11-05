using System.Security.Cryptography;
using System.Text;
using Haoyue.Runtime.Tools;

namespace Haoyue.Runtime.Workspaces;

/// <summary>
/// 项目任务目录：每个在项目工作区里发起的任务（后台任务 / 定时任务）在项目根下拥有
/// 独立子目录 <c>.haoyue/tasks/&lt;task&gt;/</c>，任务产物写入其中的 <c>outputs/</c>。
/// 统一命名使任务目录可枚举、可清理；建目录前后都用 <see cref="PathGuard.IsInside"/>
/// 校验真实路径，既挡住 taskId 拼出越界路径，也挡住 .haoyue/tasks 或其下的子目录被
/// 替换成指向项目外的符号链接 / junction。全局任务不建任务目录，沿用既有落点。
/// </summary>
public static class TaskScopes
{
    /// <summary>相对项目根的任务目录前缀。</summary>
    public const string RelativeRoot = ".haoyue/tasks";

    /// <summary>项目内的任务目录父目录。</summary>
    public static string RootOf(WorkspaceInfo workspace) =>
        Path.Combine(workspace.Root, ".haoyue", "tasks");

    /// <summary>
    /// 任务 id → 目录名。安全 id 原样使用（如 bg-20261105-123456-abc123）；
    /// 含路径分隔符、通配符等文件系统不安全字符或过长时，清洗后追加 id 摘要，
    /// 既保持可枚举，又避免不同 id 清洗后撞到同一个目录。
    /// </summary>
    public static string DirectoryName(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("任务 id 不能为空。", nameof(taskId));

        var safe = new string(taskId
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-')
            .ToArray())
            .Trim('-');
        // 纯点名（"." / ".."）会被 Path.Combine 当成上一级目录，必须替换掉。
        if (safe.Trim('.').Length == 0) safe = "task";
        if (safe == taskId && taskId.Length <= 64) return taskId;

        if (safe.Length > 48) safe = safe[..48].Trim('-');
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(taskId)))
            .ToLowerInvariant()[..8];
        return $"{safe}-{digest}";
    }

    /// <summary>任务在项目内的目录（不保证已存在）。</summary>
    public static string DirectoryFor(WorkspaceInfo workspace, string taskId) =>
        Path.Combine(RootOf(workspace), DirectoryName(taskId));

    /// <summary>
    /// 把工作区绑定到该任务的任务目录并创建它。全局工作区（无项目归属）原样返回，
    /// 已绑定任务目录的工作区（子代理继承父任务）也原样返回，避免重复建目录。
    /// </summary>
    public static WorkspaceInfo Bind(WorkspaceInfo workspace, string taskId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.IsGlobal || workspace.TaskDir is not null) return workspace;
        if (!Directory.Exists(workspace.Root))
            throw new InvalidOperationException(
                $"Workspace root does not exist: {workspace.Root}. Refusing to place task outputs outside a project.");

        var dir = DirectoryFor(workspace, taskId);
        EnsureInside(workspace, dir);
        Directory.CreateDirectory(dir);
        // 建目录后再复核一次：目录可能在检查与创建之间被换成指向项目外的链接。
        EnsureInside(workspace, dir);

        return new WorkspaceInfo
        {
            Root = workspace.Root,
            ProjectKinds = workspace.ProjectKinds,
            Config = workspace.Config,
            IsGlobal = false,
            Trusted = workspace.Trusted,
            HasWorkspaceConfig = workspace.HasWorkspaceConfig,
            HasWorkspaceMcp = workspace.HasWorkspaceMcp,
            TaskId = taskId,
            TaskDir = dir,
        };
    }

    /// <summary>枚举项目内已存在的任务目录名（按名称排序）。越界时返回空。</summary>
    public static IReadOnlyList<string> List(WorkspaceInfo workspace)
    {
        if (workspace.IsGlobal) return [];
        var root = RootOf(workspace);
        if (!Directory.Exists(root) || !PathGuard.IsInside(workspace.Root, root)) return [];
        try
        {
            return Directory.EnumerateDirectories(root)
                .Select(Path.GetFileName)
                .OfType<string>()
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>删除一个任务目录及其产物；全局工作区、越界或不存在时返回 false。</summary>
    public static bool Remove(WorkspaceInfo workspace, string taskId)
    {
        if (workspace.IsGlobal) return false;
        var dir = DirectoryFor(workspace, taskId);
        if (!Directory.Exists(dir) || !PathGuard.IsInside(workspace.Root, dir)) return false;
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void EnsureInside(WorkspaceInfo workspace, string dir)
    {
        if (!PathGuard.IsInside(workspace.Root, dir))
            throw new InvalidOperationException(
                $"Task directory escapes the workspace: {dir}. "
                + $"Refusing to place task outputs outside {workspace.Root}.");
    }
}
