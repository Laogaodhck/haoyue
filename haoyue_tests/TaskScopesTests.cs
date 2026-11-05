using System.Diagnostics;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>
/// 任务目录必须落在所属项目根内：统一命名可枚举、可清理，且建目录与产物写入都
/// 经过 PathGuard 的真实路径校验，无法借 taskId 或符号链接/junction 越界。
/// </summary>
public sealed class TaskScopesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-task-scopes", Guid.NewGuid().ToString("N"));

    public TaskScopesTests() => Directory.CreateDirectory(_dir);

    private WorkspaceInfo NewProject(string name)
    {
        var root = Path.Combine(_dir, name);
        Directory.CreateDirectory(root);
        return new WorkspaceInfo { Root = root, ProjectKinds = [] };
    }

    [Fact]
    public void Bind_CreatesTaskDirectoryInsideTheProject()
    {
        var workspace = NewProject("proj");

        var scoped = TaskScopes.Bind(workspace, "bg-1");

        var expected = Path.Combine(workspace.Root, ".haoyue", "tasks", "bg-1");
        Assert.Equal("bg-1", scoped.TaskId);
        Assert.Equal(expected, scoped.TaskDir);
        Assert.True(Directory.Exists(expected), $"任务目录未创建：{expected}");
        Assert.True(PathGuard.IsInside(workspace.Root, scoped.TaskDir!));
        // 围栏与工作目录仍是项目根：任务目录只是项目内的产物子目录。
        Assert.Equal(workspace.Root, scoped.Root);
        Assert.Equal(workspace.Root, PathGuard.WorkspaceRoot(scoped));
        Assert.Equal(Path.Combine(expected, "outputs"), scoped.OutputsDir);
        Assert.Contains("bg-1", TaskScopes.List(workspace));
    }

    [Fact]
    public void Bind_LeavesWorkspaceWithoutProjectUnchanged()
    {
        var global = new WorkspaceManager().CreateGlobal(Path.Combine(_dir, "global"));

        var scoped = TaskScopes.Bind(global, "bg-1");

        Assert.Null(scoped.TaskId);
        Assert.Null(scoped.TaskDir);
        Assert.False(Directory.Exists(Path.Combine(global.Root, ".haoyue", "tasks")));
    }

    [Fact]
    public void Bind_SeparatesTaskDirectoriesOfDifferentProjects()
    {
        var a = NewProject("proj-a");
        var b = NewProject("proj-b");

        var taskA = TaskScopes.Bind(a, "bg-1");
        var taskB = TaskScopes.Bind(b, "bg-1");

        Assert.NotEqual(taskA.TaskDir, taskB.TaskDir);
        Assert.True(Directory.Exists(taskA.TaskDir));
        Assert.True(Directory.Exists(taskB.TaskDir));
        Assert.True(PathGuard.IsInside(a.Root, taskA.TaskDir!));
        Assert.False(PathGuard.IsInside(b.Root, taskA.TaskDir!));
        Assert.True(PathGuard.IsInside(b.Root, taskB.TaskDir!));
        Assert.False(PathGuard.IsInside(a.Root, taskB.TaskDir!));
    }

    [Fact]
    public void Bind_NeutralizesTraversalInTaskId()
    {
        var workspace = NewProject("traversal");
        var outside = Path.Combine(_dir, "outside-traversal");

        var scoped = TaskScopes.Bind(workspace, "../outside-traversal");

        Assert.True(PathGuard.IsInside(workspace.Root, scoped.TaskDir!));
        Assert.StartsWith(Path.Combine(workspace.Root, ".haoyue", "tasks"), scoped.TaskDir!);
        Assert.False(Directory.Exists(outside));
    }

    [Fact]
    public void Bind_RejectsTaskRootLinkedOutsideTheProject()
    {
        var workspace = NewProject("linked");
        var outside = Path.Combine(_dir, "outside-linked");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(workspace.Root, ".haoyue"));
        var tasksRoot = Path.Combine(workspace.Root, ".haoyue", "tasks");
        if (!TryCreateDirectoryLink(tasksRoot, outside)) return; // 平台无权限创建链接：跳过

        var ex = Assert.Throws<InvalidOperationException>(() => TaskScopes.Bind(workspace, "bg-1"));
        Assert.Contains("escapes the workspace", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(outside, "bg-1")));
    }

    [Fact]
    public void ArtifactDirectory_RejectsTaskDirectorySwappedForOutsideLink()
    {
        var workspace = NewProject("swapped");
        var scoped = TaskScopes.Bind(workspace, "bg-1");
        var outside = Path.Combine(_dir, "outside-artifacts");
        Directory.CreateDirectory(outside);
        Directory.Delete(scoped.TaskDir!, recursive: true);
        if (!TryCreateDirectoryLink(scoped.TaskDir!, outside)) return;

        var swapped = new WorkspaceInfo
        {
            Root = scoped.Root,
            ProjectKinds = scoped.ProjectKinds,
            TaskId = scoped.TaskId,
            TaskDir = scoped.TaskDir,
        };
        var dir = PathGuard.ArtifactDirectory(swapped, "images", out var error);

        Assert.Null(dir);
        Assert.Contains("escapes the workspace", error);
    }

    [Fact]
    public void ArtifactDirectory_StaysInsideTheTaskDirectory()
    {
        var workspace = NewProject("artifacts");
        var scoped = TaskScopes.Bind(workspace, "bg-1");

        var dir = PathGuard.ArtifactDirectory(scoped, "images", out var error);

        Assert.Null(error);
        Assert.Equal(Path.Combine(scoped.TaskDir!, "outputs", "images"), dir);
        Assert.True(PathGuard.IsInside(workspace.Root, dir!));
    }

    [Fact]
    public void Remove_DeletesOnlyTheGivenTaskDirectory()
    {
        var workspace = NewProject("remove");
        var first = TaskScopes.Bind(workspace, "bg-a");
        var second = TaskScopes.Bind(workspace, "bg-b");

        Assert.True(TaskScopes.Remove(first, "bg-a"));
        Assert.False(Directory.Exists(first.TaskDir));
        Assert.True(Directory.Exists(second.TaskDir));
        Assert.DoesNotContain("bg-a", TaskScopes.List(workspace));
        Assert.Contains("bg-b", TaskScopes.List(workspace));
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Windows 未开启开发者模式时退化为 junction（普通用户可创建）。
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                });
                process?.WaitForExit(5_000);
                return Directory.Exists(link);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
