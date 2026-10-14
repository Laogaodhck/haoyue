import { describe, expect, it } from 'vitest'
import { journalCursor, openTurnsOf, recoverTurns, type JournalRow } from './turn-recovery'
import type { ThreadItem } from './types'

function terminal(
  id: number,
  sessionId: string,
  timestamp: string,
  payload: Record<string, unknown> = {}
): JournalRow {
  return { id, timestamp, type: 'TurnCompletedEvent', event: 'turn_completed', sessionId, payload }
}

describe('journalCursor', () => {
  it('takes the highest id of the batch', () => {
    expect(journalCursor([terminal(3, 's1', '2026-10-14T01:00:00Z'), terminal(9, 's1', '2026-10-14T01:05:00Z')], 4))
      .toBe(9)
  })

  it('never rewinds below the current cursor', () => {
    expect(journalCursor([terminal(2, 's1', '2026-10-14T01:00:00Z')], 8)).toBe(8)
  })
})

describe('recoverTurns', () => {
  it('resolves the newest terminal per session and skips older turns', () => {
    const rows: JournalRow[] = [
      terminal(1, 's1', '2026-10-14T00:50:00Z', { cancelled: false }),
      terminal(2, 's2', '2026-10-14T01:10:00Z', { error: '模型调用失败' }),
      terminal(5, 's1', '2026-10-14T01:20:00Z', { finishReason: 'stop', undoableFiles: ['a.ts', 42, ''] })
    ]
    const turns = [
      { threadId: 't1', sessionId: 's1', startedAt: Date.parse('2026-10-14T01:00:00Z') },
      { threadId: 't2', sessionId: 's2', startedAt: Date.parse('2026-10-14T01:05:00Z') }
    ]
    expect(recoverTurns(rows, turns)).toEqual([
      {
        threadId: 't1',
        journalId: 5,
        outcome: 'done',
        error: undefined,
        finishReason: 'stop',
        undoableFiles: ['a.ts']
      },
      {
        threadId: 't2',
        journalId: 2,
        outcome: 'error',
        error: '模型调用失败',
        finishReason: undefined,
        undoableFiles: undefined
      }
    ])
  })

  it('drops terminals that belong to a turn the client started later', () => {
    const rows = [terminal(4, 's1', '2026-10-14T01:00:00Z')]
    const turns = [{ threadId: 't1', sessionId: 's1', startedAt: Date.parse('2026-10-14T02:00:00Z') }]
    expect(recoverTurns(rows, turns)).toEqual([])
  })

  it('marks a cancelled turn as interrupted, not as a finished answer', () => {
    const rows = [terminal(7, 's1', '2026-10-14T01:00:00Z', { cancelled: true })]
    const turns = [{ threadId: 't1', sessionId: 's1', startedAt: 0 }]
    expect(recoverTurns(rows, turns)[0]?.outcome).toBe('cancelled')
  })

  it('ignores non-terminal rows and rows without a session', () => {
    const rows: JournalRow[] = [
      { id: 1, timestamp: '2026-10-14T01:00:00Z', type: 'ErrorEvent', event: 'error', payload: { message: 'boom' } },
      { id: 2, timestamp: '2026-10-14T01:00:00Z', type: 'TurnCompletedEvent', event: 'turn_completed', payload: {} }
    ]
    expect(recoverTurns(rows, [{ threadId: 't1', sessionId: 's1', startedAt: 0 }])).toEqual([])
  })

  it('returns nothing when no turn is in flight', () => {
    expect(recoverTurns([terminal(1, 's1', '2026-10-14T01:00:00Z')], [])).toEqual([])
  })
})

describe('openTurnsOf', () => {
  function thread(overrides: Partial<ThreadItem>): ThreadItem {
    return {
      id: 't1',
      title: '任务',
      messages: [],
      updatedAt: 0,
      archived: false,
      ...overrides
    } as ThreadItem
  }

  it('starts from the in-flight assistant bubble timestamp', () => {
    const threads = [
      thread({ running: true, sessionId: 's1', assistantId: 'm2', messages: [
        { id: 'm1', role: 'user', content: '问题', createdAt: 1_000 } as never,
        { id: 'm2', role: 'assistant', content: '', createdAt: 2_500, state: 'streaming' } as never
      ] }),
      thread({ running: false, sessionId: 's2' })
    ]
    expect(openTurnsOf(threads)).toEqual([{ threadId: 't1', sessionId: 's1', startedAt: 2_500 }])
  })

  it('accepts any terminal when the placeholder bubble is gone', () => {
    const threads = [thread({ running: true, sessionId: 's1' })]
    expect(openTurnsOf(threads)).toEqual([{ threadId: 't1', sessionId: 's1', startedAt: 0 }])
  })
})
