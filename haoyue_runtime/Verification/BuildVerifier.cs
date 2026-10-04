using System.Text;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Verification;

public sealed record VerifyResult(bool Success, string Command, string Output);

public interface IVerifier
{
    /// <summary>Returns the build/check command for this workspace, or null when none applies.</summary>
    string? ResolveCommand(WorkspaceInfo workspace);

    /// <summary>
    /// Returns the verification chain for this workspace: the workspace <c>verifyCommands</c>
    /// list when configured, otherwise the single auto-detected / <c>verifyCommand</c> command.
    /// Empty when no verification applies.
    /// </summary>
    IReadOnlyList<string> ResolveCommands(WorkspaceInfo workspace);

    Task<VerifyResult> VerifyAsync(WorkspaceInfo workspace, CancellationToken ct);
}

/// <summary>
/// Runs the project's build/check command(s) after the agent edits files, so failures
/// feed straight back into the repair loop. Multi-step chains run in order with
/// fail-fast semantics: the first failing step stops the chain and its output is
/// summarized into the repair prompt. Steps run as independent shell invocations,
/// which keeps chains portable across shells (PowerShell 5.1 lacks <c>&amp;&amp;</c>).
/// </summary>
public sealed class BuildVerifier : IVerifier
{
    /// <summary>Trailing size kept for a failed step's raw output before error-line extraction.</summary>
    private const int MaxStepOutputChars = 8000;

    /// <summary>Target size of the summarized failure output handed to the repair prompt.</summary>
    private const int MaxSummaryChars = 4000;

    /// <summary>Lines matching these keywords (plus one context line on each side) survive truncation.</summary>
    private static readonly Regex ErrorLinePattern = new(
        "error|failed|fatal|exception|warning|异常|错误|失败|警告",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string? ResolveCommand(WorkspaceInfo workspace) =>
        ResolveCommands(workspace).FirstOrDefault();

    public IReadOnlyList<string> ResolveCommands(WorkspaceInfo workspace)
    {
        var configured = workspace.Config?.VerifyCommands;
        if (configured is { Count: > 0 })
            return configured.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();

        if (!string.IsNullOrWhiteSpace(workspace.Config?.VerifyCommand))
            return [workspace.Config.VerifyCommand];

        foreach (var kind in workspace.ProjectKinds)
        {
            switch (kind)
            {
                case "dotnet": return ["dotnet build --nologo -v q"];
                case "rust": return ["cargo check --quiet"];
                case "go": return ["go build ./..."];
                case "node":
                    var packageJson = Path.Combine(workspace.Root, "package.json");
                    try
                    {
                        if (File.Exists(packageJson) &&
                            File.ReadAllText(packageJson).Contains("\"build\"", StringComparison.Ordinal))
                            return ["npm run build"];
                    }
                    catch (IOException) { }
                    break;
            }
        }
        return [];
    }

    public async Task<VerifyResult> VerifyAsync(WorkspaceInfo workspace, CancellationToken ct)
    {
        var commands = ResolveCommands(workspace);
        if (commands.Count == 0)
            return new VerifyResult(true, "", "No verification command applies to this workspace.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(10)); // shared budget across the whole chain

        var summary = new StringBuilder();
        try
        {
            for (var i = 0; i < commands.Count; i++)
            {
                var command = commands[i];
                var (exitCode, output) = await RunCommandAsync(workspace.Root, command, cts.Token).ConfigureAwait(false);

                if (exitCode == 0)
                {
                    summary.AppendLine($"[{i + 1}/{commands.Count}] {command} → ok");
                    continue;
                }

                // Fail-fast: report the failing step with a summarized error block.
                summary.AppendLine($"[{i + 1}/{commands.Count}] {command} → failed (exit {exitCode})");
                var failure = SummarizeFailure(output);
                summary.Append(failure);
                return new VerifyResult(false, command, summary.ToString().Trim());
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Only the 10-minute budget lands here; user cancellation propagates.
            return new VerifyResult(false, commands[^1], "Verification timed out after 10 minutes.");
        }

        return new VerifyResult(true, string.Join(" && ", commands), summary.ToString().Trim());
    }

    private static async Task<(int ExitCode, string Output)> RunCommandAsync(
        string workingDirectory, string command, CancellationToken ct)
    {
        var (shell, args) = ResolveShell(command);

        var result = await Cli.Wrap(shell)
            .WithArguments(args)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        var output = new StringBuilder(result.StandardOutput);
        if (result.StandardError.Length > 0) output.AppendLine().Append(result.StandardError);

        var text = output.ToString().Trim();
        if (text.Length > MaxStepOutputChars) text = text[^MaxStepOutputChars..]; // errors are usually at the end
        return (result.ExitCode, text);
    }

    /// <summary>
    /// Keeps the failure output compact but useful: when the raw output is short it passes
    /// through unchanged; otherwise lines matching error keywords (with one context line on
    /// each side) are preserved first and the remainder is taken from the tail, so the
    /// repair prompt sees the actual error block instead of pages of build noise.
    /// </summary>
    public static string SummarizeFailure(string output)
    {
        if (string.IsNullOrWhiteSpace(output) || output.Length <= MaxSummaryChars)
            return output;

        var lines = output.Replace("\r\n", "\n").Split('\n');
        var keep = new bool[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            if (!ErrorLinePattern.IsMatch(lines[i])) continue;
            for (var j = Math.Max(0, i - 1); j <= Math.Min(lines.Length - 1, i + 1); j++)
                keep[j] = true;
        }

        var summary = new StringBuilder();
        summary.AppendLine("[output truncated — lines matching error keywords are preserved]");
        var keptAny = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!keep[i]) continue;
            summary.AppendLine(lines[i]);
            keptAny = true;
        }
        if (!keptAny) summary.AppendLine(lines[^1]);

        var text = summary.ToString().TrimEnd();
        if (text.Length > MaxSummaryChars)
            text = text[..MaxSummaryChars] + "\n[…]";
        return text;
    }

    private static (string Shell, string[] Args) ResolveShell(string command)
    {
        if (!OperatingSystem.IsWindows())
            return ("/bin/bash", ["-c", command]);

        var bash = FindOnPath("bash.exe");
        if (bash is not null) return (bash, ["-c", command]);

        var pwsh = FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe");
        if (pwsh is not null) return (pwsh, ["-NoProfile", "-NonInteractive", "-Command", command]);

        return ("cmd.exe", ["/d", "/s", "/c", command]);
    }

    private static string? FindOnPath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(dir => Path.Combine(dir.Trim(), fileName))
        .FirstOrDefault(File.Exists);
}
