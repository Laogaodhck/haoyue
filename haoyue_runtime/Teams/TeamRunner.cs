using System.Diagnostics;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Teams;

/// <summary>
/// 智能体团体编排器。四种协作模式共用同一条子任务委派链路
/// （<see cref="IAgentDelegator.RunSubTaskAsync(WorkspaceInfo,string,AgentInvocation,TurnExecutionScope?,CancellationToken)"/>）
/// 与同一个 <see cref="TurnExecutionScope"/> 共享上下文：
/// <list type="bullet">
/// <item>sequential：按成员顺序执行，上游结论作为下游上下文。</item>
/// <item>parallel：全部成员并行，各自结果写入共享黑板。</item>
/// <item>supervisor：协调者分派 → 成员并行执行 → 协调者汇总，最多 MaxRounds 轮。</item>
/// <item>review：执行者产出 → 审核者评审，未通过则带反馈重做，最多 MaxRounds 轮。</item>
/// </list>
/// 每个成员执行都受整体超时、单成员重试与失败策略约束；轨迹逐步落盘到
/// <see cref="TeamRunStore"/>，运行结束后把整体结论写回普通助手消息（不额外分组卡片）。
/// </summary>
public sealed class TeamRunner(IAgentDelegator delegator, ISessionStore? sessions = null, IEventBus? events = null)
{
    private const int MaxContextChars = 6000;
    private readonly TeamRunStore _store = new();
    private readonly Lock _gate = new();

    /// <summary>
    /// 受理即返回：落盘 running 轨迹、登记取消令牌后启动后台编排。
    /// 返回的快照可直接回给 <c>team.run</c>；后续进度与终态经事件通道推送。
    /// </summary>
    public TeamRun Start(
        WorkspaceInfo workspace,
        TeamDefinition team,
        string task,
        string? sessionId,
        TurnExecutionScope? parentScope)
    {
        var context = CreateContext(workspace, team, task, sessionId, parentScope);
        Persist(context, context.Run);
        TeamRunIndex.Add(context.RunId, workspace.Root);

        var timeout = new CancellationTokenSource();
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(team.TimeoutSeconds, 1, TeamStore.MaxTimeoutSeconds)));
        var lease = TeamRunRegistry.Register(context.RunId, timeout);
        context.Ct = timeout.Token;
        Publish(context);

        _ = Task.Run(async () =>
        {
            try
            {
                await RunCoreAsync(context, timeout, lease, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // RunCoreAsync 已收敛业务失败；这里只兜底未观测异常。
            }
        });
        return context.Run;
    }

    /// <summary>同步等待整次运行结束（测试与内部调用）；调用方取消同样收敛为 cancelled。</summary>
    public async Task<TeamRun> RunAsync(
        WorkspaceInfo workspace,
        TeamDefinition team,
        string task,
        string? sessionId,
        TurnExecutionScope? parentScope,
        CancellationToken ct)
    {
        var context = CreateContext(workspace, team, task, sessionId, parentScope);
        Persist(context, context.Run);
        TeamRunIndex.Add(context.RunId, workspace.Root);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(team.TimeoutSeconds, 1, TeamStore.MaxTimeoutSeconds)));
        using var lease = TeamRunRegistry.Register(context.RunId, timeout);
        context.Ct = timeout.Token;
        Publish(context);
        return await RunCoreAsync(context, timeout, lease, ct).ConfigureAwait(false);
    }

    private static RunContext CreateContext(
        WorkspaceInfo workspace,
        TeamDefinition team,
        string task,
        string? sessionId,
        TurnExecutionScope? parentScope)
    {
        if (string.IsNullOrWhiteSpace(task))
            throw new ArgumentException("团体任务不能为空。", nameof(task));

        var enabled = team.Members.Where(member => member.Enabled).ToList();
        if (enabled.Count == 0)
            throw new ArgumentException("团体至少需要一个启用成员。", nameof(team));
        var runId = $"team-run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        return new RunContext
        {
            Workspace = workspace,
            Team = team,
            Task = task,
            RunId = runId,
            Members = enabled,
            Scope = parentScope ?? new TurnExecutionScope(
                string.IsNullOrWhiteSpace(sessionId) ? runId : sessionId!, workspace.Root),
            Run = new TeamRun(
                runId, team.Id, sessionId ?? "", "running", DateTimeOffset.UtcNow, null, 0,
                enabled.Select(member => new TeamRunMember(member.Id, member.Role, "pending", 0, 0, null, ""))
                    .ToList(),
                null, null, workspace.Root),
        };
    }

    private async Task<TeamRun> RunCoreAsync(
        RunContext context, CancellationTokenSource timeout, IDisposable lease,
        CancellationToken callerToken)
    {
        try
        {
            var ct = timeout.Token;
            string? result = null;
            string? error = null;
            var status = "succeeded";
            try
            {
                result = context.Team.Mode switch
                {
                    "parallel" => await RunParallelAsync(context).ConfigureAwait(false),
                    "supervisor" => await RunSupervisorAsync(context).ConfigureAwait(false),
                    "review" => await RunReviewAsync(context).ConfigureAwait(false),
                    _ => await RunSequentialAsync(context).ConfigureAwait(false),
                };
            }
            catch (TeamAbortException ex)
            {
                status = "failed";
                error = ex.Message;
            }
            catch (OperationCanceledException)
            {
                status = "cancelled";
                var byUser = callerToken.IsCancellationRequested
                             || TeamRunRegistry.WasCancelledByUser(context.RunId);
                error = byUser
                    ? "团体运行已取消。"
                    : $"团体运行超过 {context.Team.TimeoutSeconds} 秒，已停止。";
            }
            catch (Exception ex)
            {
                status = "failed";
                error = ex.Message;
            }

            if (status == "succeeded" && context.Incomplete) status = "partial";
            if (status == "succeeded" && context.Failures.Count > 0) status = "partial";
            if (status is "succeeded" or "partial" && result is not null && context.Failures.Count > 0)
            {
                var note = $"\n\n> 降级运行：以下成员未完成本轮职责：{string.Join("、", context.Failures)}。以上为其余成员可得结论。";
                if (context.Team.FailureStrategy == "fallback") result += note;
            }

            var finished = context.Run with
            {
                Status = status,
                EndedAt = DateTimeOffset.UtcNow,
                Result = result,
                Error = error,
            };
            Persist(context, finished);
            AppendToSession(context.Workspace, context.Run.SessionId, status, result, error);
            return finished;
        }
        finally
        {
            TeamRunIndex.Remove(context.RunId);
            lease.Dispose();
            timeout.Dispose();
        }
    }

    private void Publish(RunContext context)
    {
        if (events is null) return;
        try
        {
            events.Publish(new TeamRunUpdatedEvent(
                context.RunId, context.Team.Id, context.Workspace.Root, context.Run.Status, context.Run.Rounds));
        }
        catch (Exception)
        {
            // 事件总线不可用时不影响运行本身；轨迹仍可从 team.runs 读取。
        }
    }

    private async Task<string?> RunSequentialAsync(RunContext context)
    {
        SetRounds(context, 1);
        var outputs = new List<(TeamMember Member, string Text)>();
        foreach (var member in context.Members)
        {
            context.Ct.ThrowIfCancellationRequested();
            var prompt = MemberPrompt(context.Team, member, context.Task, UpstreamBlock(outputs));
            var text = await ExecuteAsync(context, member, "work", prompt).ConfigureAwait(false);
            if (text is not null) outputs.Add((member, text));
        }
        return ComposeOutputs(outputs);
    }

    private async Task<string?> RunParallelAsync(RunContext context)
    {
        SetRounds(context, 1);
        var runs = context.Members.Select(member =>
        {
            var prompt = MemberPrompt(context.Team, member, context.Task,
                "你与其他成员并行工作；共享黑板可用于读取他人结论，但不要假设他人已完成。");
            return ExecuteAsync(context, member, "work", prompt);
        }).ToList();
        var texts = await Task.WhenAll(runs).ConfigureAwait(false);
        var outputs = context.Members
            .Zip(texts)
            .Where(pair => pair.Second is not null)
            .Select(pair => (pair.First, pair.Second!))
            .ToList();
        return ComposeOutputs(outputs);
    }

    private async Task<string?> RunSupervisorAsync(RunContext context)
    {
        var coordinator = Coordinator(context);
        var workers = context.Members.Where(member => member.Id != coordinator.Id).ToList();
        string? previous = null;
        string? final = null;
        for (var round = 1; round <= context.MaxRounds; round++)
        {
            context.Ct.ThrowIfCancellationRequested();
            SetRounds(context, round);
            var plan = await ExecuteAsync(context, coordinator, $"plan-{round}",
                SupervisorPlanPrompt(context.Team, context.Task, workers, previous, round, context.MaxRounds))
                .ConfigureAwait(false);
            if (plan is null) break;

            var workerRuns = workers.Select(worker => ExecuteAsync(context, worker, $"work-{round}",
                WorkerPrompt(context.Team, worker, context.Task, plan, previous, round, context.MaxRounds)))
                .ToList();
            var workerTexts = await Task.WhenAll(workerRuns).ConfigureAwait(false);
            var outputs = workers
                .Zip(workerTexts)
                .Where(pair => pair.Second is not null)
                .Select(pair => (pair.First, pair.Second!))
                .ToList();

            var summary = await ExecuteAsync(context, coordinator, $"summary-{round}",
                SupervisorSummaryPrompt(context.Team, context.Task, plan, outputs, round, context.MaxRounds))
                .ConfigureAwait(false);
            if (summary is null)
            {
                final = ComposeOutputs(outputs);
                break;
            }
            previous = summary;
            final = summary;
            if (round >= context.MaxRounds || HasVerdict(summary, "DONE")) break;
        }
        return final;
    }

    private async Task<string?> RunReviewAsync(RunContext context)
    {
        var reviewer = Coordinator(context);
        var executor = context.Members.FirstOrDefault(member => member.Id != reviewer.Id) ?? reviewer;
        var feedback = "";
        string? final = null;
        string? lastReview = null;
        for (var round = 1; round <= context.MaxRounds; round++)
        {
            context.Ct.ThrowIfCancellationRequested();
            SetRounds(context, round);
            var output = await ExecuteAsync(context, executor, $"execute-{round}",
                ExecutorPrompt(context.Team, executor, context.Task, feedback, round, context.MaxRounds))
                .ConfigureAwait(false);
            if (output is null) break;
            final = output;

            var review = await ExecuteAsync(context, reviewer, $"review-{round}",
                ReviewPrompt(context.Team, context.Task, output, round, context.MaxRounds))
                .ConfigureAwait(false);
            if (review is null) break;
            lastReview = review;
            if (HasVerdict(review, "APPROVE")) break;
            if (round >= context.MaxRounds)
            {
                context.Incomplete = true;
                break;
            }
            feedback = review;
        }
        if (final is null) return null;
        return lastReview is null ? final : $"{final}\n\n---\n审核意见：\n{lastReview}";
    }

    /// <summary>执行一个成员：重试、超时、失败策略与轨迹更新都在这里收敛。</summary>
    private async Task<string?> ExecuteAsync(
        RunContext context, TeamMember member, string phase, string prompt)
    {
        var index = context.Members.FindIndex(item => item.Id == member.Id);
        var stepsBefore = context.Scope.Steps.Count;
        var watch = Stopwatch.StartNew();
        if (index >= 0) Update(context, index, item => item with { Status = "running" });

        AgentTurnResult? succeeded = null;
        Exception? lastError = null;
        for (var attempt = 0; attempt <= context.MaxRetries; attempt++)
        {
            context.Ct.ThrowIfCancellationRequested();
            try
            {
                var result = await delegator.RunSubTaskAsync(
                    context.Workspace, prompt,
                    new AgentInvocation(member.ExpertId, member.Model, member.Tools),
                    context.Scope, context.Ct).ConfigureAwait(false);
                if (result.Cancelled) throw new OperationCanceledException(context.Ct);
                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    lastError = new InvalidOperationException(result.Error);
                }
                else
                {
                    succeeded = result;
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
            if (attempt < context.MaxRetries)
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)), context.Ct).ConfigureAwait(false);
        }

        watch.Stop();
        var toolCalls = Math.Max(0, context.Scope.Steps.Count - stepsBefore);
        if (succeeded is null)
        {
            var message = lastError?.Message ?? "成员未返回结果。";
            if (index >= 0)
            {
                Update(context, index, item => item with
                {
                    Status = "failed",
                    DurationMs = watch.ElapsedMilliseconds,
                    ToolCalls = toolCalls,
                    Error = message,
                });
            }
            context.Failures.Add($"{member.Name}（{phase}）");
            if (context.Team.FailureStrategy == "stop")
                throw new TeamAbortException($"成员「{member.Name}」执行失败：{message}");
            return null;
        }

        if (index >= 0)
        {
            Update(context, index, item => item with
            {
                Status = "succeeded",
                DurationMs = watch.ElapsedMilliseconds,
                ToolCalls = toolCalls,
                OutputPreview = Preview(succeeded.Text),
            });
        }
        // 共享上下文：成员结果写入黑板，后续成员/轮次可读。
        context.Scope.WriteBlackboard($"team/{context.RunId}/{phase}/{member.Id}", Preview(succeeded.Text), member.Name);
        return succeeded.Text;
    }

    private TeamMember Coordinator(RunContext context) =>
        context.Members.FirstOrDefault(member =>
            member.Id.Equals(context.Team.CoordinatorId, StringComparison.Ordinal))
        ?? context.Members[0];

    private void Persist(RunContext context, TeamRun run)
    {
        lock (_gate)
        {
            context.Run = run;
            _store.Save(context.Workspace, run);
        }
        Publish(context);
    }

    private void Update(RunContext context, int index, Func<TeamRunMember, TeamRunMember> mutate)
    {
        lock (_gate)
        {
            var members = new List<TeamRunMember>(context.Run.Members);
            if (index < 0 || index >= members.Count) return;
            members[index] = mutate(members[index]);
            context.Run = context.Run with { Members = members };
            _store.Save(context.Workspace, context.Run);
        }
        Publish(context);
    }

    private void SetRounds(RunContext context, int rounds)
    {
        lock (_gate)
        {
            if (context.Run.Rounds >= rounds) return;
            context.Run = context.Run with { Rounds = rounds };
            _store.Save(context.Workspace, context.Run);
        }
        Publish(context);
    }

    private void AppendToSession(
        WorkspaceInfo workspace, string? sessionId, string status, string? result, string? error)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        var text = result;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = status == "cancelled"
                ? "团体运行已取消。"
                : $"团体运行失败：{error ?? "未知错误"}";
        }
        if (sessions is null) return;
        try
        {
            var session = sessions.Load(workspace, sessionId!);
            if (session is not null) sessions.Append(session, ChatMessage.Assistant(text!));
        }
        catch
        {
            // 落库失败不应覆盖运行结论；轨迹里仍可看到 Result/Error。
        }
    }

    private static string? ComposeOutputs(List<(TeamMember Member, string Text)> outputs)
    {
        if (outputs.Count == 0) return null;
        if (outputs.Count == 1) return outputs[0].Text;
        var builder = new System.Text.StringBuilder();
        foreach (var (member, text) in outputs)
        {
            if (builder.Length > 0) builder.Append("\n\n");
            builder.Append("## ").Append(member.Name);
            if (!string.IsNullOrWhiteSpace(member.Role)) builder.Append("（").Append(member.Role).Append('）');
            builder.Append('\n').Append(text.Trim());
        }
        return builder.ToString();
    }

    private static string UpstreamBlock(List<(TeamMember Member, string Text)> outputs)
    {
        if (outputs.Count == 0) return "这是本轮的第一棒，没有上游结论。";
        var builder = new System.Text.StringBuilder("上游成员结论：\n");
        foreach (var (member, text) in outputs)
            builder.Append("- ").Append(member.Name).Append("：").Append(Preview(text, 1500)).Append('\n');
        return builder.ToString();
    }

    private static string MemberPrompt(TeamDefinition team, TeamMember member, string task, string context) =>
        $"""
        你是「{team.Name}」的成员：{member.Name}
        角色定位：{(string.IsNullOrWhiteSpace(member.Role) ? "协作成员" : member.Role)}
        职责说明：{(string.IsNullOrWhiteSpace(member.Responsibility) ? "完成分配给你的任务部分。" : member.Responsibility)}
        协作模式：{ModeLabel(team.Mode)}

        用户任务：
        {task}

        {context}

        工作纪律：
        - 只做职责范围内的工作，不要代替其他成员发言。
        - 结论先行；必要时给出关键依据与可执行步骤。
        - 不要复述其他成员的原文，只引用必要结论。
        """;

    private static string SupervisorPlanPrompt(
        TeamDefinition team, string task, List<TeamMember> workers, string? previous, int round, int maxRounds) =>
        $"""
        你是「{team.Name}」的协调者。当前是第 {round}/{maxRounds} 轮分派。
        成员清单：
        {string.Join("\n", workers.Select(member => $"- {member.Id}｜{member.Name}｜{member.Role}：{member.Responsibility}"))}

        用户任务：
        {task}

        {(string.IsNullOrWhiteSpace(previous) ? "" : $"上一轮协调者结论：\n{Preview(previous)}")}

        请输出一份简洁的分派计划：每个成员负责什么、验收标准是什么。
        只输出计划本身，不要执行成员工作。
        """;

    private static string WorkerPrompt(
        TeamDefinition team, TeamMember member, string task, string plan, string? previous, int round, int maxRounds) =>
        $"""
        你是「{team.Name}」的成员：{member.Name}（{member.Role}）。当前是第 {round}/{maxRounds} 轮。
        职责：{member.Responsibility}

        用户任务：
        {task}

        协调者分派计划：
        {Preview(plan)}

        {(string.IsNullOrWhiteSpace(previous) ? "" : $"上一轮协调者结论：\n{Preview(previous)}")}

        请完成分派给你的部分，输出可被协调者直接汇总的结论。
        """;

    private static string SupervisorSummaryPrompt(
        TeamDefinition team, string task, string plan, List<(TeamMember Member, string Text)> outputs, int round, int maxRounds) =>
        $"""
        你是「{team.Name}」的协调者。第 {round}/{maxRounds} 轮汇总。
        用户任务：
        {task}

        分派计划：
        {Preview(plan)}

        成员结果：
        {string.Join("\n\n---\n", outputs.Select(pair => $"{pair.Member.Name}（{pair.Member.Role}）：\n{Preview(pair.Text)}"))}

        请给出面向用户的最终答复，整合成员结论并指出仍存在的风险或缺口。
        如果任务已经完成，最后一行只写 DONE；如果还需下一轮，最后一行只写 CONTINUE。
        """;

    private static string ExecutorPrompt(
        TeamDefinition team, TeamMember member, string task, string feedback, int round, int maxRounds) =>
        $"""
        你是「{team.Name}」的执行者：{member.Name}（{member.Role}）。当前是第 {round}/{maxRounds} 轮。
        职责：{member.Responsibility}

        用户任务：
        {task}

        {(string.IsNullOrWhiteSpace(feedback) ? "这是首轮执行。" : $"上一轮审核意见：\n{Preview(feedback)}\n\n请针对意见修正产出。")}

        请交付本轮最终产出，便于审核者直接判断。
        """;

    private static string ReviewPrompt(TeamDefinition team, string task, string output, int round, int maxRounds) =>
        $"""
        你是「{team.Name}」的审核者。当前是第 {round}/{maxRounds} 轮评审。
        用户任务：
        {task}

        执行者产出：
        {Preview(output)}

        请先给出具体问题与修改建议；最后一行只写 APPROVE（通过）或 REJECT（需重做）。
        """;

    private static bool HasVerdict(string text, string verdict)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 && lines[^1].Contains(verdict, StringComparison.OrdinalIgnoreCase);
    }

    private static string ModeLabel(string mode) => mode switch
    {
        "parallel" => "并行执行",
        "supervisor" => "主管调度",
        "review" => "评审制",
        _ => "顺序执行",
    };

    private static string Preview(string text, int max = 800)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }

    private sealed class RunContext
    {
        public required WorkspaceInfo Workspace { get; init; }
        public required TeamDefinition Team { get; init; }
        public required string Task { get; init; }
        public required string RunId { get; init; }
        public required List<TeamMember> Members { get; init; }
        public required TurnExecutionScope Scope { get; init; }
        public required TeamRun Run { get; set; }
        public CancellationToken Ct { get; set; }
        public List<string> Failures { get; } = [];
        public bool Incomplete { get; set; }
        public int MaxRounds => Math.Clamp(Team.MaxRounds, 1, TeamStore.MaxRoundsLimit);
        public int MaxRetries => Math.Clamp(Team.MaxRetries, 0, TeamStore.MaxRetriesLimit);
    }

    private sealed class TeamAbortException(string message) : Exception(message);
}
