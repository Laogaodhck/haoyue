using System.Text.Json.Nodes;
using Haoyue.Runtime.Prompts;

namespace Haoyue.Runtime.Tools.Builtin;

/// <summary>Runs a shell command (bash when available, cmd.exe otherwise) inside the workspace.</summary>
public sealed class BashTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override string Name => "bash";
    public override string StatusLabel => "Running command";
    public override bool Mutating => true; // a shell command may change anything
    /// <summary>5.2：执行任意命令 = 系统面 + 写入面，默认触发首次同意。</summary>
    public ToolRiskTier RiskTier => ToolRiskTier.Write | ToolRiskTier.System;

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
            ? await ShellRunner.ExecuteSandboxedAsync(sandbox, shell, shellArgs, cwd, timeout, cts.Token, ct).ConfigureAwait(false)
            : await ShellRunner.ExecuteDirectAsync(shell, shellArgs, cwd, timeout, cts.Token, ct).ConfigureAwait(false);
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
        var (shell, args, _) = ShellRunner.ResolveShellWithKind(command);
        return (shell, args);
    }

    private static string Shorten(string command) =>
        command.Length > 60 ? command[..60] + "…" : command;
}
