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
public sealed record AgentTurnResult(string Text, bool Cancelled, string? Error, string? FinishReason = null);

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
    Data.ModelTokenStats? tokenStats = null)
{
    private readonly ISkillManager? _skills = skills;
    private readonly TurnUndoRegistry? _undoRegistry = undoRegistry;
    private readonly IToolExecutionPolicy _toolPolicy = toolPolicy ?? new Tools.ToolExecutionPolicy();
    private readonly Data.ModelTokenStats? _tokenStats = tokenStats;
    // H-E: per-model 校准系数的进程内缓存（DB 行极小，一次读取后本进程复用；
    // 与 ContextWindowCalibrator 一致，配置变更本就要重启运行时）。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _ratioCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Optional daemon hook: invoked for every mutating step so the crash journal can
    /// carry an executed-steps ledger for post-crash reconciliation. Set by the daemon
    /// before the turn starts; null in CLI / test contexts.
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

    public async Task<AgentTurnResult> RunTurnAsync(
        AgentSession session,
        WorkspaceInfo workspace,
        string userInput,
        CancellationToken ct,
        ReasoningLevel? reasoningLevel = null,
        IReadOnlyList<ChatImageAttachment>? images = null,
        AgentSteeringQueue? steering = null)
    {
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

        var mutated = false;
        var repairAttempts = 0;
        var finalText = "";
        string? error = null;
        var cancelled = false;
        // 回合终态：默认「模型答完了」，每个非正常出口（截断、拒答、空回答、超步数、
        // 预算、取消、异常）都显式改写它。finally 里算出的对外值见 turnOutcome。
        var turnFinishReason = TurnFinishReason.Stop;
        string turnOutcome = TurnFinishReason.Stop;
        // 回合预算计量：token/费用取自每步 completion.Usage（provider 不回传用量则恒为 0），
        // 墙钟从回合开始计时，是唯一不依赖 usage 的兜底刹车。
        var turnClock = Stopwatch.StartNew();
        long turnInputTokens = 0, turnOutputTokens = 0;
        decimal turnCost = 0m;
        var budgetWrapUpRequested = false;
        var budgetWrapUpStep = 0;
        // Vision relevance is tracked incrementally (see turnHasImagesFlag) instead of
        // rescanning history: after a compaction the scan range becomes ambiguous, and
        // images from earlier turns must not drag a text-only turn onto a vision model.
        var turnHasImagesFlag = images is { Count: > 0 };
        // Declared outside the try so the terminal WorkflowEvent below the catch blocks
        // can always publish the step number, even when a catch path is taken.
        var step = 0;

        // Strategy-repair tracking: consecutive failures of the same tool with the same
        // normalized arguments usually mean the model is stuck retrying a dead end.
        var failureSignature = "";
        var consecutiveFailures = 0;
        // Same-signature success streak: identical repeated reads/globs usually mean the
        // model is spinning. Reset by a different call, a failure, or any mutation.
        var successSignature = "";
        var successStreak = 0;

        // Per-turn execution ledger + compensation stack (retrospective, not a
        // prospective plan): every tool execution appends a step; file tools register
        // their pre-turn content for user-confirmed undo after the turn ends.
        var turnScope = new TurnExecutionScope(session.Header.Id, workspace.Root);

        // Per-turn plan state: update_plan writes here (via ToolContext.PlanTracker) and
        // the guard below turns plan drift into targeted corrective notices.
        var turnPlan = new TurnPlan();
        var planGuard = new PlanNudgeGuard();

        // Self-recovery tracking: total tool calls feed the no-progress guard; the flags
        // keep every one-shot corrective notice from firing twice in one turn.
        var toolCallCount = 0;
        var strategyPivotNudged = false;
        var noProgressNudged = false;
        var redundantCallNudged = false;
        var answerCompletenessNudged = false;
        var readOnlyMode = AgentModeExtensions.Parse(workspace.Config?.Mode ?? agentConfig.Mode).IsReadOnly();

        // Declared before HandleToolResult so the nudge guards below can defer to it:
        // once the step-budget wrap-up takes over, every corrective notice competes with
        // the final summary for the model's remaining output budget.
        var wrapUpRequested = false;

        // H-D 回合级失败工具隔离：同签名失败达到换策略阈值后，该工具在本回合剩余时间内
        // 被隔离（ExecuteToolAsync 直接拒绝执行）。策略提示是软的、模型可无视；隔离是
        // 硬动作，把"摆烂重试 20 次同一命令"的最坏消耗从 MaxSteps 全烧压到阈值即刹车。
        var quarantinedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Planning hint: nudge complex-looking inputs toward a declared plan before the
        // first model step. Conservative heuristic (length + sequencing markers), only
        // in edit/auto modes, once per turn — simple tasks proceed untouched.
        if (!readOnlyMode && AgentHeuristics.LooksLikeMultiStepTask(userInput))
        {
            sessionStore.Append(session, ChatMessage.User(
                ">>> [planning hint] 这个任务看起来包含多个步骤或目标。建议先调用 update_plan 把工作拆解为 2-5 个具体的里程碑步骤，再逐步执行并保持状态同步；如果实际很简单，直接开始即可，不必先计划。"));
            events.Publish(new StatusEvent("Planning hint injected"));
        }

        void HandleToolResult(ToolCallRequest call, ToolExecution execution)
        {
            mutated |= execution.ToolMutated;
            turnHasImagesFlag |= execution.Message.Images is { Count: > 0 };
            toolCallCount++;
            sessionStore.Append(session, execution.Message);
            if (execution.Message.ToolSuccess)
            {
                consecutiveFailures = 0;
                failureSignature = "";
                var successCall = $"{call.Name}|{ToolArguments.Signature(call.ArgumentsJson)}";
                if (successCall == successSignature) successStreak++;
                else
                {
                    successSignature = successCall;
                    successStreak = 1;
                }
                if (execution.ToolMutated)
                {
                    // A mutation invalidates whatever the earlier identical calls observed:
                    // re-reading the same file after editing it is legitimate.
                    successSignature = "";
                    successStreak = 0;
                }
                else if (successStreak == RedundantCallThreshold && !redundantCallNudged && !wrapUpRequested)
                {
                    redundantCallNudged = true;
                    sessionStore.Append(session, ChatMessage.User(
                        $">>> [redundant call] 工具 {call.Name} 已用相同参数成功执行 {successStreak} 次，结果大概率没有变化。请勿原样重复调用：若需要新信息，请调整参数或换用其他工具；若只是确认状态，请基于已有结果继续。"));
                    events.Publish(new WarningEvent(
                        $"工具 {call.Name} 以相同参数重复成功执行 {successStreak} 次，已注入冗余调用提示。"));
                }
                return;
            }
            successSignature = "";
            successStreak = 0;
            var signature = $"{call.Name}|{ToolArguments.Signature(call.ArgumentsJson)}";
            if (signature == failureSignature) consecutiveFailures++;
            else
            {
                failureSignature = signature;
                consecutiveFailures = 1;
            }
            if (consecutiveFailures == RepeatedFailureNudgeThreshold)
            {
                sessionStore.Append(session, ChatMessage.User(
                    $">>> [repeated tool failure] 工具 {call.Name} 已用相同参数连续失败 {consecutiveFailures} 次。请停止原样重试：先用读取类工具确认文件与环境的当前状态，再调整参数或换一种方法完成任务。"));
                events.Publish(new WarningEvent(
                    $"工具 {call.Name} 连续失败 {consecutiveFailures} 次，已注入策略纠偏提示。"));
            }
            if (consecutiveFailures == StrategyPivotFailureThreshold && !strategyPivotNudged)
            {
                strategyPivotNudged = true;
                sessionStore.Append(session, ChatMessage.User(
                    $">>> [strategy pivot] 工具 {call.Name} 已用相同参数连续失败 {consecutiveFailures} 次，当前方法大概率不可行。请彻底改变思路：换用其他工具或方式完成这一步、先阅读相关文件与文档弄清上下文、或重新拆解问题，而不是继续微调同样的参数。"));
                events.Publish(new WarningEvent(
                    $"工具 {call.Name} 连续失败 {consecutiveFailures} 次，已注入换策略提示。"));
                // H-D 硬动作：提示之外把该工具加入回合级隔离名单——后续调用在执行前
                // 直接被拒，模型无法用"再试一次同样的命令"烧完整个步数预算。
                if (quarantinedTools.Add(call.Name))
                    events.Publish(new WarningEvent(
                        $"工具 {call.Name} 已加入本回合隔离名单：同签名连续失败 {consecutiveFailures} 次后停止执行该工具，请换用其他工具或思路。"));
            }
        }

        try
        {
            // Repair iterations are extra model turns appended after a failed build
            // verification; they must not consume the main task's MaxSteps budget.
            // Reserve one extra step per allowed repair so long multi-step tasks are
            // not cut short by their own repair loop.
            var maxSteps = agentConfig.MaxSteps + Math.Clamp(agentConfig.MaxRepairAttempts, 0, 128);
            var compactedCount = 0;
            var lastCompactionStep = 0;
            var truncatedSteps = 0;
            var reachedMaxSteps = false;
            var emptyAnswerNudged = false;
            // Composing the system prompt reads memory / AGENTS.md / prompt files from disk.
            // Its inputs cannot change between steps of one turn (same model, tools and flags),
            // so compose once and reuse: this removes per-step file I/O and keeps the provider
            // prompt prefix byte-identical for cache hits.
            string? cachedPromptKey = null;
            string cachedSystemPrompt = "";
            // H-E: 工具 schema 的 token 估算按 promptKey 缓存——它是每步请求的固定
            // 大头（tool manifest alone is thousands of tokens），从窗口预算中扣掉。
            int cachedToolSchemaTokens = 0;
            events.Publish(new WorkflowEvent(0, "start", "开始任务"));
            while (true)
            {
                step++;
                if (step > maxSteps)
                {
                    // Budget exhausted. Ending the turn here would leave the user without
                    // any conclusion, so grant exactly one wrap-up turn: the nudge goes in
                    // at step == maxSteps + 1, the model summarizes at maxSteps + 2, and
                    // anything beyond that ends the turn.
                    if (!wrapUpRequested)
                    {
                        wrapUpRequested = true;
                        sessionStore.Append(session, ChatMessage.User(
                            ">>> [step budget] 已达到本回合最大步数上限。请立即停止调用工具，直接总结当前进展、已完成的改动与未完成事项，给出最终回答。"
                            + AgentNotices.RenderLedgerSummary(turnScope.Steps)));
                        events.Publish(new StatusEvent("Step budget reached; wrapping up"));
                        continue;
                    }
                    if (step > maxSteps + 2)
                    {
                        reachedMaxSteps = true;
                        break;
                    }
                }

                // 回合预算闸门：与步数闸门一样先给一步收尾，让用户拿到进展总结而不是半截话。
                // 收尾步已发出的工具调用仍会执行完（历史里不能留悬空 tool_use），所以最坏多花
                // 一批工具的成本，随后立即刹车。
                var budgetBreach = TurnBudgetBreach(
                    agentConfig, turnInputTokens + turnOutputTokens, turnCost, turnClock.Elapsed);
                if (budgetBreach is not null)
                {
                    if (!budgetWrapUpRequested)
                    {
                        budgetWrapUpRequested = true;
                        budgetWrapUpStep = step;
                        sessionStore.Append(session, ChatMessage.User(
                            $">>> [turn budget] {budgetBreach}。请立即停止调用工具，直接总结当前进展、已完成的改动与未完成事项，给出最终回答。"
                            + AgentNotices.RenderLedgerSummary(turnScope.Steps)));
                        events.Publish(new WarningEvent(
                            $"{budgetBreach}，本回合进入收尾。可在 ~/.haoyue/config.json 调大 agent.turnTokenBudget / turnCostBudget / turnWallClockSeconds，或把任务拆成多回合。"));
                        events.Publish(new StatusEvent("Turn budget reached; wrapping up"));
                        continue;
                    }
                    // 收尾提示注入后只再给两步：仍不肯收尾就说明模型已经跑偏，继续等只会把预算烧得更深。
                    if (step > budgetWrapUpStep + 2)
                    {
                        turnFinishReason = TurnFinishReason.Budget;
                        events.Publish(new WarningEvent(
                            $"{budgetBreach}，收尾步仍未给出结论，本回合已强制停止。上方内容为此前的中间输出。"));
                        break;
                    }
                }

                ct.ThrowIfCancellationRequested();
                var preSteering = AppendSteering(session, steering);
                turnHasImagesFlag |= preSteering.Any(message => message.Images is { Count: > 0 });
                PublishSteering(preSteering);
                if (steering?.IsAbortRequested == true)
                {
                    // M-B: 用户通过 steering 显式请求中止——不再发起新的模型步骤，
                    // 回合以"被用户中止"收尾（finally 里落中断痕迹与撤销台账）。
                    cancelled = true;
                    events.Publish(new StatusEvent("Steering abort requested; turn stopped"));
                    break;
                }

                // Vision relevance: only what this turn produced decides whether vision is
                // needed — the current input, mid-turn steering (possibly with a screenshot)
                // and tool results such as capture_screen. Images from earlier turns must
                // not drag a text-only turn onto a vision model or re-upload on follow-ups.
                var turnHasImages = turnHasImagesFlag;
                var model = turnHasImages ? ResolveVisionModel(workspace) : providerManager.ResolveActive(workspace.Config);
                var requiresVision = turnHasImages && model.Model.Capabilities.Vision;
                var effectiveNetworkEnabled = session.Header.NetworkEnabled && agentConfig.NetworkEnabled;
                var tools = ActiveTools(workspace, model, effectiveNetworkEnabled, skillTriggerContext);
                var expertId = session.Header.ExpertId;
                var promptKey = string.Join('|',
                    model.Provider.Id, model.Model.Id, effectiveNetworkEnabled, tools.Count,
                    string.Join(",", tools.Select(t => t.Name)),
                    workspace.Config?.Mode ?? agentConfig.Mode,
                    workspace.Config?.Personality ?? agentConfig.Personality,
                    OutputLanguage.Resolve(workspace.Config, agentConfig),
                    ShouldVerify(workspace, agentConfig),
                    expertId ?? "");
                if (promptKey != cachedPromptKey)
                {
                    cachedSystemPrompt = await ComposeSystemPromptAsync(
                        workspace, model, tools, effectiveNetworkEnabled, skillTriggerContext, expertId, ct).ConfigureAwait(false);
                    cachedToolSchemaTokens = ContextPlanner.EstimateToolTokens(
                        tools.Select(t => new ToolDefinition(t.Name, t.Description, t.ParameterSchema)).ToList());
                    cachedPromptKey = promptKey;
                }
                var systemPrompt = cachedSystemPrompt;
                // Window ceiling calibration (M7): a mis-declared ContextWindow previously
                // overflowed forever. When a context-length failure was recorded for this
                // model, the calibrated (smaller) ceiling drives the window budget.
                var windowCeiling = ContextWindowCalibrator.GetCeiling(model.Provider.Id, model.Model.Id);
                // H-E: per-model 实测校准系数（ProviderManager 每请求后回写 EWMA）。
                var calibration = TokenCalibration(model);
                // M-C: 视觉回合只保留最近的图（去重 + 总量/token 预算），不再把全部
                // 历史截图每步重传——延迟/费用随会话线性增长的核心缺口。
                var source = requiresVision
                    ? PruneVisionHistory(session.Messages, VisionImageTokenBudget(model))
                    : WithoutImages(session.Messages);
                var history = ContextPlanner.FitToWindow(
                    source, model.Model, systemPrompt, windowCeiling, calibration, cachedToolSchemaTokens);
                // Context compaction: when plain trimming would have to drop history, first
                // summarize the old part so long turns keep their memory and can finish.
                // A failed compaction never aborts the turn — it falls back to the trim.
                // Up to 3 passes per turn with a cool-down gap: very long tool-heavy turns
                // used to hit the window a second time and degrade to hard trimming.
                if (agentConfig.EnableContextCompaction
                    && compactedCount < 3
                    && (compactedCount == 0 || step - lastCompactionStep >= 6)
                    && history.Count + 4 <= source.Count)
                {
                    PublishWorkflow(step, "compact", "压缩记忆");
                    await CompactContextAsync(session, workspace, model, source, systemPrompt, ct, windowCeiling, calibration, cachedToolSchemaTokens).ConfigureAwait(false);
                    compactedCount++;
                    lastCompactionStep = step;
                    source = requiresVision
                        ? PruneVisionHistory(session.Messages, VisionImageTokenBudget(model))
                        : WithoutImages(session.Messages);
                    history = ContextPlanner.FitToWindow(
                        source, model.Model, systemPrompt, windowCeiling, calibration, cachedToolSchemaTokens);
                }

                events.Publish(new StatusEvent("Thinking"));
                PublishWorkflow(step, "think", "思考");
                if (step == 1 && userMessage.Images is { Count: > 0 })
                    foreach (var image in userMessage.Images)
                        events.Publish(new ImageViewedEvent(image.Id, image.Name, image.MediaType));
                events.Publish(new ModelInvocationStartedEvent(model.Provider.Id, model.Model.Id, step));

                LlmCompletion completion;
                try
                {
                    completion = await StreamOnceAsync(
                        model, workspace, systemPrompt, history, tools,
                        reasoningLevel ?? agentConfig.ReasoningLevel, requiresVision, ct,
                        calibration, cachedToolSchemaTokens).ConfigureAwait(false);
                }
                catch (LlmException ex) when (ContextWindowCalibrator.IsContextOverflow(ex.Message))
                {
                    // Feed the observed request size back as a calibrated ceiling for this
                    // model so the next turn fits before sending (M7 closed loop).
                    var estimated = ContextPlanner.EstimateTokens(systemPrompt, calibration)
                                    + history.Sum(m => ContextPlanner.EstimateTokens(m, calibration))
                                    + cachedToolSchemaTokens
                                    + model.Model.MaxOutput;
                    ContextWindowCalibrator.RecordOverflow(model.Provider.Id, model.Model.Id, estimated);
                    events.Publish(new WarningEvent(
                        $"模型 {model.Model.Id} 的上下文窗口声明（{model.Model.ContextWindow}）与实际不符，已按实测上限 {estimated} tokens 校正后续请求。"));
                    throw;
                }

                var assistant = new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Text = completion.Text,
                    Thinking = completion.Thinking.Length > 0 ? completion.Thinking : null,
                    ModelRef = $"{model.Provider.Id}/{model.Model.Id}",
                    FinishReason = TurnFinishReason.Normalize(completion.FinishReason),
                    ToolCalls = completion.ToolCalls.Count > 0 ? completion.ToolCalls : null,
                    ViewedImages = step == 1
                        ? userMessage.Images?.Select(image => new ChatImageReference(image.Id, image.Name)).ToList()
                        : null,
                };
                sessionStore.Append(session, assistant);
                // 逐步累计本回合开销：token 取 TotalInputTokens（含缓存命中的完整 prompt 规模），
                // 费用与 usage 账本同口径（ProviderManager.ComputeCost）。
                var stepUsage = completion.Usage;
                turnInputTokens += stepUsage.TotalInputTokens;
                turnOutputTokens += stepUsage.OutputTokens;
                turnCost += ProviderManager.ComputeCost(model.Model, stepUsage);
                if (completion.Text.Length > 0)
                {
                    finalText = completion.Text;
                    events.Publish(new AssistantMessageCompletedEvent(completion.Text));
                }

                // Guidance can arrive while the model is streaming. Append it after the
                // current completion and continue with a fresh model step; the in-flight
                // request is never cancelled or rewritten.
                var guidance = AppendSteering(session, steering);
                turnHasImagesFlag |= guidance.Any(message => message.Images is { Count: > 0 });

                // StreamOnceAsync preserves partial text on cancellation. Stop the turn after
                // persisting that text instead of reporting the cancelled request as completed.
                ct.ThrowIfCancellationRequested();

                if (completion.ToolCalls.Count > 0)
                {
                    var planToolCalls = completion.ToolCalls.Count(c => c.Name == "update_plan");
                    var readOnlyBatch = new List<ToolCallRequest>();
                    foreach (var call in completion.ToolCalls)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (steering?.IsAbortRequested == true)
                        {
                            // M-B: 在途批次中 pending 的调用（含 mutating）不再执行——
                            // 用户喊"停"之后，已经排队但还没开始的写入/命令必须踩刹车。
                            cancelled = true;
                            events.Publish(new WarningEvent("已收到 steering 中止请求，本批次剩余工具调用不再执行。"));
                            break;
                        }
                        var resolved = toolRegistry.Resolve(call.Name);
                        if (resolved is not null && !resolved.Mutating)
                        {
                            readOnlyBatch.Add(call);
                        }
                        else
                        {
                            if (readOnlyBatch.Count > 0)
                            {
                                foreach (var readOnly in readOnlyBatch) PublishWorkflow(step, "tool", readOnly.Name);
                                var batchResults = await Task.WhenAll(readOnlyBatch.Select(c => ExecuteToolAsync(c, workspace, model, turnScope, turnPlan, ct, quarantinedTools))).ConfigureAwait(false);
                                for (var i = 0; i < batchResults.Length; i++)
                                    HandleToolResult(readOnlyBatch[i], batchResults[i]);
                                readOnlyBatch.Clear();
                            }

                            PublishWorkflow(step, "tool", call.Name);
                            HandleToolResult(call, await ExecuteToolAsync(call, workspace, model, turnScope, turnPlan, ct, quarantinedTools).ConfigureAwait(false));
                        }
                    }

                    if (cancelled) break; // steering abort：跳过批次尾部与后续收尾逻辑

                    if (readOnlyBatch.Count > 0)
                    {
                        foreach (var readOnly in readOnlyBatch) PublishWorkflow(step, "tool", readOnly.Name);
                        var batchResults = await Task.WhenAll(readOnlyBatch.Select(c => ExecuteToolAsync(c, workspace, model, turnScope, turnPlan, ct, quarantinedTools))).ConfigureAwait(false);
                        for (var i = 0; i < batchResults.Length; i++)
                            HandleToolResult(readOnlyBatch[i], batchResults[i]);
                    }

                    // Plan discipline: compare the executed work against the declared plan
                    // and nudge when they drift (finished-but-still-working / stale statuses).
                    // Deferred during wrap-up: the summary must not share budget with nudges.
                    var planNudge = planGuard.Evaluate(turnPlan, completion.ToolCalls.Count, planToolCalls);
                    if (planNudge is not null && !wrapUpRequested)
                    {
                        sessionStore.Append(session, ChatMessage.User(planNudge.Notice));
                        events.Publish(new WarningEvent(planNudge.Warning));
                    }

                    // No-progress guard: lots of tool work with zero file changes and no plan
                    // usually means aimless exploration. Skipped in read-only modes, where
                    // pure research is the job, and after the budget wrap-up took over.
                    if (!noProgressNudged && toolCallCount >= NoProgressToolCallThreshold
                        && !mutated && !turnPlan.HasPlan && !readOnlyMode && !wrapUpRequested)
                    {
                        noProgressNudged = true;
                        sessionStore.Append(session, ChatMessage.User(
                            $">>> [no progress] 本回合已调用 {toolCallCount} 次工具但没有产生任何文件改动，也没有制定计划。请自我检查：用 update_plan 把剩余工作拆解为明确步骤；若任务只需要调研，直接总结发现并回答用户；若当前方法行不通，换一种思路。"));
                        events.Publish(new WarningEvent(
                            $"已连续调用 {toolCallCount} 次工具但无任何文件改动，已注入进度自检提示。"));
                    }

                    PublishSteering(guidance);
                    continue;
                }

                if (guidance.Count > 0)
                {
                    PublishSteering(guidance);
                    continue;
                }

                // The model ran out of output tokens mid-answer (finish_reason length/max_tokens).
                // That is not "done" — keep the turn going so long generations and long thinking
                // phases can finish instead of silently ending with partial (or empty) output.
                if (IsOutputTruncated(completion.FinishReason))
                {
                    truncatedSteps++;
                    if (truncatedSteps > agentConfig.MaxOutputContinuations)
                    {
                        // 续写次数用尽仍被长度上限打断：这就是半截回答，不能报「任务完成」。
                        turnFinishReason = TurnFinishReason.Normalize(completion.FinishReason);
                        events.Publish(new WarningEvent(
                            $"输出连续 {agentConfig.MaxOutputContinuations} 次达到长度上限，回合已停止。可在模型配置中调大 maxOutput 或拆分任务。"));
                        break;
                    }
                    if (completion.Text.Length == 0)
                    {
                        var continuationNote = ChatMessage.User(
                            ">>> [output truncated] 上一轮输出因达到单次长度上限被截断，且没有产出任何内容。请直接给出最终回答或调用工具开始执行，不要再进行超长思考。");
                        sessionStore.Append(session, continuationNote);
                    }
                    events.Publish(new StatusEvent("Output truncated; continuing"));
                    PublishWorkflow(step, "think", "续写");
                    PublishSteering(guidance);
                    continue;
                }
                truncatedSteps = 0;

                // 模型明确拒答或被内容过滤拦截：文本通常是拒绝理由而不是答案。过去这一路
                // 也报「任务完成」，用户看不出请求根本没被执行。
                if (TurnFinishReason.Normalize(completion.FinishReason) == TurnFinishReason.Refusal)
                {
                    turnFinishReason = TurnFinishReason.Refusal;
                    events.Publish(new WarningEvent(
                        "模型拒绝了本次请求（finish_reason=refusal）。上方文本若存在，是模型给出的拒绝理由，不代表任务已完成。"));
                    break;
                }

                // A completion with no text, no tool calls and no truncation is an empty
                // answer — often a decoding hiccup. Nudge once instead of ending the turn
                // with nothing; the second empty answer ends the turn as before.
                if (completion.Text.Length == 0 && !emptyAnswerNudged)
                {
                    emptyAnswerNudged = true;
                    sessionStore.Append(session, ChatMessage.User(
                        ">>> [empty answer] 上一轮没有产出任何文本内容。请直接给出面向用户的最终回答；如任务未完成，请说明当前进展与下一步。"));
                    events.Publish(new WarningEvent("模型返回了空回答，已请求重新生成。"));
                    PublishSteering(guidance);
                    continue;
                }
                if (completion.Text.Length == 0 && emptyAnswerNudged)
                {
                    // Second empty answer: end explicitly. Returning the previous step's
                    // text as the "final answer" misleads users into reading stale middle
                    // output as a conclusion. 这也是「没答上」而不是「答完了」：走 error 终态，
                    // 界面给出可重试的失败卡片，而不是一个空白气泡。
                    turnFinishReason = TurnFinishReason.Empty;
                    error = "模型连续两次返回空回答，本回合没有产出任何结论，请直接重试。";
                    events.Publish(new WarningEvent(
                        "模型连续两次返回空回答，回合已结束；上方内容为此前的中间输出，不代表最终结论。可直接重试本任务。"));
                    finalText = "";
                    break;
                }

                // Model believes it is done. If it changed files, prove the project still builds.
                // Skipped once wrap-up took over: repair turns are step-budgeted too, so a
                // verification failure injected at maxSteps+1 could never run its repair —
                // a "dead" corrective notice the model is told to act on but cannot.
                if (mutated && ShouldVerify(workspace, agentConfig) && repairAttempts < agentConfig.MaxRepairAttempts
                    && !wrapUpRequested)
                {
                    PublishWorkflow(step, "verify", "构建验证");
                    var repairMessage = await RunVerificationAsync(workspace, repairAttempts + 1, ct).ConfigureAwait(false);
                    if (repairMessage is not null)
                    {
                        repairAttempts++;
                        mutated = false; // reset; the repair edits set it again
                        sessionStore.Append(session, ChatMessage.User(repairMessage));
                        continue;
                    }
                }

                // Answer completeness: a turn that changed files should tell the user what
                // changed. If the final answer names none of the touched files, ask once.
                if (mutated && !answerCompletenessNudged && completion.Text.Length > 0
                    && !wrapUpRequested
                    && !AgentHeuristics.MentionsAnyChange(completion.Text, turnScope.Changes))
                {
                    answerCompletenessNudged = true;
                    var names = string.Join("、", turnScope.Changes.Take(5).Select(c => Path.GetFileName(c.AbsolutePath)));
                    sessionStore.Append(session, ChatMessage.User(
                        $">>> [answer completeness] 本回合修改了 {turnScope.Changes.Count} 个文件（{names}），但最终回答没有提到这些改动。请补充：改动了哪些文件、各自做了什么。"));
                    events.Publish(new WarningEvent("最终回答未提及已修改的文件，已注入回答完整性提示。"));
                    continue;
                }
                var lateGuidance = AppendSteering(session, steering);
                if (lateGuidance.Count > 0)
                {
                    PublishSteering(lateGuidance);
                    continue;
                }
                if (steering is not null && !steering.TryCompleteIfEmpty()) continue;
                // 预算收尾已接管：这一步给出的是「烧完配额后的进展总结」，不能报「任务完成」。
                turnFinishReason = budgetWrapUpRequested
                    ? TurnFinishReason.Budget
                    : TurnFinishReason.Normalize(completion.FinishReason);
                break;
            }
            if (reachedMaxSteps)
            {
                // 收尾回合也没能给出结论：这是「步数烧光」，不是完成。
                turnFinishReason = TurnFinishReason.MaxSteps;
                events.Publish(new WarningEvent(
                    $"已达到最大步数 {maxSteps}，任务可能尚未完成。可在 ~/.haoyue/config.json 中调大 agent.maxSteps。"));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (LlmException ex)
        {
            error = ex.Message;
            events.Publish(new ErrorEvent("LLM request failed", ex.Message));
        }
        catch (Exception ex)
        {
            // 意外异常（存储/IO 等）也要走受控失败路径：否则 TurnCompletedEvent 与
            // workflow 收尾事件都不会发布，客户端的工作流视图停留在最后一步。
            // error 面向用户只给消息；堆栈放进 Detail，客户端「技术详情」可直接定位。
            error = $"Unexpected error: {ex.Message}";
            events.Publish(new ErrorEvent(
                "Agent turn failed",
                ex.StackTrace is { Length: > 0 } stack ? $"{error}\n{ex.GetType().FullName}: {stack}" : error));
        }

        finally
        {
            // Terminal WorkflowEvent MUST live on the common path after all catches: the
            // previous placement inside the try made the "error" branch unreachable, so
            // failed/cancelled turns never closed the client's workflow view. kind reflects
            // the real outcome instead of always reporting "任务完成". The try/catch block
            // above became try/catch/finally when the turn scope arrived — same ordering,
            // one fewer place to duplicate cleanup.
            // 终态优先级：取消 > 具体未完成原因 > 异常 > 正常完成。error 存在但循环里没给出
            // 更具体的原因时才记为 error——空回答、截断、拒答自带的原因更有解释力。
            turnOutcome = cancelled
                ? TurnFinishReason.Cancelled
                : error is not null && turnFinishReason == TurnFinishReason.Stop
                    ? TurnFinishReason.Error
                    : turnFinishReason;
            events.Publish(new WorkflowEvent(
                step,
                turnOutcome is TurnFinishReason.Cancelled or TurnFinishReason.Error
                    ? "error"
                    : TurnFinishReason.IsIncomplete(turnOutcome) ? "incomplete" : "done",
                turnOutcome switch
                {
                    TurnFinishReason.Cancelled => "任务已取消",
                    TurnFinishReason.Error => "任务失败",
                    var reason when TurnFinishReason.IsIncomplete(reason) => "任务未完成",
                    _ => "任务完成",
                },
                TurnFinishReason.ChineseLabel(turnOutcome)));

            PublishSteering(AppendSteering(session, steering));
            steering?.Complete();

            // Interrupted turns leave their executed trace in the session so the next
            // "continue" turn knows what already happened — no silent half-state.
            if (turnScope.Steps.Count > 0 && (error is not null || cancelled))
            {
                sessionStore.Append(session, ChatMessage.User(
                    $">>> [turn interrupted] 本回合{(cancelled ? "被用户中断" : "因错误终止")}。中断前已执行的步骤：\n{turnScope.RenderTrace()}\n继续任务时请基于以上进度，不要重复已完成的工作。"));
            }

            // Deposit the undo ledger and tell clients what can be reverted. A failed or
            // cancelled turn gets an explicit warning; a successful turn carries the file
            // list on the completion event so the UI can offer undo anyway.
            var failedTurn = error is not null || cancelled;
            var ledger = turnScope.BuildLedger(failedTurn);
            if (ledger is not null)
            {
                _undoRegistry?.Deposit(ledger);
                if (failedTurn)
                {
                    events.Publish(new WarningEvent(
                        $"本回合{(cancelled ? "被取消" : "失败")}，但已有 {ledger.Changes.Count} 个文件被修改。可通过撤销操作恢复到修改前的内容。"));
                }
            }

            events.Publish(new TurnCompletedEvent(
                session.Header.Id, cancelled, error,
                ledger is null ? null : ledger.Changes.Select(c => ToRelativePath(c.AbsolutePath, workspace.Root)).ToList(),
                turnOutcome));

            // H-C 回合结束触发式沉淀提示：有实际执行步骤的成功回合给一条客户端可见的
            // 轻提示（不进模型上下文、零 token 成本）——回合内自觉写入优先，后台
            // ConsolidationRunner 的定时扫描兜底，双通道补齐「经历→记忆」。
            if (error is null && !cancelled && turnScope.Steps.Count > 0)
            {
                events.Publish(new StatusEvent(
                    "本回合有实际改动。如有值得长期记住的约定或决定，告诉我即可沉淀；后台记忆固化也会定期扫描近期会话。"));
            }
        }
        return new AgentTurnResult(finalText, cancelled, error, turnOutcome);
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
