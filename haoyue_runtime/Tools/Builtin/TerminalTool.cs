using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Prompts;

namespace Haoyue.Runtime.Tools.Builtin;

/// <summary>
/// 内置终端：持久会话的 shell 执行。与 bash 工具的一次性执行不同，terminal 会话
/// 记住工作目录——`cd` 后的后续命令仍在该目录执行，贴合"先 cd 再反复构建/测试"
/// 的终端工作流。每个会话串行执行（同一会话并发请求排队），进程树终止、沙箱、
/// 风险审计与 bash 完全同构。会话不保持常驻进程：每条命令独立起 shell，只是
/// cwd 通过标记行回读延续（环境变量等其余 shell 状态不跨命令保留）。
/// </summary>
public sealed class TerminalTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override string Name => "terminal";
    public override string StatusLabel => "Running in terminal";
    public override bool Mutating => true; // a shell command may change anything

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("action", ToolSchema.String("Action: exec (default), list, or close", "exec", "list", "close"), false),
        ("command", ToolSchema.String("Shell command to execute (exec only)"), false),
        ("session_id", ToolSchema.String("Terminal session id, default \"default\"; sessions persist their working directory"), false),
        ("cwd", ToolSchema.String("Working directory for this command (defaults to the session's current directory)"), false),
        ("timeout_seconds", ToolSchema.Integer("Timeout in seconds (default from config)"), false));

    // 会话键 = 工作区根 + 会话名，跨工作区天然隔离；进程级存活（daemon 常驻）。
    private static readonly ConcurrentDictionary<string, TerminalSession> Sessions = new(StringComparer.Ordinal);

    private sealed class TerminalSession
    {
        public required string Cwd { get; set; }
        public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.UtcNow;
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var action = (GetString(arguments, "action") ?? "exec").Trim().ToLowerInvariant();
        var sessionId = GetString(arguments, "session_id") ?? "default";
        var root = context.Workspace.IsGlobal ? Directory.GetCurrentDirectory() : context.Workspace.Root;

        switch (action)
        {
            case "list":
                return FormatList(root);
            case "close":
                return await CloseSessionAsync(root, sessionId).ConfigureAwait(false);
            case "exec":
                break;
            default:
                return ToolResult.Fail($"Unknown action '{action}'. Use exec, list, or close.");
        }

        var command = GetString(arguments, "command");
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Fail("command is required for the exec action.");

        var key = SessionKey(root, sessionId);
        var session = Sessions.GetOrAdd(key, _ => new TerminalSession
        {
            Cwd = context.Workspace.IsGlobal ? Directory.GetCurrentDirectory() : context.Workspace.Root,
        });

        var cwd = GetString(arguments, "cwd") is { Length: > 0 } requested
            ? context.ResolvePath(requested)
            : session.Cwd;
        if (!Directory.Exists(cwd))
            return ToolResult.Fail($"Working directory not found: {cwd}");

        var timeout = TimeSpan.FromSeconds(Math.Clamp(
            GetInt(arguments, "timeout_seconds") ?? context.Agent.BashTimeoutSeconds, 1, 3600));

        // 同一会话串行：并发回合共享终端时按到达顺序排队，避免 cwd 竞态与输出交错。
        await session.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var marker = Guid.NewGuid().ToString("N")[..8];
            var (shell, shellArgs, kind) = ShellRunner.ResolveShellWithKind(command);
            var wrapped = WrapCommand(command, marker, kind);
            string[] finalArgs = kind == ShellRunner.ShellKind.Cmd
                ? ["/d", "/v:on", "/s", "/c", wrapped] // /v:on 启用延迟展开（!CD! / !ERRORLEVEL!）
                : [.. shellArgs[..^1], wrapped];

            var (risk, riskReasons) = BashRiskClassifier.Classify(command);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var sandbox = OperatingSystem.IsWindows() && context.Agent.BashSandbox.Enabled
                ? context.Agent.BashSandbox
                : null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var outcome = sandbox is not null
                ? await ShellRunner.ExecuteSandboxedAsync(sandbox, shell, finalArgs, cwd, timeout, cts.Token, ct).ConfigureAwait(false)
                : await ShellRunner.ExecuteDirectAsync(shell, finalArgs, cwd, timeout, cts.Token, ct).ConfigureAwait(false);
            stopwatch.Stop();

            Audit(context, command, cwd, risk, riskReasons, outcome.Outcome, outcome.ExitCode, stopwatch.ElapsedMilliseconds, sandboxed: sandbox is not null);

            // H-B：与 bash 一致——失败输出把预算倾向尾部，保住构建/测试日志的失败摘要。
            var text = outcome.Outcome == "completed" && outcome.ExitCode == 0
                ? context.Truncate(outcome.Output, "command output")
                : context.TruncateTailPrioritized(outcome.Output, "command output", tailShare: 0.7);

            var (newCwd, output) = ExtractCwd(text, marker);
            string? appliedCwd = null;
            if (outcome.Outcome == "completed" && outcome.ExitCode == 0)
            {
                session.LastUsedAt = DateTimeOffset.UtcNow;
                var normalized = newCwd is null ? null : NormalizePath(newCwd);
                if (normalized is not null && Directory.Exists(normalized))
                {
                    session.Cwd = normalized;
                    // cwd 注记只在目录实际变化时出现（pwd 每条命令都回读）。
                    if (!string.Equals(normalized, cwd,
                            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        appliedCwd = normalized;
                }
            }

            return FormatResult(command, output, outcome.ExitCode ?? -1, sessionId,
                outcome.Outcome == "timeout" ? $"Command timed out after {timeout.TotalSeconds:0}s: {Shorten(command)}" : null,
                appliedCwd);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>Appends the CWD read-back to the command so the session can follow `cd`.</summary>
    internal static string WrapCommand(string command, string marker, ShellRunner.ShellKind kind) => kind switch
    {
        ShellRunner.ShellKind.Bash =>
            // cygpath -w（Git Bash/MSYS 自带）把 /tmp/... 这类挂载路径转回 Win32 路径；
            // WSL 与真 Unix 无 cygpath，回退 pwd（WSL 路径由 NormalizePath 处理）。
            command + $"\n__haoyue_rc=$?\nprintf '%s\\n' \"__HAOYUE_CWD_{marker}__$(cygpath -w \"$(pwd)\" 2>/dev/null || pwd)\"\nexit $__haoyue_rc",
        ShellRunner.ShellKind.PowerShell =>
            command + "\n$haoyue_rc = if ($null -ne $LASTEXITCODE) { $LASTEXITCODE } elseif ($?) { 0 } else { 1 }"
            + $"\nWrite-Output \"__HAOYUE_CWD_{marker}__$((Get-Location).Path)\"\nexit $haoyue_rc",
        ShellRunner.ShellKind.Cmd =>
            command + $" & echo __HAOYUE_CWD_{marker}__!CD!& exit /b !ERRORLEVEL!",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// Strips every marker line from the output and returns the LAST marker's directory
    /// (the shell prints exactly one, but a command that echoes the token must not win).
    /// Null cwd when the marker never arrived (command called exit early) — the session
    /// then keeps its previous directory.
    /// </summary>
    internal static (string? NewCwd, string Output) ExtractCwd(string output, string marker)
    {
        var token = $"__HAOYUE_CWD_{marker}__";
        string? cwd = null;
        var kept = new List<string>();
        var start = 0;
        while (start < output.Length)
        {
            var end = output.IndexOf('\n', start);
            var line = end < 0 ? output[start..] : output[start..end];
            var idx = line.IndexOf(token, StringComparison.Ordinal);
            if (idx >= 0)
                cwd = line[(idx + token.Length)..].Trim().TrimEnd('\r');
            else
                kept.Add(line);
            if (end < 0) break;
            start = end + 1;
        }

        if (cwd is null)
            return (null, output.TrimEnd());
        return (cwd, string.Join("\n", kept).TrimEnd());
    }

    /// <summary>
    /// Maps POSIX-style paths reported by Unix-flavored shells on Windows back to
    /// Win32 paths: Git Bash prints /c/Users/... (MSYS) or /mnt/c/Users/... (WSL),
    /// and drive-letter paths may arrive with forward slashes (C:/Users/...). Without
    /// this the session cwd would fail Directory.Exists and every later command would
    /// fall back to the workspace root.
    /// </summary>
    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;

        if (OperatingSystem.IsWindows())
        {
            // MSYS/Git Bash: /c/Users/... → C:\Users\...
            if (path.Length >= 3 && path[0] == '/' && path[1] is >= 'a' and <= 'z' && path[2] == '/')
                return char.ToUpperInvariant(path[1]) + ":" + path[2..].Replace('/', '\\');
            // WSL: /mnt/c/Users/... → C:\Users\...
            if (path.Length >= 7 && path.StartsWith("/mnt/", StringComparison.Ordinal)
                && path[5] is >= 'a' and <= 'z' && path[6] == '/')
                return char.ToUpperInvariant(path[5]) + ":" + path[7..].Replace('/', '\\');
            // Drive letter with forward slashes: C:/Users/... → C:\Users\...
            if (path.Length >= 3 && path[1] == ':' && (path[2] == '/' || path[2] == '\\'))
                return path.Replace('/', '\\');
        }
        return path;
    }

    private static ToolResult FormatList(string root)
    {
        var prefix = SessionKey(root, "");
        var rows = Sessions
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv =>
            {
                var id = kv.Key[(kv.Key.IndexOf('|') + 1)..];
                return $"- {id}: cwd={kv.Value.Cwd}, last used {kv.Value.LastUsedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            })
            .ToList();
        return ToolResult.Ok(rows.Count == 0 ? "(no sessions)" : string.Join("\n", rows),
            $"{rows.Count} terminal session(s)");
    }

    private static async Task<ToolResult> CloseSessionAsync(string root, string sessionId)
    {
        var key = SessionKey(root, sessionId);
        if (!Sessions.TryRemove(key, out var session))
            return ToolResult.Fail($"No terminal session '{sessionId}' in this workspace.");

        var acquired = await session.Gate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (!acquired)
        {
            Sessions[key] = session; // busy — restore it instead of dropping a queued caller
            return ToolResult.Fail($"Session '{sessionId}' is busy; close again when its command finishes.");
        }
        session.Gate.Dispose();
        return ToolResult.Ok($"Closed terminal session '{sessionId}'.");
    }

    private static string SessionKey(string root, string sessionId) => $"{root}|{sessionId}";

    private static ToolResult FormatResult(
        string command, string text, int exitCode, string sessionId, string? failure, string? cwd)
    {
        var cwdNote = cwd is null ? "" : $"\n(terminal cwd: {cwd})";
        if (failure is not null)
        {
            var partial = text.Length == 0 ? "" : "\n超时前已产生的部分输出：\n" + text;
            return ToolResult.Fail(failure + partial + cwdNote);
        }

        var trimmed = text.Length == 0 ? "(no output)" : text;
        var summary = $"{Shorten(command)} → exit {exitCode} [{sessionId}]";
        return exitCode == 0
            ? ToolResult.Ok(trimmed + cwdNote, summary)
            : new ToolResult { Success = false, Output = $"Exit code {exitCode}\n{trimmed}{cwdNote}", Summary = summary };
    }

    /// <summary>G6 审计：terminal 与 bash 落同构的 JSONL 审计记录。</summary>
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

    private static string Shorten(string command) =>
        command.Length > 60 ? command[..60] + "…" : command;
}
