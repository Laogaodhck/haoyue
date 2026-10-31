using System.Diagnostics;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>
/// 边界检查必须看「真实目标」：工作区内的链接指向外部目录/文件时，
/// 逻辑路径仍在根目录下，但不解析链接就会让读写越界。
/// </summary>
public sealed class PathGuardSymlinkTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "haoyue-symlink-tests", Guid.NewGuid().ToString("N"));
    private readonly string _workspace;

    public PathGuardSymlinkTests()
    {
        _workspace = Path.Combine(_temp, "workspace");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(Path.Combine(_temp, "outside"));
    }

    [Fact]
    public void IsInside_RejectsLinkPointingOutside()
    {
        var outsideFile = Path.Combine(_temp, "outside", "secret.txt");
        File.WriteAllText(outsideFile, "outside");
        var link = Path.Combine(_workspace, "escape.txt");
        if (!TryCreateFileLink(link, outsideFile)) return; // 平台无权限创建链接：跳过

        Assert.False(PathGuard.IsInside(_workspace, link));
        Assert.False(PathGuard.IsReadable(Workspace(), new AgentConfig(), link, out var reason));
        Assert.Contains("escapes the workspace", reason);
    }

    [Fact]
    public void IsInside_AcceptsLinkPointingInside()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "src"));
        var link = Path.Combine(_workspace, "src-link");
        if (!TryCreateDirectoryLink(link, Path.Combine(_workspace, "src"))) return;

        Assert.True(PathGuard.IsInside(_workspace, link));
        Assert.True(PathGuard.IsInside(_workspace, Path.Combine(link, "file.cs")));
    }

    [Fact]
    public void IsCredentialPath_SeesThroughFileLink()
    {
        var outsideKey = Path.Combine(_temp, "outside", "id_rsa");
        File.WriteAllText(outsideKey, "key");
        var link = Path.Combine(_workspace, "notes.txt");
        if (!TryCreateFileLink(link, outsideKey)) return;

        Assert.True(PathGuard.IsCredentialPath(link));
    }

    [Fact]
    public void IsReadable_AcceptsOrdinaryWorkspaceFile()
    {
        var file = Path.Combine(_workspace, "readme.md");
        File.WriteAllText(file, "hi");
        Assert.True(PathGuard.IsReadable(Workspace(), new AgentConfig(), file, out _));
    }

    private WorkspaceInfo Workspace() => new() { Root = _workspace, ProjectKinds = [] };

    private static bool TryCreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
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
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
