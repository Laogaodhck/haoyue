using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>
/// H-A：默认（非沙箱）路径的进程树治理与超时输出保留。CliWrap 3.10 没有
/// killEntireProcessTree 选项，取消只杀父进程——`npm start &` 类命令超时后会留下
/// 无限消耗资源的孤儿进程，且已产生的输出被整体丢弃。这两条测试锁定修复行为：
/// ① 超时回传部分输出；② 整棵进程树（含后台孙子进程）被终止。
/// </summary>
public class BashProcessTreeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-bash-tree-" + Guid.NewGuid().ToString("N"));

    public BashProcessTreeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static ToolContext NewContext(string root) => new()
    {
        Workspace = new WorkspaceInfo { Root = root, ProjectKinds = [] },
        Events = new EventBus(),
        Agent = new AgentConfig(),
    };

    /// <summary>Windows 上无 bash.exe 时（无 Git Bash 的环境）跳过——命令需要 POSIX shell。</summary>
    private static bool HasPosixShell()
    {
        if (!OperatingSystem.IsWindows()) return true;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir.Trim(), "bash.exe")));
    }

    [Fact]
    public async Task Timeout_ReturnsPartialOutput_InsteadOfDiscardingIt()
    {
        if (!HasPosixShell()) return;
        var tool = new BashTool(new FilePromptProvider());
        // 先打印再挂起：超时结果必须包含超时前已产生的输出，模型才能诊断"超时前发生了什么"。
        var result = await tool.ExecuteAsync(
            new JsonObject
            {
                ["command"] = "echo early-output; sleep 30",
                ["timeout_seconds"] = 1,
            },
            NewContext(_dir), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("timed out", result.Output);
        Assert.Contains("early-output", result.Output);
    }

    [Fact]
    public async Task Timeout_KillsGrandchildProcessTree()
    {
        if (!HasPosixShell()) return;
        var marker = Path.Combine(_dir, "orphan-marker.txt");
        var tool = new BashTool(new FilePromptProvider());
        // 后台孙子进程在父命令被超时终止后仍会写标记文件：旧行为（只杀父进程）下标记
        // 会出现；进程树治理后整棵树被终止，标记永不出现。
        var result = await tool.ExecuteAsync(
            new JsonObject
            {
                ["command"] = $"(sleep 4; echo alive > \"{marker.Replace('\\', '/')}\") & sleep 30",
                ["timeout_seconds"] = 1,
            },
            NewContext(_dir), CancellationToken.None);

        Assert.False(result.Success);
        // 给潜在孤儿 6 秒作案窗口；被正确终止则标记不会出现。
        await Task.Delay(6000);
        Assert.False(File.Exists(marker),
            "orphaned grandchild survived the timeout — the process tree was not terminated");
    }

    [Fact]
    public async Task SuccessPath_StillReturnsExitCodeAndOutput()
    {
        if (!HasPosixShell()) return;
        var tool = new BashTool(new FilePromptProvider());
        var result = await tool.ExecuteAsync(
            new JsonObject { ["command"] = "echo hello-tree; exit 0", ["timeout_seconds"] = 30 },
            NewContext(_dir), CancellationToken.None);

        Assert.True(result.Success, result.Output);
        Assert.Contains("hello-tree", result.Output);
        Assert.Contains("exit 0", result.Summary);
    }
}
