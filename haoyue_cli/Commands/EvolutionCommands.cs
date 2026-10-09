using System.CommandLine;
using Haoyue.Runtime;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Evolution;
using Haoyue.Runtime.Providers;
using Spectre.Console;

namespace Haoyue.Cli.Commands;

/// <summary>
/// Evolution engine surface for the terminal: the same read-only inspection the
/// desktop workbench offers (defects, history, stats, pending drafts) plus the
/// P3 human gate, so the review flow stays scriptable without a GUI session.
/// </summary>
public static class EvolutionCommands
{
    public static Command Build()
    {
        var command = new Command("evolve", "Inspect the evolution engine (defects, drafts, history, stats)");

        var defects = new Command("defects", "Aggregate journaled failure signals into defect reports (with health score)");
        defects.SetAction(_ =>
        {
            using var rt = CliHost.CreateRuntime();
            var store = new EvolutionStore(rt.Database);
            var reports = new DefectAggregator(rt.Database)
                .Aggregate(DefectAggregator.DefaultScanLimit, store.ReflectionSessionIds());
            RenderDefects(AnsiConsole.Console, reports);
            return 0;
        });

        var pending = new Command("pending", "List skill drafts awaiting review (candidate or deferred)");
        pending.SetAction(_ =>
        {
            using var rt = CliHost.CreateRuntime();
            RenderPending(AnsiConsole.Console, NewRunner(rt).ListCandidates());
            return 0;
        });

        var limitOption = new Option<int>("--limit") { Description = "Number of runs to show (default 20)" };
        var history = new Command("history", "Show past reflection runs (trigger, processing, errors)");
        history.Add(limitOption);
        history.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            RenderHistory(AnsiConsole.Console, new EvolutionStore(rt.Database).ListRuns(parse.GetValue(limitOption)));
            return 0;
        });

        var stats = new Command("stats", "Show adoption rate, per-skill usage and defect recurrence");
        stats.SetAction(_ =>
        {
            using var rt = CliHost.CreateRuntime();
            var store = new EvolutionStore(rt.Database);
            var reports = new DefectAggregator(rt.Database)
                .Aggregate(DefectAggregator.DefaultScanLimit, store.ReflectionSessionIds());
            RenderStats(AnsiConsole.Console, EvolutionAnalytics.ComputeStats(rt.Database, store, reports));
            return 0;
        });

        var fingerprintArg = new Argument<string>("fingerprint") { Description = "Defect fingerprint (a unique prefix also works)" };
        var decisionArg = new Argument<string>("decision") { Description = "adopt | reject | defer" };
        var promptOption = new Option<string?>("--prompt") { Description = "Rewrite the skill prompt before adoption (adopt only)" };
        var decide = new Command("decide", "Apply the human review decision to a skill draft");
        decide.Add(fingerprintArg); decide.Add(decisionArg); decide.Add(promptOption);
        decide.SetAction(parse =>
        {
            var decision = parse.GetRequiredValue(decisionArg).Trim().ToLowerInvariant();
            if (decision is not ("adopt" or "reject" or "defer"))
            {
                AnsiConsole.MarkupLine("[red]decision 必须是 adopt、reject 或 defer。[/]");
                return 1;
            }
            using var rt = CliHost.CreateRuntime();
            var runner = NewRunner(rt);
            var prefix = parse.GetRequiredValue(fingerprintArg).Trim();
            var fingerprint = ResolveFingerprint(runner.ListCandidates(), prefix);
            if (fingerprint is null)
            {
                AnsiConsole.MarkupLine($"[red]未找到匹配的待审草稿：{Markup.Escape(prefix)}[/]（[cyan]haoyue evolve pending[/] 查看清单）");
                return 1;
            }
            try
            {
                var record = runner.Decide(fingerprint, decision, parse.GetValue(promptOption));
                AnsiConsole.MarkupLine($"[green]{Markup.Escape(record.Status)}[/] {Markup.Escape(record.Fingerprint[..8])}");
                return 0;
            }
            catch (InvalidOperationException ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                return 1;
            }
        });

        command.Add(defects); command.Add(pending); command.Add(history); command.Add(stats); command.Add(decide);
        return command;
    }

    private static ReflectionRunner NewRunner(HaoyueRuntime rt) => new(
        rt, new FileLockCoordinator(), new LlmHttpFactory(),
        new CircuitBreaker(new RetryConfig()));

    private static string? ResolveFingerprint(
        IReadOnlyList<ReflectionRunner.CandidateSummary> candidates, string prefix)
    {
        var matches = candidates
            .Where(c => c.Fingerprint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0].Fingerprint : null;
    }

    internal static void RenderDefects(IAnsiConsole console, IReadOnlyList<DefectReport> reports)
    {
        var health = EvolutionAnalytics.ComputeHealth(reports);
        console.MarkupLine($"[bold]健康分[/] {health.Score}/100（{health.Grade}） · [gray]{reports.Count} 条缺陷报告[/]");
        console.WriteLine();
        if (reports.Count == 0)
        {
            console.MarkupLine("[gray]没有待处理的缺陷信号。[/]");
            return;
        }
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("指纹").AddColumn("类型").AddColumn("严重度").AddColumn("次数").AddColumn("工具").AddColumn("摘要");
        foreach (var report in reports)
            table.AddRow(
                Markup.Escape(report.Fingerprint[..8]),
                Markup.Escape(report.Kind.ToString()),
                SeverityColor(report.Severity),
                report.Occurrences.ToString(),
                Markup.Escape(report.ToolName ?? "-"),
                Markup.Escape(Truncate(report.ErrorSummary ?? "-", 60)));
        console.Write(table);
    }

    internal static void RenderPending(IAnsiConsole console, IReadOnlyList<ReflectionRunner.CandidateSummary> candidates)
    {
        if (candidates.Count == 0)
        {
            console.MarkupLine("[gray]没有待审的技能草稿。[/]");
            return;
        }
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("指纹").AddColumn("技能").AddColumn("状态").AddColumn("类型").AddColumn("生成时间");
        foreach (var candidate in candidates)
            table.AddRow(
                Markup.Escape(candidate.Fingerprint[..8]),
                Markup.Escape(candidate.SkillName),
                candidate.Status == EvolutionStatus.Deferred ? "[yellow]deferred[/]" : "[cyan]candidate[/]",
                Markup.Escape(candidate.Kind),
                Markup.Escape(candidate.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
        console.Write(table);
        console.MarkupLine("[gray]用 [cyan]haoyue evolve decide <指纹> adopt|reject|defer[/] 完成人工终审。[/]");
    }

    internal static void RenderHistory(IAnsiConsole console, IReadOnlyList<EvolutionStore.EvolutionRun> runs)
    {
        if (runs.Count == 0)
        {
            console.MarkupLine("[gray]还没有反思回合记录。[/]");
            return;
        }
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Id").AddColumn("触发").AddColumn("时间").AddColumn("处理").AddColumn("产出").AddColumn("失败").AddColumn("错误");
        foreach (var run in runs)
            table.AddRow(
                run.Id.ToString(),
                TriggerColor(run.Trigger),
                Markup.Escape(run.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm")),
                run.Processed.ToString(),
                run.Candidates.ToString(),
                run.Failed.ToString(),
                Markup.Escape(Truncate(run.Error ?? "-", 40)));
        console.Write(table);
    }

    internal static void RenderStats(IAnsiConsole console, EvolutionAnalytics.StatsResult result)
    {
        console.MarkupLine(
            $"[bold]反思[/] {result.Runs} 次（产出 {result.CandidatesProduced} 草稿） · [green]采纳 {result.Adopted}[/] · " +
            $"[red]拒绝 {result.Rejected}[/] · [yellow]暂缓 {result.Deferred}[/] · 无需行动 {result.NoAction} · 失败 {result.Failed} · 采纳率 {result.AdoptionRate}%");
        if (result.Skills.Count == 0) return;
        console.WriteLine();
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("技能").AddColumn("缺陷类型").AddColumn("采纳时间").AddColumn("使用次数").AddColumn("已解决");
        foreach (var skill in result.Skills)
            table.AddRow(
                Markup.Escape(skill.SkillName),
                Markup.Escape(skill.Kind),
                Markup.Escape(skill.AdoptedAt.ToLocalTime().ToString("yyyy-MM-dd")),
                skill.UsageCount.ToString(),
                skill.Resolved ? "[green]是[/]" : "[yellow]否[/]");
        console.Write(table);
    }

    private static string SeverityColor(string? severity) => severity switch
    {
        "high" => "[red]high[/]",
        "medium" => "[yellow]medium[/]",
        _ => "[gray]-[/]",
    };

    private static string TriggerColor(string trigger) => trigger switch
    {
        "manual" => "[cyan]manual[/]",
        "auto" => "[green]auto[/]",
        "threshold" => "[yellow]threshold[/]",
        _ => Markup.Escape(trigger),
    };

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
