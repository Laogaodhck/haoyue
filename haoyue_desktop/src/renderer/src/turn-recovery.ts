import type { ThreadItem } from './types'

/** 台账行投影（runtime `EventJournal.Project`）：稳定事件名 + camelCase 载荷 + 会话归属。 */
export interface JournalRow {
  id: number
  timestamp: string
  type: string
  event: string
  sessionId?: string
  payload?: Record<string, unknown>
}

/** 客户端仍认为在飞的回合：线程、会话与本地开始时间。 */
export interface OpenTurn {
  threadId: string
  sessionId: string
  startedAt: number
}

export type RecoveredOutcome = 'done' | 'cancelled' | 'error'

export interface RecoveredTurn {
  threadId: string
  journalId: number
  outcome: RecoveredOutcome
  error?: string
  finishReason?: string
  undoableFiles?: string[]
}

/** 台账里的回合终态行；实时帧用 done/cancelled/error，台账行只记一次 TurnCompletedEvent。 */
const TURN_TERMINAL_EVENT = 'turn_completed'

function toId(value: unknown): number {
  const id = Number(value)
  return Number.isFinite(id) && id > 0 ? Math.trunc(id) : 0
}

/** 回放游标：取本批行的最大 id，下次只问之后的行。 */
export function journalCursor(rows: readonly JournalRow[], current = 0): number {
  let cursor = Number.isFinite(current) && current > 0 ? Math.trunc(current) : 0
  for (const row of rows) cursor = Math.max(cursor, toId(row?.id))
  return cursor
}

/**
 * 断线期间结束的回合无法从实时帧得知（帧带的是请求 id，重连后那条请求已经不存在）。
 * 台账行是唯一线索：按会话取最新一条终态行，并用本地回合开始时间挡住上一回合的旧行——
 * 同一会话在崩溃前可能已经成功跑过好几轮，只认时间线之后的那条。
 */
export function recoverTurns(
  rows: readonly JournalRow[],
  openTurns: readonly OpenTurn[]
): RecoveredTurn[] {
  if (openTurns.length === 0) return []
  const latest = new Map<string, JournalRow>()
  for (const row of rows) {
    if (row?.event !== TURN_TERMINAL_EVENT) continue
    const sessionId = typeof row.sessionId === 'string' ? row.sessionId : ''
    if (!sessionId) continue
    const previous = latest.get(sessionId)
    if (!previous || toId(row.id) > toId(previous.id)) latest.set(sessionId, row)
  }

  const recovered: RecoveredTurn[] = []
  for (const turn of openTurns) {
    const row = latest.get(turn.sessionId)
    if (!row) continue
    const at = Date.parse(row.timestamp)
    if (Number.isFinite(at) && Number.isFinite(turn.startedAt) && at < turn.startedAt) continue
    const payload = row.payload ?? {}
    const error = typeof payload.error === 'string' && payload.error ? payload.error : undefined
    const files = Array.isArray(payload.undoableFiles)
      ? payload.undoableFiles.filter((item): item is string => typeof item === 'string' && item.length > 0)
      : undefined
    recovered.push({
      threadId: turn.threadId,
      journalId: toId(row.id),
      outcome: error ? 'error' : payload.cancelled === true ? 'cancelled' : 'done',
      error,
      finishReason: typeof payload.finishReason === 'string' && payload.finishReason
        ? payload.finishReason
        : undefined,
      undoableFiles: files?.length ? files : undefined
    })
  }
  return recovered
}

/**
 * 悬在半空的回合：占位气泡创建于回合开始，它的 createdAt 就是本地时间线起点。
 * 没有气泡（极端情况下消息列表被替换）时退回 0，即接受任何终态行。
 */
export function openTurnsOf(threads: readonly ThreadItem[]): OpenTurn[] {
  const opens: OpenTurn[] = []
  for (const thread of threads) {
    if (!thread.running || !thread.sessionId) continue
    const bubble = thread.messages.find((item) => item.id === thread.assistantId)
      ?? thread.messages.findLast((item) => item.role === 'assistant')
    opens.push({
      threadId: thread.id,
      sessionId: thread.sessionId,
      startedAt: bubble?.createdAt ?? 0
    })
  }
  return opens
}
