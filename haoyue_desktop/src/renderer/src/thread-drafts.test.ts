import { beforeEach, describe, expect, it, vi } from 'vitest'
import { dropDraft, loadDrafts, persistDrafts, type ComposerDraft } from './thread-drafts'

function memoryStorage(): Storage {
  const entries = new Map<string, string>()
  return {
    get length() { return entries.size },
    clear: () => entries.clear(),
    getItem: (key: string) => entries.get(key) ?? null,
    key: (index: number) => [...entries.keys()][index] ?? null,
    removeItem: (key: string) => { entries.delete(key) },
    setItem: (key: string, value: string) => { entries.set(key, value) }
  }
}

const storage = memoryStorage()
vi.stubGlobal('localStorage', storage)

function draft(text: string): ComposerDraft {
  return {
    text,
    images: [{ id: 'img-1', name: 'shot.png', mediaType: 'image/png', sizeBytes: 4, data: 'data:image/png;base64,AAAA' }],
    files: [{ id: 'file-1', path: 'README.md', name: 'README.md', sizeBytes: 10, extension: 'md' }]
  }
}

function storedDrafts(): Record<string, ComposerDraft> {
  return JSON.parse(storage.getItem('haoyue-composer-drafts')!) as Record<string, ComposerDraft>
}

describe('thread-drafts', () => {
  beforeEach(() => storage.clear())

  it('drops image bytes before persisting so they cannot exhaust the quota', () => {
    persistDrafts(new Map([['t1', draft('hi')]]))

    const saved = storedDrafts()
    expect(saved.t1!.text).toBe('hi')
    expect(saved.t1!.images).toEqual([])
    expect(saved.t1!.files).toEqual(draft('hi').files)
    // 内存里的草稿仍保留图片，只影响重启后恢复。
    expect(loadDrafts().get('t1')!.images).toEqual([])
  })

  it('skips drafts that only held images', () => {
    persistDrafts(new Map([['t1', { ...draft(''), files: [] }]]))

    expect(storage.getItem('haoyue-composer-drafts')).toBe('{}')
  })

  it('dropDraft removes the entry and rewrites storage', () => {
    const drafts = new Map([['t1', draft('a')], ['t2', draft('b')]])
    persistDrafts(drafts)

    dropDraft(drafts, 't1')

    expect(drafts.has('t1')).toBe(false)
    expect(Object.keys(storedDrafts())).toEqual(['t2'])
  })

  it('loadDrafts treats corrupt storage as empty', () => {
    storage.setItem('haoyue-composer-drafts', '{not json')

    expect(loadDrafts().size).toBe(0)
  })
})
