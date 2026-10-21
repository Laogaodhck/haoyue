using System.Text;

namespace Haoyue.Runtime.Agents;

/// <summary>四段式汇报的结构化解析结果（3.3）。缺失段落为空集合，不视为错误。</summary>
public sealed record SubtaskReport(
    string? Conclusion,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Risks)
{
    public bool HasStructure => Conclusion is not null || Findings.Count > 0 || Files.Count > 0 || Risks.Count > 0;
}

/// <summary>
/// 子任务结果结构化聚合（3.3）：子代理按 <see cref="AgentRole.ReportTemplate"/>
/// 的四段式汇报（## 结论 / ## 关键发现 / ## 涉及文件 / ## 风险与不确定性）返回，
/// 聚合方解析出结构化摘要，父代理历史里的报告 token 占用随之下降；解析不出
/// 结构（自由文本、乱序、超长）时原样透传——兜底永不丢信息。
/// </summary>
public static class SubtaskReportAggregator
{
    /// <summary>解析四段式汇报。只认「## 标题」行（与模板一致）；正文按行收集，</summary>
    public static SubtaskReport Parse(string text)
    {
        string? conclusion = null;
        var findings = new List<string>();
        var files = new List<string>();
        var risks = new List<string>();
        var current = -1; // -1 = 前言（模板外内容），0 结论 1 发现 2 文件 3 风险
        var conclusionBuffer = new StringBuilder();

        foreach (var rawLine in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.Trim();
            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                var heading = trimmed[3..].Trim();
                // 容忍「## 结论：xxx」把内容直接写在标题行的写法。
                string? inline = null;
                current = ResolveSection(heading, ref inline);
                if (inline is { Length: > 0 })
                {
                    if (current == 0) { if (conclusionBuffer.Length > 0) conclusionBuffer.Append(' '); conclusionBuffer.Append(inline); }
                    else if (current == 1 && StripBullet(inline) is { Length: > 0 } f1) findings.Add(f1);
                    else if (current == 2 && StripBullet(inline) is { Length: > 0 } f2) files.Add(f2);
                    else if (current == 3 && StripBullet(inline) is { Length: > 0 } f3) risks.Add(f3);
                }
                continue;
            }
            if (trimmed.Length == 0) continue;
            switch (current)
            {
                case 0:
                    // 结论是一段话：模板允许多行，拼成单句。
                    if (conclusionBuffer.Length > 0) conclusionBuffer.Append(' ');
                    conclusionBuffer.Append(trimmed);
                    break;
                case 1 when StripBullet(trimmed) is { Length: > 0 } finding: findings.Add(finding); break;
                case 2 when StripBullet(trimmed) is { Length: > 0 } file: files.Add(file); break;
                case 3 when StripBullet(trimmed) is { Length: > 0 } risk: risks.Add(risk); break;
            }
        }
        conclusion = conclusionBuffer.Length > 0 ? conclusionBuffer.ToString() : null;
        return new SubtaskReport(conclusion, findings, files, risks);
    }

    /// <summary>
    /// 生成单个子任务的聚合块：结构化摘要（结论 + 截断后的列表）优先，解析不出
    /// 结构时返回 null（调用方回退原文透传）。「无」占位与空段都算无内容。
    /// </summary>
    public static string? Summarize(string status, SubtaskReport parsed, int maxItemsPerSection = 3)
    {
        if (!parsed.HasStructure) return null;
        var report = new StringBuilder();
        report.Append($"结论：{parsed.Conclusion ?? "（未给出）"}");
        AppendSection(report, "发现", parsed.Findings, maxItemsPerSection);
        AppendSection(report, "文件", parsed.Files, maxItemsPerSection);
        AppendSection(report, "风险", parsed.Risks, maxItemsPerSection);
        return report.ToString();
    }

    private static void AppendSection(StringBuilder report, string label, IReadOnlyList<string> items, int max)
    {
        if (items.Count == 0)
        {
            report.Append($" | {label}：无");
            return;
        }
        var shown = items.Take(max);
        var suffix = items.Count > max ? $"（另 {items.Count - max} 条见全文）" : "";
        report.Append($" | {label}：{string.Join("；", shown)}{suffix}");
    }

    /// <summary>标题名归段（0 结论 / 1 发现 / 2 文件 / 3 风险 / -1 模板外）。
    /// 「## 结论：xxx」形式的内联内容经 <paramref name="inline"/> 返回。</summary>
    private static int ResolveSection(string heading, ref string? inline)
    {
        (int Section, string Alias)[] sections =
        [
            (0, "结论"), (1, "关键发现"), (2, "涉及文件"), (3, "风险与不确定性"), (3, "风险"),
        ];
        foreach (var (section, alias) in sections)
        {
            if (!heading.StartsWith(alias, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = heading[alias.Length..].TrimStart();
            if (rest.Length == 0) return section;
            if (rest[0] is ':' or '：')
            {
                inline = rest[1..].Trim();
                return section;
            }
        }
        return -1;
    }

    private static string StripBullet(string line) =>
        line.StartsWith("- ", StringComparison.Ordinal) ? line[2..].Trim()
        : line.StartsWith("* ", StringComparison.Ordinal) ? line[2..].Trim()
        : line;
}
