using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Secrets;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Tools;

/// <summary>
/// One line of the uniform tool audit trail (<c>.haoyue/audit/tools.jsonl</c>): an
/// MCP tool call or a file mutation executed through the agent tool pipeline.
/// Bash keeps its own risk-classified log (<c>bash.jsonl</c>) and is not duplicated here.
/// </summary>
public sealed record ToolAuditRecord(
    string Timestamp,
    string Tool,
    string Category,
    string Target,
    string Outcome,
    string? Summary,
    long ElapsedMs);

/// <summary>
/// G6 审计扩展（L3）：把审计覆盖面从 bash-only 扩展到 MCP 工具调用与文件写入。
/// 与 BashAuditLog 相同的追加式 JSONL 设计——同目录、进程内锁、审计 IO 失败绝不
/// 中断工具执行。
/// </summary>
public static class ToolAuditLog
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string AuditFile(WorkspaceInfo workspace) =>
        Path.Combine(workspace.Root, ".haoyue", "audit", "tools.jsonl");

    /// <summary>
    /// Resolves the (category, target) pair worth auditing for a completed tool call,
    /// or null when the call stays out of the trail: non-mutating builtin tools are
    /// read-only observations; bash carries its own risk-classified audit record.
    /// Every other mutation is recorded — including the Computer-Use shell, which is a
    /// native command executor and must not leave the trail because its name is absent.
    /// </summary>
    public static (string Category, string Target)? Resolve(ITool tool, JsonObject? arguments)
    {
        if (tool.Name.StartsWith("mcp__", StringComparison.Ordinal))
            return ("mcp", tool.Name);
        if (tool.Name.Equals("bash", StringComparison.OrdinalIgnoreCase)) return null;
        if (!tool.Mutating) return null;
        return tool.Name switch
        {
            "write_file" => ("file-write", Target(arguments)),
            "edit_file" => ("file-edit", Target(arguments)),
            "computer_exec" => ("shell-exec", Command(arguments)),
            _ => ("tool-mutation", tool.Name),
        };
    }

    public static void Append(WorkspaceInfo workspace, ToolAuditRecord record)
    {
        try
        {
            var file = AuditFile(workspace);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            lock (Gate)
            {
                AuditRotation.RotateIfNeeded(file);
                var redacted = record with
                {
                    Target = CredentialRedactor.Redact(record.Target),
                    Summary = CredentialRedactor.Redact(record.Summary),
                };
                File.AppendAllText(file, JsonSerializer.Serialize(redacted, JsonOptions) + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Audit IO must never break tool execution.
        }
    }

    private static string Target(JsonObject? arguments) =>
        arguments?["path"]?.GetValue<string>() ?? "";

    /// <summary>First line of a shell command, so the trail says what ran without dumping a script.</summary>
    private static string Command(JsonObject? arguments) =>
        (arguments?["command"]?.GetValue<string>() ?? "").Split('\n')[0].Trim();
}

/// <summary>
/// Size watermark shared by the audit trails. The files used to be append-only forever,
/// which meant a year of agent work accumulated unbounded plaintext command history in
/// every workspace. One generation is kept (<c>x.jsonl</c> → <c>x.prev.jsonl</c>) so the
/// trail stays inspectable while its footprint stops growing.
/// </summary>
internal static class AuditRotation
{
    public const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>Renames the active file to its <c>.prev</c> generation once past the watermark.</summary>
    public static void RotateIfNeeded(string file)
    {
        if (!File.Exists(file)) return;
        if (new FileInfo(file).Length < MaxBytes) return;
        var previous = file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            ? file[..^".jsonl".Length] + ".prev.jsonl"
            : file + ".prev";
        try
        {
            File.Delete(previous);
            File.Move(file, previous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Rotation is best-effort: a locked previous generation must not stop auditing.
        }
    }
}
