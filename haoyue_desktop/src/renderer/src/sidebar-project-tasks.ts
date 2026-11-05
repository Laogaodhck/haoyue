import type { ChatMessage, ThreadItem } from './types'

/** 侧栏任务状态：排队 / 运行中 / 完成 / 失败 / 已取消。 */
export type SidebarTaskStatus = 'queued' | 'running' | 'completed' | 'failed' | 'cancelled'

/** task.list 返回的后台任务最小组件：状态与时间用于覆盖会话行的推导值。 */
export interface SidebarBackgroundTask {
  id: string
  status: string
  sessionId?: string | null
  workspaceRoot?: string | null
  createdAt?: string | null
  endedAt?: string | null
}

const FAILED_FINISH_REASONS = new Set(['error', 'refusal', 'content_filter', 'safety'])
const CANCELLED_FINISH_REASONS = new Set(['cancelled', 'interrupted'])

/** 项目下的任务/会话：按归属过滤，并剔除已归档。 */
export function projectThreads(threads: readonly ThreadItem[], projectId: string): ThreadItem[] {
  return threads.filter((thread) => thread.projectId === projectId && !thread.archived)
}

function parseTaskTime(task: SidebarBackgroundTask): number {
  const candidates = [task.endedAt, task.createdAt]
    .filter((value): value is string => typeof value === 'string' && value.length > 0)
    .map((value) => Date.parse(value))
    .filter((value) => Number.isFinite(value))
  return candidates.length ? Math.max(...candidates) : 0
}

/** 会话对应的后台任务（按 sessionId 关联）；没有或未关联时返回 undefined。 */
export function backgroundTaskFor(
  thread: ThreadItem,
  tasks: readonly SidebarBackgroundTask[]
): SidebarBackgroundTask | undefined {
  if (!thread.sessionId) return undefined
  let best: SidebarBackgroundTask | undefined
  for (const task of tasks) {
    if (task.sessionId !== thread.sessionId) continue
    if (!best || parseTaskTime(task) >= parseTaskTime(best)) best = task
  }
  return best
}

/** 状态判定：后台任务的权威状态优先，其次从会话自身的运行/排队/终态推导。 */
export function sidebarTaskStatus(
  thread: ThreadItem,
  task?: SidebarBackgroundTask
): SidebarTaskStatus {
  if (task) {
    switch (task.status) {
      case 'running':
      case 'queued':
      case 'completed':
      case 'failed':
      case 'cancelled':
        return task.status
      default:
        break
    }
  }
  if (thread.running) return 'running'
  if (thread.queueDraining || (thread.queuedMessages?.length ?? 0) > 0) return 'queued'
  if (!thread.sessionId && thread.messages.length === 0) return 'queued'

  const lastAssistant: ChatMessage | undefined = [...thread.messages]
    .reverse()
    .find((message) => message.role === 'assistant')
  if (!lastAssistant) return thread.sessionId ? 'completed' : 'queued'
  if (lastAssistant.state === 'error') return 'failed'
  const finishReason = lastAssistant.finishReason ?? ''
  if (FAILED_FINISH_REASONS.has(finishReason)) return 'failed'
  if (lastAssistant.interrupted || CANCELLED_FINISH_REASONS.has(finishReason)) return 'cancelled'
  return 'completed'
}

export function sidebarTaskStatusLabel(status: SidebarTaskStatus): string {
  switch (status) {
    case 'queued': return '排队'
    case 'running': return '运行中'
    case 'completed': return '完成'
    case 'failed': return '失败'
    case 'cancelled': return '已取消'
  }
}

/** 最近时间：MM-DD HH:mm；当天只显示 HH:mm。 */
export function formatTaskTime(ms: number, now: number = Date.now()): string {
  const date = new Date(ms)
  const current = new Date(now)
  const pad = (value: number): string => String(value).padStart(2, '0')
  const clock = `${pad(date.getHours())}:${pad(date.getMinutes())}`
  const sameDay = date.getFullYear() === current.getFullYear()
    && date.getMonth() === current.getMonth()
    && date.getDate() === current.getDate()
  return sameDay ? clock : `${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${clock}`
}

/**
 * 行尾「最近时间」：会话的 updatedAt 与后台任务的 endedAt/createdAt 取最大者，
 * 保证运行中的任务在看到终态时间后不会退回更早的会话更新时间。
 */
export function taskRecentTime(
  thread: ThreadItem,
  task?: SidebarBackgroundTask,
  now: number = Date.now()
): string {
  const stamps = [thread.updatedAt, task ? parseTaskTime(task) : 0]
    .filter((value) => Number.isFinite(value) && value > 0)
  if (!stamps.length) return ''
  return formatTaskTime(Math.max(...stamps), now)
}
