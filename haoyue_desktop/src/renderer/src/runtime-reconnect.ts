import type { Ref } from 'vue'
import type { DaemonState } from '../../shared/ipc'
import type { ThreadItem } from './types'

export const RUNTIME_RECONNECT_ATTEMPTS = 5

interface RetryOptions {
  attempts?: number
  delayMs?: number
  onAttempt?: (attempt: number, maximum: number) => void
  wait?: (milliseconds: number) => Promise<void>
}

const defaultWait = (milliseconds: number): Promise<void> =>
  new Promise((resolve) => globalThis.setTimeout(resolve, milliseconds))

export async function retryRuntimeConnection(
  connect: () => Promise<DaemonState>,
  options: RetryOptions = {}
): Promise<DaemonState> {
  const maximum = options.attempts ?? RUNTIME_RECONNECT_ATTEMPTS
  const delayMs = options.delayMs ?? 700
  const wait = options.wait ?? defaultWait
  let lastState: DaemonState = { connected: false, endpoint: '' }

  for (let attempt = 1; attempt <= maximum; attempt++) {
    options.onAttempt?.(attempt, maximum)
    try {
      lastState = await connect()
    } catch (error) {
      lastState = {
        connected: false,
        endpoint: lastState.endpoint,
        error: error instanceof Error ? error.message : String(error)
      }
    }
    if (lastState.connected) return lastState
    if (attempt < maximum) await wait(delayMs)
  }

  return lastState
}

export interface RuntimeReconnectDeps {
  daemonState: Ref<DaemonState>
  reconnecting: Ref<boolean>
  reconnectAttempt: Ref<number>
  reconnectPrompt: Ref<{ startup: boolean; error?: string } | null>
  conversationLoading: Ref<boolean>
  conversationLoadError: Ref<string>
  threads: Ref<ThreadItem[]>
  loadRuntimeState: () => Promise<void>
  connect?: () => Promise<DaemonState>
  notify?: (title: string, body: string) => void
  /** 连接目标切换（本机 ⇄ 局域网）：旧 Runtime 的会话状态必须由调用方丢弃。 */
  onTargetChanged?: (endpoint: string, previous: string) => void
}

/**
 * Owns the reconnect state machine for the app shell: one in-flight cycle,
 * a pause flag once the user cancels, and the reconnect prompt shown when a
 * startup or manual cycle exhausts its attempts.
 */
export function createRuntimeReconnect(deps: RuntimeReconnectDeps) {
  const connect = deps.connect ?? (() => window.haoyue.daemon.connect())
  const notify = deps.notify ?? ((title: string, body: string) => { void window.haoyue?.notify?.(title, body) })
  let reconnectTask: Promise<boolean> | null = null
  let automaticReconnectPaused = false
  let appReadyForRecovery = false
  // H24: reconnect success deserves its own feedback — previously the UI just silently
  // returned to normal and users could not tell whether their running tasks survived.
  let hasBeenConnected = false
  // 目标切换（本机 ⇄ 局域网）不是「断线恢复」：旧 Runtime 的会话状态已失效，
  // 调用方要清空本地列表，重连提示也不该再报「N 个运行中的任务已恢复跟踪」。
  let lastEndpoint = ''
  let targetSwitched = false

  function handleDaemonState(state: DaemonState): void {
    const wasConnected = deps.daemonState.value.connected
    if (lastEndpoint && lastEndpoint !== state.endpoint) {
      deps.onTargetChanged?.(state.endpoint, lastEndpoint)
      targetSwitched = true
    }
    lastEndpoint = state.endpoint
    deps.daemonState.value = state
    if (state.connected) {
      automaticReconnectPaused = false
      deps.reconnectPrompt.value = null
      if (wasConnected === false && hasBeenConnected && !targetSwitched) {
        const runningCount = deps.threads.value.filter((thread) => thread.running).length
        notify('已重新连接',
          runningCount > 0 ? `连接已恢复，${runningCount} 个运行中的任务已恢复跟踪。` : '连接已恢复。')
      }
      targetSwitched = false
      hasBeenConnected = true
      return
    }
    if (deps.conversationLoading.value) {
      deps.conversationLoading.value = false
      deps.conversationLoadError.value = '已与 Haoyue Runtime 断开连接，正在尝试重连…'
    }
    if (!appReadyForRecovery || deps.reconnecting.value || reconnectTask
      || automaticReconnectPaused || deps.reconnectPrompt.value) return
    void runReconnectCycle(false)
  }

  async function runReconnectCycle(startup: boolean): Promise<boolean> {
    if (reconnectTask) return reconnectTask
    reconnectTask = (async () => {
      deps.reconnecting.value = true
      deps.reconnectPrompt.value = null
      const state = await retryRuntimeConnection(
        connect,
        { onAttempt: (attempt) => { deps.reconnectAttempt.value = attempt } })
      deps.daemonState.value = state
      if (state.connected) {
        automaticReconnectPaused = false
        await deps.loadRuntimeState()
        return true
      }
      deps.reconnectPrompt.value = { startup, error: state.error }
      return false
    })().finally(() => {
      deps.reconnecting.value = false
      deps.reconnectAttempt.value = 0
      reconnectTask = null
    })
    return reconnectTask
  }

  async function reconnectDaemon(): Promise<void> {
    automaticReconnectPaused = false
    deps.reconnectPrompt.value = null
    if (deps.daemonState.value.connected) {
      await deps.loadRuntimeState()
      return
    }
    await runReconnectCycle(false)
  }

  async function refreshRuntimeState(): Promise<void> {
    if (deps.daemonState.value.connected) await deps.loadRuntimeState()
    else await reconnectDaemon()
  }

  function continueRuntimeReconnect(): void {
    const startup = deps.reconnectPrompt.value?.startup ?? false
    deps.reconnectPrompt.value = null
    automaticReconnectPaused = false
    void runReconnectCycle(startup)
  }

  function cancelRuntimeReconnect(): void {
    if (deps.reconnectPrompt.value?.startup) {
      void window.haoyue.closeApp()
      return
    }
    deps.reconnectPrompt.value = null
    automaticReconnectPaused = true
  }

  /** The shell only auto-reconnects after mount and before unmount. */
  function setAppReady(ready: boolean): void {
    appReadyForRecovery = ready
  }

  return {
    handleDaemonState,
    runReconnectCycle,
    reconnectDaemon,
    refreshRuntimeState,
    continueRuntimeReconnect,
    cancelRuntimeReconnect,
    setAppReady
  }
}
