import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
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

describe('daemon event handler — stream batching', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    ;(globalThis as unknown as { window: unknown }).window = {
      haoyue: { notify: vi.fn<() => Promise<void>>().mockResolvedValue(undefined) }
    }
  })
  afterEach(() => {
    vi.useRealTimers()
  })

  function chatEvent(event: string, data: string, sessionId = 's1'): DaemonMessage {
    return { id: 7, event: event as DaemonMessage['event'], data, sessionId, requestMethod: 'chat' }
  }

  function threadFixture(): ThreadItem {
    const thread: ThreadItem = {
      id: 't1',
      title: '任务',
      updatedAt: Date.now(),
      messages: [],
      sessionId: 's1',
      running: true,
      requestId: 7,
      assistantId: 'a1'
    }
    thread.messages.push({
      id: 'a1', role: 'assistant', content: '', thinking: '', tools: [], state: 'thinking', createdAt: Date.now()
    })
    return thread
  }

  it('batches rapid deltas into one reactive flush per window', () => {
    const context = createContext()
    const thread = threadFixture()
    context.threads.value.push(thread)
    const handler = createDaemonEventHandler(context)
    const assistant = thread.messages[0]!

    handler(chatEvent('delta', 'Hello'))
    handler(chatEvent('delta', ' world'))
    // Inside the batch window the message is still untouched: no reactive storm.
    expect(assistant.content).toBe('')
    vi.advanceTimersByTime(60)
    expect(assistant.content).toBe('Hello world')
    expect(assistant.state).toBe('streaming')
  })

  it('flushes pending deltas on the terminal event and marks manual stops', () => {
    const context = createContext()
    const thread = threadFixture()
    context.threads.value.push(thread)
    const handler = createDaemonEventHandler(context)
    const assistant = thread.messages[0]!

    handler(chatEvent('delta', 'partial answer'))
    handler(chatEvent('cancelled', '', 's1'))
    // Nothing lost: the buffered text reached the bubble before finalization.
    expect(assistant.content).toBe('partial answer')
    expect(assistant.state).toBe('done')
    expect(assistant.interrupted).toBe(true)

    // A clean completion must not be flagged as manually stopped.
    const thread2 = threadFixture()
    context.threads.value.push(thread2)
    handler(chatEvent('delta', 'done text', 's2'))
    handler(chatEvent('done', '', 's2'))
    expect(thread2.messages[0]!.interrupted).toBeUndefined()
  })
})
