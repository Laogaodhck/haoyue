import type { Ref } from 'vue'
import type { DaemonMessage } from '../../shared/ipc'
import type { ChatMessage, ProjectItem, ThreadItem } from './types'
import { finalizeAssistantBubbles, markThreadInterrupted } from './conversation-state'
import { makeId, phaseLabel } from './app-helpers'

export interface DaemonEventContext {
  threads: Ref<ThreadItem[]>
  activeThreadId: Ref<string>
  projects: Ref<ProjectItem[]>
  /** 启动预热（daemon 后台装权重）的全局进度：无运行回合时由 model.load.progress 驱动，
   *  用于在输入区展示「本地模型预热中 x%」；回合内加载仍走线程 phase，不重复提示。 */
  modelPreload?: Ref<{ phase: string; percent: number } | null>
  handleScheduleUpdated: () => Promise<void> | void
  handleEvolutionReflected: () => Promise<void> | void
  scrollToBottom: (smooth?: boolean, force?: boolean) => Promise<void>
  reloadThreadSession: (thread: ThreadItem, project?: ProjectItem) => Promise<void>
  rememberFinishedRequest: (thread: ThreadItem, requestId: number) => void
  isFinishedRequest: (thread: ThreadItem, requestId: number) => boolean
  scheduleQueuedDrain: (thread: ThreadItem) => void
  reloadBackgroundThreadIfIdle: (thread: ThreadItem) => void
}

export function createDaemonEventHandler(context: DaemonEventContext): (event: DaemonMessage) => void {
  const {
    threads,
    activeThreadId,
    projects,
    modelPreload,
    handleScheduleUpdated,
    handleEvolutionReflected,
    scrollToBottom,
    reloadThreadSession,
    rememberFinishedRequest,
    isFinishedRequest,
    scheduleQueuedDrain,
    reloadBackgroundThreadIfIdle
  } = context

  function appendModelError(message: ChatMessage, detail: string): void {
    const normalized = detail.trim() || 'Unknown model error'
    message.errorDetail = normalized
    if (!message.content.includes(normalized)) {
      const indentedDetail = normalized.split(/\r?\n/).map((line) => `    ${line}`).join('\n')
      message.content += `${message.content ? '\n\n' : ''}模型调用失败：\n\`\`\`text\n${indentedDetail}\n\`\`\``
    }
    message.state = 'error'
  }

  /**
   * Terminal events carry the workspace-relative files the turn's builtin file tools
   * changed (daemon-side undo ledger). A new turn replaces the list; a turn without
   * changes clears it. agent.undo itself is user-triggered from the thread footer.
   */
  function applyUndoableFiles(thread: ThreadItem, event: DaemonMessage): void {
    const raw = event.details?.undoableFiles
    thread.undoableFiles = Array.isArray(raw)
      ? raw.filter((item): item is string => typeof item === 'string' && item.length > 0)
      : undefined
  }

  // ---------------------------------------------------------------- stream throttle
  // Every delta used to mutate the reactive message immediately, forcing the active
  // bubble to re-run full markdown/highlight/katex parsing dozens of times per second
  // (O(n²) over the reply length). Deltas are now batched into a window per message;
  // terminal events flush pending buffers so nothing is lost. The window widens as
  // the message grows: a long reply re-parses its whole accumulated markdown on every
  // flush, so a fixed 50 ms window still saturates the renderer main thread on large
  // outputs (the "output stutters then freezes" report).
  interface PendingStream {
    content: string
    thinking: string
    timer: ReturnType<typeof setTimeout> | undefined
  }

  const STREAM_FLUSH_MS = 50
  const STREAM_FLUSH_MS_LARGE = 120 // accumulated stream > 12k chars
  const STREAM_FLUSH_MS_HUGE = 250 // accumulated stream > 30k chars

  function flushDelayFor(pending: PendingStream): number {
    const size = pending.content.length + pending.thinking.length
    if (size > 30000) return STREAM_FLUSH_MS_HUGE
    if (size > 12000) return STREAM_FLUSH_MS_LARGE
    return STREAM_FLUSH_MS
  }

  const pendingStreams = new Map<ChatMessage, PendingStream>()

  function flushStream(message: ChatMessage | undefined): void {
    if (!message) return
    const pending = pendingStreams.get(message)
    if (!pending) return
    pendingStreams.delete(message)
    if (pending.timer !== undefined) clearTimeout(pending.timer)
    if (pending.content) message.content += pending.content
    if (pending.thinking) message.thinking = (message.thinking ?? '') + pending.thinking
  }

  function streamInto(message: ChatMessage, apply: (pending: PendingStream) => void): void {
    let pending = pendingStreams.get(message)
    if (!pending) {
      pending = { content: '', thinking: '', timer: undefined }
      pendingStreams.set(message, pending)
    }
    apply(pending)
    if (pending.timer === undefined) {
      pending.timer = setTimeout(() => flushStream(message), flushDelayFor(pending))
    }
  }

  // ---------------------------------------------------------------- scroll throttle
  // handleDaemonEvent runs once per daemon frame (one frame per streamed token on
  // local models). scrollToBottom awaits nextTick and reads scrollHeight, i.e. a
  // forced layout per token — 50–100 reflows per second, on top of the markdown
  // re-renders above, is enough to freeze the UI. Coalesce into one trailing scroll
  // per window; explicit forced scrolls (smooth jump on new bubbles) stay immediate.
  const SCROLL_THROTTLE_MS = 120
  let scrollScheduled = false

  function scheduleAutoScroll(): void {
    if (scrollScheduled) return
    scrollScheduled = true
    setTimeout(() => {
      scrollScheduled = false
      void scrollToBottom()
    }, SCROLL_THROTTLE_MS)
  }

  return function handleDaemonEvent(event: DaemonMessage): void {
    if (event.event === 'schedule.upcoming') return
    if (event.event === 'model.load.progress') {
      // 本地权重冷加载实测要一两分钟。没有反馈时第一条消息看起来就像「发了没反应」，
      // 所以把加载阶段与进度写成运行阶段标签，Ready 后交还给常规的思考提示。
      const phase = typeof event.details?.phase === 'string' ? event.details.phase : ''
      const percent = typeof event.details?.percent === 'number' ? event.details.percent : 0
      const loading = threads.value.find((item) => item.running)
      if (!loading) {
        // 没有运行回合：这是 daemon 启动预热（默认开启）。进度写进全局 ref，
        // 输入区展示「预热中 x%」；Ready/Failed 收尾，断连时由 App 侧清掉。
        if (modelPreload) {
          modelPreload.value = phase === 'Ready'
            ? null
            : { phase, percent: phase === 'Failed' ? 1 : percent }
        }
        return
      }
      loading.phase = phase === 'Ready'
        ? '思考中'
        : phase === 'Failed'
          ? '本地模型加载失败'
          : `加载本地模型 ${Math.round(percent * 100)}%`
      return
    }
    if (event.event === 'turn.interrupted') {
      // N17: the daemon recovered a session whose turn was killed mid-flight by a
      // crash. Clear the stale running state (otherwise the spinner sticks forever)
      // and badge the half-finished assistant bubble.
      const sessionId = typeof event.details?.sessionId === 'string' ? event.details.sessionId : ''
      const thread = threads.value.find((item) => item.sessionId === sessionId)
      if (thread) markThreadInterrupted(thread, reloadThreadSession)
      return
    }
    if (event.event === 'evolution.reflected') {
      // A reflection pass finished (possibly unattended). Refresh the candidate
      // review banner; notify only when new candidates actually await review.
      void handleEvolutionReflected()
      return
    }
    if (event.event === 'memory.consolidated') {
      // 固化是后台按水位自发跑的：MEMORY.md 被改掉却没有任何动静，用户只会觉得
      // 「记忆被偷偷改了」。有实际产出或失败时给一条系统通知，正文用 runtime 的 memoryNote。
      const error = typeof event.details?.error === 'string' ? event.details.error.trim() : ''
      if (error) {
        void window.haoyue?.notify?.('记忆固化失败', error)
        return
      }
      const factsAdded = Number(event.details?.factsAdded) || 0
      const promoted = Number(event.details?.memoryPromoted) || 0
      if (factsAdded === 0 && promoted === 0) return
      const note = typeof event.details?.memoryNote === 'string' ? event.details.memoryNote.trim() : ''
      void window.haoyue?.notify?.('记忆固化完成', [
        `新增 ${factsAdded} 条事实`,
        promoted > 0 ? `提升 ${promoted} 条到长期记忆` : '',
        note
      ].filter(Boolean).join(' · '))
      return
    }
    if (event.event === 'schedule.updated') {
      void handleScheduleUpdated()
      // Outcomes reach the desktop as events, but nobody watches the schedule page
      // at 3 a.m.: surface failures (and cancellations — a timeout aborts the run)
      // as a system notification so a dead cron task cannot stay silent.
      const status = typeof event.details?.status === 'string' ? event.details.status : ''
      if (status === 'error' || status === 'cancelled') {
        const name = typeof event.details?.name === 'string' && event.details.name ? event.details.name : '定时任务'
        const error = typeof event.details?.error === 'string' ? event.details.error.trim() : ''
        void window.haoyue?.notify?.('定时任务失败', error ? `「${name}」执行失败：${error}` : `「${name}」执行失败`)
      }
      return
    }
    const isChatRequest = event.requestMethod === 'chat'
      || event.requestMethod === 'agent.runTurn'
      || event.requestMethod === 'agent/runTurn'
    // The steer request only acknowledges enqueueing (or reports that the turn
    // just finished). Its error/result must never be interpreted as the active
    // chat turn's terminal state.
    if (event.requestMethod === 'agent.steer') return
    if (!isChatRequest && !event.sessionId) return
    const thread = (event.sessionId
      ? threads.value.find((item) => item.sessionId === event.sessionId)
      : undefined)
      ?? threads.value.find((item) => item.requestId === event.id)
      ?? (!event.sessionId && activeThreadId.value && threads.value.find((item) => item.id === activeThreadId.value)?.running
        ? threads.value.find((item) => item.id === activeThreadId.value)
        : undefined)
    if (!thread) return

    // Guidance is drained by the Agent and forwarded under the chat request id, so it
    // can arrive after the turn's terminal response already resolved (IPC race). It
    // must be applied before the stale-request guards below: the optimistic guidance
    // message is already rendered, and its pending counter always needs releasing.
    if (event.event === 'steer') {
      thread.pendingGuidance = Math.max(0, (thread.pendingGuidance ?? 1) - 1)
      const currentAssistant = thread.messages.find((item) => item.id === thread.assistantId)
      if (currentAssistant) {
        currentAssistant.state = 'done'
        const nextAssistant: ChatMessage = {
          id: makeId(),
          role: 'assistant',
          content: '',
          thinking: '',
          tools: [],
          state: 'thinking',
          createdAt: Date.now()
        }
        thread.messages.push(nextAssistant)
        thread.assistantId = nextAssistant.id
        if (thread.id === activeThreadId.value) void scrollToBottom(true, true)
      } else if (!thread.running && thread.sessionId) {
        void reloadThreadSession(thread, projects.value.find((project) => project.id === thread.projectId))
      }
      if (isChatRequest) thread.requestId ??= event.id
      return
    }

    // A terminal response can reach the request continuation before its renderer
    // event callback. If the next queued turn has already started, ignore all
    // delayed events belonging to the completed request — but still finalize any
    // leftover "..." placeholder bubbles, otherwise a dropped terminal event
    // leaves them on screen forever.
    if (isChatRequest && isFinishedRequest(thread, event.id)) {
      if (event.event === 'done' || event.event === 'cancelled')
        finalizeAssistantBubbles(thread.messages, 'done')
      else if (event.event === 'error')
        finalizeAssistantBubbles(thread.messages, 'error')
      return
    }
    if (isChatRequest && thread.requestId !== undefined && thread.requestId !== event.id) return
    // Steering acknowledgements have their own request id and must never replace
    // the id of the active chat turn (used by cancellation and stale-event checks).
    if (isChatRequest) thread.requestId ??= event.id
    const message = thread.messages.find((item) => item.id === thread.assistantId)
    const terminalEvent = event.event === 'done' || event.event === 'cancelled' || event.event === 'error'
    if (!message && !terminalEvent) return
    const isBackgroundThread = thread.id !== activeThreadId.value
    const eventCallId = typeof event.details?.callId === 'string' ? event.details.callId : undefined
    const findTool = () => {
      if (eventCallId) {
        for (let i = thread.messages.length - 1; i >= 0; i--) {
          const m = thread.messages[i]
          if (m?.role === 'assistant' && m.tools) {
            const found = m.tools.find((tool) => tool.callId === eventCallId || tool.id === eventCallId)
            if (found) return found
          }
        }
      }
      return message?.tools?.findLast((tool) => tool.state === 'running')
        ?? thread.messages.findLast((m) => m.role === 'assistant')?.tools?.findLast((tool) => tool.state === 'running')
    }

    switch (event.event) {
      case 'thinking':
        if (!message) break
        streamInto(message, (pending) => { pending.thinking += event.data })
        message.state = 'thinking'
        break
      case 'delta':
        if (!message) break
        streamInto(message, (pending) => { pending.content += event.data })
        message.state = 'streaming'
        break
      case 'approval_request': {
        // 「本次允许」审批（P1-2.3）：内置高危守卫挂起了执行类工具调用。
        // 挂起期间回合阻塞等待，用户选择经 agent.respondApproval 回传。
        if (!message) break
        const callId = typeof event.details?.callId === 'string' ? event.details.callId : ''
        if (!callId) break
        thread.pendingApproval = {
          callId,
          tool: typeof event.details?.tool === 'string' ? event.details.tool : '',
          summary: event.data || '',
          reason: typeof event.details?.reason === 'string' ? event.details.reason : '',
          requestedAt: Date.now()
        }
        break
      }
      case 'status': {
        if (message && event.data.toLocaleLowerCase().includes('thinking')) message.state = 'thinking'
        thread.phase = phaseLabel(event.data)
        break
      }
      case 'image_view': {
        if (!message) break
        const imageId = typeof event.details?.imageId === 'string' ? event.details.imageId : ''
        if (!imageId) break
        message.viewedImages ??= []
        if (!message.viewedImages.some((image) => image.id === imageId))
          message.viewedImages.push({ id: imageId, name: event.data || '图片' })
        break
      }
      case 'tool_start': {
        if (!message) break
        thread.phase = '执行工具'
        message.tools ??= []
        message.tools.push({
          id: eventCallId ?? `${event.id}-${message.tools.length}`,
          callId: eventCallId,
          name: event.data,
          // 入参摘要单独存 args：结果一到会覆盖 detail，两个信息才能同时留在展开行里。
          args: typeof event.details?.summary === 'string' ? event.details.summary : undefined,
          state: 'running'
        })
        break
      }
      case 'tool_done': {
        if (!message) break
        const running = findTool()
        if (running) {
          running.state = event.details?.success === false ? 'error' : 'done'
          running.detail = event.data || running.detail
          // daemon 一直下发 durationMs，此前 ToolActivity 无字段、handler 不读——
          // 工具行展开后展示耗时（P1-2.3）。
          const durationMs = Number(event.details?.durationMs)
          if (Number.isFinite(durationMs) && durationMs > 0) running.durationMs = durationMs
        }
        break
      }
      case 'file_diff': {
        if (!message) break
        const tool = findTool()
        if (tool) {
          const diff = typeof event.details?.diff === 'string' ? event.details.diff : ''
          tool.filePath = event.data
          tool.diff = diff
          tool.addedLines = diff.split(/\r?\n/).filter((line) => line.startsWith('+') && !line.startsWith('+++')).length
          tool.removedLines = diff.split(/\r?\n/).filter((line) => line.startsWith('-') && !line.startsWith('---')).length
        }
        break
      }
      case 'model_start': {
        thread.stats ??= {}
        thread.stats.llmRounds = (thread.stats.llmRounds ?? 0) + 1
        const step = Number(event.details?.step) || 0
        const modelRef = event.data || (event.details?.provider && event.details?.model ? `${event.details.provider}/${event.details.model}` : undefined)
        if (message && modelRef && !message.modelRef) message.modelRef = modelRef
        const previousHasOutput = Boolean(
          message?.content || message?.thinking || (message?.tools?.length ?? 0))
        if (message && step > 1 && previousHasOutput) {
          flushStream(message)
          message.state = 'done'
          const nextAssistant: ChatMessage = {
            id: makeId(),
            role: 'assistant',
            content: '',
            thinking: '',
            tools: [],
            modelRef,
            state: 'thinking',
            createdAt: Date.now()
          }
          thread.messages.push(nextAssistant)
          thread.assistantId = nextAssistant.id
        }
        break
      }
      case 'usage': {
        thread.stats ??= {}
        const inputTokens = Number(event.details?.inputTokens) || 0
        const outputTokens = Number(event.details?.outputTokens) || 0
        const totalInputTokens = Number(event.details?.totalInputTokens) || inputTokens
        const cachedInputTokens = Number(event.details?.cachedInputTokens) || 0
        const elapsedMs = Number(event.details?.elapsedMs) || 0
        thread.stats.inputTokens = (thread.stats.inputTokens ?? 0) + inputTokens
        thread.stats.outputTokens = (thread.stats.outputTokens ?? 0) + outputTokens
        thread.stats.totalInputTokens = (thread.stats.totalInputTokens ?? 0) + totalInputTokens
        // 上下文占比只按最近一轮单次 prompt 规模计：totalInputTokens 是本次调用的完整
        // prompt（含缓存命中），累计口径会随轮数虚涨，直接覆盖而非累加。
        thread.stats.lastRoundInputTokens = totalInputTokens
        thread.stats.cachedInputTokens = (thread.stats.cachedInputTokens ?? 0) + cachedInputTokens
        thread.stats.outputElapsedMs = (thread.stats.outputElapsedMs ?? 0) + elapsedMs
        break
      }
      case 'workflow': {
        const kind = String(event.details?.kind ?? '')
        const step = Number(event.details?.step) || 0
        if (kind === 'start') {
          thread.customPlan = undefined
          thread.turnStepHighWater = 0
        } else if (step > (thread.turnStepHighWater ?? 0)) {
          thread.stats ??= {}
          thread.stats.executionSteps = (thread.stats.executionSteps ?? 0)
            + step - (thread.turnStepHighWater ?? 0)
          thread.turnStepHighWater = step
        }
        if (kind === 'verify'
          && message?.content
          && (message.state === 'thinking' || message.state === 'streaming')) {
          flushStream(message)
          message.state = 'done'
        }
        break
      }
      case 'plan_update': {
        const rawSteps = event.details?.steps as Array<{ title?: string; status?: string; detail?: string }> | undefined
        if (Array.isArray(rawSteps) && rawSteps.length > 0) {
          thread.customPlan = rawSteps.map((s, index) => {
            const rawStatus = (s.status || '').toLowerCase()
            const state = (rawStatus === 'completed' || rawStatus === 'done')
              ? 'done'
              : (rawStatus === 'in_progress' || rawStatus === 'running')
                ? 'running'
                : 'pending'
            return {
              id: `plan-step-${index + 1}`,
              step: index + 1,
              title: s.title || `步骤 ${index + 1}`,
              detail: s.detail,
              state
            }
          })
        }
        break
      }
      case 'warning': {
        if (!message || !event.data) break
        message.warnings ??= []
        if (!message.warnings.includes(event.data)) message.warnings.push(event.data)
        // A stuck model can emit one warning per step; keep the newest few so the
        // bubble stays readable and the array cannot grow without bound.
        if (message.warnings.length > 5) message.warnings.splice(0, message.warnings.length - 5)
        break
      }
      // 以下分支是「显式不处理」的契约事件：编译期穷举护栏（见 switch 末尾 default 的
      // never 赋值）要求 DaemonEventName 每新增一个事件都必须在这里给出归宿，
      // 不允许再出现新事件被静默丢弃（WarningEvent 事故的根因就是没有这道护栏）。
      case 'pong':
        // 协议层探活回显，与回合状态无关。
        break
      case 'result':
        // 非 chat 请求的终态由 daemon-client 的请求 Promise 消费；回合 handler
        // 只关心 chat 流的 done/cancelled/error。
        break
      case 'bye':
        // daemon 优雅退出通知，连接层处理重连。
        break
      case 'task.updated':
        // 后台任务状态广播。当前桌面没有常驻任务中心（task.list 无 UI 消费方），
        // Sidebar 的后台任务徽标在打开页面时按 task.list 拉取；此处显式留空，
        // 待任务中心落地后在此刷新，不要改成「什么都不写」的隐式丢弃。
        break
      case 'mcp.updated':
        // MCP 服务器状态变更由 McpPanel 自行订阅处理（面板打开时才关心）。
        break
      case 'done':

      case 'cancelled':
        if (message) {
          flushStream(message)
          message.state = 'done'
          const finishReason = typeof event.details?.finishReason === 'string' ? event.details.finishReason : undefined
          if (finishReason) message.finishReason = finishReason
          // Distinguish "model finished" from "user cut it off": a half answer with a
          // plain done state read like a complete reply.
          if (event.event === 'cancelled' && message.content) message.interrupted = true
          if (!message.content && event.data) message.content = event.data
        }
        finalizeAssistantBubbles(thread.messages, 'done')
        if (isChatRequest) rememberFinishedRequest(thread, event.id)
        thread.activeTurnToken = undefined
        thread.pendingApproval = undefined
        thread.pendingGuidance = 0
        thread.running = false
        thread.requestId = undefined
        thread.assistantId = undefined
        thread.phase = undefined
        applyUndoableFiles(thread, event)
        scheduleQueuedDrain(thread)
        if (isBackgroundThread) {
          void window.haoyue.notify('后台任务完成', `「${thread.title}」已完成`)
          reloadBackgroundThreadIfIdle(thread)
        } else if (thread.sessionId) {
          void reloadThreadSession(thread, projects.value.find((project) => project.id === thread.projectId))
        }
        break
      case 'error':
        if (message) {
          flushStream(message)
          message.state = 'error'
          appendModelError(message, event.data)
        }
        finalizeAssistantBubbles(thread.messages, 'error')
        if (isChatRequest) rememberFinishedRequest(thread, event.id)
        thread.activeTurnToken = undefined
        thread.pendingApproval = undefined
        thread.pendingGuidance = 0
        thread.running = false
        thread.requestId = undefined
        thread.assistantId = undefined
        thread.phase = undefined
        applyUndoableFiles(thread, event)
        scheduleQueuedDrain(thread)
        if (isBackgroundThread) {
          void window.haoyue.notify('后台任务执行失败', `「${thread.title}」执行失败`)
        }
        if (!message || isBackgroundThread) reloadBackgroundThreadIfIdle(thread)
        break
      default: {
        // 编译期穷举护栏：DaemonEventName 是契约生成类型（daemon-contract.gen.ts），
        // 上面每个事件都有显式归宿（处理或显式忽略）。新增事件若未加分支，下面向
        // never 赋值会编译失败——杜绝「新事件静默丢弃」类回归。
        const unhandledEvent: never = event.event
        throw new Error(`Unhandled daemon event: ${String(unhandledEvent)}`)
      }
    }
    if (thread.id === activeThreadId.value) scheduleAutoScroll()
  }
}
