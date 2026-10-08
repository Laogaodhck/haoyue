using System.Text;

namespace Haoyue.Runtime.Data;

/// <summary>
/// Renders a knowledge scope as one Markdown document for backup/sharing. The
/// daemon returns the text to the desktop (which saves it through its own save
/// dialog) and the CLI writes it to a file or stdout — one format, two callers.
/// </summary>
public static class KnowledgeExport
{
    public static string ToMarkdown(string scopeLabel, IReadOnlyList<KnowledgeEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 知识库导出");
        sb.AppendLine();
        sb.AppendLine($"- 范围：{scopeLabel}");
        sb.AppendLine($"- 导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 条目数：{entries.Count}");
        sb.AppendLine();
        foreach (var entry in entries)
        {
            sb.AppendLine($"## {entry.Title}");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(entry.Tags))
                sb.AppendLine($"> 标签：{entry.Tags}  ");
            sb.AppendLine($"> 更新：{entry.UpdatedAt}");
            sb.AppendLine();
            sb.AppendLine(entry.Content);
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
