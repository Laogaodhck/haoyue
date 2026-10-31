using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Agents;

public sealed partial class Agent
{
    private readonly record struct ToolExecution(ChatMessage Message, bool ToolMutated);

    /// <summary>
    /// 只读工具并行批的执行闸门（P2）：并发上限 + 批内去重。
    /// 同批内 (Name, ArgumentsJson) 完全相同的重复调用只执行一次、结果共享——
    /// 只读结果确定，重复执行是纯浪费；并发上限 4，防止批量 read/directory_tree
    /// 把磁盘 IO 与 embedding 请求瞬间打满（真实并行收益在 2-4 就饱和）。
    /// </summary>
    private static async Task<ToolExecution[]> ExecuteReadOnlyBatchAsync(
        IReadOnlyList<ToolCallRequest> batch,
        Func<ToolCallRequest, Task<ToolExecution>> execute)
    {
        const int maxConcurrentReads = 4;
        var results = new ToolExecution[batch.Count];
        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < batch.Count; i++)
        {
            var key = batch[i].Name + "\u0000" + batch[i].ArgumentsJson;
            if (!groups.TryGetValue(key, out var indices)) groups[key] = indices = [];
            indices.Add(i);
        }
        using var gate = new SemaphoreSlim(maxConcurrentReads);
        var executed = await Task.WhenAll(groups.Values.Select(async group =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try { return (Indices: group, Result: await execute(batch[group[0]]).ConfigureAwait(false)); }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        foreach (var (indices, result) in executed)
            foreach (var i in indices)
                results[i] = result;
        return results;
    }

    private async Task<ToolExecution> ExecuteToolAsync(
        ToolCallRequest call, WorkspaceInfo workspace, ModelInfo model,
        TurnExecutionScope turnScope, TurnPlan turnPlan, CancellationToken ct,
        HashSet<string>? quarantinedCalls = null)
    {
        var tool = toolRegistry.Resolve(call.Name);
        var argsSummary = SummarizeArguments(call.ArgumentsJson);
        events.Publish(new ToolCallStartedEvent(call.Id, call.Name, argsSummary));

        if (tool is null)
        {
            var suggestions = SuggestTools(call.Name);
            var message = $"Unknown tool: {call.Name}"
                + (suggestions.Length > 0
                    ? $". Closest available tools: {string.Join(", ", suggestions)}. Use one of these or correct the tool name."
                    : "");
            events.Publish(new ToolCallCompletedEvent(call.Id, call.Name, false, message, TimeSpan.Zero));
            return new ToolExecution(ChatMessage.ToolResult(call.Id, call.Name, message, false), false);
        }

        // H-D 回合级隔离：同一「工具 + 参数签名」失败达到换策略阈值后，该签名在本回合
        // 剩余时间内直接拒绝执行——比任何提示都硬，模型无法无视。只封这次调用，不封
        // 整个工具：bash 的一条失败命令不代表本回合其它命令不可执行。
        var callSignature = $"{tool.Name}|{ToolArguments.Signature(call.ArgumentsJson)}";
        if (quarantinedCalls is not null && quarantinedCalls.Contains(callSignature))
        {
            var message = $"This exact call to '{tool.Name}' is quarantined for the rest of this turn: it failed "
                + $"{StrategyPivotFailureThreshold} times in a row with identical arguments. Do not repeat this call — "
                + "change the arguments, switch to a different tool or approach, or finish with the results you already have.";
            events.Publish(new ToolCallCompletedEvent(call.Id, call.Name, false, message, TimeSpan.Zero));
            return new ToolExecution(ChatMessage.ToolResult(call.Id, call.Name, message, false), false);
        }

        var rawMode = workspace.Config?.Mode ?? configStore.Config.Agent.Mode;
        var mode = AgentModeExtensions.Parse(rawMode);
        if (mode.IsReadOnly() && tool.Mutating)
        {
            var message = $"Tool execution denied: '{tool.Name}' is not allowed in {mode.ToDisplayString()}. Switch mode via '/mode edit' or '/mode auto' to allow file mutations.";
            events.Publish(new ToolCallCompletedEvent(call.Id, call.Name, false, message, TimeSpan.Zero));
            return new ToolExecution(ChatMessage.ToolResult(call.Id, call.Name, message, false), false);
        }

        events.Publish(new StatusEvent(tool.StatusLabel, argsSummary));

        // Best-effort repair of truncated / trailing-garbage arguments, then execute with
        // the recovered object. Unrecoverable arguments become an explicit tool error so the
        // model regenerates the call instead of the provider rejecting the whole request.
        var parsedArguments = ToolArguments.ParseDetailed(call.ArgumentsJson);
        if (parsedArguments.Obj is null)
        {
            var message = "Invalid tool arguments: the model produced arguments that are not valid JSON. Regenerate the tool call with valid JSON arguments.";
            events.Publish(new ToolCallCompletedEvent(call.Id, call.Name, false, message, TimeSpan.Zero));
            return new ToolExecution(ChatMessage.ToolResult(call.Id, call.Name, message, false), false);
        }
        var arguments = parsedArguments.Obj;

        // Mandatory execution gate (P0): unlike the prompt-level permission notice this
        // runtime policy cannot be talked around by the model. It applies in every mode,
        // to the main agent and to sub-agents, right before the tool is invoked.
        var policyDecision = _toolPolicy.Evaluate(
            tool, arguments, configStore.Config.Agent.ToolPolicy);
        if (policyDecision.Verdict == ToolPolicyVerdict.Denied)
        {
            // 内置高危守卫的拒绝走「本次允许」审批（P1-2.3）：用户自定义 allow/deny
            // 是显式配置，维持硬拒绝；只有 builtin-bash-guard 这类启发式拦截才提供
            // 一次性放行，避免用户为了跑一条命令去改配置文件。无审批通道（CLI /
            // 测试上下文）时立即按拒绝收场——等待必然超时，干等 120 秒只会让
            // 回合看起来卡死。
            if (_approvals is { Interactive: true }
                && string.Equals(policyDecision.Rule, "builtin-bash-guard", StringComparison.OrdinalIgnoreCase))
            {
                // 同上：先登记后发布，避免进程内响应器抢在登记之前。
                var pendingApproval = _approvals.RequestAsync(
                    call.Id, ApprovalRegistry.DefaultTimeoutSeconds, ct);
                events.Publish(new ApprovalRequiredEvent(
                    call.Id, tool.Name, argsSummary, policyDecision.Reason));
                var approved = await pendingApproval.ConfigureAwait(false);
                if (approved)
                {
                    events.Publish(new StatusEvent($"用户已批准执行 {tool.Name}", argsSummary));
                    goto Execute; // 一次性放行：仅本次调用生效
                }
                var rejected = $"工具 {tool.Name} 被内置高危守卫拦截，用户未批准执行（{policyDecision.Reason}）。"
                    + "可调整命令重试，或在配置 agent.toolPolicy.allow 中显式放行。";
                events.Publish(new ToolCallCompletedEvent(call.Id, tool.Name, false, rejected, TimeSpan.Zero));
                return new ToolExecution(ChatMessage.ToolResult(call.Id, tool.Name, rejected, false), false);
            }

            var message = $"Tool execution blocked by policy ({policyDecision.Rule}): {policyDecision.Reason} "
                + "如确需执行，请提示用户在配置 agent.toolPolicy.allow 中显式放行，或调整命令后重试。";
            events.Publish(new WarningEvent($"工具 {call.Name} 被执行策略拒绝（{policyDecision.Rule}）"));
            events.Publish(new ToolCallCompletedEvent(call.Id, call.Name, false, message, TimeSpan.Zero));
            return new ToolExecution(ChatMessage.ToolResult(call.Id, call.Name, message, false), false);
        }

        // 高危工具执行前同意（agent.toolPolicy.requireConsentFor）：守卫规则只拦
        // 「像破坏性命令」的调用，而 computer_exec 这类工具一启用就拿到 profile 级
        // 无沙箱 shell，必须在第一次调用前让用户知情。回合内按工具批准一次——
        // 交互式计算机使用不会每步弹卡；无审批通道（CLI/测试）立即拒绝并指路，
        // 不静默放行一个无沙箱 shell。
        var consentFor = configStore.Config.Agent.ToolPolicy.RequireConsentFor;
        if (consentFor is { Count: > 0 }
            && consentFor.Any(name => string.Equals(name, tool.Name, StringComparison.OrdinalIgnoreCase))
            && !turnScope.IsConsented(tool.Name))
        {
            if (_approvals is not { Interactive: true })
            {
                var noChannel = $"工具 {tool.Name} 需要用户同意后执行（agent.toolPolicy.requireConsentFor），"
                    + "但当前环境没有审批通道（CLI 或测试上下文）。请改用桌面端运行，或在配置中"
                    + "将该工具从 agent.toolPolicy.requireConsentFor 移除（需自行承担无沙箱 shell 的风险）。";
                events.Publish(new ToolCallCompletedEvent(call.Id, tool.Name, false, noChannel, TimeSpan.Zero));
                return new ToolExecution(ChatMessage.ToolResult(call.Id, tool.Name, noChannel, false), false);
            }
            // 先登记后发布：RequestAsync 在方法体内同步完成 GetOrAdd 登记，事件若先于
            // 登记送达，进程内响应器（桌面 RPC / 测试）会面对一个尚不存在的挂起请求。
            var pendingConsent = _approvals.RequestAsync(
                call.Id, ApprovalRegistry.DefaultTimeoutSeconds, ct);
            events.Publish(new ApprovalRequiredEvent(
                call.Id, tool.Name, argsSummary,
                $"{tool.Name} 会以当前用户权限在你计算机上执行命令（无沙箱），请确认本次允许。"));
            var consent = await pendingConsent.ConfigureAwait(false);
            if (!consent)
            {
                var denied = $"工具 {tool.Name} 未获得用户同意，已取消执行。"
                    + "可在桌面审批卡片中选择「本次允许」，或在配置 agent.toolPolicy.requireConsentFor 中调整需要同意的工具清单。";
                events.Publish(new ToolCallCompletedEvent(call.Id, tool.Name, false, denied, TimeSpan.Zero));
                return new ToolExecution(ChatMessage.ToolResult(call.Id, tool.Name, denied, false), false);
            }
            turnScope.GrantConsent(tool.Name);
            events.Publish(new StatusEvent($"用户已批准执行 {tool.Name}", argsSummary));
        }

        // 5.2 风险分级闸门（软约束）：风险维度命中 agent.toolPolicy.consentRiskTiers 的
        // 工具本回合首次调用前请求同意。与上面 requireConsentFor 的「强拒绝」区分——
        // 分级闸门在无审批通道环境（CLI / 测试）放行并记警告：不剥夺 CLI 已有的 shell
        // 能力，只保证交互环境下「执行命令/控制本机/花钱」类操作首次必过人工。
        var riskPolicy = configStore.Config.Agent.ToolPolicy;
        if (riskPolicy.RiskGating
            && !turnScope.IsConsented(tool.Name)
            && ToolRiskGating.Matches(tool.RiskTier, riskPolicy.ConsentRiskTiers))
        {
            var riskLabel = ToolRiskGating.Describe(tool.RiskTier);
            if (_approvals is { Interactive: true })
            {
                var pendingRisk = _approvals.RequestAsync(
                    call.Id, ApprovalRegistry.DefaultTimeoutSeconds, ct);
                events.Publish(new ApprovalRequiredEvent(
                    call.Id, tool.Name, argsSummary,
                    $"{tool.Name} 属于高风险操作（{riskLabel}），请确认本次允许。"
                    + "本回合后续调用不再重复询问。"));
                if (!await pendingRisk.ConfigureAwait(false))
                {
                    var deniedByRisk = $"工具 {tool.Name} 未获得用户同意（高风险：{riskLabel}），已取消执行。"
                        + "可在桌面审批卡片中选择「本次允许」，或在设置中调整「需要同意的风险维度」。";
                    events.Publish(new WarningEvent($"高风险工具 {tool.Name} 被用户拒绝（{riskLabel}）"));
                    events.Publish(new ToolCallCompletedEvent(call.Id, tool.Name, false, deniedByRisk, TimeSpan.Zero));
                    return new ToolExecution(ChatMessage.ToolResult(call.Id, tool.Name, deniedByRisk, false), false);
                }
                turnScope.GrantConsent(tool.Name);
                // 同意事件进审计：与拒绝事件并列，便于事后核对「谁在何时放行了什么」。
                events.Publish(new StatusEvent($"用户已批准高风险操作 {tool.Name}", riskLabel));
            }
            else
            {
                // 无审批通道：本回合记账一次，避免每步重复警告刷屏。
                turnScope.GrantConsent(tool.Name);
                events.Publish(new WarningEvent(
                    $"工具 {tool.Name} 属于高风险操作（{riskLabel}）；当前环境无审批通道，已放行。"
                    + "在桌面端运行可在首次调用前收到审批卡片。"));
            }
        }

        Execute:
        var context = new ToolContext
        {
            Workspace = workspace,
            Events = events,
            Agent = configStore.Config.Agent,
            MaxOutputChars = ContextPlanner.ToolOutputBudget(model.Model, configStore.Config.Agent),
            CallId = call.Id,
            Coordinator = fileLocks,
            Owner = lockScope.Owner,
            TurnScope = turnScope,
            PlanTracker = turnPlan,
        };

        var stopwatch = Stopwatch.StartNew();
        ToolResult result;
        var changesBefore = turnScope.Changes.Count;
        try
        {
            result = await tool.ExecuteAsync(arguments, context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = ToolResult.Fail($"{tool.Name} crashed: {ex.Message}");
        }
        stopwatch.Stop();

        events.Publish(new ToolCallCompletedEvent(
            call.Id, call.Name, result.Success,
            result.Summary ?? Firstline(result.Output), stopwatch.Elapsed));

        // G6 审计扩展（L3）：MCP 调用与文件写入统一落 .haoyue/audit/tools.jsonl
        //（bash 保留专属风险分级日志，不重复记录）。审计失败绝不影响执行。
        TryAuditToolExecution(workspace, tool, arguments, result, stopwatch.ElapsedMilliseconds);

        // Repaired-arguments transparency (L7): a bracket-closure repair can recover a
        // syntactically valid but semantically truncated parameter (e.g. a cut-off bash
        // command). Surface that on successful results so the model regenerates the call
        // instead of building on broken input.
        if (parsedArguments.Repaired && result.Success)
            result = result with
            {
                Output = result.Output
                    + "\n>>> [args repaired] 本调用的参数 JSON 疑似被截断，运行时已自动修复。"
                    + "若执行结果与预期不符（如命令文本不完整），请重新完整生成该调用。"
            };

        var filePath = result.FilePath is null
            ? null
            : Path.IsPathRooted(result.FilePath)
                ? Path.GetRelativePath(workspace.Root, result.FilePath)
                : result.FilePath;

        // Execution ledger: every tool call becomes a step; a step is compensable when
        // the tool registered a file change during this execution. delegate_task runs a
        // nested agent under the same lock scope, so its ledger entries flow in via the
        // same channel and get their own kind.
        var compensable = turnScope.Changes.Count > changesBefore;
        turnScope.RecordStep(
            call.Name == "delegate_task" ? "subtask" : "tool",
            call.Name, filePath ?? argsSummary, compensable, result.Success);
        if (compensable && tool.Mutating)
            turnScope.MutatingStepObserved?.Invoke(turnScope.Steps[^1]);

        return new ToolExecution(
            ChatMessage.ToolResult(call.Id, call.Name, result.Output, result.Success, result.Diff, filePath, result.Images),
            tool.Mutating && result.Success);
    }

    // ---------------------------------------------------------------- misc

    private static void TryAuditToolExecution(
        WorkspaceInfo workspace, ITool tool, JsonObject arguments, ToolResult result, long elapsedMs)
    {
        try
        {
            if (ToolAuditLog.Resolve(tool, arguments) is not { } audited) return;
            var (category, target) = audited;
            ToolAuditLog.Append(workspace, new ToolAuditRecord(
                DateTime.UtcNow.ToString("o"),
                tool.Name,
                category,
                target,
                result.Success ? "ok" : "fail",
                result.Summary ?? Firstline(result.Output),
                elapsedMs));
        }
        catch (Exception)
        {
            // 审计属于观测层：任何异常都不允许反过来中断工具执行。
        }
    }

    /// <summary>
    /// Closest registered tool names for an unknown tool call: edit-distance plus
    /// prefix overlap, top 3. Turns "Unknown tool: read_fille" into an actionable
    /// suggestion instead of a dead end the model retries blindly.
    /// </summary>
    private string[] SuggestTools(string name)
    {
        var maxDistance = Math.Max(2, name.Length / 3);
        return toolRegistry.All
            .Select(t => (Name: t.Name, Distance: LevenshteinDistance(name, t.Name, maxDistance)))
            .Where(c => c.Distance <= maxDistance
                        || c.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith(c.Name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Distance)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => c.Name)
            .Take(3)
            .ToArray();
    }

    /// <summary>Levenshtein distance with an early ceiling: past it, int.MaxValue.</summary>
    private static int LevenshteinDistance(string source, string target, int ceiling)
    {
        source = source.ToLowerInvariant();
        target = target.ToLowerInvariant();
        if (source == target) return 0;
        if (Math.Abs(source.Length - target.Length) > ceiling) return int.MaxValue;
        var previous = new int[target.Length + 1];
        var current = new int[target.Length + 1];
        for (var j = 0; j <= target.Length; j++) previous[j] = j;
        for (var i = 1; i <= source.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= target.Length; j++)
            {
                var cost = source[i - 1] == target[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                if (current[j] < rowMin) rowMin = current[j];
            }
            if (rowMin > ceiling) return int.MaxValue;
            (previous, current) = (current, previous);
        }
        return previous[target.Length];
    }

    private static string SummarizeArguments(string argumentsJson)
    {
        try
        {
            if (JsonNode.Parse(argumentsJson) is JsonObject obj)
            {
                var parts = obj
                    .Where(kv => kv.Value is JsonValue)
                    .Take(3)
                    .Select(kv =>
                    {
                        var value = kv.Value!.ToString();
                        if (value.Length > 60) value = value[..60] + "…";
                        return $"{kv.Key}: {value.ReplaceLineEndings(" ")}";
                    });
                return string.Join(", ", parts);
            }
        }
        catch (JsonException) { }
        return "";
    }

    private static string Firstline(string text)
    {
        var line = text.AsSpan().TrimStart();
        var newline = line.IndexOf('\n');
        var result = newline >= 0 ? line[..newline] : line;
        return result.Length > 160 ? string.Concat(result[..160], "…") : result.ToString();
    }
}
