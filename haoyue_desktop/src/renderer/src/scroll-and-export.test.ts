import { describe, expect, it } from 'vitest'
import { buildConversationMarkdown } from './app-helpers'
import { clearScrollPositions, readScrollPosition, saveScrollPosition, scrollPositionCount } from './scroll-memory'
import type { ChatMessage, ThreadItem } from './types'

function threadOf(messages: ChatMessage[], title = '导出测试会话'): ThreadItem {
  return { id: 't1', title, messages } as unknown as ThreadItem
}

function user(id: string, content: string, createdAt = 1_700_000_000_000): ChatMessage {
  return { id, role: 'user', content, createdAt }
}

function assistant(id: string, content: string, extra: Partial<ChatMessage> = {}): ChatMessage {
  return { id, role: 'assistant', content, createdAt: 1_700_000_060_000, ...extra }
}

describe('scroll-memory (1.6 阅读位恢复)', () => {
  it('stores exact scrollTop and returns 0 when unknown', () => {
    saveScrollPosition('thread-a', 1234.5)
    expect(readScrollPosition('thread-a')).toBe(1234.5) // 原值保存：恢复偏差 <1px
    expect(readScrollPosition('thread-b')).toBe(0)
  })

  it('treats non-positive / invalid positions as "follow bottom" (no memory)', () => {
    saveScrollPosition('thread-c', 500)
    expect(readScrollPosition('thread-c')).toBe(500)
    saveScrollPosition('thread-c', 0) // 回到底部 = 清除记忆
    expect(readScrollPosition('thread-c')).toBe(0)
    saveScrollPosition('thread-c', Number.NaN)
    expect(readScrollPosition('thread-c')).toBe(0)
  })

  it('clears memories except the kept thread (task switch/delete path)', () => {
    saveScrollPosition('keep', 10)
    saveScrollPosition('drop-1', 20)
    saveScrollPosition('drop-2', 30)
    clearScrollPositions('keep')
    expect(readScrollPosition('keep')).toBe(10)
    expect(scrollPositionCount()).toBe(1)
    clearScrollPositions()
    expect(scrollPositionCount()).toBe(0)
  })
})

describe('buildConversationMarkdown (1.6 会话导出)', () => {
  it('includes every message with role, time and content', () => {
    const thread = threadOf([
      user('u1', '第一问'),
      assistant('a1', '第一答', { modelRef: 'openai/gpt-4o' }),
      user('u2', '第二问'),
      assistant('a2', '第二答'),
    ])
    const markdown = buildConversationMarkdown(thread, 1_700_000_100_000)

    expect(markdown.startsWith('# 导出测试会话')).toBe(true)
    expect(markdown).toContain('消息数：4')
    // 消息完整率 100%：4 条全部落盘，顺序与角色正确。
    for (const content of ['第一问', '第一答', '第二问', '第二答']) expect(markdown).toContain(content)
    expect(markdown.indexOf('第一问')).toBeLessThan(markdown.indexOf('第一答'))
    expect(markdown).toContain('## 用户')
    expect(markdown).toContain('## 助手')
    expect(markdown).toContain('openai/gpt-4o')
  })

  it('summarizes tool calls without dumping raw results', () => {
    const thread = threadOf([
      assistant('a1', '看下结果', {
        tools: [
          { id: 't1', name: 'read_file', state: 'done', args: '{"path":"src/a.ts"}', detail: 'export const a = 1\n第二行' },
          { id: 't2', name: 'bash', state: 'error', args: 'npm test', detail: 'failed' },
        ],
      }),
    ])
    const markdown = buildConversationMarkdown(thread)
    expect(markdown).toContain('- `read_file` · 完成 · {"path":"src/a.ts"}')
    expect(markdown).toContain('- `bash` · 失败 · npm test')
    expect(markdown).not.toContain('第二行') // 只取首行，不导出全文
  })

  it('marks interrupted turns and non-stop finish reasons', () => {
    const thread = threadOf([
      assistant('a1', '半截回答', { interrupted: true, finishReason: 'length' }),
    ])
    const markdown = buildConversationMarkdown(thread)
    expect(markdown).toContain('回答被手动停止')
    expect(markdown).toContain('回合终态：length')
  })

  it('handles empty content and empty threads', () => {
    const empty = buildConversationMarkdown(threadOf([], ''))
    expect(empty.startsWith('# 未命名会话')).toBe(true)
    expect(empty).toContain('消息数：0')

    const blank = buildConversationMarkdown(threadOf([user('u1', '   ')]))
    expect(blank).toContain('（空消息）')
  })
})
