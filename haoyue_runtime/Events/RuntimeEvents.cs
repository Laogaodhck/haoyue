namespace Haoyue.Runtime.Events;

/// <summary>
/// Base type for every event flowing through the runtime event bus.
/// The renderer (CLI/GUI/Web) consumes these; the runtime never touches the console.
/// </summary>
/// <param name="TurnId">回合身份（daemon 请求 id / CLI 回合号）。由 <see cref="TurnStamp"/>
/// 在回合执行体入口统一盖章，事件记录本身无需逐个携带；无回合上下文时为 null。</param>
public abstract record RuntimeEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>回合身份：台账归组与 events.recent 回放按它对齐实时帧的请求 id。</summary>
    public string? TurnId { get; init; }
}

// ---------------------------------------------------------------- turn lifecycle

public sealed record TurnStartedEvent(string SessionId, string UserInput) : RuntimeEvent;

public sealed record UserSteerEvent(string Instruction) : RuntimeEvent;

/// <param name="UndoableFiles">Workspace-relative paths changed by builtin file tools this
/// turn; a later agent.undo RPC can revert them. Null when nothing was changed.</param>
/// <param name="FinishReason">回合终态（Haoyue.Runtime.Agents.TurnFinishReason 词汇表）。
/// 「模型答完了」与「连接断了留半截」「步数烧光」过去在事件里无法区分。</param>
public sealed record TurnCompletedEvent(
    string SessionId,
    bool Cancelled,
    string? Error,
    IReadOnlyList<string>? UndoableFiles = null,
    string? FinishReason = null) : RuntimeEvent;

/// <summary>High-level agent status: Thinking, Reading files, Searching, Editing, Building, Verifying…</summary>
public sealed record StatusEvent(string Status, string? Detail = null) : RuntimeEvent;

// ---------------------------------------------------------------- streaming

public sealed record AssistantTextDeltaEvent(string Delta) : RuntimeEvent;

public sealed record AssistantMessageCompletedEvent(string Text) : RuntimeEvent;

public sealed record ThinkingDeltaEvent(string Delta) : RuntimeEvent;

public sealed record ThinkingCompletedEvent : RuntimeEvent;

/// <summary>Emitted immediately before an uploaded image enters the model request.</summary>
public sealed record ImageViewedEvent(string ImageId, string Name, string MediaType) : RuntimeEvent;

// ---------------------------------------------------------------- tools

public sealed record ToolCallStartedEvent(string CallId, string ToolName, string ArgumentSummary) : RuntimeEvent;

public sealed record ToolCallProgressEvent(string CallId, string Message) : RuntimeEvent;

public sealed record ToolCallCompletedEvent(
    string CallId,
    string ToolName,
    bool Success,
    string ResultSummary,
    TimeSpan Duration) : RuntimeEvent;

/// <summary>A unified diff produced by a file-mutating tool, for rich rendering.</summary>
public sealed record FileDiffEvent(string CallId, string FilePath, string UnifiedDiff) : RuntimeEvent;

/// <summary>
/// A retrieval that came back empty. Successful-but-empty tool calls are invisible to
/// <see cref="Haoyue.Runtime.Evolution.DefectAggregator"/>'s failure clustering (it only reads
/// Success == false), so a knowledge layer that simply lacks the answer never reaches the
/// evolution engine. This event closes that gap: repeated misses on the same query become a
/// coverage defect the reflection turn can turn into a knowledge_save draft.
/// CorpusSize distinguishes 「库里就没有内容」 from 「有内容但检索不到」 and is null when the
/// producer cannot know it cheaply.
/// </summary>
public sealed record RetrievalMissEvent(
    string ToolName,
    string Query,
    int? CorpusSize = null,
    string? Reason = null) : RuntimeEvent;

// ---------------------------------------------------------------- provider layer

public sealed record ModelInvocationStartedEvent(string ProviderId, string ModelId, int Step) : RuntimeEvent;

public sealed record ProviderRetryEvent(string ModelRef, int Attempt, string Reason, TimeSpan Delay) : RuntimeEvent;

public sealed record ProviderSwitchedEvent(string FromRef, string ToRef, string Reason) : RuntimeEvent;

/// <param name="Performance">本地推理性能遥测（仅本地 provider 的真实测量；云端调用为 null）。
/// 2026-10-20 接通消费线：usage.jsonl 落盘 + 聚合 + 实时事件下发，桌面输入框读数。</param>
public sealed record UsageRecordedEvent(
    string ProviderId,
    string ModelId,
    long InputTokens,
    long OutputTokens,
    decimal Cost,
    TimeSpan Elapsed,
    long TotalInputTokens,
    long CachedInputTokens,
    Haoyue.Runtime.Providers.InferencePerformance? Performance = null) : RuntimeEvent;

// ---------------------------------------------------------------- workflow & plan

/// <summary>A node in the agent's live execution flowchart (think / tool / verify …).</summary>
public sealed record WorkflowEvent(int Step, string Kind, string Label, string? Detail = null) : RuntimeEvent;

/// <summary>Emitted when the AI agent creates or updates its task plan steps.</summary>
public sealed record PlanUpdatedEvent(string StepsJson, string? Explanation = null) : RuntimeEvent;

// ---------------------------------------------------------------- verification

public sealed record VerificationStartedEvent(string Command, int Attempt) : RuntimeEvent;

public sealed record VerificationCompletedEvent(bool Success, string Summary, int Attempt) : RuntimeEvent;

// ---------------------------------------------------------------- scheduling

/// <summary>Emitted when a scheduled task enters the one-minute pre-run window.</summary>
public sealed record ScheduledTaskUpcomingEvent(
    string TaskId,
    string Name,
    DateTimeOffset RunAt) : RuntimeEvent;

/// <summary>Emitted after a scheduled task run records its outcome.</summary>
public sealed record ScheduledTaskCompletedEvent(
    string TaskId,
    string Name,
    string? SessionId,
    string Status,
    string? Error = null,
    string? Output = null) : RuntimeEvent;

// ---------------------------------------------------------------- computer use

public sealed record ComputerActionEvent(
    string Action,
    int? X,
    int? Y,
    string? Target,
    bool Success,
    string? Error,
    string? Base64Screenshot) : RuntimeEvent;

// ---------------------------------------------------------------- diagnostics

public sealed record WarningEvent(string Message) : RuntimeEvent;

public sealed record ErrorEvent(string Message, string? Detail = null) : RuntimeEvent;

// ---------------------------------------------------------------- approval

/// <summary>
/// 执行类工具请求用户「本次允许」（P1-2.3，2026-10-20 扩展）。两个触发源：
/// ① 内置高危守卫拦截了破坏性命令（bash / computer_exec / terminal 的启发式规则）——
/// 桌面弹出审批卡片，用户选择经 agent.respondApproval 回传；超时/拒绝按拒绝处理；
/// ② agent.toolPolicy.requireConsentFor 清单内工具（默认 computer_exec：一启用即
/// profile 级无沙箱 shell）的首次调用——回合内批准一次后放行后续调用。
/// 用户自定义 toolPolicy 规则不触发本事件——那类拒绝必须改配置。
/// </summary>
public sealed record ApprovalRequiredEvent(
    string CallId,
    string ToolName,
    string Summary,
    string Reason) : RuntimeEvent;

// ---------------------------------------------------------------- evolution

/// <summary>Emitted after an evolution reflection turn finishes (engine stage E2).</summary>
public sealed record EvolutionReflectionCompletedEvent(
    string SessionId,
    int Processed,
    int Candidates,
    int NoAction,
    int Skipped,
    string? Error = null) : RuntimeEvent;

/// <summary>
/// Emitted after a memory-consolidation pass finishes (H-C): recent session
/// experience distilled into project facts. Superseded counts facts that merged
/// into (or refreshed) an existing fact instead of growing the store.
/// </summary>
public sealed record MemoryConsolidationCompletedEvent(
    string SessionId,
    int SessionsScanned,
    int FactsAdded,
    int FactsSuperseded,
    string? Error = null,
    int MemoryPromoted = 0,
    string? MemoryNote = null) : RuntimeEvent;

/// <summary>Explicit user feedback on a finished turn (P4 thumbs-down). The defect
/// aggregator turns each negative feedback into its own defect report so the next
/// reflection pass can analyze the session transcript.</summary>
public sealed record UserFeedbackEvent(
    string SessionId, string Kind, string? Reason = null) : RuntimeEvent;

/// <summary>后台任务（BackgroundTaskService）状态迁移广播：启动后的每次终态迁移都会发布，
/// 前端据此刷新任务列表；任务进度本身复用回合级流式事件经 events.recent 重放。</summary>
public sealed record BackgroundTaskEvent(
    string TaskId, string Status, string? SessionId = null, string? Error = null) : RuntimeEvent;

// ---------------------------------------------------------------- local model loading

/// <summary>
/// 本地模型加载进度（预热 / 首请求加载 / model.preload 时发布）。llama.cpp 权重
/// 加载是单次原生调用，百分比为「阶段边界 + 历史 ETA 插值」的诚实估计； EstimatedRemainingMs
/// 为 null 表示无历史耗时指纹可用。daemon 以 model.load.progress 广播转发。
/// </summary>
public sealed record ModelLoadProgressEvent(
    string ModelRef,
    string Phase,
    double Percent,
    long ElapsedMs,
    long? EstimatedRemainingMs = null,
    string? Message = null) : RuntimeEvent;

