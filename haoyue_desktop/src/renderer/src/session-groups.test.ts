import { describe, expect, it } from 'vitest'
import { groupThreadsByTime, sessionGroupKeyFor } from './session-groups'
import type { ThreadItem } from './types'

function makeThread(id: string, updatedAt: number, extra: Partial<ThreadItem> = {}): ThreadItem {
  return {
    id,
    title: `t-${id}`,
    updatedAt,
    messages: [],
    ...extra
  }
}

const NOW = new Date(2026, 9, 20, 12, 0, 0) // 2026-10-20 12:00 本地时间
const DAY = 86_400_000
const todayStart = new Date(2026, 9, 20, 0, 0, 0).getTime()

describe('sessionGroupKeyFor', () => {
  it('今天：本地日 0 点起都算今天', () => {
    expect(sessionGroupKeyFor(todayStart, NOW)).toBe('today')
    expect(sessionGroupKeyFor(NOW.getTime(), NOW)).toBe('today')
  })

  it('昨天：今天 0 点之前、昨天 0 点之后', () => {
    expect(sessionGroupKeyFor(todayStart - 1, NOW)).toBe('yesterday')
    expect(sessionGroupKeyFor(todayStart - DAY, NOW)).toBe('yesterday')
  })

  it('近 7 天：昨天往前再数 5 个自然日', () => {
    expect(sessionGroupKeyFor(todayStart - DAY - 1, NOW)).toBe('week')
    expect(sessionGroupKeyFor(todayStart - 6 * DAY, NOW)).toBe('week')
  })

  it('近 30 天', () => {
    expect(sessionGroupKeyFor(todayStart - 6 * DAY - 1, NOW)).toBe('month')
    expect(sessionGroupKeyFor(todayStart - 29 * DAY, NOW)).toBe('month')
  })

  it('更早', () => {
    expect(sessionGroupKeyFor(todayStart - 29 * DAY - 1, NOW)).toBe('earlier')
  })

  it('非法时间戳归入更早', () => {
    expect(sessionGroupKeyFor(Number.NaN, NOW)).toBe('earlier')
  })
})

describe('groupThreadsByTime', () => {
  it('按分组顺序输出，组内按更新时间倒序', () => {
    const threads = [
      makeThread('old', todayStart - 40 * DAY),
      makeThread('month', todayStart - 10 * DAY),
      makeThread('today-b', NOW.getTime() - 1000),
      makeThread('yesterday', todayStart - DAY),
      makeThread('today-a', NOW.getTime())
    ]
    const groups = groupThreadsByTime(threads, NOW)
    expect(groups.map((group) => group.key)).toEqual(['today', 'yesterday', 'month', 'earlier'])
    const today = groups[0]!
    expect(today.rows.map((thread) => thread.id)).toEqual(['today-a', 'today-b'])
    expect(today.label).toBe('今天')
  })

  it('归档会话不参与分组，空分组不输出', () => {
    const threads = [
      makeThread('archived', NOW.getTime(), { archived: true }),
      makeThread('today', NOW.getTime())
    ]
    const groups = groupThreadsByTime(threads, NOW)
    expect(groups).toHaveLength(1)
    expect(groups[0]!.rows.map((thread) => thread.id)).toEqual(['today'])
  })

  it('空列表返回空数组', () => {
    expect(groupThreadsByTime([], NOW)).toEqual([])
  })
})
