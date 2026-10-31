using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Skills;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Verification;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Agents;

/// <param name="FinishReason">回合终态（<see cref="TurnFinishReason"/> 词汇表）。
/// 为 null 时调用方按「stop」处理——旧调用点与测试桩保持兼容。</param>
/// <summary>
/// One finished turn. 3.1 起携带本回合的总消耗（主步骤 + 并入的委派子任务用量），
/// 供测试桩与上层（进化实验室、批量委派）汇总；默认参数保持既有构造兼容。
/// </summary>
public sealed record AgentTurnResult(
    string Text,
    bool Cancelled,
    string? Error,
    string? FinishReason = null,
    long InputTokens = 0,
    long OutputTokens = 0,
    decimal Cost = 0m);

/// <summary>
/// The agent loop: compose prompt → stream model → execute tools → repeat,
/// with automatic build verification and repair after file mutations.
/// All progress is published to the event bus; the agent never touches the console.
/// </summary>
public sealed partial class Agent(
    IConfigStore configStore,
    IProviderManager providerManager,
    IToolRegistry toolRegistry,
    IPromptProvider promptProvider,
    PromptComposer promptComposer,
    IWorkspaceManager workspaceManager,
    ISessionStore sessionStore,
    IVerifier verifier,
    IEventBus events,
    IFileLockCoordinator fileLocks,
    FileLockScope lockScope,
    ISkillManager? skills = null,
    TurnUndoRegistry? undoRegistry = null,
    IToolExecutionPolicy? toolPolicy = null,
    Data.ModelTokenStats? tokenStats = null,
    ApprovalRegistry? approvals = null)
{
    private readonly ISkillManager? _skills = skills;
    private readonly TurnUndoRegistry? _undoRegistry = undoRegistry;
    private readonly IToolExecutionPolicy _toolPolicy = toolPolicy ?? new Tools.ToolExecutionPolicy();
    private readonly Data.ModelTokenStats? _tokenStats = tokenStats;
    private readonly ApprovalRegistry? _approvals = approvals;
    // H-E: per-model 校准系数的进程内缓存（DB 行极小，一次读取后本进程复用；
    // 与 ContextWindowCalibrator 一致，配置变更本就要重启运行时）。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _ratioCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Optional daemon hook: invoked for every mutating step so the crash journal can
    /// carry an executed-steps ledger for post-crash reconciliation. Set by the daemon
    /// before the turn starts; null in CLI / test contexts.
    /// 回合开始时挂到 <see cref="TurnExecutionScope"/> 上，因此共享父台账的子代理步骤也进日志。
    /// </summary>
    public Action<TurnStepRecord>? MutatingStepObserved { get; set; }

    /// <summary>Identical failed tool calls (same tool + normalized args) tolerated before a strategy nudge is injected.</summary>
    internal const int RepeatedFailureNudgeThreshold = 2;

    /// <summary>Same-signature failures that escalate to a one-shot "change your approach" notice.</summary>
    internal const int StrategyPivotFailureThreshold = RepeatedFailureNudgeThreshold * 2;

    /// <summary>Tool calls with zero mutations and no plan tolerated before a no-progress nudge (edit/auto modes only).</summary>
    internal const int NoProgressToolCallThreshold = 8;

    /// <summary>Identical successful calls (same tool + args, no mutation between) tolerated before a redundant-call nudge.</summary>
    internal const int RedundantCallThreshold = 3;

    /// <summary>
    /// Runtime notices (step budget, output truncation, empty answer, context compaction,
    /// repeated tool failure, steering …) are injected as user-role messages whose text
    /// starts with ">>> [". Without neutralization a user could impersonate such a notice
    /// (fake step budget, fake instructions) by typing the same prefix. Break the exact
    /// marker in user-provided text — ">>> [" becomes ">> [" — while leaving ordinary
    /// content untouched.
    /// </summary>
    internal static string SanitizeRuntimeNoticePrefix(string input)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains(">>> ["))
            return input;
        var lines = input.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith(">>> [")) continue;
            var indent = line[..(line.Length - trimmed.Length)];
            lines[i] = indent + ">>" + trimmed[3..];
        }
        return string.Join("\n", lines);
    }

    /// <param name="inheritedScope">
    /// 委派链上层回合的执行台账。传入时本回合的步骤与文件补偿直接记入父台账（而不是新建一份），
    /// 因此 <c>agent.undo</c> 能一次撤销整回合——包括子代理写下的文件。为 null 时是独立回合。
    /// </param>
    public async Task<AgentTurnResult> RunTurnAsync(
        AgentSession session,
        WorkspaceInfo workspace,
        string userInput,
        CancellationToken ct,
        ReasoningLevel? reasoningLevel = null,
        IReadOnlyList<ChatImageAttachment>? images = null,
        AgentSteeringQueue? steering = null,
        TurnExecutionScope? inheritedScope = null)
    {
        // 回合身份上线下沉到事件层（TurnStamp）：本回合发布的所有事件都带 TurnId，
        // 台账与 events.recent 回放因此能按回合归组。宿主（daemon 用客户端请求 id）
        // 已设定身份时此处为空操作——委派子代理共享父回合身份。
        using var turnIdentity = TurnStamp.BeginScope($"turn-{Guid.NewGuid().ToString("N")[..8]}");

        // Empty input would burn a model round on a guaranteed-empty answer. Text OR
        // images must be present; the desktop and CLI frontends never send both empty,
        // but scheduled tasks and API callers can.
        if (string.IsNullOrWhiteSpace(userInput) && images is not { Count: > 0 })
        {
            const string message = "输入为空：请提供有效的问题或指令后再发送。";
            events.Publish(new ErrorEvent("Empty input", message));
            return new AgentTurnResult("", false, message, TurnFinishReason.Error);
        }

        var agentConfig = configStore.Config.Agent;
        events.Publish(new TurnStartedEvent(session.Header.Id, userInput));
        // Neutralize user text that mimics the runtime-notice prefix before it enters
        // history (see SanitizeRuntimeNoticePrefix).
        userInput = SanitizeRuntimeNoticePrefix(userInput);
        var userMessage = ChatMessage.User(userInput, images);
        // Everything appended from here on belongs to this turn; earlier entries are history.
        var turnMessageIndex = session.Messages.Count;
        sessionStore.Append(session, userMessage);
        // Skill trigger matching runs against a stickiness window (current input plus
        // recent user turns) so multi-turn tasks keep their skills; computed once per
        // turn and shared by the tool policy and the composed prompt.
        var skillTriggerContext = BuildSkillTriggerContext(session.Messages, turnMessageIndex, userInput);
        // N5: model-pulled skills (declare_skill) never leak across turns — the shared
        // manager (CLI) is reset here; isolated turn runtimes get a fresh manager anyway.
        _skills?.ResetTurnDeclarations();

        // Per-turn execution ledger + compensation stack (retrospective, not a
        // prospective plan): every tool execution appends a step; file tools register
        // their pre-turn content for user-confirmed undo after the turn ends.
        // 委派子代理共享父回合的台账（inheritedScope）：否则子代理写的文件既进不了父回合的
        // 补偿栈、也没有任何会话能撤销它——「一键撤销本回合」会漏掉整条子任务链。
        var turnScope = inheritedScope ?? new TurnExecutionScope(session.Header.Id, workspace.Root);
        // daemon 每回合在 Agent 实例上挂崩溃日志观察者；继承父台账时观察者已在父作用域上。
        turnScope.MutatingStepObserved ??= MutatingStepObserved;

        var state = new TurnState
        {
            Session = session,
            Workspace = workspace,
            UserMessage = userMessage,
            Steering = steering,
            Scope = turnScope,
            InheritedScope = inheritedScope,
            // Per-turn plan state: update_plan writes here (via ToolContext.PlanTracker) and
            // the plan guard turns drift into targeted corrective notices.
            Plan = new TurnPlan(),
            PlanGuard = new PlanNudgeGuard(),
            Config = agentConfig,
            ReadOnlyMode = AgentModeExtensions.Parse(workspace.Config?.Mode ?? agentConfig.Mode).IsReadOnly(),
            Ct = ct,
            SkillTriggerContext = skillTriggerContext,
            // Vision relevance is tracked incrementally (see turnHasImagesFlag) instead of
            // rescanning history: after a compaction the scan range becomes ambiguous, and
            // images from earlier turns must not drag a text-only turn onto a vision model.
            TurnHasImages = images is { Count: > 0 },
        };

        // Planning hint: nudge complex-looking inputs toward a declared plan before the
        // first model step. Conservative heuristic (length + sequencing markers), only
        // in edit/auto modes, once per turn — simple tasks proceed untouched.
        if (!state.ReadOnlyMode && AgentHeuristics.LooksLikeMultiStepTask(userInput))
        {
            sessionStore.Append(session, ChatMessage.User(
                ">>> [planning hint] 这个任务看起来包含多个步骤或目标。建议先调用 update_plan 把工作拆解为 2-5 个具体的里程碑步骤，再逐步执行并保持状态同步；如果实际很简单，直接开始即可，不必先计划。"));
            events.Publish(new StatusEvent("Planning hint injected"));
        }

        try
        {
            await RunLoopAsync(state, reasoningLevel).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            state.Cancelled = true;
        }
        catch (LlmException ex)
        {
            state.Error = ex.Message;
            events.Publish(new ErrorEvent("LLM request failed", ex.Message));
        }
        catch (Exception ex)
        {
            // 意外异常（存储/IO 等）也要走受控失败路径：否则 TurnCompletedEvent 与
            // workflow 收尾事件都不会发布，客户端的工作流视图停留在最后一步。
            // error 面向用户只给消息；堆栈放进 Detail，客户端「技术详情」可直接定位。
            state.Error = $"Unexpected error: {ex.Message}";
            events.Publish(new ErrorEvent(
                "Agent turn failed",
                ex.StackTrace is { Length: > 0 } stack ? $"{state.Error}\n{ex.GetType().FullName}: {stack}" : state.Error));
        }
        finally
        {
            FinalizeTurn(state);
        }

        // 3.1 兜底并入：最后一批委派子任务的用量可能还没被主循环取走（子任务完成
        // 晚于最后一个模型步骤），回合结束时必须收干净，否则结论失真。
        var (finalDelegatedIn, finalDelegatedOut, finalDelegatedCost) = state.Scope.TakeDelegatedUsage();
        state.InputTokens += finalDelegatedIn;
        state.OutputTokens += finalDelegatedOut;
        state.Cost += finalDelegatedCost;
        return new AgentTurnResult(state.FinalText, state.Cancelled, state.Error, state.Outcome,
            state.InputTokens, state.OutputTokens, state.Cost);
    }

    private static string ToRelativePath(string absolutePath, string root) =>
        absolutePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? absolutePath[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : absolutePath;

    private void PublishWorkflow(int step, string kind, string label, string? detail = null) =>
        events.Publish(new WorkflowEvent(step, kind, label, detail));

    /// <summary>
    /// M-C: 单请求图片 token 预算——模型窗口的 10%（钳制 1k..8k tokens），与
    /// <see cref="ContextPlanner.EstimateImageTokens(ChatImageAttachment)"/> 同一量纲。
    /// 视觉历史超过该预算时更早的图被剥离，当前关键截图优先。
    /// </summary>
    private static int VisionImageTokenBudget(ModelInfo model) =>
        Math.Clamp((int)(model.Model.ContextWindow * 0.10), 1_000, 8_000);

    /// <summary>
    /// H-E: 当前模型的 token 估算校准系数（actual/estimated 的 EWMA）。ProviderManager
    /// 每次成功请求后把偏差写回 ModelTokenStats，这里读出来乘进后续估算——低估（系数&gt;1）
    /// 时窗口预算收紧避免超限 400，高估（系数&lt;1）时放宽避免过早压缩。
    /// </summary>
    private double TokenCalibration(ModelInfo model)
    {
        if (_tokenStats is null) return 1.0;
        var key = $"{model.Provider.Id}|{model.Model.Id}";
        return _ratioCache.GetOrAdd(key, static (_, state) =>
        {
            var (stats, providerId, modelId) = state;
            try { return stats.GetRatio(providerId, modelId); }
            catch (Exception) { return 1.0; } // 校准不可用时退回纯启发式
        }, (_tokenStats, model.Provider.Id, model.Model.Id));
    }

    /// <summary>
    /// True when the model did not actually finish: the provider hit its output-token cap,
    /// the stream never reported a terminal event, or the connection dropped after the first
    /// token. "interrupted" belongs here — a half answer left by a dropped stream must be
    /// continued (or honestly reported as truncated), never presented as a completed reply.
    /// </summary>
    /// <summary>
    /// 回合预算越界判定：token / 费用 / 墙钟任一超限返回可读的越界说明，否则 null。
    /// 前两项依赖 provider 回传 usage——本地通道不回传时恒为 0，只有墙钟能触发。
    /// </summary>
    internal static string? TurnBudgetBreach(AgentConfig config, long totalTokens, decimal cost, TimeSpan elapsed)
    {
        if (config.TurnTokenBudget > 0 && totalTokens >= config.TurnTokenBudget)
            return $"token 用量 {totalTokens} 已达本回合上限 {config.TurnTokenBudget}";
        if (config.TurnCostBudget > 0 && cost >= (decimal)config.TurnCostBudget)
            return $"费用 {cost:0.####} 已达本回合上限 {config.TurnCostBudget:0.####}";
        if (config.TurnWallClockSeconds > 0 && elapsed.TotalSeconds >= config.TurnWallClockSeconds)
            return $"耗时 {elapsed.TotalSeconds:0.#}s 已达本回合上限 {config.TurnWallClockSeconds}s";
        return null;
    }

    private static bool IsOutputTruncated(string? finishReason) =>
        finishReason is "length" or "max_tokens" or "incomplete" or "interrupted";

    private IReadOnlyList<ChatMessage> AppendSteering(AgentSession session, AgentSteeringQueue? steering)
    {
        if (steering is null) return [];
        var messages = steering.Drain();
        foreach (var message in messages) sessionStore.Append(session, message);
        return messages;
    }

    private void PublishSteering(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages) events.Publish(new UserSteerEvent(message.Text));
    }
}
