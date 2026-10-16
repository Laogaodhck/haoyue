import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import type { DaemonState } from '../../shared/ipc'
import type { ChatMessage, ImageAttachment, ThreadItem } from './types'
import {
  imageSourceKey,
  loadEarlierMessages,
  reloadThreadSession,
  requestSessionImageBytes,
  SESSION_PAGE_SIZE,
  sessionImageSources
} from './session-sync'

const daemonRequest = vi.fn<(method: string, params: Record<string, unknown>) => Promise<{ data: string }>>()
const context = { daemonState: ref({ connected: true } as unknown as DaemonState) }

function chat(content: string): ChatMessage {
  return { id: `local:${content}`, role: 'user', content } as ChatMessage
}

function thread(extra: Partial<ThreadItem> = {}): ThreadItem {
  return {
    id: 'task-1',
    title: '任务',
    updatedAt: 0,
    messages: [],
    sessionId: 's-1',
    sessionLoaded: true,
    ...extra
  }
}

function page(messages: Array<Record<string, unknown>>, extra: Record<string, unknown> = {}): string {
  return JSON.stringify({
    id: 's-1',
    createdAt: '2026-10-16T00:00:00Z',
    updatedAt: '2026-10-16T00:01:00Z',
    messages,
    ...extra
  })
}

async function settled(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0))
}

beforeEach(() => {
  daemonRequest.mockReset()
  for (const key of Object.keys(sessionImageSources)) delete sessionImageSources[key]
  ;(globalThis as unknown as { window: unknown }).window = { haoyue: { daemon: { request: daemonRequest } } }
})

describe('session paging', () => {
  it('prepends an earlier page in front and moves the cursor back', async () => {
    const item = thread({
      messages: [chat('近1'), chat('近2')],
      sessionFirstIndex: 6,
      sessionHasMore: true
    })
    daemonRequest.mockResolvedValueOnce({
      data: page(
        [{ role: 'user', text: '旧1' }, { role: 'assistant', text: '旧2' }],
        { total: 10, firstIndex: 4, hasMore: true }
      )
    })

    await loadEarlierMessages(context, item)

    expect(item.messages.map((message) => message.content)).toEqual(['旧1', '旧2', '近1', '近2'])
    // 全局序号参与消息 id，翻页后不会与已有窗口撞 key（v-for :key）。
    expect(item.messages.map((message) => message.id)).toEqual(['s-1:4', 's-1:5', 'local:近1', 'local:近2'])
    expect(item.sessionFirstIndex).toBe(4)
    expect(item.sessionTotal).toBe(10)
    expect(item.sessionHasMore).toBe(true)
    expect(item.loadingEarlier).toBe(false)
    expect(daemonRequest).toHaveBeenCalledWith('session.get', expect.objectContaining({
      id: 's-1',
      limit: SESSION_PAGE_SIZE,
      before: 6,
      global: true
    }))
  })

  it('stops paging once the runtime reports no earlier messages', async () => {
    const item = thread({ messages: [chat('近1')], sessionFirstIndex: 0, sessionHasMore: false })
    await loadEarlierMessages(context, item)
    expect(daemonRequest).not.toHaveBeenCalled()
    expect(item.messages).toHaveLength(1)
  })

  it('keeps the button retryable when a page fails', async () => {
    const item = thread({ messages: [chat('近1')], sessionFirstIndex: 6, sessionHasMore: true })
    daemonRequest.mockRejectedValueOnce(new Error('管道已关闭'))
    await loadEarlierMessages(context, item)
    expect(item.messages).toHaveLength(1)
    expect(item.loadingEarlier).toBe(false)
    expect(item.sessionFirstIndex).toBe(6)

    daemonRequest.mockResolvedValueOnce({
      data: page([{ role: 'user', text: '旧1' }], { total: 7, firstIndex: 5, hasMore: true })
    })
    await loadEarlierMessages(context, item)
    expect(item.messages.map((message) => message.content)).toEqual(['旧1', '近1'])
  })

  it('does not reload a thread that is mid-turn', async () => {
    const item = thread({ running: true, messages: [chat('进行中')] })
    await reloadThreadSession(context, item)
    expect(daemonRequest).not.toHaveBeenCalled()
    expect(item.messages).toHaveLength(1)
  })

  it('replaces the window and refreshes the header when idle', async () => {
    const item = thread({ messages: [chat('过期内容')], sessionFirstIndex: 3, sessionHasMore: true })
    daemonRequest.mockResolvedValueOnce({
      data: page([{ role: 'user', text: '新内容' }], { total: 2, firstIndex: 0, hasMore: false, title: '改名了' })
    })
    await reloadThreadSession(context, item)
    expect(item.messages.map((message) => message.content)).toEqual(['新内容'])
    expect(item.title).toBe('改名了')
    expect(item.sessionFirstIndex).toBe(0)
    expect(item.sessionHasMore).toBe(false)
  })
})

describe('deferred session image bytes', () => {
  const image: ImageAttachment = { id: 'img-1', name: 'a.png', mediaType: 'image/png', sizeBytes: 3 }

  it('fetches each pending image once, even for concurrent callers', async () => {
    daemonRequest.mockResolvedValue({
      data: JSON.stringify({ id: 'img-1', name: 'a.png', mediaType: 'image/png', data: 'AQID', sizeBytes: 3 })
    })
    const item = thread()

    requestSessionImageBytes(context, item, undefined, image)
    requestSessionImageBytes(context, item, undefined, image)
    await settled()

    expect(daemonRequest).toHaveBeenCalledTimes(1)
    expect(daemonRequest).toHaveBeenCalledWith('session.image', expect.objectContaining({
      id: 's-1',
      imageId: 'img-1',
      global: true
    }))
    expect(sessionImageSources[imageSourceKey('s-1', 'img-1')]).toBe('data:image/png;base64,AQID')
  })

  it('skips images that already carry bytes and threads without a session', async () => {
    requestSessionImageBytes(context, thread(), undefined, { ...image, data: 'AQID' })
    requestSessionImageBytes(context, thread({ sessionId: undefined }), undefined, image)
    expect(daemonRequest).not.toHaveBeenCalled()
  })

  it('stays silent offline and retryable after a failure', async () => {
    const offline = { daemonState: ref({ connected: false } as unknown as DaemonState) }
    requestSessionImageBytes(offline, thread(), undefined, image)
    expect(daemonRequest).not.toHaveBeenCalled()

    daemonRequest.mockRejectedValueOnce(new Error('not found'))
    requestSessionImageBytes(context, thread(), undefined, image)
    await settled()
    expect(sessionImageSources[imageSourceKey('s-1', 'img-1')]).toBeUndefined()

    daemonRequest.mockResolvedValueOnce({
      data: JSON.stringify({ id: 'img-1', mediaType: 'image/png', data: 'BAUG' })
    })
    requestSessionImageBytes(context, thread(), undefined, image)
    await settled()
    expect(sessionImageSources[imageSourceKey('s-1', 'img-1')]).toBe('data:image/png;base64,BAUG')
  })
})
