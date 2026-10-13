using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using CliWrap;
using CliWrap.Buffered;
using Haoyue.Runtime.Prompts;

namespace Haoyue.Runtime.Tools.Builtin;

/// <summary>Runs a shell command (bash when available, cmd.exe otherwise) inside the workspace.</summary>
public sealed class BashTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override string Name => "bash";
    public override string StatusLabel => "Running command";
    public override bool Mutating => true; // a shell command may change anything

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("command", ToolSchema.String("Shell command to execute"), true),
        ("cwd", ToolSchema.String("Working directory (defaults to the workspace root)"), false),
        ("timeout_seconds", ToolSchema.Integer("Timeout in seconds (default from config)"), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var command = GetString(arguments, "command");
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Fail("command is required.");

        var cwd = context.ResolvePath(GetString(arguments, "cwd") ?? ".");
        if (!Directory.Exists(cwd))
            return ToolResult.Fail($"Working directory not found: {cwd}");

        var timeout = TimeSpan.FromSeconds(Math.Clamp(
            GetInt(arguments, "timeout_seconds") ?? context.Agent.BashTimeoutSeconds, 1, 3600));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var (shell, shellArgs) = ResolveShell(command);
        var (risk, riskReasons) = BashRiskClassifier.Classify(command);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var sandbox = OperatingSystem.IsWindows() && context.Agent.BashSandbox.Enabled
            ? context.Agent.BashSandbox
            : null;

        var outcome = sandbox is not null
            ? await ExecuteSandboxedAsync(sandbox, shell, shellArgs, cwd, timeout, cts.Token, ct).ConfigureAwait(false)
            : await ExecuteDirectAsync(shell, shellArgs, cwd, timeout, cts.Token, ct).ConfigureAwait(false);
        stopwatch.Stop();

        Audit(context, command, cwd, risk, riskReasons, outcome.Outcome, outcome.ExitCode, stopwatch.ElapsedMilliseconds, sandboxed: sandbox is not null);

        // H-B: 失败输出（非零退出码 / 超时部分输出）把预算倾向尾部——构建与测试日志的
        // 失败摘要、exit code、统计都在尾部，头截断会系统性丢掉诊断信息。成功输出保持
        // 头截断（进度与排名信息在头部）。
        var text = outcome.Outcome == "completed" && outcome.ExitCode == 0
            ? context.Truncate(outcome.Output, "command output")
            : context.TruncateTailPrioritized(outcome.Output, "command output", tailShare: 0.7);
        return FormatResult(
            command,
            text,
            outcome.ExitCode ?? -1,
            outcome.Outcome == "timeout" ? $"Command timed out after {timeout.TotalSeconds:0}s: {Shorten(command)}" : null);
    }

    /// <summary>
    /// 默认路径（未开沙箱）：自托管 Process，替代 CliWrap 的 ExecuteBufferedAsync。
    /// CliWrap 3.10 没有 killEntireProcessTree 选项（上游 issue 仍开放），取消时只杀
    /// 父进程，`npm start &` / `dotnet build` 派生子进程的场景超时后会留下无限消耗
    /// 资源的孤儿进程。这里自己保证三件事：① 进程树终止（Windows kill-on-close Job，
    /// Unix setsid 进程组整组 SIGKILL）；② 超时/取消后短暂排空管道，把已产生的输出
    /// 回传给模型而不是整体丢弃（与沙箱路径 DrainQuietly 语义拉齐，M9 覆盖缺口）；
    /// ③ 解码与 CliWrap 的 Encoding.Default（.NET 8+ 即 UTF-8 无 BOM）保持一致。
    /// </summary>
    private static async Task<(string Outcome, int? ExitCode, string Output)> ExecuteDirectAsync(
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
            // CliWrap 旧路径按 Encoding.Default 解码；.NET 8+ 上它就是 UTF-8 无 BOM。
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

    private static string MergeStreams(string stdout, string stderr) =>
        stdout.Length > 0
            ? stdout + (stderr.Length > 0 ? "\n" + stderr : "")
            : stderr;

    /// <summary>
    /// H-A 进程树终止。Windows 挂接了 kill-on-close Job 时关闭 job 句柄即终止树内
    /// 所有进程；否则 Unix 走 setsid 进程组整组 SIGKILL（负 pid），最后退化为 .NET
    /// 的进程树遍历（macOS 无 setsid 时的兜底）。
    /// </summary>
    private static void KillProcessTree(System.Diagnostics.Process process, WindowsJobObject? job)
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
    private static WindowsJobObject? TryCreateKillJob(System.Diagnostics.Process process)
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
    private static void ApplyProcessGroupIsolation(
        System.Diagnostics.ProcessStartInfo startInfo, string shell, string[] shellArgs)
    {
        if (OperatingSystem.IsWindows()) return;
        if (FindOnPath("setsid") is not { } setsid) return;
        startInfo.FileName = setsid;
        startInfo.ArgumentList.Clear();
        startInfo.ArgumentList.Add(shell);
        foreach (var argument in shellArgs) startInfo.ArgumentList.Add(argument);
    }

    /// <summary>沙箱路径：System.Diagnostics.Process + Job Object。kill-on-close 保证超时命令
    /// 的整棵进程树被终止；内存/进程数/UI 限制由内核强制。</summary>
    private static async Task<(string Outcome, int? ExitCode, string Output)> ExecuteSandboxedAsync(
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

    /// <summary>Awaits a pipe-drain task bounded by 2s; returns whatever was captured, or "".</summary>
    private static async Task<string> DrainQuietly(Task<string> task)
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

    private static ToolResult FormatResult(string command, string text, int exitCode, string? failure)
    {
        if (failure is not null)
        {
            // Timeout failures carry whatever output was produced before the kill, so the
            // model can diagnose instead of blindly retrying the same command.
            var partial = text.Length == 0 ? "" : "\n超时前已产生的部分输出：\n" + text;
            return ToolResult.Fail(failure + partial);
        }

        var trimmed = text.Length == 0 ? "(no output)" : text;
        var summary = $"{Shorten(command)} → exit {exitCode}";
        return exitCode == 0
            ? ToolResult.Ok(trimmed, summary)
            : new ToolResult { Success = false, Output = $"Exit code {exitCode}\n{trimmed}", Summary = summary };
    }

    /// <summary>G6 审计：每条执行的命令都落一条 JSONL 记录（命令、风险分级、结果、是否沙箱）。</summary>
    private static void Audit(
        ToolContext context, string command, string cwd,
        BashRisk risk, string[] reasons, string outcome, int? exitCode, long elapsedMs, bool sandboxed = false)
    {
        if (context.Workspace is null) return;
        BashAuditLog.Append(context.Workspace, new BashAuditRecord(
            DateTime.UtcNow.ToString("o"),
            command,
            cwd,
            risk.ToString().ToLowerInvariant(),
            reasons,
            outcome,
            exitCode,
            elapsedMs,
            sandboxed));
    }

    private static (string Shell, string[] Args) ResolveShell(string command)
    {
        if (!OperatingSystem.IsWindows())
            return ("/bin/bash", ["-c", command]);

        var bash = FindOnPath("bash.exe");
        if (bash is not null)
            return (bash, ["-c", command]);

        var pwsh = FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe");
        if (pwsh is not null)
            return (pwsh, ["-NoProfile", "-NonInteractive", "-Command", command]);

        return ("cmd.exe", ["/d", "/s", "/c", command]);
    }

    private static string? FindOnPath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(dir => Path.Combine(dir.Trim(), fileName))
        .FirstOrDefault(File.Exists);

    private static string Shorten(string command) =>
        command.Length > 60 ? command[..60] + "…" : command;
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
