import { ref } from 'vue'
import { describe, expect, it, vi } from 'vitest'
import type { DaemonState } from '../../shared/ipc'
import type { ThreadItem } from './types'
import { createRuntimeReconnect, retryRuntimeConnection, RUNTIME_RECONNECT_ATTEMPTS } from './runtime-reconnect'

const offline = (error = 'offline'): DaemonState => ({ connected: false, endpoint: 'test', error })
const online = (): DaemonState => ({ connected: true, endpoint: 'test' })

describe('retryRuntimeConnection', () => {
  it('stops after five failed attempts', async () => {
    const connect = vi.fn(async () => offline())
    const wait = vi.fn(async () => undefined)

    const result = await retryRuntimeConnection(connect, { wait })

    expect(result.connected).toBe(false)
    expect(connect).toHaveBeenCalledTimes(RUNTIME_RECONNECT_ATTEMPTS)
    expect(wait).toHaveBeenCalledTimes(RUNTIME_RECONNECT_ATTEMPTS - 1)
  })

  it('returns immediately after a successful attempt', async () => {
    const states = [offline(), offline(), online()]
    const connect = vi.fn(async () => states.shift() ?? offline())

    const result = await retryRuntimeConnection(connect, { wait: async () => undefined })

    expect(result.connected).toBe(true)
    expect(connect).toHaveBeenCalledTimes(3)
  })
})

function createHarness(connect: () => Promise<DaemonState>, initialThreads: ThreadItem[] = []) {
  const daemonState = ref<DaemonState>(offline())
  const reconnecting = ref(false)
  const reconnectAttempt = ref(0)
  const reconnectPrompt = ref<{ startup: boolean; error?: string } | null>(null)
  const conversationLoading = ref(false)
  const conversationLoadError = ref('')
  const threads = ref<ThreadItem[]>(initialThreads)
  const loadRuntimeState = vi.fn(async () => undefined)
  const notify = vi.fn()
  const controller = createRuntimeReconnect({
    daemonState,
    reconnecting,
    reconnectAttempt,
    reconnectPrompt,
    conversationLoading,
    conversationLoadError,
    threads,
    loadRuntimeState,
    connect,
    notify
  })
  return { controller, daemonState, reconnectPrompt, reconnecting, loadRuntimeState, notify }
}

describe('createRuntimeReconnect', () => {
  it('does not auto-reconnect before the shell is ready', async () => {
    const connect = vi.fn(async () => online())
    const harness = createHarness(connect)

    harness.controller.handleDaemonState(offline())
    await new Promise((resolve) => setTimeout(resolve, 0))

    expect(connect).not.toHaveBeenCalled()
  })

  it('runs one cycle for concurrent disconnect signals', async () => {
    let resolveConnect: (state: DaemonState) => void = () => {}
    const connect = vi.fn(() => new Promise<DaemonState>((resolve) => { resolveConnect = resolve }))
    const harness = createHarness(connect)
    harness.controller.setAppReady(true)

    const first = harness.controller.runReconnectCycle(true)
    const second = harness.controller.runReconnectCycle(true)
    resolveConnect(online())

    await expect(first).resolves.toBe(true)
    await expect(second).resolves.toBe(true)
    expect(connect).toHaveBeenCalledTimes(1)
    expect(harness.loadRuntimeState).toHaveBeenCalledTimes(1)
  })

  it('reports the running-turn count on a reconnect', () => {
    const harness = createHarness(async () => online(), [{ id: 'a', running: true } as ThreadItem])
    harness.controller.handleDaemonState(online())
    harness.controller.handleDaemonState(offline())
    harness.notify.mockClear()

    harness.controller.handleDaemonState(online())

    expect(harness.notify).toHaveBeenCalledWith('已重新连接', '连接已恢复，1 个运行中的任务已恢复跟踪。')
  })

  it('pauses automatic reconnect after a manual cancel', async () => {
    const connect = vi.fn(async () => online())
    const harness = createHarness(connect)
    harness.controller.setAppReady(true)
    harness.reconnectPrompt.value = { startup: false, error: 'nope' }

    harness.controller.cancelRuntimeReconnect()
    harness.controller.handleDaemonState(offline())
    await new Promise((resolve) => setTimeout(resolve, 0))

    expect(connect).not.toHaveBeenCalled()
    expect(harness.reconnectPrompt.value).toBeNull()
  })
})
