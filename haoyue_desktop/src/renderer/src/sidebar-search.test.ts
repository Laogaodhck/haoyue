import { describe, expect, it } from 'vitest'
import {
  REMOTE_SEARCH_MIN_CHARS,
  TASK_ROW_LIMIT,
  capTaskList,
  filterUnloadedHits,
  shouldSearchRemotely
} from './sidebar-search'
import type { SessionSearchHit, ThreadItem } from './types'

function hit(id: string, title: string, matchCount = 1): SessionSearchHit {
  return { id, title, matchCount }
}

function thread(id: string, updatedAt: number, extra: Partial<ThreadItem> = {}): ThreadItem {
  return { id, title: `任务 ${id}`, updatedAt, messages: [], ...extra }
}

describe('sidebar remote session search', () => {
  it('only searches remotely once the query is meaningful', () => {
    expect(REMOTE_SEARCH_MIN_CHARS).toBe(2)
    expect(shouldSearchRemotely('')).toBe(false)
    expect(shouldSearchRemotely('  ')).toBe(false)
    expect(shouldSearchRemotely('a')).toBe(false)
    expect(shouldSearchRemotely(' a ')).toBe(false)
    expect(shouldSearchRemotely('ab')).toBe(true)
    expect(shouldSearchRemotely('麒麟')).toBe(true)
  })

  it('hides backend hits already present in the local thread list', () => {
    const known = new Set(['s-known-1', 's-known-2'])
    const hits = [
      hit('s-known-1', '本地已有'),
      hit('s-remote-1', '未加载项目'),
      hit('s-known-2', '本地已有二'),
      hit('s-remote-2', '已归档')
    ]

    const filtered = filterUnloadedHits(hits, known)

    expect(filtered.map((item) => item.id)).toEqual(['s-remote-1', 's-remote-2'])
  })

  it('keeps every hit when nothing is loaded locally', () => {
    const hits = [hit('a', '甲'), hit('b', '乙')]
    expect(filterUnloadedHits(hits, new Set())).toEqual(hits)
  })
})

describe('sidebar task list windowing', () => {
  // 45 个任务，更新时间依次递增：t45 最新。
  const many = Array.from({ length: TASK_ROW_LIMIT + 5 }, (_, index) => thread(`t${index + 1}`, index + 1))

  it('renders only the most recent rows and reports the rest as hidden', () => {
    const list = capTaskList(many, { query: '', expanded: false })

    expect(list.rows).toHaveLength(TASK_ROW_LIMIT)
    expect(list.hidden).toBe(5)
    expect(list.rows[0]?.id).toBe(`t${many.length}`)
    expect(list.rows.at(-1)?.id).toBe('t6')
  })

  it('keeps the active task even when it falls below the cut line', () => {
    const list = capTaskList(many, { query: '', expanded: false, activeThreadId: 't2' })

    expect(list.rows.map((item) => item.id)).toContain('t2')
    // 补进来的当前任务算作已渲染：多一行，hidden 相应少一个。
    expect(list.rows).toHaveLength(TASK_ROW_LIMIT + 1)
    expect(list.hidden).toBe(4)
  })

  it('returns everything once the list has been expanded', () => {
    const list = capTaskList(many, { query: '', expanded: true })

    expect(list.rows).toHaveLength(many.length)
    expect(list.hidden).toBe(0)
  })

  it('drops archived tasks and applies the sidebar filter before counting', () => {
    const mixed = [
      ...many,
      thread('archived', 999, { archived: true }),
      thread('match', 998, { title: '麒麟 方案' })
    ]

    expect(capTaskList(mixed, { query: '', expanded: false }).rows.map((item) => item.id)).not.toContain('archived')

    const searched = capTaskList(mixed, { query: '麒麟', expanded: false })
    expect(searched.rows.map((item) => item.id)).toEqual(['match'])
    expect(searched.hidden).toBe(0)
  })

  it('leaves short lists untouched so ordinary workspaces see no change', () => {
    const few = many.slice(0, 3)
    const list = capTaskList(few, { query: '', expanded: false })

    expect(list.rows).toHaveLength(3)
    expect(list.hidden).toBe(0)
  })
})
