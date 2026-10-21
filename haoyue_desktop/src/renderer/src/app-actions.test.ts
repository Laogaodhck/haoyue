import { describe, expect, it, vi } from 'vitest'
import { createChatActions, type ChatActionDeps } from './app-actions'
import type { ChatMessage, ThreadItem } from './types'

function threadOf(messages: ChatMessage[]): ThreadItem {
  return { id: 't1', title: '测试会话', messages } as unknown as ThreadItem
}

function user(id: string, content: string): ChatMessage {
  return { id, role: 'user', content, createdAt: 1_700_000_000_000 }
}

function assistant(id: string, content: string): ChatMessage {
  return { id, role: 'assistant', content, createdAt: 1_700_000_060_000 }
}

function deps(thread: ThreadItem | null, overrides: Partial<ChatActionDeps> = {}) {
  const calls = {
    undoVisible: [] as boolean[],
    models: [] as string[],
    modes: [] as string[],
    depths: [] as string[],
    cleared: [] as string[],
    saved: [] as Array<{ name: string; content: string }>,
    notified: [] as string[],
  }
  const deps: ChatActionDeps = {
    getThread: () => thread,
    setUndoVisible: (visible) => calls.undoVisible.push(visible),
    switchModel: (model) => calls.models.push(model),
    switchMode: (mode) => calls.modes.push(mode),
    switchDepth: (level) => calls.depths.push(level),
    clearDraft: (t) => calls.cleared.push(t.id),
    saveTextFile: async (name, content) => { calls.saved.push({ name, content }); return `/tmp/${name}` },
    notify: (title) => calls.notified.push(title),
    models: () => ['openai/gpt-4o', 'local/qwen3'],
    ...overrides,
  }
  return { actions: createChatActions(deps), calls }
}

describe('createChatActions (1.5 slash 命令路由)', () => {
  it('routes /导出会话 to the markdown export with a safe file name', async () => {
    const thread = threadOf([user('u1', '问题'), assistant('a1', '回答')])
    const { actions, calls } = deps(thread)

    actions.slashCommand('export', '')
    await Promise.resolve()
    await vi.waitFor(() => expect(calls.saved).toHaveLength(1))
    expect(calls.saved[0]!.name).toMatch(/^测试会话-\d{4}-\d{2}-\d{2}\.md$/)
    expect(calls.saved[0]!.content).toContain('问题')
    expect(calls.saved[0]!.content).toContain('回答')
    expect(calls.notified).toContain('已导出会话')
  })

  it('routes /清空草稿 to draft clearing', () => {
    const thread = threadOf([])
    const { actions, calls } = deps(thread)
    actions.slashCommand('clear', '')
    expect(calls.cleared).toEqual(['t1'])
  })

  it('resolves /模型 arguments against the available models', () => {
    const { actions, calls } = deps(threadOf([]))
    actions.slashCommand('model', 'qwen')
    expect(calls.models).toEqual(['local/qwen3'])
    // 无匹配 → 显式提示，不静默失败。
    actions.slashCommand('model', '不存在的模型')
    expect(calls.notified).toContain('命令参数不合法')
  })

  it('routes /模式 and /深度 through the change channels', () => {
    const { actions, calls } = deps(threadOf([]))
    actions.slashCommand('mode', 'plan')
    expect(calls.modes).toEqual(['plan'])
    actions.slashCommand('depth', 'deep')
    expect(calls.depths).toHaveLength(1)
  })

  it('does nothing without an active thread', () => {
    const { actions, calls } = deps(null)
    actions.slashCommand('export', '')
    actions.slashCommand('clear', '')
    expect(calls.saved).toHaveLength(0)
    expect(calls.cleared).toHaveLength(0)
  })
})

describe('createChatActions (1.2 删除撤销 / 版本恢复)', () => {
  it('deletes a message, opens the undo window and restores it in place', () => {
    const first = assistant('a1', '第一条')
    const second = assistant('a2', '第二条')
    const thread = threadOf([first, second])
    const { actions, calls } = deps(thread)

    actions.deleteMessage(second)
    expect(thread.messages.map((m) => m.id)).toEqual(['a1'])
    expect(calls.undoVisible).toEqual([true])

    actions.undoDeletion()
    expect(thread.messages.map((m) => m.id)).toEqual(['a1', 'a2'])
    expect(calls.undoVisible).toEqual([true, false])
  })

  it('deleting the streaming bubble clears the assistant pointer', () => {
    const reply = assistant('a1', '流式中')
    const thread = threadOf([reply])
    thread.assistantId = 'a1'
    const { actions } = deps(thread)
    actions.deleteMessage(reply)
    expect(thread.assistantId).toBeUndefined()
  })

  it('restores the latest assistant version of a prompt', () => {
    const prompt = user('u1', '原问题')
    prompt.versions = [
      { role: 'assistant', content: '旧答案 A', createdAt: 1, reason: 'regenerate' },
      { role: 'assistant', content: '旧答案 B', createdAt: 2, reason: 'regenerate' },
    ]
    const thread = threadOf([prompt, assistant('a1', '当前答案')])
    const { actions, calls } = deps(thread)

    actions.restoreVersion(prompt)
    expect(thread.messages.map((m) => m.id)).toHaveLength(2)
    expect(thread.messages[1]!.content).toBe('旧答案 B') // 取版本链最近的 assistant 版本
    expect(calls.undoVisible).toContain(false)
  })

  it('ignores invalid delete/restore targets instead of corrupting the list', () => {
    const thread = threadOf([assistant('a1', '唯一消息')])
    const { actions, calls } = deps(thread)
    actions.deleteMessage(assistant('missing', ''))
    expect(thread.messages).toHaveLength(1)
    expect(calls.undoVisible).toHaveLength(0)

    actions.restoreVersion(assistant('missing', ''))
    expect(thread.messages).toHaveLength(1)
  })
})
