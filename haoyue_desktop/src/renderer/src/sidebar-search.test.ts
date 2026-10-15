import { describe, expect, it } from 'vitest'
import {
  REMOTE_SEARCH_MIN_CHARS,
  filterUnloadedHits,
  shouldSearchRemotely
} from './sidebar-search'
import type { SessionSearchHit } from './types'

function hit(id: string, title: string, matchCount = 1): SessionSearchHit {
  return { id, title, matchCount }
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
