using System.Text.Json.Nodes;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Providers;

namespace Haoyue.Runtime.Tools.Builtin;

/// <summary>
/// Fans several self-contained subtasks out to parallel sub-agents and returns a
/// per-task aggregated report. One failed subtask does not fail the batch — the
/// report marks it so the caller can retry selectively.
///
/// 3.1/3.2/3.3：每个子任务占用回合级委派配额（不足即整批拒绝并告知剩余额度），
/// 用量汇入父预算；可选 timeout 给每个子任务独立计时（一个卡死只烧掉自己的
/// 额度，已完成的兄弟任务照常返回）；四段式汇报解析为结构化摘要，
/// 解析不出结构时原文透传——兜底永不丢信息。
/// </summary>
public sealed class DelegateTasksTool(IPromptProvider prompts, IAgentDelegator delegator) : ITool
{
    public const int MinTasks = 2;
    public const int MaxTasks = 6;

    public string Name => AgentDelegator.ParallelDelegateToolName;

    public string Description =>
        prompts.TryGet("tool/delegate_tasks")
        ?? "Fan out 2-6 self-contained subtasks to parallel sub-agents and return their aggregated results.";

    public JsonObject ParameterSchema => ToolSchema.Object(
        ("tasks", ToolSchema.Array(
            $"完整、自包含的子任务描述列表（{MinTasks}-{MaxTasks} 个）。每个子代理都看不到当前对话历史，描述必须自包含。",
            ToolSchema.String("子任务描述")), true),
        ("role", ToolSchema.String($"可选角色预设，应用到全部子任务：{AgentRole.ValidRoles}。命中时每个子代理注入角色纪律提示词并收敛工具白名单。"), false),
        ("timeout_seconds", ToolSchema.Integer($"可选：每个子任务的独立超时（{DelegateTool.MinTimeoutSeconds}-{DelegateTool.MaxTimeoutSeconds} 秒）。缺省时跟随父回合——可能卡死的子任务建议显式设置。"), false));

    public bool Mutating => true;

    public string StatusLabel => "Delegating subtasks in parallel";

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        if (arguments["tasks"] is not JsonArray array || array.Count == 0)
            return ToolResult.Fail($"delegate_tasks 需要 tasks 参数：{MinTasks}-{MaxTasks} 个子任务描述的数组。");

        var role = arguments["role"]?.GetValue<string>()?.Trim();
        if (role is { Length: > 0 } && !AgentRole.Builtins.ContainsKey(role))
            return ToolResult.Fail($"未知角色「{role}」。可选：{AgentRole.ValidRoles}。");

        var tasks = array
            .Select(node => node?.GetValue<string>()?.Trim())
            .Where(task => !string.IsNullOrWhiteSpace(task))
            .Cast<string>()
            .ToList();

        if (tasks.Count < MinTasks)
            return ToolResult.Fail($"并行委派至少需要 {MinTasks} 个子任务；单个子任务请改用 delegate_task。");
        if (tasks.Count > MaxTasks)
            // Silent Take(6) dropped tasks without telling the model — it would report
            // work as done that was never dispatched. Fail loudly instead.
            return ToolResult.Fail(
                $"子任务数量超过上限：收到 {tasks.Count} 个，一次最多 {MaxTasks} 个。"
                + "请合并相近的子任务，或把多余的子任务留到下一轮再委派。");

        // 3.1 配额整批校验：额度不足时整批拒绝（部分派发部分拒绝会让模型误以为全部完成）。
        var scope = context.TurnScope;
        var quota = context.Agent.DelegationMaxPerTurn;
        if (scope is not null && scope.DelegationCount + tasks.Count > quota)
            return ToolResult.Fail(
                $"委派配额不足：本回合已发起 {scope.DelegationCount} 个子任务，"
                + $"再派 {tasks.Count} 个将超出 agent.delegationMaxPerTurn = {quota}。"
                + $"请把本轮子任务压缩到 {Math.Max(0, quota - scope.DelegationCount)} 个以内，其余留到下一轮。");
        if (scope is not null)
            foreach (var _ in tasks)
                scope.TryConsumeDelegationSlot(quota);

        // 3.2 每个子任务独立计时：linked CTS（父 CT + 超时），单个卡死只终止自己。
        var timeoutSeconds = arguments["timeout_seconds"]?.GetValue<int?>();
        var runs = tasks.Select((task, index) => RunWithTimeoutAsync(
            context, task, role, timeoutSeconds, ct, index));
        var results = await Task.WhenAll(runs).ConfigureAwait(false);

        var report = new System.Text.StringBuilder();
        var failed = 0;
        for (var i = 0; i < tasks.Count; i++)
        {
            var result = results[i];
            var status = result.TimedOut ? "超时" : !string.IsNullOrWhiteSpace(result.AgentResult.Error) ? "失败" : result.AgentResult.Cancelled ? "已取消" : "完成";
            if (status != "完成") failed++;
            report.AppendLine($"## 子任务 {i + 1} — {status}");
            if (result.TimedOut)
            {
                report.AppendLine($"超时（{timeoutSeconds ?? 0} 秒）已终止：该子任务可能陷入循环，父回合可拆小重试。");
            }
            else if (!string.IsNullOrWhiteSpace(result.AgentResult.Error))
            {
                report.AppendLine($"错误：{result.AgentResult.Error}");
            }
            else if (string.IsNullOrWhiteSpace(result.AgentResult.Text))
            {
                report.AppendLine("（子任务完成，但没有返回文本。）");
            }
            else
            {
                // 3.3 结构化摘要优先（父代理历史里的报告 token 占用下降），
                // 解析不出四段式结构时原样透传——兜底永不丢信息。
                var parsed = SubtaskReportAggregator.Parse(result.AgentResult.Text);
                var digest = SubtaskReportAggregator.Summarize(status, parsed);
                if (digest is not null)
                    report.AppendLine(digest).AppendLine();
                report.AppendLine(context.TruncateTailPrioritized(result.AgentResult.Text, "subtask output", tailShare: 0.7));
            }
            report.AppendLine();
        }
        var summary = $"{tasks.Count} 个并行子任务：{tasks.Count - failed} 完成，{failed} 失败/取消/超时";
        // 子任务报告的结论在各子任务文本的尾部，失败摘要更是在最后——头截断会
        // 系统性丢掉它们（H-B）。
        return ToolResult.Ok(context.TruncateTailPrioritized(report.ToString().TrimEnd(), "subtask output", tailShare: 0.7), summary);
    }

    private async Task<(AgentResultLike AgentResult, bool TimedOut)> RunWithTimeoutAsync(
        ToolContext context, string task, string? role, int? timeoutSeconds,
        CancellationToken ct, int index)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutSeconds is { } requested)
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(requested, DelegateTool.MinTimeoutSeconds, DelegateTool.MaxTimeoutSeconds)));
        try
        {
            var result = await delegator.RunSubTaskAsync(
                context.Workspace, task, role, context.TurnScope, timeoutCts.Token).ConfigureAwait(false);
            context.TurnScope?.AddDelegatedUsage(result.InputTokens, result.OutputTokens, result.Cost);
            return (AgentResultLike.FromResult(result), false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the whole turn is being cancelled — let the agent loop unwind
        }
        catch (OperationCanceledException)
        {
            return (AgentResultLike.TimedOut(), true);
        }
        catch (LlmException ex)
        {
            return (AgentResultLike.FromError(ex.Message), false);
        }
    }

    /// <summary>批量委派的每任务结果归一形态：真实结果 / 超时 / LLM 失败。</summary>
    private readonly record struct AgentResultLike(string Text, bool Cancelled, string? Error, long InputTokens, long OutputTokens, decimal Cost)
    {
        public static AgentResultLike FromResult(AgentTurnResult result) =>
            new(result.Text, result.Cancelled, result.Error, result.InputTokens, result.OutputTokens, result.Cost);
        public static AgentResultLike TimedOut() => new("", true, null, 0, 0, 0m);
        public static AgentResultLike FromError(string error) => new("", false, error, 0, 0, 0m);
    }
}
