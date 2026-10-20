using System.Runtime.InteropServices;
using System.Text;
using Haoyue.Runtime.Configuration;

namespace Haoyue.Runtime.Tools;

/// <summary>
/// bash 与 terminal 共享的 shell 进程执行核心：进程树终止、超时排空、输出合并。
/// 从 BashTool 抽出（两个入口必须保持同构的进程治理语义），自身不解析命令、不审计。
/// </summary>
internal static class ShellRunner
{
    /// <summary>Resolved shell family — terminal 会话按类型包裹命令以回读工作目录。</summary>
    public enum ShellKind { Bash, PowerShell, Cmd }

    /// <summary>
    /// bash → pwsh/powershell → cmd.exe 的解析顺序（与 bash 工具一致），附带 shell
    /// 家族标签供 terminal 的命令包裹使用。
    /// </summary>
    public static (string Shell, string[] Args, ShellKind Kind) ResolveShellWithKind(string command)
    {
        if (!OperatingSystem.IsWindows())
            return ("/bin/bash", ["-c", command], ShellKind.Bash);

        var bash = FindOnPath("bash.exe");
        if (bash is not null)
            return (bash, ["-c", command], ShellKind.Bash);

        var pwsh = FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe");
        if (pwsh is not null)
            return (pwsh, ["-NoProfile", "-NonInteractive", "-Command", command], ShellKind.PowerShell);

        return ("cmd.exe", ["/d", "/s", "/c", command], ShellKind.Cmd);
    }

    /// <summary>
    /// 默认路径（未开沙箱）：自托管 Process，替代 CliWrap 的 ExecuteBufferedAsync。
    /// CliWrap 3.10 没有 killEntireProcessTree 选项（上游 issue 仍开放），取消时只杀
    /// 父进程，`npm start &` / `dotnet build` 派生子进程的场景超时后会留下无限消耗
    /// 资源的孤儿进程。这里自己保证三件事：① 进程树终止（Windows kill-on-close Job，
    /// Unix setsid 进程组整组 SIGKILL）；② 超时/取消后短暂排空管道，把已产生的输出
    /// 回传给模型而不是整体丢弃；③ 解码为 UTF-8 无 BOM（与 CliWrap 的 Encoding.Default
    /// 在 .NET 8+ 上一致）。
    /// </summary>
    public static async Task<(string Outcome, int? ExitCode, string Output)> ExecuteDirectAsync(
        string shell, string[] shellArgs, string cwd, TimeSpan timeout,
        CancellationToken linkedCt, CancellationToken outerCt)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = shell,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = cwd,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        foreach (var argument in shellArgs) startInfo.ArgumentList.Add(argument);
        ApplyProcessGroupIsolation(startInfo, shell, shellArgs);

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        if (!process.Start())
            return ("failed", null, "process failed to start");

        // stdin 立即关闭：与 CliWrap 的空管道语义一致，读 stdin 的命令拿到 EOF 而不是挂起。
        try { process.StandardInput.Close(); } catch (System.IO.IOException) { }

        // Windows：kill-on-close Job（无内存/进程数限制，仅兜底整棵进程树）。分配失败
        // （嵌套 Job 受限环境）不阻断执行，KillProcessTree 会退化为进程树遍历终止。
        var killJob = TryCreateKillJob(process);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(linkedCt).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return ("completed", process.ExitCode, MergeStreams(stdout, stderr));
        }
        catch (OperationCanceledException)
        {
            // 先终止整棵进程树，再决定回传部分输出还是向上抛取消。
            KillProcessTree(process, killJob);
            if (outerCt.IsCancellationRequested) throw;
            var stdout = await DrainQuietly(stdoutTask).ConfigureAwait(false);
            var stderr = await DrainQuietly(stderrTask).ConfigureAwait(false);
            return ("timeout", null, MergeStreams(stdout, stderr));
        }
        finally
        {
            killJob?.Dispose();
        }
    }

    /// <summary>沙箱路径：System.Diagnostics.Process + Job Object。kill-on-close 保证超时命令
    /// 的整棵进程树被终止；内存/进程数/UI 限制由内核强制。</summary>
    public static async Task<(string Outcome, int? ExitCode, string Output)> ExecuteSandboxedAsync(
        Configuration.BashSandboxConfig sandbox, string shell, string[] shellArgs,
        string cwd, TimeSpan timeout, CancellationToken linkedCt, CancellationToken outerCt)
    {
        using var job = new WindowsJobObject(sandbox.MemoryLimitMb, sandbox.MaxProcesses, sandbox.UiRestrictions);
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = shell,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = cwd,
        };
        foreach (var argument in shellArgs) startInfo.ArgumentList.Add(argument);

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        if (!process.Start())
            return ("failed", null, "process failed to start");

        if (!job.Assign(process.Handle))
            return ("failed", null, $"job assignment failed (Win32 error {Marshal.GetLastWin32Error()})");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(linkedCt).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return ("completed", process.ExitCode, MergeStreams(stdout, stderr));
        }
        catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
        {
            // 超时：using 块随即释放 job，KILL_ON_JOB_CLOSE 终止整棵进程树。
            // 输出管道在进程死亡后排空——短暂等待并把已产生的 stdout/stderr 一并
            // 回传，模型才能诊断"超时前发生了什么"，而不是拿到空输出盲目重试。
            var stdout = await DrainQuietly(stdoutTask).ConfigureAwait(false);
            var stderr = await DrainQuietly(stderrTask).ConfigureAwait(false);
            return ("timeout", null, MergeStreams(stdout, stderr));
        }
    }

    public static string MergeStreams(string stdout, string stderr) =>
        stdout.Length > 0
            ? stdout + (stderr.Length > 0 ? "\n" + stderr : "")
            : stderr;

    /// <summary>
    /// H-A 进程树终止。Windows 挂接了 kill-on-close Job 时关闭 job 句柄即终止树内
    /// 所有进程；否则 Unix 走 setsid 进程组整组 SIGKILL（负 pid），最后退化为 .NET
    /// 的进程树遍历（macOS 无 setsid 时的兜底）。
    /// </summary>
    public static void KillProcessTree(System.Diagnostics.Process process, WindowsJobObject? job)
    {
        if (job is not null)
        {
            job.Dispose(); // KILL_ON_JOB_CLOSE：最后一个句柄关闭即终止整棵树
            return;
        }
        if (!OperatingSystem.IsWindows() && ProcessGroupKill.Kill(process.Id))
            return;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 进程已退出或已无权访问——没有可杀的东西了。
        }
    }

    /// <summary>创建 kill-on-close Job 并挂入进程；失败返回 null（退化为树遍历终止）。</summary>
    public static WindowsJobObject? TryCreateKillJob(System.Diagnostics.Process process)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var job = new WindowsJobObject(memoryLimitMb: 0, maxProcesses: 0, uiRestrictions: false);
            return job.Assign(process.Handle) ? job : Dispose(job);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }

        static WindowsJobObject? Dispose(WindowsJobObject job)
        {
            job.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Unix: 用 setsid 把 shell 裹进独立会话/进程组，超时后才能整组终止（否则按组
    /// kill 会误杀 daemon 自己所在的组）。setsid 在直接 exec 模式下不 fork（被 spawn
    /// 的子进程不是组首领），因此 Process.Id 即 pgid。setsid 缺席（macOS 默认不带）
    /// 时保持直接启动，由 <see cref="KillProcessTree"/> 的树遍历兜底。
    /// </summary>
    public static void ApplyProcessGroupIsolation(
        System.Diagnostics.ProcessStartInfo startInfo, string shell, string[] shellArgs)
    {
        if (OperatingSystem.IsWindows()) return;
        if (FindOnPath("setsid") is not { } setsid) return;
        startInfo.FileName = setsid;
        startInfo.ArgumentList.Clear();
        startInfo.ArgumentList.Add(shell);
        foreach (var argument in shellArgs) startInfo.ArgumentList.Add(argument);
    }

    /// <summary>Awaits a pipe-drain task bounded by 2s; returns whatever was captured, or "".</summary>
    public static async Task<string> DrainQuietly(Task<string> task)
    {
        try
        {
            var completed = await Task.WhenAny(task, Task.Delay(2000)).ConfigureAwait(false);
            return completed == task ? await task.ConfigureAwait(false) : "";
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            return "";
        }
    }

    public static string? FindOnPath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(dir => Path.Combine(dir.Trim(), fileName))
        .FirstOrDefault(File.Exists);
}

/// <summary>
/// Unix 进程组信号（H-A）。setsid 包裹启动的子进程 pgid == pid，向 -pid 发 SIGKILL
/// 即终止整组。libc 的 kill(2) 在 Linux 与 macOS 均可解析（.NET 运行时把 "libc"
/// 映射到各自平台的 C 库）；解析失败时返回 false，调用方退化为进程树遍历。
/// </summary>
internal static class ProcessGroupKill
{
    private const int SIGKILL = 9;

    [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static extern int kill(int pid, int sig);

    public static bool Kill(int processId)
    {
        if (processId <= 0) return false;
        try
        {
            return kill(-processId, SIGKILL) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
