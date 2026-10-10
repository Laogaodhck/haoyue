using System.Text.Encodings.Web;
using System.Text.Json;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Tools;

/// <summary>Bash command risk level, assigned before execution.</summary>
public enum BashRisk
{
    Low = 0,
    Medium = 1,
    High = 2,
}

/// <summary>
/// Pre-execution risk classification for bash commands. This is the audit layer
/// (G6): it records what ran and how risky it was judged, complementing the G1
/// policy gate which denies the worst patterns outright. Classification is purely
/// syntactic — a conservative list of mutating/networking/destructive shapes.
/// </summary>
public static class BashRiskClassifier
{
    /// <summary>Highest risk: destructive to filesystem roots, system state, or remote code execution.</summary>
    private static readonly (string Pattern, string Reason)[] HighPatterns =
    [
        (@"\brm\s+(?:-{1,2}[\w-]+\s+)*-(?:\w*r\w*f|\w*f\w*r)\w*\s", "递归强删"),
        (@"\b(?:rd|rmdir)\s+/s\b", "Windows 递归删目录"),
        (@"Remove-Item\s+(?:-\w+\s+)*-Recurse", "PowerShell 递归删除"),
        (@"\bdel\s+/[sfq]", "Windows 递归删除"),
        (@"\b(?:format|mkfs|diskpart)\b", "磁盘格式化/分区"),
        (@"\bdd\s+[^\n]*\bof=/dev/", "直写块设备"),
        (@"\bshutdown\b|\breboot\b", "关机/重启"),
        (@"\b(?:curl|wget|irm|iwr)\b[^;\n|]*\|\s*(?:ba|z|da)?sh\b", "下载内容管道执行"),
        (@"\biex\b|\bInvoke-Expression\b", "表达式注入执行"),
        (@"\breg\s+delete\s+HK", "注册表删除"),
        (@"\bvssadmin\s+delete\s+shadows\b", "删除卷影副本"),
        (@"\bbcdedit\b", "引导配置修改"),
        (@"\btaskkill\b[^&\n]*/f\b", "强制终止进程"),
    ];

    /// <summary>Medium: mutates state, touches the network, installs packages or kills processes.</summary>
    private static readonly (string Pattern, string Reason)[] MediumPatterns =
    [
        (@"\bgit\s+push\b", "推送远端"),
        (@"\bgit\s+reset\s+--hard\b|\bgit\s+clean\s+-[fd]", "丢弃本地变更"),
        (@"\b(?:npm|pnpm|yarn|pip|uv|cargo|dotnet)\s+(?:publish|install|add|remove|uninstall)\b", "包管理变更"),
        (@"\b(?:apt|apt-get|brew|choco|winget|scoop)\s+\w+", "系统包管理"),
        (@"\bdocker\s+(?:run|rm|rmi|system\s+prune|volume\s+rm)\b", "容器生命周期"),
        (@"\bkill\b\s|killall\b|\bStop-Process\b", "终止进程"),
        (@"\bchmod\b|\bchown\b|\bicacls\b", "权限变更"),
        (@"\brm\s+", "删除文件"),
        (@"\brmdir\b|\bdel\b", "删除文件"),
        (@"Remove-Item\b", "删除文件"),
        (@"\bmv\b\s", "移动/覆盖文件"),
        (@"\b(?:curl|wget|scp|sftp|ssh|ftp)\b", "网络访问"),
        (@"\bInvoke-WebRequest\b|\biwr\b|\birm\b", "网络访问"),
        (@"\bnet\s+(?:stop|start)\b|\bsc\b\s|\bsystemctl\b", "服务控制"),
        (@"\bkill\s+-9\b", "强制终止进程"),
        (@">\s*/|[12]>\s*/", "重定向到绝对路径"),
        (@"\bgit\s+branch\s+-D\b|\bgit\s+tag\s+-d\b", "删除引用"),
    ];

    /// <summary>Classifies one command line into (level, reasons). Multiple hits stack to the worst level.</summary>
    public static (BashRisk Level, string[] Reasons) Classify(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return (BashRisk.Low, []);

        var reasons = new List<string>();
        var level = BashRisk.Low;
        foreach (var (pattern, reason) in HighPatterns)
        {
            if (TryMatch(pattern, command) && level < BashRisk.High)
                level = BashRisk.High;
            if (TryMatch(pattern, command) && !reasons.Contains(reason))
                reasons.Add(reason);
        }
        if (level >= BashRisk.High)
            return (level, [.. reasons]);

        foreach (var (pattern, reason) in MediumPatterns)
        {
            if (TryMatch(pattern, command))
            {
                if (level < BashRisk.Medium) level = BashRisk.Medium;
                if (!reasons.Contains(reason)) reasons.Add(reason);
            }
        }
        return (level, [.. reasons]);
    }

    private static bool TryMatch(string pattern, string command)
    {
        try
        {
            return System.Text.RegularExpressions.Regex.IsMatch(
                command, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        catch (ArgumentException)
        {
            return false; // a broken pattern must never break execution
        }
    }
}

/// <summary>
/// Append-only JSONL audit trail of executed bash commands, one file per workspace
/// (<c>.haoyue/audit/bash.jsonl</c>). Writes are line-buffered appends under a lock;
/// the file is intentionally never rotated or truncated by the runtime.
/// </summary>
public static class BashAuditLog
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string AuditFile(WorkspaceInfo workspace) =>
        Path.Combine(workspace.Root, ".haoyue", "audit", "bash.jsonl");

    public static void Append(WorkspaceInfo workspace, BashAuditRecord record)
    {
        try
        {
            var file = AuditFile(workspace);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            lock (Gate)
            {
                File.AppendAllText(file, JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // Audit IO must never break command execution.
        }
    }
}

public sealed record BashAuditRecord(
    string Timestamp,
    string Command,
    string Cwd,
    string Risk,
    string[] Reasons,
    string Outcome,
    int? ExitCode,
    long ElapsedMs,
    bool Sandboxed = false);
