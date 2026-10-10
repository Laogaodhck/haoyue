using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Haoyue.Runtime.ComputerUse.SystemControl;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;

namespace Haoyue.Runtime.ComputerUse;

/// <summary>
/// Native shell execution for Computer Use: PowerShell, cmd, bash, and Python,
/// resolved for the detected OS family. 'auto' (and an omitted kind) picks the
/// OS default — PowerShell on Windows, bash on Linux/macOS — so the model can
/// delegate dialect choice; Windows-only kinds fail with a precise message on
/// POSIX hosts and vice versa instead of a cryptic spawn error.
///
/// Decoding follows what each child actually emits: PowerShell (forced UTF-8 via
/// Console.OutputEncoding) and Python (-X utf8) decode as UTF-8; cmd.exe pipes
/// bytes in the OEM console codepage (GBK on zh-CN systems — chcp 65001 does not
/// affect its piped echo), so cmd output decodes with GetOEMCP(). On timeout the
/// whole process tree is killed — taskkill /T on Windows, Kill(entireProcessTree)
/// elsewhere — child processes otherwise keep the pipes open and the call would
/// hang or silently succeed. Python resolution is shared with computer_sysinfo
/// via <see cref="PythonLocator"/> (configured path → PATH → py launcher/common
/// install dirs), so "is Python installed" is answered identically everywhere.
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
        ("kind", ToolSchema.String("Shell to run: 'auto' (OS default — PowerShell on Windows, bash on Linux/macOS; also used when kind is omitted), 'powershell', 'bash' (POSIX only), 'cmd' (Windows only), or 'python'", "auto", "powershell", "bash", "cmd", "python"), true),
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
            await KillTreeAsync(process).ConfigureAwait(false);
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

    /// <summary>Kills the whole process tree on timeout. taskkill /T on Windows
    /// (it also catches console children .NET's Kill may miss); on POSIX the
    /// runtime's entireProcessTree kill is the native equivalent.</summary>
    private static async Task KillTreeAsync(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return;
        }
        try
        {
            using var killer = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "taskkill.exe"),
                Arguments = $"/T /F /PID {process.Id}",
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

    /// <summary>Shell resolution keyed off the detected OS family. Internal for tests:
    /// kind is normalized already (null/empty → auto). Each unsupported combination
    /// throws an ArgumentException that names the OS and the correct kind to use.</summary>
    internal CommandSpec Resolve(string? kind, string command) => kind switch
    {
        null or "" or "auto" => OperatingSystem.IsWindows() ? ResolvePowerShell(command) : ResolveBash(command),
        "powershell" or "pwsh" or "ps" => ResolvePowerShell(command),
        "bash" or "sh" => ResolveBash(command),
        "cmd" => ResolveCmd(command),
        "python" or "py" => ResolvePython(command, config.PythonPath),
        _ => throw new ArgumentException(
            $"Unknown shell kind '{kind}'. Available kinds on {SystemEnvironment.OsLabel}: " +
            (OperatingSystem.IsWindows()
                ? "'auto' (= powershell), 'powershell', 'cmd', 'python'."
                : "'auto' (= bash), 'bash', 'powershell' (requires pwsh), 'python'."))
    };

    private static CommandSpec ResolvePowerShell(string command)
    {
        if (OperatingSystem.IsWindows())
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

        // POSIX: only pwsh (Core) exists here; Windows PowerShell never does.
        var pwsh = FindOnPath("pwsh");
        if (pwsh is null)
            throw new ArgumentException(
                $"PowerShell (pwsh) is not installed on this {SystemEnvironment.OsLabel} host; use kind='bash' (or omit kind — auto resolves to bash).");

        var posixScript = "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + command;
        var posixEncoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(posixScript));
        return new CommandSpec(
            pwsh,
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", posixEncoded],
            "PowerShell (pwsh)",
            Encoding.UTF8);
    }

    private static CommandSpec ResolveBash(string command)
    {
        if (OperatingSystem.IsWindows())
            throw new ArgumentException(
                "kind='bash' is not available on Windows hosts (no login shell is provisioned). Use kind='powershell' or 'cmd' — or omit kind and 'auto' resolves to PowerShell.");

        foreach (var candidate in new[] { "/bin/bash", "/usr/bin/bash", "/bin/sh" })
        {
            if (File.Exists(candidate))
                return new CommandSpec(candidate, ["-c", command], Path.GetFileName(candidate), Encoding.UTF8);
        }
        throw new ArgumentException("No POSIX shell found (looked for /bin/bash, /usr/bin/bash, /bin/sh).");
    }

    private static CommandSpec ResolveCmd(string command)
    {
        if (!OperatingSystem.IsWindows())
            throw new ArgumentException(
                $"kind='cmd' only exists on Windows, and this host is {SystemEnvironment.OsLabel}. Use kind='bash' (or omit kind — auto resolves to bash).");

        return new CommandSpec(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["/d", "/s", "/c", command],
            "cmd",
            ResolveOemEncoding());
    }

    private static CommandSpec ResolvePython(string command, string? configuredPath)
    {
        // Same resolution the sysinfo tool reports, so "Python: 已安装" always means
        // kind=python works and vice versa — one source of truth, OS-aware.
        var install = PythonLocator.Find(configuredPath);
        if (install is null)
        {
            throw new ArgumentException(OperatingSystem.IsWindows()
                ? "Python not found: add python.exe to PATH, install the py launcher, or set computerUse.pythonPath in the config."
                : $"Python not found on this {SystemEnvironment.OsLabel} host: install python3 (e.g. 'sudo apt install python3') or set computerUse.pythonPath in the config.");
        }
        return new CommandSpec(install.Executable, ["-X", "utf8", "-c", command], "Python", Encoding.UTF8);
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

    internal sealed record CommandSpec(string Executable, string[] Arguments, string Label, Encoding OutputEncoding);
}
