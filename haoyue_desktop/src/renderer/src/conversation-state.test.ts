import { describe, expect, it } from 'vitest'
import { finalizeAssistantBubbles, preserveTruncatedVersions, removeMessage, restoreAssistantVersion, restoreMessage } from './conversation-state'
import type { ChatMessage, ThreadItem } from './types'

function assistant(overrides: Partial<ChatMessage> = {}): ChatMessage {
  return { id: overrides.id ?? 'a', role: 'assistant', content: '', createdAt: 0, ...overrides }
}

function user(id: string, content = 'hi'): ChatMessage {
  return { id, role: 'user', content, createdAt: 0 }
}

function threadOf(messages: ChatMessage[]): ThreadItem {
  return { messages } as unknown as ThreadItem
}

describe('finalizeAssistantBubbles', () => {
  it('finalizes lingering thinking/streaming bubbles as done and leaves others untouched', () => {
    const messages = [
      assistant({ id: 'a', state: 'thinking' }),
      assistant({ id: 'b', state: 'streaming', content: 'done text' }),
      assistant({ id: 'c', state: 'done' }),
      { id: 'u', role: 'user' as const, content: 'hi', createdAt: 0 }
    ]
    finalizeAssistantBubbles(messages, 'done')
    expect(messages[0]!.state).toBe('done')
    expect(messages[1]!.state).toBe('done')
    expect(messages[2]!.state).toBe('done')
    expect(messages[3]!.state).toBeUndefined()
  })

  it('marks lingering bubbles as error for terminal error events', () => {
    const messages = [assistant({ state: 'streaming' }), assistant({ state: 'thinking' })]
    finalizeAssistantBubbles(messages, 'error')
    expect(messages[0]!.state).toBe('error')
    expect(messages[1]!.state).toBe('error')
  })

  it('finalizes a mid-turn steer bubble even when the captured assistant is the older one', () => {
    // A steer closes the current bubble and opens a fresh one; when the turn
    // settles the continuation only knows the old bubble, so the walk must
    // finalize both and not leave the fresh "..." placeholder on screen.
    const messages = [
      assistant({ id: 'captured', state: 'done', content: 'answer so far' }),
      assistant({ id: 'steer-bubble', state: 'streaming', content: 'steered answer' })
    ]
    finalizeAssistantBubbles(messages, 'done')
    expect(messages[1]!.state).toBe('done')
  })
})

describe('preserveTruncatedVersions (1.2 消息版本保留)', () => {
  it('regenerate: 把被截断的 assistant 回复压入保留锚点的 versions', () => {
    const prompt = user('u1:s0', '重写这段')
    const oldReply = assistant({ id: 'a1:s1', content: '旧回复', modelRef: 'prov/m1' })
    const thread = threadOf([prompt, oldReply])

    // regenerate 截断 keepCount=1：只保留 prompt，旧回复入版本链。
    preserveTruncatedVersions(thread, 1, 'regenerate')
    thread.messages = thread.messages.slice(0, 1)

    const versions = thread.messages[0]!.versions!
    expect(versions).toHaveLength(1)
    expect(versions[0]).toMatchObject({ role: 'assistant', content: '旧回复', reason: 'regenerate', modelRef: 'prov/m1' })
  })

  it('regenerate: 中间的 user/steer 消息不入版本链', () => {
    const prompt = user('u1:s0')
    const oldReply = assistant({ content: '旧回复' })
    const steer = user('u2:s2', '补充一句')
    const thread = threadOf([prompt, oldReply, steer])

    preserveTruncatedVersions(thread, 1, 'regenerate')
    // 只有 assistant 进版本；user 消息不重复保留（prompt 在 composer 里可见）。
    expect(thread.messages[0]!.versions).toHaveLength(1)
  })

  it('edit: 原 user 提示与其后回复整段入保留锚点的版本链', () => {
    const earlier = assistant({ id: 'a0:s0', content: '前文' })
    const beingEdited = user('u1:s1', '编辑前的原文')
    const reply = assistant({ content: '将被截断' })
    const thread = threadOf([earlier, beingEdited, reply])

    // edit 截断 keepCount=1：被截掉的整段历史（原提示 + 回复）都保留，可整体恢复。
    preserveTruncatedVersions(thread, 1, 'edit')
    const versions = earlier.versions!
    expect(versions).toHaveLength(2)
    expect(versions[0]).toMatchObject({ role: 'user', content: '编辑前的原文', reason: 'edit' })
    expect(versions[1]).toMatchObject({ role: 'assistant', content: '将被截断', reason: 'edit' })
  })

  it('capped: 版本链最多保留 10 条', () => {
    const prompt = user('u1:s0')
    const thread = threadOf([prompt])
    for (let round = 0; round < 13; round++) {
      thread.messages = [prompt, assistant({ content: `v${round}` })]
      preserveTruncatedVersions(thread, 1, 'regenerate')
    }
    expect(thread.messages[0]!.versions).toHaveLength(10)
    // 最旧的被裁掉：v0/v1/v2 不在链里。
    expect(thread.messages[0]!.versions![0]!.content).toBe('v3')
  })
})

describe('restoreAssistantVersion', () => {
  it('restores a version as a fresh assistant bubble after truncation', () => {
    const prompt = user('u1:s0', '问题')
    prompt.versions = [{ role: 'assistant', content: '历史版本内容', createdAt: 1, reason: 'regenerate', modelRef: 'prov/m1' }]
    const thread = threadOf([prompt, assistant({ content: '当前新回复' })])

    expect(restoreAssistantVersion(thread, 'u1:s0', 0)).toBe(true)
    expect(thread.messages).toHaveLength(2)
    const restored = thread.messages[1]!
    expect(restored.role).toBe('assistant')
    expect(restored.content).toBe('历史版本内容')
    expect(restored.state).toBe('done')
    expect(restored.modelRef).toBe('prov/m1')
  })

  it('returns false for stale references instead of corrupting the list', () => {
    const thread = threadOf([user('u1:s0')])
    expect(restoreAssistantVersion(thread, 'missing', 0)).toBe(false)
    expect(restoreAssistantVersion(thread, 'u1:s0', 5)).toBe(false)
    expect(restoreAssistantVersion(thread, 'u1:s0', 0)).toBe(false) // versions 为空
    expect(thread.messages).toHaveLength(1)
  })

  it('refuses to restore a user-message version as an assistant bubble', () => {
    const prompt = user('u1:s0')
    prompt.versions = [{ role: 'user', content: '编辑前的原文', createdAt: 1, reason: 'edit' }]
    const thread = threadOf([prompt, assistant({})])
    expect(restoreAssistantVersion(thread, 'u1:s0', 0)).toBe(false)
    expect(thread.messages).toHaveLength(2)
  })
})

describe('single-message delete + undo (1.2)', () => {
  it('removes the message and restores it at the original index', () => {
    const a = assistant({ id: 'a1', content: '第一条' })
    const b = assistant({ id: 'a2', content: '第二条' })
    const c = assistant({ id: 'a3', content: '第三条' })
    const thread = threadOf([a, b, c])

    const deletion = removeMessage(thread, 'a2')
    expect(deletion).not.toBeNull()
    expect(thread.messages.map((m) => m.id)).toEqual(['a1', 'a3'])

    restoreMessage(thread, deletion!)
    expect(thread.messages.map((m) => m.id)).toEqual(['a1', 'a2', 'a3'])
    expect(thread.messages[1]!.content).toBe('第二条')
  })

  it('returns null for unknown ids and clamps restore index after appends', () => {
    const thread = threadOf([assistant({ id: 'a1' })])
    expect(removeMessage(thread, 'nope')).toBeNull()

    const deletion = removeMessage(thread, 'a1')
    thread.messages.push(assistant({ id: 'new' }))
    restoreMessage(thread, deletion!)
    expect(thread.messages.map((m) => m.id)).toEqual(['a1', 'new'])
  })
})
