using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;

namespace Haoyue.Runtime.ComputerUse;

/// <summary>
/// Native shell execution for Computer Use: PowerShell, cmd, and Python.
/// Lets the agent script system work (files, processes, services, queries)
/// instead of driving GUIs for everything.
///
/// Decoding follows what each child actually emits: PowerShell (forced UTF-8 via
/// Console.OutputEncoding) and Python (-X utf8) decode as UTF-8; cmd.exe pipes
/// bytes in the OEM console codepage (GBK on zh-CN systems — chcp 65001 does not
/// affect its piped echo), so cmd output decodes with GetOEMCP(). On timeout the
/// whole process tree is killed (taskkill /T) — child processes otherwise keep the
/// pipes open and the call would hang or silently succeed.
/// </summary>
public sealed class ComputerExecTool(IPromptProvider prompts, ComputerUseConfig config) : BuiltinTool(prompts)
{
    public override string Name => "computer_exec";
    public override string StatusLabel => "Running shell";
    public override bool Mutating => true;
    public override bool RequiresWorkspace => false;

    private const int MaxTimeoutSeconds = 600;

    static ComputerExecTool()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
    }

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("kind", ToolSchema.String("Shell to run: 'powershell', 'cmd', or 'python'"), true),
        ("command", ToolSchema.String("Command or script text to execute"), true),
        ("cwd", ToolSchema.String("Working directory (absolute path recommended; defaults to the user profile)"), false),
        ("timeout_seconds", ToolSchema.Integer("Timeout in seconds (default 60, max 600)"), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var kind = GetString(arguments, "kind")?.Trim().ToLowerInvariant();
        var command = GetString(arguments, "command");
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Fail("command is required.");

        var cwd = GetString(arguments, "cwd");
        if (string.IsNullOrWhiteSpace(cwd))
            cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Directory.Exists(cwd))
            return ToolResult.Fail($"Working directory not found: {cwd}");

        CommandSpec spec;
        try
        {
            spec = Resolve(kind, command);
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail(ex.Message);
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(
            GetInt(arguments, "timeout_seconds") ?? 60, 1, MaxTimeoutSeconds));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var psi = new ProcessStartInfo
        {
            FileName = spec.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = cwd,
            StandardOutputEncoding = spec.OutputEncoding,
            StandardErrorEncoding = spec.OutputEncoding
        };
        foreach (var arg in spec.Arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ToolResult.Fail($"{spec.Label} could not start: {ex.Message}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            await KillTreeAsync(process.Id).ConfigureAwait(false);
        }

        string stdout, stderr;
        try
        {
            stdout = await stdoutTask.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            stdout = "";
            stderr = "(output unavailable: process tree did not close in time)";
        }

        if (timedOut)
        {
            return ToolResult.Fail($"{spec.Label} command timed out after {timeout.TotalSeconds:0}s: {Shorten(command)}");
        }

        var output = new StringBuilder();
        if (stdout.Length > 0) output.Append(stdout);
        if (stderr.Length > 0)
        {
            if (output.Length > 0) output.AppendLine();
            output.Append("[stderr] ").Append(stderr);
        }

        var exitCode = process.ExitCode;
        var text = output.Length == 0 ? "(no output)" : context.Truncate(output.ToString(), "command output");
        var summary = $"{spec.Label}: {Shorten(command)} → exit {exitCode}";

        context.Events.Publish(new ComputerActionEvent(
            Action: "exec_" + kind,
            X: null,
            Y: null,
            Target: Shorten(command),
            Success: exitCode == 0,
            Error: exitCode == 0 ? null : $"exit {exitCode}",
            Base64Screenshot: null));

        return exitCode == 0
            ? ToolResult.Ok(text, summary)
            : new ToolResult { Success = false, Output = $"Exit code {exitCode}\n{text}", Summary = summary };
    }

    private static async Task KillTreeAsync(int processId)
    {
        try
        {
            using var killer = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "taskkill.exe"),
                Arguments = $"/T /F /PID {processId}",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (killer is not null)
                await killer.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best effort — the timeout error is reported regardless
        }
    }

    private CommandSpec Resolve(string? kind, string command) => kind switch
    {
        "powershell" or "pwsh" or "ps" => ResolvePowerShell(command),
        "cmd" => new CommandSpec(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["/d", "/s", "/c", command],
            "cmd",
            ResolveOemEncoding()),
        "python" or "py" => ResolvePython(command),
        _ => throw new ArgumentException($"Unknown shell kind '{kind}'. Use 'powershell', 'cmd', or 'python'.")
    };

    private static CommandSpec ResolvePowerShell(string command)
    {
        var exe = FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe");
        if (exe is null)
            throw new ArgumentException("PowerShell not found on PATH (looked for pwsh.exe and powershell.exe).");

        // -EncodedCommand bypasses every quoting pitfall; the preamble forces UTF-8
        // output and silences the progress stream that pollutes captured output.
        var script = "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + command;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return new CommandSpec(
            exe,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded],
            "PowerShell",
            Encoding.UTF8);
    }

    private CommandSpec ResolvePython(string command)
    {
        if (!string.IsNullOrWhiteSpace(config.PythonPath))
            return new CommandSpec(config.PythonPath, ["-X", "utf8", "-c", command], "Python", Encoding.UTF8);

        var python = FindOnPath("python.exe") ?? FindOnPath("python3.exe");
        if (python is not null)
            return new CommandSpec(python, ["-X", "utf8", "-c", command], "Python", Encoding.UTF8);

        var launcher = FindOnPath("py.exe");
        if (launcher is not null)
            return new CommandSpec(launcher, ["-3", "-X", "utf8", "-c", command], "Python", Encoding.UTF8);

        throw new ArgumentException(
            "Python not found: add python.exe to PATH or set computerUse.pythonPath in the config.");
    }

    // cmd.exe writes piped output in the OEM console codepage (chcp changes it for
    // console handles, not reliably for its own piped echo), so decode with GetOEMCP.
    private static Encoding ResolveOemEncoding()
    {
        if (!OperatingSystem.IsWindows()) return Encoding.UTF8;
        try
        {
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch
        {
            return Encoding.Latin1;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static string? FindOnPath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(dir => Path.Combine(dir.Trim(), fileName))
        .FirstOrDefault(File.Exists);

    private static string Shorten(string command) =>
        command.Length > 80 ? command[..80] + "…" : command;

    private sealed record CommandSpec(string Executable, string[] Arguments, string Label, Encoding OutputEncoding);
}
