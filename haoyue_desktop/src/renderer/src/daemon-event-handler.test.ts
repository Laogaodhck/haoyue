import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import type { Ref } from 'vue'
import type { DaemonEventContext } from './daemon-event-handler'
import { createDaemonEventHandler } from './daemon-event-handler'
import { extractModelError, finishReasonLabel, stripModelErrorBlock } from './app-helpers'
import type { DaemonMessage } from '../../shared/ipc'
import type { ProjectItem, ThreadItem } from './types'

function createContext(): DaemonEventContext & {
  handleScheduleUpdated: ReturnType<typeof vi.fn>
  handleEvolutionReflected: ReturnType<typeof vi.fn>
} {
  return {
    threads: ref([]) as Ref<ThreadItem[]>,
    activeThreadId: ref(''),
    projects: ref([]) as Ref<ProjectItem[]>,
    handleScheduleUpdated: vi.fn<() => void>(),
    handleEvolutionReflected: vi.fn<() => void>(),
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

describe('daemon event handler — evolution reflection broadcast', () => {
  it('routes evolution.reflected to the candidate refresh callback', () => {
    const context = createContext()
    const handler = createDaemonEventHandler(context)

    const event: DaemonMessage = {
      id: 0,
      event: 'evolution.reflected',
      data: '进化引擎反思完成',
      details: { sessionId: 's9', processed: 1, candidates: 1, noAction: 0, skipped: 0 }
    }

    expect(() => handler(event)).not.toThrow()
    expect(context.handleEvolutionReflected).toHaveBeenCalledTimes(1)
  })
})

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

  it('collects runtime warnings onto the active assistant bubble', () => {
    const context = createContext()
    const thread = threadFixture()
    context.threads.value.push(thread)
    const handler = createDaemonEventHandler(context)
    const assistant = thread.messages[0]!

    handler(chatEvent('warning', '工具 bash 被执行策略拒绝（deny）'))
    handler(chatEvent('warning', '工具 bash 被执行策略拒绝（deny）'))
    expect(assistant.warnings).toEqual(['工具 bash 被执行策略拒绝（deny）'])

    handler(chatEvent('warning', '模型连接中断，已保留中断前生成的部分输出'))
    expect(assistant.warnings?.length).toBe(2)
    // 告警是补充信息，绝不能把回合状态从思考/生成中改掉。
    expect(assistant.state).toBe('thinking')
  })

  it('keeps the argument summary next to the result for the expandable tool row', () => {
    const context = createContext()
    const thread = threadFixture()
    context.threads.value.push(thread)
    const handler = createDaemonEventHandler(context)
    const assistant = thread.messages[0]!

    handler({ ...chatEvent('tool_start', 'read_file'), details: { callId: 'call-1', summary: 'src/main.ts' } })
    expect(assistant.tools![0]!.args).toBe('src/main.ts')
    expect(assistant.tools![0]!.detail).toBeUndefined()
    expect(assistant.tools![0]!.state).toBe('running')

    handler({
      ...chatEvent('tool_done', '已读取 120 行'),
      details: { callId: 'call-1', success: true, durationMs: 1500 }
    })
    expect(assistant.tools![0]!.detail).toBe('已读取 120 行')
    // 结果到达后入参必须还在：展开行要同时回答「按什么调的」和「调到了什么」。
    expect(assistant.tools![0]!.args).toBe('src/main.ts')
    expect(assistant.tools![0]!.durationMs).toBe(1500)
    expect(assistant.tools![0]!.state).toBe('done')
  })

  it('carries the turn finish reason onto the terminal bubble', () => {
    const context = createContext()
    const thread = threadFixture()
    context.threads.value.push(thread)
    const handler = createDaemonEventHandler(context)
    const assistant = thread.messages[0]!

    handler(chatEvent('delta', '半截回答'))
    handler({ ...chatEvent('done', '半截回答'), details: { finishReason: 'length' } })
    expect(assistant.finishReason).toBe('length')
    expect(finishReasonLabel(assistant.finishReason)).toContain('截断')
    // 预算闸门将回合刹车时同样必须可见，不能显示成正常收尾。
    expect(finishReasonLabel('budget')).toContain('预算')
    // 正常完成与工具调用步骤都不该被标注成「没答完」。
    expect(finishReasonLabel('stop')).toBeUndefined()
    expect(finishReasonLabel('tool_calls')).toBeUndefined()
    expect(finishReasonLabel(undefined)).toBeUndefined()
  })

  it('stores structured error detail for the error card', () => {
    const context = createContext()
    const thread = threadFixture()
    context.threads.value.push(thread)
    const handler = createDaemonEventHandler(context)
    const assistant = thread.messages[0]!

    handler(chatEvent('delta', '半截回答'))
    handler(chatEvent('error', 'HTTP 429: rate limit exceeded', 's1'))

    expect(assistant.state).toBe('error')
    expect(assistant.errorDetail).toBe('HTTP 429: rate limit exceeded')
    // Technical block stays in content (persistence), card strips it for display.
    expect(assistant.content).toContain('模型调用失败：')
    expect(extractModelError(assistant.content)).toBe('HTTP 429: rate limit exceeded')
    expect(stripModelErrorBlock(assistant.content)).toBe('半截回答')

    // Duplicate error events must not stack identical details twice.
    handler(chatEvent('error', 'HTTP 429: rate limit exceeded', 's1'))
    expect(assistant.content.match(/模型调用失败：/g)).toHaveLength(1)
  })
})

describe('daemon event handler — turn.interrupted (N17)', () => {
  beforeEach(() => {
    ;(globalThis as unknown as { window: unknown }).window = {
      haoyue: { notify: vi.fn<() => Promise<void>>().mockResolvedValue(undefined) }
    }
  })

  function interruptedEvent(sessionId: string): DaemonMessage {
    return { id: 0, event: 'turn.interrupted', data: '', details: { sessionId, reason: 'daemon_crash' } }
  }

  function runningThread(): ThreadItem {
    const thread: ThreadItem = {
      id: 't1', title: '任务', updatedAt: Date.now(), messages: [],
      sessionId: 's1', running: true, assistantId: 'a1'
    }
    thread.messages.push({ id: 'a1', role: 'assistant', content: '半截回答', state: 'streaming', createdAt: 1 })
    return thread
  }

  it('clears stale running state, badges the partial bubble and reloads', () => {
    const context = createContext()
    const thread = runningThread()
    context.threads.value.push(thread)
    const handler = createDaemonEventHandler(context)

    handler(interruptedEvent('s1'))

    expect(thread.running).toBe(false)
    expect(thread.assistantId).toBeUndefined()
    expect(thread.sessionLoaded).toBe(false)
    expect(thread.messages[0]!.interrupted).toBe(true)
    expect(thread.messages[0]!.state).toBe('done')
    expect(context.reloadThreadSession).toHaveBeenCalledWith(thread)
  })

  it('ignores session ids it does not know', () => {
    const context = createContext()
    const thread = runningThread()
    context.threads.value.push(thread)
    const handler = createDaemonEventHandler(context)

    expect(() => handler(interruptedEvent('unknown'))).not.toThrow()
    expect(thread.running).toBe(true)
    expect(context.reloadThreadSession).not.toHaveBeenCalled()
  })
})

describe('daemon event handler — turn undo ledger (TurnScope)', () => {
  beforeEach(() => {
    ;(globalThis as unknown as { window: unknown }).window = {
      haoyue: { notify: vi.fn<() => Promise<void>>().mockResolvedValue(undefined) }
    }
  })

  function terminalEvent(
    event: 'done' | 'cancelled' | 'error',
    details?: Record<string, unknown>
  ): DaemonMessage {
    return { id: 7, event, data: '', sessionId: 's1', requestMethod: 'chat', details }
  }

  function finishedThread(): ThreadItem {
    return {
      id: 't1', title: '任务', updatedAt: Date.now(), messages: [],
      sessionId: 's1', running: true, requestId: 7, assistantId: 'a1'
    }
  }

  it('stores undoableFiles from the terminal envelope', () => {
    const context = createContext()
    const thread = finishedThread()
    context.threads.value.push(thread)
    const handler = createDaemonEventHandler(context)

    handler(terminalEvent('done', { undoableFiles: ['src/a.ts', 'src/b.ts'] }))

    expect(thread.undoableFiles).toEqual(['src/a.ts', 'src/b.ts'])
    expect(thread.running).toBe(false)
  })

  it('clears undoableFiles when the turn changed nothing or an older list existed', () => {
    const context = createContext()
    const thread = finishedThread()
    thread.undoableFiles = ['old.ts']
    context.threads.value.push(thread)
    const handler = createDaemonEventHandler(context)

    handler(terminalEvent('done'))
    expect(thread.undoableFiles).toBeUndefined()
  })

  it('filters non-string entries from a malformed payload', () => {
    const context = createContext()
    const thread = finishedThread()
    context.threads.value.push(thread)
    const handler = createDaemonEventHandler(context)

    handler(terminalEvent('cancelled', { undoableFiles: ['ok.ts', 42, '', null] }))

    expect(thread.undoableFiles).toEqual(['ok.ts'])
  })
})

describe('daemon event handler — local model load progress', () => {
  function runningThread(): ThreadItem {
    return {
      id: 't1',
      title: '任务',
      updatedAt: Date.now(),
      messages: [],
      sessionId: 's1',
      running: true,
      phase: '思考中'
    }
  }

  function loadEvent(phase: string, percent: number): DaemonMessage {
    return {
      id: 0,
      event: 'model.load.progress',
      data: '本地模型加载进度',
      details: { model: 'gemma.gguf', phase, percent }
    }
  }

  it('labels the running thread with the loading phase', () => {
    const context = createContext()
    const thread = runningThread()
    context.threads.value.push(thread)
    const handler = createDaemonEventHandler(context)

    handler(loadEvent('Loading', 0.624))

    expect(thread.phase).toBe('加载本地模型 62%')
  })

  it('ignores progress when no thread is running', () => {
    const context = createContext()
    const thread = runningThread()
    thread.running = false
    context.threads.value.push(thread)
    const handler = createDaemonEventHandler(context)

    handler(loadEvent('Loading', 0.3))

    expect(thread.phase).toBe('思考中')
  })

  it('keeps the chip alive on ready and reports failure', () => {
    const context = createContext()
    const thread = runningThread()
    thread.phase = '加载本地模型 62%'
    context.threads.value.push(thread)
    const handler = createDaemonEventHandler(context)

    handler(loadEvent('Ready', 1))
    expect(thread.phase).toBe('思考中')

    handler(loadEvent('Failed', 1))
    expect(thread.phase).toBe('本地模型加载失败')
  })
})
