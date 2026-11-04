using System.Diagnostics;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Daemon;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Agents;

public sealed partial class Agent
{
    /// <summary>
    /// 单回合的可变状态。主循环、工具结果处理、答案判定与收尾共用同一份状态，
    /// 避免把十几个互相影响的局部变量逐层透传；生命周期严格等于一次 RunTurnAsync。
    /// </summary>
    private sealed class TurnState
    {
        public required AgentSession Session { get; init; }
        public required WorkspaceInfo Workspace { get; init; }
        public required ChatMessage UserMessage { get; init; }
        public required AgentSteeringQueue? Steering { get; init; }
        public required TurnExecutionScope Scope { get; init; }
        public required TurnExecutionScope? InheritedScope { get; init; }
        public required TurnPlan Plan { get; init; }
        public required PlanNudgeGuard PlanGuard { get; init; }
        public required AgentConfig Config { get; init; }
        public required bool ReadOnlyMode { get; init; }
        public required CancellationToken Ct { get; init; }
        public required string? SkillTriggerContext { get; init; }

        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public bool Mutated;
        public int RepairAttempts;
        public string FinalText = "";
        public string? Error;
        public bool Cancelled;
        public string FinishReason = TurnFinishReason.Stop;
        public string Outcome = TurnFinishReason.Stop;
        public int Step;
        public long InputTokens;
        public long OutputTokens;
        public decimal Cost;
        public bool BudgetWrapUpRequested;
        public int BudgetWrapUpStep;
        public bool TurnHasImages;

        /// <summary>Consecutive failures of the same tool + normalized arguments.</summary>
        public string FailureSignature = "";
        public int ConsecutiveFailures;
        /// <summary>Same-signature success streak (identical repeated reads usually mean spinning).</summary>
        public string SuccessSignature = "";
        public int SuccessStreak;

        public int ToolCallCount;
        public bool StrategyPivotNudged;
        public bool NoProgressNudged;
        public bool RedundantCallNudged;
        public bool AnswerCompletenessNudged;
        public bool WrapUpRequested;
        public readonly HashSet<string> QuarantinedCalls = new(StringComparer.OrdinalIgnoreCase);

        public int MaxSteps;
        public int CompactedCount;
        public int LastCompactionStep;
        public int TruncatedSteps;
        public bool ReachedMaxSteps;
        public bool EmptyAnswerNudged;
        public string? CachedPromptKey;
        public string CachedSystemPrompt = "";
        public int CachedToolSchemaTokens;
    }

    private readonly record struct StepStream(ModelInfo Model, LlmCompletion Completion);

    /// <summary>
    /// Tracks per-tool-result signals (mutation, image production, repeated/identical calls)
    /// and injects one-shot corrective notices. All notices are deferred during wrap-up so
    /// the final summary keeps the remaining output budget.
    /// </summary>
    private void HandleToolResult(TurnState s, ToolCallRequest call, ToolExecution execution)
    {
        s.Mutated |= execution.ToolMutated;
        s.TurnHasImages |= execution.Message.Images is { Count: > 0 };
        s.ToolCallCount++;
        sessionStore.Append(s.Session, execution.Message);
        if (execution.Message.ToolSuccess)
        {
            s.ConsecutiveFailures = 0;
            s.FailureSignature = "";
            var successCall = $"{call.Name}|{ToolArguments.Signature(call.ArgumentsJson)}";
            if (successCall == s.SuccessSignature) s.SuccessStreak++;
            else
            {
                s.SuccessSignature = successCall;
                s.SuccessStreak = 1;
            }
            if (execution.ToolMutated)
            {
                // A mutation invalidates whatever the earlier identical calls observed:
                // re-reading the same file after editing it is legitimate.
                s.SuccessSignature = "";
                s.SuccessStreak = 0;
            }
            else if (s.SuccessStreak == RedundantCallThreshold && !s.RedundantCallNudged && !s.WrapUpRequested)
            {
                s.RedundantCallNudged = true;
                sessionStore.Append(s.Session, ChatMessage.User(
                    $">>> [redundant call] 工具 {call.Name} 已用相同参数成功执行 {s.SuccessStreak} 次，结果大概率没有变化。请勿原样重复调用：若需要新信息，请调整参数或换用其他工具；若只是确认状态，请基于已有结果继续。"));
                events.Publish(new WarningEvent(
                    $"工具 {call.Name} 以相同参数重复成功执行 {s.SuccessStreak} 次，已注入冗余调用提示。"));
            }
            return;
        }
        s.SuccessSignature = "";
        s.SuccessStreak = 0;
        var signature = $"{call.Name}|{ToolArguments.Signature(call.ArgumentsJson)}";
        if (signature == s.FailureSignature) s.ConsecutiveFailures++;
        else
        {
            s.FailureSignature = signature;
            s.ConsecutiveFailures = 1;
        }
        if (s.ConsecutiveFailures == RepeatedFailureNudgeThreshold)
        {
            sessionStore.Append(s.Session, ChatMessage.User(
                $">>> [repeated tool failure] 工具 {call.Name} 已用相同参数连续失败 {s.ConsecutiveFailures} 次。请停止原样重试：先用读取类工具确认文件与环境的当前状态，再调整参数或换一种方法完成任务。"));
            events.Publish(new WarningEvent(
                $"工具 {call.Name} 连续失败 {s.ConsecutiveFailures} 次，已注入策略纠偏提示。"));
        }
        if (s.ConsecutiveFailures == StrategyPivotFailureThreshold && !s.StrategyPivotNudged)
        {
            s.StrategyPivotNudged = true;
            sessionStore.Append(s.Session, ChatMessage.User(
                $">>> [strategy pivot] 工具 {call.Name} 已用相同参数连续失败 {s.ConsecutiveFailures} 次，当前方法大概率不可行。请彻底改变思路：换用其他工具或方式完成这一步、先阅读相关文件与文档弄清上下文、或重新拆解问题，而不是继续微调同样的参数。"));
            events.Publish(new WarningEvent(
                $"工具 {call.Name} 连续失败 {s.ConsecutiveFailures} 次，已注入换策略提示。"));
            // H-D 硬动作：提示之外把该调用签名加入回合级隔离名单——后续同参调用
            // 在执行前直接被拒，模型无法用"再试一次同样的命令"烧完整个步数预算。
            if (s.QuarantinedCalls.Add(signature))
                events.Publish(new WarningEvent(
                    $"工具 {call.Name} 的这次调用（工具 + 相同参数）已加入本回合隔离名单："
                    + $"同签名连续失败 {s.ConsecutiveFailures} 次后停止执行，换参数或换工具即可继续。"));
        }
    }

    /// <summary>
    /// 执行一批工具调用，只读调用先并行成批、变更调用按原顺序逐个执行。
    /// 返回 true 表示用户通过 steering 中止，调用方应立即结束回合。
    /// </summary>
    private async Task<bool> ExecuteToolCallsAsync(
        TurnState s, ModelInfo model, IReadOnlyList<ToolCallRequest> toolCalls)
    {
        var readOnlyBatch = new List<ToolCallRequest>();
        foreach (var call in toolCalls)
        {
            s.Ct.ThrowIfCancellationRequested();
            if (s.Steering?.IsAbortRequested == true)
            {
                // M-B: 在途批次中 pending 的调用（含 mutating）不再执行——
                // 用户喊"停"之后，已经排队但还没开始的写入/命令必须踩刹车。
                s.Cancelled = true;
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
                    foreach (var readOnly in readOnlyBatch) PublishWorkflow(s.Step, "tool", readOnly.Name);
                    var batchResults = await ExecuteReadOnlyBatchAsync(readOnlyBatch,
                        c => ExecuteToolAsync(c, s.Workspace, model, s.Scope, s.Plan, s.Ct, s.QuarantinedCalls)).ConfigureAwait(false);
                    for (var i = 0; i < batchResults.Length; i++)
                        HandleToolResult(s, readOnlyBatch[i], batchResults[i]);
                    readOnlyBatch.Clear();
                }

                PublishWorkflow(s.Step, "tool", call.Name);
                HandleToolResult(s, call, await ExecuteToolAsync(call, s.Workspace, model, s.Scope, s.Plan, s.Ct, s.QuarantinedCalls).ConfigureAwait(false));
            }
        }

        if (s.Cancelled) return true; // steering abort：跳过批次尾部与后续收尾逻辑

        if (readOnlyBatch.Count > 0)
        {
            foreach (var readOnly in readOnlyBatch) PublishWorkflow(s.Step, "tool", readOnly.Name);
            var batchResults = await ExecuteReadOnlyBatchAsync(readOnlyBatch,
                c => ExecuteToolAsync(c, s.Workspace, model, s.Scope, s.Plan, s.Ct, s.QuarantinedCalls)).ConfigureAwait(false);
            for (var i = 0; i < batchResults.Length; i++)
                HandleToolResult(s, readOnlyBatch[i], batchResults[i]);
        }
        return false;
    }

    /// <summary>
    /// 一次模型步：解析模型与工具视图、组装/缓存系统提示、按窗口裁剪与压缩历史、
    /// 再流式请求一次。上下文溢出时回写校准上限后原样上抛（由 RunTurnAsync 统一处理）。
    /// </summary>
    private async Task<StepStream> StreamStepAsync(TurnState s, ReasoningLevel? reasoningLevel)
    {
        // Vision relevance: only what this turn produced decides whether vision is
        // needed — the current input, mid-turn steering (possibly with a screenshot)
        // and tool results such as capture_screen. Images from earlier turns must
        // not drag a text-only turn onto a vision model or re-upload on follow-ups.
        var turnHasImages = s.TurnHasImages;
        var model = turnHasImages ? ResolveVisionModel(s.Workspace) : ResolveStepModel(s.Workspace);
        var requiresVision = turnHasImages && model.Model.Capabilities.Vision;
        var effectiveNetworkEnabled = s.Session.Header.NetworkEnabled && s.Config.NetworkEnabled;
        var tools = ActiveTools(s.Workspace, model, effectiveNetworkEnabled, s.SkillTriggerContext);
        // 工具调用能力探测（P1-2.1）：Capabilities.ToolCalling 是纯用户声明，
        // 本地客户端直接忽略 request.Tools。探测明确不支持时不注入工具 schema——
        // 否则模型空谈工具且每步白烧 schema token。Unknown（探测失败）保持旧行为。
        if (tools.Count > 0
            && await providerManager.ProbeToolCallingAsync(model, s.Ct).ConfigureAwait(false)
                == ToolCallingSupport.Unsupported)
        {
            // 静默剥离工具清单：不再向对话流发布警告（用户反馈该提示每回合刷屏、
            // 无行动价值）。探测结果仍由 ProviderManager 记录，日志/诊断面可查。
            tools = Array.Empty<ITool>();
        }
        var expertId = s.Session.Header.ExpertId;
        // 技能段是本回合唯一会逐回合变化的 prompt 段，落在 PromptSlot.Skill（最后），
        // 所以把「本回合已声明技能」并入缓存键只会让 key 在 declare_skill 后改变一次，
        // 前半段前缀依旧稳定；漏掉它则声明后仍复用旧 prompt，declare_skill 完全失效。
        var declaredSkillsKey = _skills is null
            ? ""
            : string.Join(",", _skills.TurnDeclaredSkills());
        var promptKey = string.Join('|',
            model.Provider.Id, model.Model.Id, effectiveNetworkEnabled, tools.Count,
            string.Join(",", tools.Select(t => t.Name)),
            s.Workspace.Config?.Mode ?? s.Config.Mode,
            s.Workspace.Config?.Personality ?? s.Config.Personality,
            OutputLanguage.Resolve(s.Workspace.Config, s.Config),
            ShouldVerify(s.Workspace, s.Config),
            expertId ?? "",
            declaredSkillsKey);
        if (promptKey != s.CachedPromptKey)
        {
            s.CachedSystemPrompt = await ComposeSystemPromptAsync(
                s.Workspace, model, tools, effectiveNetworkEnabled, s.SkillTriggerContext, expertId, s.Ct).ConfigureAwait(false);
            s.CachedToolSchemaTokens = ContextPlanner.EstimateToolTokens(
                tools.Select(t => new ToolDefinition(t.Name, t.Description, t.ParameterSchema)).ToList());
            s.CachedPromptKey = promptKey;
        }
        var systemPrompt = s.CachedSystemPrompt;
        // Window ceiling calibration (M7): a mis-declared ContextWindow previously
        // overflowed forever. When a context-length failure was recorded for this
        // model, the calibrated (smaller) ceiling drives the window budget.
        var windowCeiling = ContextWindowCalibrator.GetCeiling(model.Provider.Id, model.Model.Id);
        // H-E: per-model 实测校准系数（ProviderManager 每请求后回写 EWMA）。
        var calibration = TokenCalibration(model);
        // M-C: 视觉回合只保留最近的图（去重 + 总量/token 预算），不再把全部
        // 历史截图每步重传——延迟/费用随会话线性增长的核心缺口。
        var source = requiresVision
            ? PruneVisionHistory(s.Session.Messages, VisionImageTokenBudget(model))
            : WithoutImages(s.Session.Messages);
        var history = ContextPlanner.FitToWindow(
            source, model.Model, systemPrompt, windowCeiling, calibration, s.CachedToolSchemaTokens);
        // Context compaction: when plain trimming would have to drop history, first
        // summarize the old part so long turns keep their memory and can finish.
        // A failed compaction never aborts the turn — it falls back to the trim.
        // Up to 3 passes per turn with a cool-down gap: very long tool-heavy turns
        // used to hit the window a second time and degrade to hard trimming.
        //
        // 主动压缩（P1-2.1）：被动触发（FitToWindow 丢历史）意味着窗口已经打满，
        // 那几步仍在硬挤，溢出后校准只对下一回合生效。投影用量（系统提示 + 历史 +
        // 工具 schema + 输出预留）达到水位上限即提前压缩，把溢出消灭在发送前。
        var projectedTokens = ContextPlanner.EstimateTokens(systemPrompt, calibration)
            + source.Sum(m => ContextPlanner.EstimateTokens(m, calibration))
            + s.CachedToolSchemaTokens
            + model.Model.MaxOutput;
        var trimWouldDrop = history.Count + 4 <= source.Count;
        var nearWatermark = windowCeiling > 0 && projectedTokens >= windowCeiling * 0.85;
        if (s.Config.EnableContextCompaction
            && s.CompactedCount < 3
            && (s.CompactedCount == 0 || s.Step - s.LastCompactionStep >= 6)
            && (trimWouldDrop || nearWatermark))
        {
            PublishWorkflow(s.Step, "compact", "压缩记忆");
            await CompactContextAsync(s.Session, s.Workspace, model, source, systemPrompt, s.Ct,
                windowCeiling, calibration, s.CachedToolSchemaTokens).ConfigureAwait(false);
            s.CompactedCount++;
            s.LastCompactionStep = s.Step;
            source = requiresVision
                ? PruneVisionHistory(s.Session.Messages, VisionImageTokenBudget(model))
                : WithoutImages(s.Session.Messages);
            history = ContextPlanner.FitToWindow(
                source, model.Model, systemPrompt, windowCeiling, calibration, s.CachedToolSchemaTokens);
        }

        events.Publish(new StatusEvent("Thinking"));
        PublishWorkflow(s.Step, "think", "思考");
        if (s.Step == 1 && s.UserMessage.Images is { Count: > 0 })
            foreach (var image in s.UserMessage.Images)
                events.Publish(new ImageViewedEvent(image.Id, image.Name, image.MediaType));
        events.Publish(new ModelInvocationStartedEvent(model.Provider.Id, model.Model.Id, s.Step));

        LlmCompletion completion;
        try
        {
            completion = await StreamOnceAsync(
                model, s.Workspace, systemPrompt, history, tools,
                reasoningLevel ?? s.Config.ReasoningLevel, requiresVision, s.Ct,
                calibration, s.CachedToolSchemaTokens).ConfigureAwait(false);
        }
        catch (LlmException ex) when (ContextWindowCalibrator.IsContextOverflow(ex.Message))
        {
            // Feed the observed request size back as a calibrated ceiling for this
            // model so the next turn fits before sending (M7 closed loop).
            var estimated = ContextPlanner.EstimateTokens(systemPrompt, calibration)
                            + history.Sum(m => ContextPlanner.EstimateTokens(m, calibration))
                            + s.CachedToolSchemaTokens
                            + model.Model.MaxOutput;
            ContextWindowCalibrator.RecordOverflow(model.Provider.Id, model.Model.Id, estimated);
            events.Publish(new WarningEvent(
                $"模型 {model.Model.Id} 的上下文窗口声明（{model.Model.ContextWindow}）与实际不符，已按实测上限 {estimated} tokens 校正后续请求。"));
            throw;
        }

        return new StepStream(model, completion);
    }

    /// <summary>
    /// 模型本步没有工具调用时的答案判定（截断续写 / 拒答 / 空回答 / 构建验证 / 回答完整性 /
    /// 迟到 guidance）。返回 true 表示继续下一轮，false 表示回合到此结束。
    /// </summary>
    private async Task<bool> TryContinueAfterAnswerAsync(
        TurnState s, LlmCompletion completion, IReadOnlyList<ChatMessage> guidance)
    {
        // The model ran out of output tokens mid-answer (finish_reason length/max_tokens).
        // That is not "done" — keep the turn going so long generations and long thinking
        // phases can finish instead of silently ending with partial (or empty) output.
        if (IsOutputTruncated(completion.FinishReason))
        {
            s.TruncatedSteps++;
            if (s.TruncatedSteps > s.Config.MaxOutputContinuations)
            {
                // 续写次数用尽仍被长度上限打断：这就是半截回答，不能报「任务完成」。
                s.FinishReason = TurnFinishReason.Normalize(completion.FinishReason);
                events.Publish(new WarningEvent(
                    $"输出连续 {s.Config.MaxOutputContinuations} 次达到长度上限，回合已停止。可在模型配置中调大 maxOutput 或拆分任务。"));
                return false;
            }
            if (completion.Text.Length == 0)
            {
                var continuationNote = ChatMessage.User(
                    ">>> [output truncated] 上一轮输出因达到单次长度上限被截断，且没有产出任何内容。请直接给出最终回答或调用工具开始执行，不要再进行超长思考。");
                sessionStore.Append(s.Session, continuationNote);
            }
            events.Publish(new StatusEvent("Output truncated; continuing"));
            PublishWorkflow(s.Step, "think", "续写");
            PublishSteering(guidance);
            return true;
        }
        s.TruncatedSteps = 0;

        // 模型明确拒答或被内容过滤拦截：文本通常是拒绝理由而不是答案。过去这一路
        // 也报「任务完成」，用户看不出请求根本没被执行。
        if (TurnFinishReason.Normalize(completion.FinishReason) == TurnFinishReason.Refusal)
        {
            s.FinishReason = TurnFinishReason.Refusal;
            events.Publish(new WarningEvent(
                "模型拒绝了本次请求（finish_reason=refusal）。上方文本若存在，是模型给出的拒绝理由，不代表任务已完成。"));
            return false;
        }

        // A completion with no text, no tool calls and no truncation is an empty
        // answer — often a decoding hiccup. Nudge once instead of ending the turn
        // with nothing; the second empty answer ends the turn as before.
        if (completion.Text.Length == 0 && !s.EmptyAnswerNudged)
        {
            s.EmptyAnswerNudged = true;
            sessionStore.Append(s.Session, ChatMessage.User(
                ">>> [empty answer] 上一轮没有产出任何文本内容。请直接给出面向用户的最终回答；如任务未完成，请说明当前进展与下一步。"));
            events.Publish(new WarningEvent("模型返回了空回答，已请求重新生成。"));
            PublishSteering(guidance);
            return true;
        }
        if (completion.Text.Length == 0 && s.EmptyAnswerNudged)
        {
            // Second empty answer: end explicitly. Returning the previous step's
            // text as the "final answer" misleads users into reading stale middle
            // output as a conclusion. 这也是「没答上」而不是「答完了」：走 error 终态，
            // 界面给出可重试的失败卡片，而不是一个空白气泡。
            s.FinishReason = TurnFinishReason.Empty;
            s.Error = "模型连续两次返回空回答，本回合没有产出任何结论，请直接重试。";
            events.Publish(new WarningEvent(
                "模型连续两次返回空回答，回合已结束；上方内容为此前的中间输出，不代表最终结论。可直接重试本任务。"));
            s.FinalText = "";
            return false;
        }

        // Model believes it is done. If it changed files, prove the project still builds.
        // Skipped once wrap-up took over: repair turns are step-budgeted too, so a
        // verification failure injected at maxSteps+1 could never run its repair —
        // a "dead" corrective notice the model is told to act on but cannot.
        if (s.Mutated && ShouldVerify(s.Workspace, s.Config) && s.RepairAttempts < s.Config.MaxRepairAttempts
            && !s.WrapUpRequested)
        {
            PublishWorkflow(s.Step, "verify", "构建验证");
            var repairMessage = await RunVerificationAsync(s.Workspace, s.RepairAttempts + 1, s.Ct).ConfigureAwait(false);
            if (repairMessage is not null)
            {
                s.RepairAttempts++;
                s.Mutated = false; // reset; the repair edits set it again
                sessionStore.Append(s.Session, ChatMessage.User(repairMessage));
                return true;
            }
        }

        // Answer completeness: a turn that changed files should tell the user what
        // changed. If the final answer names none of the touched files, ask once.
        if (s.Mutated && !s.AnswerCompletenessNudged && completion.Text.Length > 0
            && !s.WrapUpRequested
            && !AgentHeuristics.MentionsAnyChange(completion.Text, s.Scope.Changes))
        {
            s.AnswerCompletenessNudged = true;
            var names = string.Join("、", s.Scope.Changes.Take(5).Select(c => Path.GetFileName(c.AbsolutePath)));
            sessionStore.Append(s.Session, ChatMessage.User(
                $">>> [answer completeness] 本回合修改了 {s.Scope.Changes.Count} 个文件（{names}），但最终回答没有提到这些改动。请补充：改动了哪些文件、各自做了什么。"));
            events.Publish(new WarningEvent("最终回答未提及已修改的文件，已注入回答完整性提示。"));
            return true;
        }
        var lateGuidance = AppendSteering(s.Session, s.Steering);
        if (lateGuidance.Count > 0)
        {
            PublishSteering(lateGuidance);
            return true;
        }
        if (s.Steering is not null && !s.Steering.TryCompleteIfEmpty()) return true;
        // 预算收尾已接管：这一步给出的是「烧完配额后的进展总结」，不能报「任务完成」。
        s.FinishReason = s.BudgetWrapUpRequested
            ? TurnFinishReason.Budget
            : TurnFinishReason.Normalize(completion.FinishReason);
        return false;
    }

    /// <summary>主循环：步数/预算闸门 → steering → 模型步 → 工具批次或答案判定 → 直到终态。</summary>
    private async Task RunLoopAsync(TurnState s, ReasoningLevel? reasoningLevel)
    {
        // Repair iterations are extra model turns appended after a failed build
        // verification; they must not consume the main task's MaxSteps budget.
        // Reserve one extra step per allowed repair so long multi-step tasks are
        // not cut short by their own repair loop.
        s.MaxSteps = s.Config.MaxSteps + Math.Clamp(s.Config.MaxRepairAttempts, 0, 128);
        events.Publish(new WorkflowEvent(0, "start", "开始任务"));
        while (true)
        {
            s.Step++;
            if (s.Step > s.MaxSteps)
            {
                // Budget exhausted. Ending the turn here would leave the user without
                // any conclusion, so grant exactly one wrap-up turn: the nudge goes in
                // at step == maxSteps + 1, the model summarizes at maxSteps + 2, and
                // anything beyond that ends the turn.
                if (!s.WrapUpRequested)
                {
                    s.WrapUpRequested = true;
                    sessionStore.Append(s.Session, ChatMessage.User(
                        ">>> [step budget] 已达到本回合最大步数上限。请立即停止调用工具，直接总结当前进展、已完成的改动与未完成事项，给出最终回答。"
                        + AgentNotices.RenderLedgerSummary(s.Scope.Steps)));
                    events.Publish(new StatusEvent("Step budget reached; wrapping up"));
                    continue;
                }
                if (s.Step > s.MaxSteps + 2)
                {
                    s.ReachedMaxSteps = true;
                    break;
                }
            }

            // 回合预算闸门：与步数闸门一样先给一步收尾，让用户拿到进展总结而不是半截话。
            // 收尾步已发出的工具调用仍会执行完（历史里不能留悬空 tool_use），所以最坏多花
            // 一批工具的成本，随后立即刹车。
            var budgetBreach = TurnBudgetBreach(
                s.Config, s.InputTokens + s.OutputTokens, s.Cost, s.Clock.Elapsed);
            if (budgetBreach is not null)
            {
                if (!s.BudgetWrapUpRequested)
                {
                    s.BudgetWrapUpRequested = true;
                    s.BudgetWrapUpStep = s.Step;
                    sessionStore.Append(s.Session, ChatMessage.User(
                        $">>> [turn budget] {budgetBreach}。请立即停止调用工具，直接总结当前进展、已完成的改动与未完成事项，给出最终回答。"
                        + AgentNotices.RenderLedgerSummary(s.Scope.Steps)));
                    events.Publish(new WarningEvent(
                        $"{budgetBreach}，本回合进入收尾。可在 ~/.haoyue/config.json 调大 agent.turnTokenBudget / turnCostBudget / turnWallClockSeconds，或把任务拆成多回合。"));
                    events.Publish(new StatusEvent("Turn budget reached; wrapping up"));
                    continue;
                }
                // 收尾提示注入后只再给两步：仍不肯收尾就说明模型已经跑偏，继续等只会把预算烧得更深。
                if (s.Step > s.BudgetWrapUpStep + 2)
                {
                    s.FinishReason = TurnFinishReason.Budget;
                    events.Publish(new WarningEvent(
                        $"{budgetBreach}，收尾步仍未给出结论，本回合已强制停止。上方内容为此前的中间输出。"));
                    break;
                }
            }

            s.Ct.ThrowIfCancellationRequested();
            var preSteering = AppendSteering(s.Session, s.Steering);
            s.TurnHasImages |= preSteering.Any(message => message.Images is { Count: > 0 });
            PublishSteering(preSteering);
            if (s.Steering?.IsAbortRequested == true)
            {
                // M-B: 用户通过 steering 显式请求中止——不再发起新的模型步骤，
                // 回合以"被用户中止"收尾（finally 里落中断痕迹与撤销台账）。
                s.Cancelled = true;
                events.Publish(new StatusEvent("Steering abort requested; turn stopped"));
                break;
            }

            var stream = await StreamStepAsync(s, reasoningLevel).ConfigureAwait(false);
            var model = stream.Model;
            var completion = stream.Completion;

            var assistant = new ChatMessage
            {
                Role = ChatRole.Assistant,
                Text = completion.Text,
                Thinking = completion.Thinking.Length > 0 ? completion.Thinking : null,
                ModelRef = $"{model.Provider.Id}/{model.Model.Id}",
                FinishReason = TurnFinishReason.Normalize(completion.FinishReason),
                ToolCalls = completion.ToolCalls.Count > 0 ? completion.ToolCalls : null,
                ViewedImages = s.Step == 1
                    ? s.UserMessage.Images?.Select(image => new ChatImageReference(image.Id, image.Name)).ToList()
                    : null,
            };
            sessionStore.Append(s.Session, assistant);
            // 逐步累计本回合开销：token 取 TotalInputTokens（含缓存命中的完整 prompt 规模），
            // 费用与 usage 账本同口径（ProviderManager.ComputeCost）。
            var stepUsage = completion.Usage;
            s.InputTokens += stepUsage.TotalInputTokens;
            s.OutputTokens += stepUsage.OutputTokens;
            s.Cost += ProviderManager.ComputeCost(model.Model, stepUsage);
            // 3.1 委派子任务的用量并入父回合：随后的预算闸门立即看到这笔开销，
            // 「子代理逃逸预算」（6 路并行各烧满额度父回合不知情）从此有账可查。
            var (delegatedIn, delegatedOut, delegatedCost) = s.Scope.TakeDelegatedUsage();
            s.InputTokens += delegatedIn;
            s.OutputTokens += delegatedOut;
            s.Cost += delegatedCost;
            if (completion.Text.Length > 0)
            {
                s.FinalText = completion.Text;
                events.Publish(new AssistantMessageCompletedEvent(completion.Text));
            }

            // Guidance can arrive while the model is streaming. Append it after the
            // current completion and continue with a fresh model step; the in-flight
            // request is never cancelled or rewritten.
            var guidance = AppendSteering(s.Session, s.Steering);
            s.TurnHasImages |= guidance.Any(message => message.Images is { Count: > 0 });

            // StreamOnceAsync preserves partial text on cancellation. Stop the turn after
            // persisting that text instead of reporting the cancelled request as completed.
            s.Ct.ThrowIfCancellationRequested();

            if (completion.ToolCalls.Count > 0)
            {
                var planToolCalls = completion.ToolCalls.Count(c => c.Name == "update_plan");
                if (await ExecuteToolCallsAsync(s, model, completion.ToolCalls).ConfigureAwait(false)) break;

                // Plan discipline: compare the executed work against the declared plan
                // and nudge when they drift (finished-but-still-working / stale statuses).
                // Deferred during wrap-up: the summary must not share budget with nudges.
                var planNudge = s.PlanGuard.Evaluate(s.Plan, completion.ToolCalls.Count, planToolCalls);
                if (planNudge is not null && !s.WrapUpRequested)
                {
                    sessionStore.Append(s.Session, ChatMessage.User(planNudge.Notice));
                    events.Publish(new WarningEvent(planNudge.Warning));
                }

                // No-progress guard: lots of tool work with zero file changes and no plan
                // usually means aimless exploration. Skipped in read-only modes, where
                // pure research is the job, and after the budget wrap-up took over.
                if (!s.NoProgressNudged && s.ToolCallCount >= NoProgressToolCallThreshold
                    && !s.Mutated && !s.Plan.HasPlan && !s.ReadOnlyMode && !s.WrapUpRequested)
                {
                    s.NoProgressNudged = true;
                    sessionStore.Append(s.Session, ChatMessage.User(
                        $">>> [no progress] 本回合已调用 {s.ToolCallCount} 次工具但没有产生任何文件改动，也没有制定计划。请自我检查：用 update_plan 把剩余工作拆解为明确步骤；若任务只需要调研，直接总结发现并回答用户；若当前方法行不通，换一种思路。"));
                    events.Publish(new WarningEvent(
                        $"已连续调用 {s.ToolCallCount} 次工具但无任何文件改动，已注入进度自检提示。"));
                }

                PublishSteering(guidance);
                continue;
            }

            if (guidance.Count > 0)
            {
                PublishSteering(guidance);
                continue;
            }

            if (await TryContinueAfterAnswerAsync(s, completion, guidance).ConfigureAwait(false)) continue;
            break;
        }
        if (s.ReachedMaxSteps)
        {
            // 收尾回合也没能给出结论：这是「步数烧光」，不是完成。
            s.FinishReason = TurnFinishReason.MaxSteps;
            events.Publish(new WarningEvent(
                $"已达到最大步数 {s.MaxSteps}，任务可能尚未完成。可在 ~/.haoyue/config.json 中调大 agent.maxSteps。"));
        }
    }

    /// <summary>
    /// 回合终态收口：工作流终态事件、迟到 steering、中断痕迹、撤销台账与完成事件。
    /// 必须无条件执行（原实现位于 finally），否则客户端的工作流视图会停在中途。
    /// </summary>
    private void FinalizeTurn(TurnState s)
    {
        // Terminal WorkflowEvent MUST live on the common path after all catches: the
        // previous placement inside the try made the "error" branch unreachable, so
        // failed/cancelled turns never closed the client's workflow view. kind reflects
        // the real outcome instead of always reporting "任务完成". The try/catch block
        // above became try/catch/finally when the turn scope arrived — same ordering,
        // one fewer place to duplicate cleanup.
        // 终态优先级：取消 > 具体未完成原因 > 异常 > 正常完成。error 存在但循环里没给出
        // 更具体的原因时才记为 error——空回答、截断、拒答自带的原因更有解释力。
        s.Outcome = s.Cancelled
            ? TurnFinishReason.Cancelled
            : s.Error is not null && s.FinishReason == TurnFinishReason.Stop
                ? TurnFinishReason.Error
                : s.FinishReason;
        events.Publish(new WorkflowEvent(
            s.Step,
            s.Outcome is TurnFinishReason.Cancelled or TurnFinishReason.Error
                ? "error"
                : TurnFinishReason.IsIncomplete(s.Outcome) ? "incomplete" : "done",
            s.Outcome switch
            {
                TurnFinishReason.Cancelled => "任务已取消",
                TurnFinishReason.Error => "任务失败",
                var reason when TurnFinishReason.IsIncomplete(reason) => "任务未完成",
                _ => "任务完成",
            },
            TurnFinishReason.ChineseLabel(s.Outcome)));

        PublishSteering(AppendSteering(s.Session, s.Steering));
        s.Steering?.Complete();

        // Interrupted turns leave their executed trace in the session so the next
        // "continue" turn knows what already happened — no silent half-state.
        if (s.Scope.Steps.Count > 0 && (s.Error is not null || s.Cancelled))
        {
            sessionStore.Append(s.Session, ChatMessage.User(
                $">>> [turn interrupted] 本回合{(s.Cancelled ? "被用户中断" : "因错误终止")}。中断前已执行的步骤：\n{s.Scope.RenderTrace()}\n继续任务时请基于以上进度，不要重复已完成的工作。"));
        }

        // Deposit the undo ledger and tell clients what can be reverted. A failed or
        // cancelled turn gets an explicit warning; a successful turn carries the file
        // list on the completion event so the UI can offer undo anyway.
        // 继承父台账时不在子会话上落账：补偿栈归父回合，父回合结束时统一 deposit，
        // 否则同一批文件改动会有两份互不相干的撤销入口（撤销两次或漏撤一次）。
        var failedTurn = s.Error is not null || s.Cancelled;
        var ledger = s.Scope.BuildLedger(failedTurn);
        if (ledger is not null)
        {
            if (s.InheritedScope is null) _undoRegistry?.Deposit(ledger);
            if (failedTurn)
            {
                events.Publish(new WarningEvent(
                    $"本回合{(s.Cancelled ? "被取消" : "失败")}，但已有 {ledger.Changes.Count} 个文件被修改。可通过撤销操作恢复到修改前的内容。"));
            }
        }

        events.Publish(new TurnCompletedEvent(
            s.Session.Header.Id, s.Cancelled, s.Error,
            s.InheritedScope is not null || ledger is null
                ? null
                : ledger.Changes.Select(c => ToRelativePath(c.AbsolutePath, s.Workspace.Root)).ToList(),
            s.Outcome));

        // 事件钩子（task.completed）：回合收尾后按配置自动执行 Webhook / 本地命令。
        // fire-and-forget——钩子失败或误配绝不回头影响回合结果；命令动作会以当前
        // 进程身份访问文件与网络，这是用户显式配置的自动化行为。
        HookRunner.Trigger("task.completed", new System.Text.Json.Nodes.JsonObject
        {
            ["sessionId"] = s.Session.Header.Id,
            ["cancelled"] = s.Cancelled,
            ["error"] = s.Error,
            ["finishReason"] = s.Outcome,
        }, configStore);

        // H-C 回合结束触发式沉淀提示：有实际执行步骤的成功回合给一条客户端可见的
        // 轻提示（不进模型上下文、零 token 成本）——回合内自觉写入优先，后台
        // ConsolidationRunner 的定时扫描兜底，双通道补齐「经历→记忆」。
        if (s.Error is null && !s.Cancelled && s.Scope.Steps.Count > 0)
        {
            events.Publish(new StatusEvent(
                "本回合有实际改动。如有值得长期记住的约定或决定，告诉我即可沉淀；后台记忆固化也会定期扫描近期会话。"));
        }
    }
}
