using System.Runtime.InteropServices;
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

        if (sandbox is not null)
        {
            var outcome = await ExecuteSandboxedAsync(sandbox, shell, shellArgs, cwd, timeout, cts.Token, ct).ConfigureAwait(false);
            stopwatch.Stop();
            Audit(context, command, cwd, risk, riskReasons, outcome.Outcome, outcome.ExitCode, stopwatch.ElapsedMilliseconds, sandboxed: true);
            return FormatResult(
                command,
                context.Truncate(outcome.Output, "command output"),
                outcome.ExitCode ?? -1,
                outcome.Outcome == "timeout" ? $"Command timed out after {timeout.TotalSeconds:0}s: {Shorten(command)}" : null);
        }

        try
        {
            var result = await Cli.Wrap(shell)
                .WithArguments(shellArgs)
                .WithWorkingDirectory(cwd)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cts.Token);
            stopwatch.Stop();

            Audit(context, command, cwd, risk, riskReasons, "completed", result.ExitCode, stopwatch.ElapsedMilliseconds);

            var text = result.StandardOutput.Length > 0
                ? result.StandardOutput + (result.StandardError.Length > 0 ? "\n" + result.StandardError : "")
                : result.StandardError.ToString();
            return FormatResult(command, context.Truncate(text, "command output"), result.ExitCode, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            Audit(context, command, cwd, risk, riskReasons, "timeout", null, stopwatch.ElapsedMilliseconds);
            return ToolResult.Fail($"Command timed out after {timeout.TotalSeconds:0}s: {Shorten(command)}");
        }
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
            var output = (stdout.Length > 0 ? stdout : "") + (stderr.Length > 0 ? (stdout.Length > 0 ? "\n" : "") + stderr : "");
            return ("completed", process.ExitCode, output);
        }
        catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
        {
            // 超时：using 块随即释放 job，KILL_ON_JOB_CLOSE 终止整棵进程树。
            return ("timeout", null, "");
        }
    }

    private static ToolResult FormatResult(string command, string text, int exitCode, string? failure)
    {
        if (failure is not null)
            return ToolResult.Fail(failure);

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
