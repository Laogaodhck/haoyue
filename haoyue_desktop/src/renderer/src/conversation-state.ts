import type { ChatMessage, ProjectItem, ThreadItem } from './types'

export type TerminalKind = 'done' | 'cancelled' | 'error'

/**
 * After a turn's terminal event no assistant bubble may remain in a generating state.
 * The terminal handler normally finalizes the bubble pointed to by thread.assistantId,
 * but that pointer can miss the real bubble (a steer created a fresh bubble, or the
 * message list was replaced while a request was in flight), which left the "..." dots
 * on screen forever even though the turn had already ended. This walks every message
 * and finalizes any bubble that is still thinking/streaming.
 */
export function finalizeAssistantBubbles(
  messages: ChatMessage[],
  kind: TerminalKind
): void {
  const terminalState = kind === 'error' ? 'error' : 'done'
  for (const item of messages) {
    if (item.role !== 'assistant') continue
    if (item.state !== 'thinking' && item.state !== 'streaming') continue
    item.state = terminalState
  }
}

/**
 * N17: a daemon crash can kill a turn mid-flight while the client keeps its stale
 * "running" state, which would leave the spinner (and guards built on it) stuck
 * forever. Clears the in-flight state, badges the half-finished assistant bubble,
 * and drops the loaded-session flag so the interruption notice appended by the
 * daemon gets pulled on the next reload.
 */
export function markThreadInterrupted(
  thread: ThreadItem,
  reload?: (thread: ThreadItem, project?: ProjectItem) => Promise<void> | void
): void {
  thread.running = false
  thread.queueDraining = false
  thread.assistantId = undefined
  for (let i = thread.messages.length - 1; i >= 0; i--) {
    const message = thread.messages[i]
    if (message?.role === 'assistant') {
      message.interrupted = true
      message.state = 'done'
      break
    }
  }
  thread.sessionLoaded = false
  void reload?.(thread)
}
