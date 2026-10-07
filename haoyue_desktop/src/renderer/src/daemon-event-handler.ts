import type { Ref } from 'vue'
import type { DaemonMessage } from '../../shared/ipc'
import type { ChatMessage, ProjectItem, ThreadItem } from './types'
import { finalizeAssistantBubbles, markThreadInterrupted } from './conversation-state'
import { makeId, phaseLabel } from './app-helpers'

export interface DaemonEventContext {
  threads: Ref<ThreadItem[]>
  activeThreadId: Ref<string>
  projects: Ref<ProjectItem[]>
  handleScheduleUpdated: () => Promise<void> | void
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
    handleScheduleUpdated,
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

  // ---------------------------------------------------------------- stream throttle
  // Every delta used to mutate the reactive message immediately, forcing the active
  // bubble to re-run full markdown/highlight/katex parsing dozens of times per second
  // (O(n²) over the reply length). Deltas are now batched into a ~50 ms window per
  // message; terminal events flush pending buffers so nothing is lost.
  interface PendingStream {
    content: string
    thinking: string
    timer: ReturnType<typeof setTimeout> | undefined
  }

  const STREAM_FLUSH_MS = 50
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
      pending.timer = setTimeout(() => flushStream(message), STREAM_FLUSH_MS)
    }
  }

  return function handleDaemonEvent(event: DaemonMessage): void {
    if (event.event === 'schedule.upcoming') return
    if (event.event === 'turn.interrupted') {
      // N17: the daemon recovered a session whose turn was killed mid-flight by a
      // crash. Clear the stale running state (otherwise the spinner sticks forever)
      // and badge the half-finished assistant bubble.
      const sessionId = typeof event.details?.sessionId === 'string' ? event.details.sessionId : ''
      const thread = threads.value.find((item) => item.sessionId === sessionId)
      if (thread) markThreadInterrupted(thread, reloadThreadSession)
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
          detail: typeof event.details?.summary === 'string' ? event.details.summary : undefined,
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
      case 'done':

      case 'cancelled':
        if (message) {
          flushStream(message)
          message.state = 'done'
          // Distinguish "model finished" from "user cut it off": a half answer with a
          // plain done state read like a complete reply.
          if (event.event === 'cancelled' && message.content) message.interrupted = true
          if (!message.content && event.data) message.content = event.data
        }
        finalizeAssistantBubbles(thread.messages, 'done')
        if (isChatRequest) rememberFinishedRequest(thread, event.id)
        thread.activeTurnToken = undefined
        thread.pendingGuidance = 0
        thread.running = false
        thread.requestId = undefined
        thread.assistantId = undefined
        thread.phase = undefined
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
        thread.pendingGuidance = 0
        thread.running = false
        thread.requestId = undefined
        thread.assistantId = undefined
        thread.phase = undefined
        scheduleQueuedDrain(thread)
        if (isBackgroundThread) {
          void window.haoyue.notify('后台任务执行失败', `「${thread.title}」执行失败`)
        }
        if (!message || isBackgroundThread) reloadBackgroundThreadIfIdle(thread)
        break
    }
    if (thread.id === activeThreadId.value) void scrollToBottom()
  }
}
