using System.Diagnostics;
using System.Text;

namespace Haoyue.Runtime.ComputerUse.SystemControl;

/// <summary>Outcome of a script run through <see cref="ShellCommandRunner"/>.</summary>
internal sealed record ShellOutcome(int ExitCode, string Output, string? Error)
{
    public bool Success => Error is null && ExitCode == 0;

    public static ShellOutcome Failed(string error) => new(-1, "", error);
}

/// <summary>
/// Shared cross-platform script runner for System Control tools.
/// Windows: PowerShell with forced UTF-8 output (-EncodedCommand, same preamble as
/// ComputerExecTool); Linux: /bin/sh -c. On timeout the whole process tree is killed
/// via Process.Kill(entireProcessTree: true) so pipes always close.
/// </summary>
internal static class ShellCommandRunner
{
    public static async Task<ShellOutcome> RunAsync(
        string script,
        string? cwd = null,
        int timeoutSeconds = 120,
        CancellationToken ct = default)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 600));

        string fileName;
        var args = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            var ps = ExecutableLocator.FindOnPath("pwsh.exe") ?? ExecutableLocator.FindOnPath("powershell.exe");
            if (ps is null)
                return ShellOutcome.Failed("PowerShell 未找到（PATH 中无 pwsh.exe / powershell.exe），无法执行系统查询。");
            // -EncodedCommand bypasses quoting pitfalls; the preamble forces UTF-8 output
            // and silences the progress stream that would pollute captured output.
            var preamble = "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(preamble + script));
            fileName = ps;
            args.AddRange(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded]);
        }
        else
        {
            fileName = "/bin/sh";
            args.AddRange(["-c", script]);
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
            psi.WorkingDirectory = cwd;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return ShellOutcome.Failed($"进程启动失败: {ex.Message}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return ShellOutcome.Failed("调用已取消。");
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { }
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
            return ShellOutcome.Failed($"命令超时（{timeout.TotalSeconds:0}s）");

        var sb = new StringBuilder();
        if (stdout.Length > 0) sb.Append(stdout.TrimEnd());
        if (stderr.Length > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append("[stderr] ").Append(stderr.TrimEnd());
        }

        return new ShellOutcome(process.ExitCode, sb.ToString(), null);
    }
}

/// <summary>PATH search helper shared by System Control tools and Python detection.</summary>
public static class ExecutableLocator
{
    public static string? FindOnPath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(dir => Path.Combine(dir.Trim(), fileName))
        .FirstOrDefault(File.Exists);
}

/// <summary>A located Python interpreter and how it was found.</summary>
public sealed record PythonInstall(string Executable, string Source);

/// <summary>
/// Python interpreter detection for Computer Use. Order: configured path → PATH
/// (python / python3) → Windows py launcher. Returns null when Python is not
/// installed; callers must degrade gracefully (report status instead of failing).
/// </summary>
public static class PythonLocator
{
    public static PythonInstall? Find(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return new PythonInstall(configuredPath, "配置项 computerUse.pythonPath");

        if (OperatingSystem.IsWindows())
        {
            foreach (var name in new[] { "python.exe", "python3.exe" })
            {
                var exe = ExecutableLocator.FindOnPath(name);
                if (exe is not null) return new PythonInstall(exe, "PATH");
            }
            var launcher = ExecutableLocator.FindOnPath("py.exe");
            if (launcher is not null) return new PythonInstall(launcher, "py 启动器");
        }
        else
        {
            foreach (var name in new[] { "python3", "python" })
            {
                var exe = ExecutableLocator.FindOnPath(name);
                if (exe is not null) return new PythonInstall(exe, "PATH");
            }
        }
        return null;
    }
}
