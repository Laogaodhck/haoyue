import type { ComputedRef, Ref } from 'vue'
import type { DaemonState } from '../../shared/ipc'
import { daemonRequest } from './daemon-rpc'
import { formatTurnFailure } from './app-helpers'
import { finalizeAssistantBubbles, markThreadInterrupted } from './conversation-state'
import { journalCursor, openTurnsOf, recoverTurns, type JournalRow, type RecoveredTurn } from './turn-recovery'
import type { ProjectItem, ThreadItem } from './types'

export interface ConfigAnomaly {
  hasAnomaly: boolean
  detail?: string
  configFile?: string
  backupFile?: string
}

export interface RuntimeRecoveryDeps {
  daemonState: Ref<DaemonState>
  threads: Ref<ThreadItem[]>
  activeThread: ComputedRef<ThreadItem | undefined>
  projects: Ref<ProjectItem[]>
  selectThread: (id: string) => Promise<void>
  reloadThreadSession: (thread: ThreadItem, project?: ProjectItem) => Promise<void>
  scheduleQueuedDrain: (thread: ThreadItem) => void
  configAnomaly: Ref<ConfigAnomaly | null>
  rebuildingConfig: Ref<boolean>
  loadRuntimeState: () => Promise<void>
  notify?: (title: string, body: string) => void
}

/**
 * Reconciles client state that only the daemon can settle after a restart or a
 * dropped link: interrupted turns, terminals missed while offline, and a config
 * rebuild that invalidates the in-memory anomaly banner.
 */
export function createRuntimeRecovery(deps: RuntimeRecoveryDeps) {
  const notify = deps.notify ?? ((title: string, body: string) => { void window.haoyue?.notify?.(title, body) })
  // 断线期间结束的回合收不到实时终态帧：帧带的是请求 id，重连后那条请求已经不存在了。
  // 台账行是唯一线索，于是带上「见过的最大行号」只补播缺的那几行；否则任务会永远卡在运行中，
  // 队列也不会继续下发。
  let lastJournalId = 0

  // N17: after a daemon crash + restart, the startup recovery appends interruption
  // notices and records the recovered session ids. The client may reconnect long
  // after that broadcast window, so it pulls the list via agent.interrupted and
  // clears the stale "running" state that would otherwise stick forever.
  async function reconcileInterruptedTurns(): Promise<void> {
    if (!deps.daemonState.value.connected) return
    let sessionIds: string[] = []
    try {
      const response = await daemonRequest('agent.interrupted')
      sessionIds = JSON.parse(response.data) as string[]
    } catch {
      return
    }
    if (!Array.isArray(sessionIds) || sessionIds.length === 0) return
    const interruptedIds = new Set(sessionIds)
    const hits = deps.threads.value.filter((thread) => thread.sessionId && interruptedIds.has(thread.sessionId))
    if (hits.length === 0) return
    for (const thread of hits) markThreadInterrupted(thread)
    notify(
      '回合被中断',
      hits.length === 1
        ? `「${hits[0]!.title}」的回合因 Runtime 重启而中断，可重新发送消息继续。`
        : `${hits.length} 个任务的回合因 Runtime 重启而中断，可重新发送消息继续。`)
    const active = deps.activeThread.value
    if (active?.sessionId && interruptedIds.has(active.sessionId)) {
      await deps.selectThread(active.id)
    }
  }

  async function replayMissedTerminals(): Promise<void> {
    if (!deps.daemonState.value.connected) return
    const opens = openTurnsOf(deps.threads.value)
    if (opens.length === 0) return
    let rows: JournalRow[] = []
    try {
      const response = await daemonRequest('events.recent', { limit: 500, after: lastJournalId })
      const parsed = JSON.parse(response.data) as JournalRow[]
      if (Array.isArray(parsed)) rows = parsed
    } catch {
      return
    }
    lastJournalId = journalCursor(rows, lastJournalId)
    const recovered = recoverTurns(rows, opens)
    for (const item of recovered) {
      const thread = deps.threads.value.find((candidate) => candidate.id === item.threadId)
      if (thread) await applyRecoveredTurn(thread, item)
    }
    if (recovered.length === 0) return
    const titles = recovered
      .map((item) => deps.threads.value.find((candidate) => candidate.id === item.threadId)?.title ?? '任务')
    notify(
      '已补回断线期间的结果',
      recovered.length === 1
        ? `「${titles[0]}」在连接中断期间已经结束，结果与失败原因已补回。`
        : `${recovered.length} 个任务（${titles.join('、')}）在连接中断期间已经结束，结果已补回。`)
  }

  /** 台账行没有回答正文，先回会话存储整体重载，再补终态标记——顺序反了会被水合覆盖掉。 */
  async function applyRecoveredTurn(thread: ThreadItem, recovered: RecoveredTurn): Promise<void> {
    thread.running = false
    thread.requestId = undefined
    thread.assistantId = undefined
    thread.phase = undefined
    thread.activeTurnToken = undefined
    thread.pendingGuidance = 0
    finalizeAssistantBubbles(thread.messages, recovered.outcome === 'error' ? 'error' : 'done')
    thread.undoableFiles = recovered.undoableFiles
    await deps.reloadThreadSession(thread, deps.projects.value.find((project) => project.id === thread.projectId))
    const bubble = thread.messages.findLast((item) => item.role === 'assistant')
    if (bubble) {
      if (recovered.finishReason) bubble.finishReason = recovered.finishReason
      if (recovered.outcome === 'cancelled') bubble.interrupted = true
      if (recovered.outcome === 'error') {
        bubble.state = 'error'
        bubble.errorDetail = recovered.error ?? bubble.errorDetail
        if (!bubble.content) bubble.content = formatTurnFailure(recovered.error ?? '').content
      }
    }
    deps.scheduleQueuedDrain(thread)
  }

  async function handleRebuildConfig(): Promise<void> {
    deps.rebuildingConfig.value = true
    try {
      await daemonRequest('config.rebuild')
      deps.configAnomaly.value = null
      await deps.loadRuntimeState()
    } catch (err) {
      console.error('Failed to rebuild config:', err)
    } finally {
      deps.rebuildingConfig.value = false
    }
  }

  return { reconcileInterruptedTurns, replayMissedTerminals, handleRebuildConfig }
}
