import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import type { Ref } from 'vue'
import type { DaemonEventContext } from './daemon-event-handler'
import { createDaemonEventHandler } from './daemon-event-handler'
import type { DaemonMessage } from '../../shared/ipc'
import type { ProjectItem, ThreadItem } from './types'

function createContext(): DaemonEventContext & { handleScheduleUpdated: ReturnType<typeof vi.fn> } {
  return {
    threads: ref([]) as Ref<ThreadItem[]>,
    activeThreadId: ref(''),
    projects: ref([]) as Ref<ProjectItem[]>,
    handleScheduleUpdated: vi.fn<() => void>(),
    scrollToBottom: vi.fn<(smooth?: boolean, force?: boolean) => Promise<void>>().mockResolvedValue(undefined),
    reloadThreadSession: vi.fn<(thread: ThreadItem, project?: ProjectItem) => Promise<void>>().mockResolvedValue(undefined),
    rememberFinishedRequest: vi.fn<(thread: ThreadItem, requestId: number) => void>(),
    isFinishedRequest: vi.fn<(thread: ThreadItem, requestId: number) => boolean>(() => false),
    scheduleQueuedDrain: vi.fn<(thread: ThreadItem) => void>(),
    reloadBackgroundThreadIfIdle: vi.fn<(thread: ThreadItem) => void>()
  }
}

function scheduleUpdated(status: string, extra?: Record<string, unknown>): DaemonMessage {
  return {
    id: 0,
    event: 'schedule.updated',
    data: '晨报',
    details: { taskId: 'task-1', name: '晨报', status, ...extra }
  }
}

describe('daemon event handler — schedule notifications', () => {
  let notify: ReturnType<typeof vi.fn>

  beforeEach(() => {
    notify = vi.fn<() => Promise<void>>().mockResolvedValue(undefined)
    // The event handler is renderer code that touches `window.haoyue`; the vitest
    // suite runs in node, so stub the minimal surface instead of pulling in jsdom.
    ;(globalThis as unknown as { window: unknown }).window = { haoyue: { notify } }
  })

  it('notifies on failed scheduled tasks with the error detail', () => {
    const context = createContext()
    const handler = createDaemonEventHandler(context)

    handler(scheduleUpdated('error', { error: 'provider offline' }))

    expect(context.handleScheduleUpdated).toHaveBeenCalledTimes(1)
    expect(notify).toHaveBeenCalledTimes(1)
    expect(notify).toHaveBeenCalledWith('定时任务失败', '「晨报」执行失败：provider offline')
  })

  it('notifies on cancelled runs (timeout aborts) without an error string', () => {
    const context = createContext()
    const handler = createDaemonEventHandler(context)

    handler(scheduleUpdated('cancelled'))

    expect(notify).toHaveBeenCalledWith('定时任务失败', '「晨报」执行失败')
  })

  it('stays silent on successful runs', () => {
    const context = createContext()
    const handler = createDaemonEventHandler(context)

    handler(scheduleUpdated('success'))

    expect(context.handleScheduleUpdated).toHaveBeenCalledTimes(1)
    expect(notify).not.toHaveBeenCalled()
  })

  it('tolerates missing details without notifying or throwing', () => {
    const context = createContext()
    const handler = createDaemonEventHandler(context)
    const event: DaemonMessage = { id: 0, event: 'schedule.updated', data: '' }

    expect(() => handler(event)).not.toThrow()
    expect(context.handleScheduleUpdated).toHaveBeenCalledTimes(1)
    expect(notify).not.toHaveBeenCalled()
  })

  it('ignores upcoming notices entirely', () => {
    const context = createContext()
    const handler = createDaemonEventHandler(context)
    const event: DaemonMessage = {
      id: 0,
      event: 'schedule.upcoming',
      data: '晨报',
      details: { taskId: 'task-1', name: '晨报', runAt: '2026-01-01T09:00:00Z' }
    }

    expect(() => handler(event)).not.toThrow()
    expect(notify).not.toHaveBeenCalled()
    expect(context.handleScheduleUpdated).not.toHaveBeenCalled()
  })
})
