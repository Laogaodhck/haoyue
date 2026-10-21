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
    public void JobObject_MemoryLimit_IsActuallyConfigured()
    {
        // 回归（2026-10-21）：2048MB * 1024 * 1024 曾因 int 溢出为负，导致
        // JobObjectLimitProcessMemory flag 被静默丢弃——默认配置下内存上限从未生效。
        // 回读内核侧配置锁死 flag 与数值，防止结构或算术回归再次静默丢限。
        if (!OperatingSystem.IsWindows()) return;

        using var job = new WindowsJobObject(memoryLimitMb: 2048, maxProcesses: 256, uiRestrictions: false);
        var info = new JobQuery.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        var ok = JobQuery.QueryInformationJobObject(
            job.HandleForTest, JobQuery.JobObjectInfoClass.JobObjectExtendedLimitInformation,
            out info, System.Runtime.InteropServices.Marshal.SizeOf<JobQuery.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(), IntPtr.Zero);
        Assert.True(ok, $"QueryInformationJobObject failed, Win32Error={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");

        var flags = (uint)info.BasicLimitInformation.LimitFlags;
        Assert.True((flags & 0x100) != 0, $"ProcessMemory flag missing, flags=0x{flags:X}");
        Assert.True((flags & 0x8) != 0, $"ActiveProcess flag missing, flags=0x{flags:X}");
        Assert.Equal(2048UL * 1024 * 1024, (ulong)info.ProcessMemoryLimit);
    }

    [Fact]
    public async Task JobObject_MemoryLimit_TerminatesRunawayAllocation()
    {
        // 逃逸测试：job 内进程累计 commit 5GB 远超 2048MB 上限 → 内核在第 ~21 块
        // 拒绝分配。用 Python 而非 .NET：MemoryError 是可优雅退出的普通异常
        //（.NET OOM 会 failfast），断言稳定且不依赖分配器的失败形态。
        if (!OperatingSystem.IsWindows()) return;
        var python = FindPython();
        if (python is null) return; // 本机无 Python 时跳过（逃逸机制由上一条回读用例锁定）

        using var job = new WindowsJobObject(memoryLimitMb: 2048, maxProcesses: 0, uiRestrictions: false);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = python,
            Arguments = "-X utf8 -c \"x = []\nfor i in range(50):\n    x.append(bytearray(100 * 1024 * 1024))\nprint('alloc-done')\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        Assert.True(job.Assign(process.Handle));

        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60_000));
        Assert.True(process.ExitCode != 0 || !stdout.Contains("alloc-done", StringComparison.Ordinal),
            $"runaway allocation completed unimpeded: exit={process.ExitCode}, stdout={stdout}");
    }

    private static string? FindPython()
    {
        foreach (var name in new[] { "python.exe", "python3.exe" })
        {
            var found = ShellRunner.FindOnPath(name);
            if (found is not null) return found;
        }
        return null;
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

/// <summary>QueryInformationJobObject 最小 P/Invoke（仅测试用：回读 job limit 配置）。</summary>
internal static class JobQuery
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool QueryInformationJobObject(
        IntPtr hJob, JobObjectInfoClass infoClass, out JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpInfo,
        int cbInfo, IntPtr lpReturnLength);

    public enum JobObjectInfoClass { JobObjectExtendedLimitInformation = 9 }

    [Flags]
    public enum LimitFlags : uint
    {
        JobObjectLimitActiveProcess = 0x00000008,
        JobObjectLimitProcessMemory = 0x00000100,
        JobObjectLimitJobMemory = 0x00000200,
        JobObjectLimitKillOnJobClose = 0x00002000,
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public LimitFlags LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
