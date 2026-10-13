using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// </summary>
    public static (string Category, string Target)? Resolve(ITool tool, JsonObject? arguments)
    {
        if (tool.Name.StartsWith("mcp__", StringComparison.Ordinal))
            return ("mcp", tool.Name);
        if (!tool.Mutating) return null;
        return tool.Name switch
        {
            "write_file" => ("file-write", Target(arguments)),
            "edit_file" => ("file-edit", Target(arguments)),
            _ => null,
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
                File.AppendAllText(file, JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Audit IO must never break tool execution.
        }
    }

    private static string Target(JsonObject? arguments) =>
        arguments?["path"]?.GetValue<string>() ?? "";
}
