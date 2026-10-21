import type { ChatMessage, MessageVersion, ProjectItem, ThreadItem } from './types'

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

// ---------------------------------------------------------------------------
// 1.2 消息版本保留 + 单条删除（全部为纯函数：状态机可测，App.vue 只做薄接线）

/** 单条删除的撤销凭据：原下标 + 消息本体，撤销时插回原位。 */
export interface PendingDeletion {
  index: number
  message: ChatMessage
}

/**
 * regenerate / edit 的后端截断已把消息从会话里移除，前端在截断视图前把被替换的
 * assistant 回复（regenerate）或原 user 提示（edit）压入保留区最后一条消息的
 * versions——仅本次会话可见，不持久化（后端没有版本存储）。
 *
 * keepCount 语义与 session.truncate 对齐：保留 messages.slice(0, keepCount)。
 */
export function preserveTruncatedVersions(
  thread: ThreadItem,
  keepCount: number,
  reason: 'regenerate' | 'edit'
): void {
  const anchor = thread.messages[keepCount - 1]
  if (!anchor) return
  for (let i = keepCount; i < thread.messages.length; i++) {
    const removed = thread.messages[i]
    if (!removed) continue
    // edit 场景被截断的第一条就是原 user 提示本身；regenerate 场景只保留 assistant 回复。
    if (reason === 'regenerate' && removed.role !== 'assistant') continue
    const versions = anchor.versions ?? (anchor.versions = [])
    const version: MessageVersion = {
      role: removed.role,
      content: removed.content,
      createdAt: removed.createdAt,
      reason,
      modelRef: removed.modelRef,
    }
    versions.push(version)
    // 版本链上限：历史版本是内存态，无界增长只会拖慢渲染。
    if (versions.length > 10) versions.splice(0, versions.length - 10)
  }
}

/**
 * 恢复某条 user 提示的一个历史 assistant 版本：移除该提示之后的现有回复，把版本
 * 内容作为新的 assistant 消息插入。返回 false 表示引用失效（消息/版本不存在）。
 */
export function restoreAssistantVersion(
  thread: ThreadItem,
  userMessageId: string,
  versionIndex: number
): boolean {
  const promptIndex = thread.messages.findIndex((item) => item.id === userMessageId)
  const prompt = promptIndex >= 0 ? thread.messages[promptIndex] : undefined
  const version = prompt?.versions?.[versionIndex]
  if (!prompt || !version || version.role !== 'assistant') return false
  thread.messages = thread.messages.slice(0, promptIndex + 1)
  thread.messages.push({
    id: `${userMessageId}:restored:${versionIndex}:${Date.now()}`,
    role: 'assistant',
    content: version.content,
    createdAt: Date.now(),
    state: 'done',
    modelRef: version.modelRef,
    tools: [],
  })
  return true
}

/** 单条删除：从列表移除并返回撤销凭据；消息不存在或已移除时返回 null。 */
export function removeMessage(thread: ThreadItem, messageId: string): PendingDeletion | null {
  const index = thread.messages.findIndex((item) => item.id === messageId)
  if (index < 0) return null
  const [message] = thread.messages.splice(index, 1)
  return message ? { index, message } : null
}

/** 撤销单条删除：插回原下标（原位可能已被新消息占用，插到凭据位置即可）。 */
export function restoreMessage(thread: ThreadItem, deletion: PendingDeletion): void {
  thread.messages.splice(Math.min(deletion.index, thread.messages.length), 0, deletion.message)
}

/**
 * 单条删除的撤销缓冲（1.2）：同一时刻只保留最近一次删除，TTL 过期自动作废。
 * 计时器与凭据管理集中在此，App.vue 只需要两个转发调用。
 */
export class DeletionUndoBuffer {
  private state: {
    thread: ThreadItem
    deletion: PendingDeletion
    timer: ReturnType<typeof setTimeout>
  } | null = null

  constructor(private readonly onExpire?: () => void) {}

  /** 删除并开启撤销窗口；消息不存在时返回 false 且不影响既有缓冲。 */
  delete(thread: ThreadItem, messageId: string, ttlMs = 5000): boolean {
    const deletion = removeMessage(thread, messageId)
    if (!deletion) return false
    this.clear()
    this.state = {
      thread,
      deletion,
      timer: setTimeout(() => { this.state = null; this.onExpire?.() }, ttlMs),
    }
    return true
  }

  /** 撤销最近一次删除（插回原位）。无待撤销时返回 false。 */
  undo(): boolean {
    const pending = this.state
    if (!pending) return false
    this.clear()
    restoreMessage(pending.thread, pending.deletion)
    return true
  }

  hasPending(): boolean {
    return this.state !== null
  }

  private clear(): void {
    if (!this.state) return
    clearTimeout(this.state.timer)
    this.state = null
  }
}
