using System.Diagnostics;
using System.Text;
using Haoyue.Runtime;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Benchmarks.AgentEval;

/// <summary>
/// Agent 级端到端评测：固定任务集 × 真实 runtime 链路（隔离运行时 + 已配置提供商），
/// 以确定性文件断言判分，产出可回归的通过率报告。
/// 用法：dotnet run --project benchmarks/agent-eval -- [--filter create-file] [--steps 24]
/// 结果写入 benchmarks/agent-eval/results/（JSON 明细 + Markdown 报告）。
/// 退出码：全部通过 0，存在失败 1（可接 CI）。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string? filter = null;
        var maxStepsOverride = 0;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--filter" && i + 1 < args.Length) filter = args[++i];
            if (args[i] == "--steps" && i + 1 < args.Length && int.TryParse(args[++i], out var s)) maxStepsOverride = s;
        }

        var repoRoot = FindRepoRoot();
        var resultsDir = Path.Combine(repoRoot, "benchmarks", "agent-eval", "results");
        Directory.CreateDirectory(resultsDir);
        var runStamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var workspaceBase = Path.Combine(resultsDir, "workspaces", runStamp);
        Directory.CreateDirectory(workspaceBase);

        var tasks = EvalTasks.Default
            .Where(t => filter is null || t.Id.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (tasks.Count == 0)
        {
            Console.WriteLine($"没有匹配 filter 的任务：{filter}");
            return 2;
        }

        Console.WriteLine("=== 浩玥 Agent 级评测 ===");
        Console.WriteLine($"任务数: {tasks.Count}，工作区: {workspaceBase}");
        Console.WriteLine($"提供商: 取 ~/.haoyue/config.json 当前活动配置");
        Console.WriteLine();

        var results = new List<TaskResult>();
        foreach (var task in tasks)
        {
            Console.WriteLine($"--- {task.Id} | {task.Name} ---");
            results.Add(await RunTaskAsync(task, Path.Combine(workspaceBase, task.Id), maxStepsOverride));
            var last = results[^1];
            Console.WriteLine(last.Passed
                ? $"  ✅ 通过（{last.Duration.TotalSeconds:0.0}s）"
                : $"  ❌ 失败（{last.Duration.TotalSeconds:0.0}s）");
            foreach (var detail in last.Details)
                Console.WriteLine($"  {(detail.Passed ? "✔" : "✘")} {detail.Description}");
            Console.WriteLine();
        }

        var passed = results.Count(r => r.Passed);
        var report = RenderReport(runStamp, results);
        var reportPath = Path.Combine(resultsDir, $"report-{runStamp}.md");
        await File.WriteAllTextAsync(reportPath, report);
        await File.WriteAllTextAsync(
            Path.Combine(resultsDir, $"results-{runStamp}.json"),
            System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));

        Console.WriteLine($"通过率: {passed}/{results.Count}");
        Console.WriteLine($"报告: {reportPath}");
        return passed == results.Count ? 0 : 1;
    }

    private static async Task<TaskResult> RunTaskAsync(EvalTask task, string workspaceDir, int maxStepsOverride)
    {
        Directory.CreateDirectory(workspaceDir);
        foreach (var (path, content) in task.Setup ?? [])
        {
            var fullPath = Path.Combine(workspaceDir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, content);
        }

        var sw = Stopwatch.StartNew();
        string? error = null;
        try
        {
            var workspace = new WorkspaceManager().Detect(workspaceDir);
            var fileLocks = new FileLockCoordinator();
            var owner = $"agent-eval/{task.Id}";
            await using var runtime = HaoyueRuntime.CreateIsolated(workspace, fileLocks, owner);

            // 策略回归任务注入专属拒绝规则，验证 P0 闸门的强制行为。
            if (task.DenyRules is { Count: > 0 })
            {
                runtime.ConfigStore.Config.Agent.ToolPolicy.Deny = [.. task.DenyRules.Select(rule =>
                    new Haoyue.Runtime.Tools.ToolPolicyRule
                    {
                        Tool = rule.Tool, Pattern = rule.Pattern, Reason = rule.Reason,
                    })];
            }
            if (maxStepsOverride > 0)
                runtime.ConfigStore.Config.Agent.MaxSteps = maxStepsOverride;

            var session = runtime.Sessions.Create(workspace, networkEnabled: false);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var result = await runtime.Agent.RunTurnAsync(session, workspace, task.Prompt, cts.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result.Error)) error = result.Error;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        sw.Stop();

        var details = new List<JudgeDetail>();
        foreach (var judge in task.Judges)
        {
            var (passed, description) = Evaluate(judge, workspaceDir);
            details.Add(new JudgeDetail(description, passed));
        }

        var allPassed = error is null && details.All(d => d.Passed);
        return new TaskResult(task.Id, task.Name, allPassed, error,
            [.. details], sw.Elapsed);
    }

    private static (bool Passed, string Description) Evaluate(EvalJudge judge, string workspaceDir)
    {
        var fullPath = Path.Combine(workspaceDir, judge.Path);
        return judge.Type switch
        {
            "fileExists" => (File.Exists(fullPath), $"文件存在 {judge.Path}"),
            "fileAbsent" => (!File.Exists(fullPath), $"文件不存在 {judge.Path}"),
            "fileContains" => EvaluateContains(fullPath, judge.Path, judge.Text, mustContain: true),
            "fileNotContains" => EvaluateContains(fullPath, judge.Path, judge.Text, mustContain: false),
            _ => (false, $"未知判分类型 {judge.Type}"),
        };
    }

    private static (bool Passed, string Description) EvaluateContains(
        string fullPath, string relativePath, string? text, bool mustContain)
    {
        var description = $"{(mustContain ? "包含" : "不包含")}「{text}」于 {relativePath}";
        if (!File.Exists(fullPath)) return (!mustContain, description);
        var content = File.ReadAllText(fullPath);
        var contains = content.Contains(text ?? "", StringComparison.OrdinalIgnoreCase);
        return (mustContain ? contains : !contains, description);
    }

    private static string RenderReport(string runStamp, IReadOnlyList<TaskResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Agent 级评测报告 {runStamp}");
        sb.AppendLine();
        sb.AppendLine($"- 任务数：{results.Count}，通过：{results.Count(r => r.Passed)}，" +
                      $"通过率：{results.Count(r => r.Passed) / (double)results.Count:P0}");
        sb.AppendLine();
        sb.AppendLine("| 任务 | 结果 | 用时 | 失败明细 |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var result in results)
        {
            var failures = string.Join("；", result.Details.Where(d => !d.Passed).Select(d => d.Description));
            if (result.Error is not null) failures = (failures.Length > 0 ? failures + "；" : "") + $"回合错误：{result.Error}";
            sb.AppendLine($"| {result.Id} ({result.Name}) | {(result.Passed ? "✅" : "❌")} | {result.Duration.TotalSeconds:0.0}s | {(failures.Length > 0 ? failures : "—")} |");
        }
        sb.AppendLine();
        sb.AppendLine("判分基于文件系统断言（确定性），可离线复跑；工作区快照保留于 results/workspaces/ 供人工复核。");
        return sb.ToString();
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var current = dir; current is not null; current = Path.GetDirectoryName(current))
        {
            if (File.Exists(Path.Combine(current, "Haoyue.slnx"))) return current;
        }
        throw new InvalidOperationException("未找到仓库根（Haoyue.slnx）。请从仓库内运行评测。");
    }
}

public sealed record JudgeDetail(string Description, bool Passed);

public sealed record TaskResult(
    string Id,
    string Name,
    bool Passed,
    string? Error,
    IReadOnlyList<JudgeDetail> Details,
    TimeSpan Duration);
