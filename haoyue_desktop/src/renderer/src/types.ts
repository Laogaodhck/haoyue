export interface ProjectItem {
  id: string
  name: string
  path: string
  loaded?: boolean
}

/** 右侧统一工作区面板（WorkDock）的页签：Git 更改 / 提交记录 / 工具活动 / 内置浏览器 / 后台任务 / 任务监控。 */
export type WorkDockTab = 'changes' | 'history' | 'tools' | 'browser' | 'tasks' | 'monitor'

/** 桌面端「任务监控」展示偏好（advanced.get/set 的 taskMonitor 节，与 runtime 契约同构）。 */
export interface TaskMonitorConfig {
  /** 任务监控形式：fixed（右栏页签）| floating（悬浮窗）。 */
  mode: 'fixed' | 'floating'
  /** 固定模式默认展示：show（打开右栏默认选中监控页签）| hidden（需手动点开）。 */
  fixedDefaultShow: 'show' | 'hidden'
  /** 进度与上下文：任务回顾 / 任务目标 / 计划。 */
  progress: { review: boolean; goal: boolean; plan: boolean }
  /** 执行活动：子智能体 / 后台进程 / 侧边聊天 / Skill 与 MCP。 */
  activity: { subAgents: boolean; backgroundProcesses: boolean; sideChat: boolean; skillMcp: boolean }
  /** 结果与来源：产出 / 网页查阅 / 来源 / 知识。 */
  results: { outputs: boolean; webVisits: boolean; sources: boolean; knowledge: boolean }
  /** 辅助入口：记忆更新 / 演示画面。 */
  access: { memoryUpdate: boolean; demoScreen: boolean }
}

/** 任务监控偏好的缺省值（advanced.get 失败/未返回时面板回退）。 */
export function defaultTaskMonitorConfig(): TaskMonitorConfig {
  return {
    mode: 'fixed',
    fixedDefaultShow: 'show',
    progress: { review: true, goal: true, plan: true },
    activity: { subAgents: true, backgroundProcesses: true, sideChat: true, skillMcp: true },
    results: { outputs: true, webVisits: true, sources: true, knowledge: true },
    access: { memoryUpdate: true, demoScreen: true }
  }
}

/** 事件钩子（hooks.list/save/delete 读面，与 runtime 契约同构）。 */
export interface HookInfo {
  name: string
  trigger: 'session.start' | 'tool.before' | 'tool.after' | 'task.completed' | 'schedule.completed'
  action: 'webhook' | 'command'
  enabled: boolean
  webhookUrl: string
  /** 是否已配置 Bearer 密钥（不回传密钥明文）。 */
  hasSecret: boolean
  command: string
  /** 匹配条件：逗号分隔通配符（* ?），匹配工具名/会话 id；空 = 全部。 */
  match: string
  timeoutSeconds: number
  failurePolicy: 'warn' | 'block'
}

export interface HookProblem {
  name: string
  problem: string
}

export interface HookStatus {
  name: string
  runCount: number
  lastOk: boolean
  lastTriggeredAt: string | null
  lastError: string | null
  lastDurationMs: number | null
}

export interface HookRunRecord {
  at: string
  trigger: string
  hook: string
  target: string
  ok: boolean
  durationMs: number
  error: string | null
  output: string | null
}

export interface HooksListResult {
  hooks: HookInfo[]
  problems: HookProblem[]
  status: HookStatus[]
  ledger: HookRunRecord[]
}

export interface HooksRunsResult {
  workspace: string
  records: HookRunRecord[]
}

export interface HookTestResult {
  ok: boolean
  error: string | null
  durationMs: number
  output: string
}

/** 钩子编辑表单的载荷（hooks.save 参数）。 */
export interface HookDraft {
  name: string
  trigger: 'session.start' | 'tool.before' | 'tool.after' | 'task.completed' | 'schedule.completed'
  action: 'webhook' | 'command'
  enabled: boolean
  webhookUrl: string
  webhookSecret: string
  command: string
  match: string
  timeoutSeconds: number
  failurePolicy: 'warn' | 'block'
}

/** 智能体团体（team.* 读面，与 runtime 契约同构）。 */
export type TeamMode = 'sequential' | 'parallel' | 'supervisor' | 'review'

export interface TeamMember {
  id: string
  name: string
  role: string
  responsibility: string
  model: string | null
  expertId: string | null
  tools: string[]
  enabled: boolean
}

export interface TeamDefinition {
  id: string
  name: string
  description: string
  mode: TeamMode
  coordinatorId: string | null
  enabled: boolean
  maxRounds: number
  timeoutSeconds: number
  maxRetries: number
  failureStrategy: 'stop' | 'continue' | 'fallback'
  createdAt: string
  updatedAt: string
  members: TeamMember[]
}

export interface TeamsListResult {
  workspace: string
  teams: TeamDefinition[]
}

export interface TeamCopyResult {
  workspace: string
  team: TeamDefinition
  teams: TeamDefinition[]
}

export interface TeamRunMember {
  memberId: string
  role: string
  status: 'pending' | 'running' | 'succeeded' | 'failed'
  durationMs: number
  toolCalls: number
  error: string | null
  outputPreview: string
}

export interface TeamRun {
  runId: string
  teamId: string
  sessionId: string
  status: 'running' | 'succeeded' | 'partial' | 'failed' | 'cancelled'
  startedAt: string
  endedAt: string | null
  rounds: number
  result: string | null
  error: string | null
  workspace: string
  members: TeamRunMember[]
}

export interface TeamRunsResult {
  workspace: string
  runs: TeamRun[]
}

export interface TeamCancelResult {
  runId: string
  cancelled: boolean
}

/** Provider-neutral reasoning depth. Runtime wire clients perform API-specific mapping. */
export enum ReasoningLevel {
  None = 'none',
  Low = 'low',
  Medium = 'medium',
  High = 'high',
  Max = 'max',
  XHigh = 'xhigh',
  Ultra = 'ultra'
}

export interface ToolActivity {
  id: string
  callId?: string
  name: string
  /** 结果摘要（tool_done.data）；历史会话重载后即为持久化的工具输出全文。 */
  detail?: string
  /** 入参摘要（tool_start.summary）。此前结果一到就被覆盖，用户永远看不到工具是按什么参数调的。 */
  args?: string
  filePath?: string
  diff?: string
  addedLines?: number
  removedLines?: number
  state: 'running' | 'done' | 'error'
  /** 执行耗时（毫秒，来自 daemon tool_done 事件）；展开行显示。 */
  durationMs?: number
}

export interface ImageAttachment {
  id: string
  name: string
  mediaType: string
  /** base64 字节。session.get 只回元数据（体积不再随视觉历史膨胀），历史图片的字节
   * 由 session.image 按需补取，取到前为 undefined。 */
  data?: string | null
  sizeBytes: number
  path?: string
}

export interface FileAttachment {
  id: string
  name: string
  path: string
  sizeBytes: number
  extension: string
}

export interface ImageReference {
  id: string
  name: string
}

/** 被替换消息的会话内历史版本（1.2）：regenerate/edit 截断前压入，仅本次会话可见，不持久化。 */
export interface MessageVersion {
  role: 'user' | 'assistant'
  content: string
  createdAt: number
  reason: 'regenerate' | 'edit'
  modelRef?: string
}

export interface ChatMessage {
  id: string
  role: 'user' | 'assistant'
  content: string
  images?: ImageAttachment[]
  files?: FileAttachment[]
  thinking?: string
  /** "provider/model" that produced this assistant message. */
  modelRef?: string
  viewedImages?: ImageReference[]
  state?: 'thinking' | 'streaming' | 'done' | 'error'
  /** True when the user manually stopped the turn mid-answer (reply may be incomplete). */
  interrupted?: boolean
  /** Runtime warnings forwarded during this turn (policy denials, interrupted streams). */
  warnings?: string[]
  /** 回合终态 token（stop/length/interrupted/refusal/empty/max_steps/budget/cancelled/error）。
   *  非 stop 表示模型并没有答完，界面必须按「不完整回答」显示。 */
  finishReason?: string
  /** Raw technical detail of the model error that failed this turn (error card). */
  errorDetail?: string
  tools?: ToolActivity[]
  /** 被替换前的历史版本（挂在保留区的锚点消息上）。仅本次会话可见。 */
  versions?: MessageVersion[]
  createdAt: number
}

export interface ScheduledTaskInfo {
  id: string
  name: string
  workspace?: string
  prompt: string
  cron: string
  enabled: boolean
  lastRunAt?: string
  nextRunAt?: string
  lastStatus?: string
  lastError?: string
  lastOutput?: string
  /** 上次运行所在会话，供「打开本次会话」跳转。 */
  lastSessionId?: string
  createdAt: string
  updatedAt: string
}

export interface QueuedMessage {
  id: string
  content: string
  images: ImageAttachment[]
  files?: FileAttachment[]
  createdAt: number
}

/** Per-session runtime statistics accumulated from daemon events. */
export interface ThreadStats {
  llmRounds?: number
  executionSteps?: number
  inputTokens?: number
  outputTokens?: number
  totalInputTokens?: number
  /** 最近一轮 LLM 调用的完整 prompt 规模（usage.totalInputTokens 口径，含缓存命中部分）。
   *  上下文占比只认它：累计值随轮数线性虚涨，不能直接除以模型窗口。 */
  lastRoundInputTokens?: number
  cachedInputTokens?: number
  outputElapsedMs?: number
  /** 最近一次本地推理的性能读数（usage 事件 details.inference；仅本地 provider 有值）。
   *  让预热 / KV 前缀复用的效果对用户可见：TTFT 含加载等待，prefill 为净口径。 */
  lastInference?: {
    ttftMs: number
    loadWaitMs: number
    prefillTokensPerSecond: number
    decodeTokensPerSecond: number
    cacheHitRate: number
    prefixReuse: boolean
  } | null
}

export interface ThreadItem {
  id: string
  title: string
  projectId?: string
  updatedAt: number
  messages: ChatMessage[]
  sessionId?: string
  sessionLoaded?: boolean
  /** 会话消息总条数与已加载窗口的起点序号（session.get 分页，P1-2.3）。 */
  sessionTotal?: number
  sessionFirstIndex?: number
  /** 还有更早的消息没拉下来（「加载更早」按钮的显示条件）。 */
  sessionHasMore?: boolean
  /** 「加载更早」请求在途，避免连点重复拉同一页。 */
  loadingEarlier?: boolean
  archived?: boolean
  /** True while this task has an agent turn running, independent of the selected task. */
  running?: boolean
  /** Local request id used to cancel this task without affecting other tasks. */
  requestId?: number
  /** Request ids whose terminal events have already been applied. Used to ignore
   * delayed events from a previous turn when the next queued turn has started. */
  finishedRequestIds?: number[]
  /** Local generation marker so an older request cannot clean up a newer turn. */
  activeTurnToken?: string
  /** Current work phase shown in the header (thinking/tool/verify…). */
  phase?: string
  /** Custom high-level plan steps emitted explicitly by update_plan. */
  customPlan?: Array<{ id: string; step: number; title: string; detail?: string; state: 'running' | 'done' | 'error' | 'pending' }>
  /** Assistant placeholder receiving streamed output for the active turn. */
  assistantId?: string
  /** Per-task reasoning depth, independent from other concurrent tasks. */
  reasoningLevel?: ReasoningLevel
  /** Per-task "联网" toggle; controls web_search + web_fetch together. */
  networkEnabled?: boolean
  /** Bound expert persona id (ExpertCatalog); shown as a composer badge and sent with the first turn. */
  expertId?: string
  /** Expert display metadata for the badge (denormalized from expert.list). */
  expertLabel?: string
  /** Messages waiting for the current agent turn to finish. */
  queuedMessages?: QueuedMessage[]
  /** Prevents multiple queued messages from starting at the same time. */
  queueDraining?: boolean
  /** Locally displayed guidance messages waiting to be persisted by the Agent. */
  pendingGuidance?: number
  /** Live per-session model/token statistics, populated from daemon events. */
  stats?: ThreadStats
  /** Tracks the highest execution step seen in the current turn (workflow events). */
  turnStepHighWater?: number
  /** Workspace-relative files the latest finished turn changed; agent.undo can revert them. */
  undoableFiles?: string[]
  /** 「本次允许」审批请求（内置高危守卫挂起的工具调用，P1-2.3）。 */
  pendingApproval?: {
    callId: string
    tool: string
    summary: string
    reason: string
    requestedAt: number
  }
}

/** 跨会话全文检索的一条命中（session.search）：命中会话的头信息 + 匹配条数 + 片段。 */
export interface SessionSearchHit {
  id: string
  title: string
  workspace?: string
  archived?: boolean
  updatedAt?: string
  matchCount?: number
  /** 首条命中消息正文的窗口片段；仅标题命中时为空。 */
  snippet?: string | null
}

/** One evolution-engine skill candidate awaiting human review (P3). */
export interface EvolutionCandidate {
  fingerprint: string
  kind: string
  skillName: string
  candidateDir?: string
  summary?: string
  createdAt?: string
}


