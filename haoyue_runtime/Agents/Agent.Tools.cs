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

    private async Task<ToolExecution> ExecuteToolAsync(
        ToolCallRequest call, WorkspaceInfo workspace, ModelInfo model,
        TurnExecutionScope turnScope, TurnPlan turnPlan, CancellationToken ct,
        HashSet<string>? quarantinedTools = null)
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

        // H-D 回合级隔离：同签名失败达到换策略阈值后，该工具在本回合剩余时间内直接
        // 拒绝执行——比任何提示都硬，模型无法无视。
        if (quarantinedTools is not null && quarantinedTools.Contains(tool.Name))
        {
            var message = $"Tool '{tool.Name}' is quarantined for the rest of this turn: it failed "
                + $"{StrategyPivotFailureThreshold} times in a row with identical arguments. Do not call it again this turn — "
                + "switch to a different tool or approach, or finish with the results you already have.";
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
            var message = $"Tool execution blocked by policy ({policyDecision.Rule}): {policyDecision.Reason} "
                + "如确需执行，请提示用户在配置 agent.toolPolicy.allow 中显式放行，或调整命令后重试。";
            events.Publish(new WarningEvent($"工具 {call.Name} 被执行策略拒绝（{policyDecision.Rule}）"));
            events.Publish(new ToolCallCompletedEvent(call.Id, call.Name, false, message, TimeSpan.Zero));
            return new ToolExecution(ChatMessage.ToolResult(call.Id, call.Name, message, false), false);
        }

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
            MutatingStepObserved?.Invoke(turnScope.Steps[^1]);

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
