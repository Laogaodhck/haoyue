using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>G6 遗留项：Windows Job Object 进程级沙箱。仅在 Windows 上运行。</summary>
public class BashSandboxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-bash-sandbox-" + Guid.NewGuid().ToString("N"));

    public BashSandboxTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private WorkspaceInfo Workspace() => new() { Root = _dir, ProjectKinds = [] };

    private static ToolContext Context(WorkspaceInfo workspace, bool sandboxEnabled) => new()
    {
        Workspace = workspace,
        Events = new EventBus(),
        Agent = new AgentConfig
        {
            BashSandbox = new BashSandboxConfig
            {
                Enabled = sandboxEnabled,
                MemoryLimitMb = 2048,
                MaxProcesses = 256,
                UiRestrictions = true,
            },
        },
    };

    [Fact]
    public async Task BashTool_SandboxedEcho_CompletesAndAudits()
    {
        if (!OperatingSystem.IsWindows()) return; // Job Object 是 Windows 专属能力

        var tool = new BashTool(new FilePromptProvider());
        var result = await tool.ExecuteAsync(
            new JsonObject { ["command"] = "echo sandbox-ok", ["timeout_seconds"] = 30 },
            Context(Workspace(), sandboxEnabled: true), CancellationToken.None);

        Assert.True(result.Success, result.Output);
        Assert.Contains("sandbox-ok", result.Output);

        var lines = File.ReadAllLines(BashAuditLog.AuditFile(Workspace()))
            .Where(l => l.Length > 0).ToArray();
        var record = JsonSerializer.Deserialize<BashAuditRecord>(lines[^1])!;
        Assert.True(record.Sandboxed, "audit record must flag sandboxed execution");
        Assert.Equal("completed", record.Outcome);
        Assert.Equal(0, record.ExitCode);
    }

    [Fact]
    public void JobObject_AssignedProcess_CompletesInsideJob()
    {
        if (!OperatingSystem.IsWindows()) return; // Job Object 是 Windows 专属能力

        using var job = new WindowsJobObject(memoryLimitMb: 1024, maxProcesses: 0, uiRestrictions: true);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/d /s /c echo inside-job",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        Assert.True(job.Assign(process.Handle), "AssignProcessToJobObject must succeed");

        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(30_000));
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("inside-job", output);
    }

    [Fact]
    public void JobObject_Dispose_KillsProcessTree()
    {
        if (!OperatingSystem.IsWindows()) return; // Job Object 是 Windows 专属能力

        using var job = new WindowsJobObject(memoryLimitMb: 0, maxProcesses: 0, uiRestrictions: false);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/d /s /c ping -n 30 127.0.0.1 > nul",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        Assert.True(job.Assign(process.Handle));

        // 处置 job → KILL_ON_JOB_CLOSE 终止作业内全部进程：ping 需 30 秒，
        // 若 10 秒内退出即证明是 job 关闭强制终止（退出码不保证非 0）。
        job.Dispose();
        Assert.True(process.WaitForExit(10_000), "process must be terminated when the job closes");
    }

    [Fact]
    public void JobObject_ActiveProcessLimit_DeniesChildProcesses()
    {
        if (!OperatingSystem.IsWindows()) return; // Job Object 是 Windows 专属能力

        // MaxProcesses=1：先 ping 一拍给 Assign 留出窗口，再尝试派生子进程——子进程创建应被内核拒绝。
        using var job = new WindowsJobObject(memoryLimitMb: 0, maxProcesses: 1, uiRestrictions: false);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/d /s /c ping -n 2 127.0.0.1 > nul & cmd /d /s /c echo child",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        Assert.True(job.Assign(process.Handle));

        process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(30_000));
        Assert.NotEqual(0, process.ExitCode);
    }

    [Fact]
    public async Task BashTool_SandboxTimeout_KillsProcessTree()
    {
        if (!OperatingSystem.IsWindows()) return; // Job Object 是 Windows 专属能力

        var tool = new BashTool(new FilePromptProvider());
        var result = await tool.ExecuteAsync(
            new JsonObject { ["command"] = "ping -n 30 127.0.0.1", ["timeout_seconds"] = 2 },
            Context(Workspace(), sandboxEnabled: true), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("timed out", result.Output, StringComparison.OrdinalIgnoreCase);
    }
}
