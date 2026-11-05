import { describe, expect, it } from 'vitest'
import {
  backgroundTaskFor,
  formatTaskTime,
  projectThreads,
  sidebarTaskStatus,
  sidebarTaskStatusLabel,
  taskRecentTime,
  type SidebarBackgroundTask
} from './sidebar-project-tasks'
import type { ChatMessage, ThreadItem } from './types'

// 用本地时间构造，避免测试机时区不同导致格式化断言漂移。
const at = (y: number, m: number, d: number, h: number, min: number): number =>
  new Date(y, m - 1, d, h, min).getTime()
const iso = (y: number, m: number, d: number, h: number, min: number): string =>
  new Date(y, m - 1, d, h, min).toISOString()

const NOW = at(2026, 11, 6, 10, 0)

function thread(id: string, extra: Partial<ThreadItem> = {}): ThreadItem {
  return { id, title: `任务 ${id}`, updatedAt: NOW - 60_000, messages: [], ...extra }
}

function assistant(extra: Partial<ChatMessage> = {}): ChatMessage {
  return {
    id: 'm1',
    role: 'assistant',
    content: 'done',
    createdAt: NOW - 30_000,
    state: 'done',
    ...extra
  }
}

function task(id: string, extra: Partial<SidebarBackgroundTask> = {}): SidebarBackgroundTask {
  return { id, status: 'running', ...extra }
}

describe('sidebar project tasks', () => {
  it('lists only threads owned by the project and drops archived ones', () => {
    const threads = [
      thread('mine', { projectId: 'p1' }),
      thread('other', { projectId: 'p2' }),
      thread('global'),
      thread('archived', { projectId: 'p1', archived: true })
    ]
    expect(projectThreads(threads, 'p1').map((item) => item.id)).toEqual(['mine'])
  })

  it('associates a thread with its background task by sessionId', () => {
    const tasks = [
      task('bg-1', { sessionId: 's1', createdAt: iso(2026, 11, 6, 9, 0) }),
      task('bg-2', { sessionId: 's1', createdAt: iso(2026, 11, 6, 9, 30), endedAt: iso(2026, 11, 6, 9, 40) }),
      task('bg-3', { sessionId: 's2' })
    ]
    expect(backgroundTaskFor(thread('t', { sessionId: 's1' }), tasks)?.id).toBe('bg-2')
    expect(backgroundTaskFor(thread('t'), tasks)).toBeUndefined()
  })

  it('maps running, queued, completed, failed and cancelled', () => {
    expect(sidebarTaskStatus(thread('a', { sessionId: 's', running: true }))).toBe('running')
    expect(sidebarTaskStatus(thread('b', { sessionId: 's', queueDraining: true }))).toBe('queued')
    expect(sidebarTaskStatus(thread('c', { sessionId: 's', queuedMessages: [{ id: 'q', content: 'hi', images: [], createdAt: NOW }] }))).toBe('queued')
    expect(sidebarTaskStatus(thread('d'))).toBe('queued')
    expect(sidebarTaskStatus(thread('e', { sessionId: 's', messages: [assistant()] }))).toBe('completed')
    expect(sidebarTaskStatus(thread('f', { sessionId: 's', messages: [assistant({ state: 'error' })] }))).toBe('failed')
    expect(sidebarTaskStatus(thread('g', { sessionId: 's', messages: [assistant({ finishReason: 'error' })] }))).toBe('failed')
    expect(sidebarTaskStatus(thread('h', { sessionId: 's', messages: [assistant({ interrupted: true })] }))).toBe('cancelled')
    expect(sidebarTaskStatus(thread('i', { sessionId: 's', messages: [assistant({ finishReason: 'cancelled' })] }))).toBe('cancelled')
    expect(sidebarTaskStatus(thread('j', { sessionId: 's' }))).toBe('completed')
  })

  it('prefers the background task status over the derived one', () => {
    const running = thread('t', { sessionId: 's', running: true })
    expect(sidebarTaskStatus(running, task('bg', { status: 'failed', sessionId: 's' }))).toBe('failed')
    expect(sidebarTaskStatus(thread('t2', { sessionId: 's' }), task('bg2', { status: 'running', sessionId: 's' }))).toBe('running')
  })

  it('labels every status in Chinese', () => {
    expect(sidebarTaskStatusLabel('queued')).toBe('排队')
    expect(sidebarTaskStatusLabel('running')).toBe('运行中')
    expect(sidebarTaskStatusLabel('completed')).toBe('完成')
    expect(sidebarTaskStatusLabel('failed')).toBe('失败')
    expect(sidebarTaskStatusLabel('cancelled')).toBe('已取消')
  })

  it('formats times as HH:mm today and MM-DD HH:mm otherwise', () => {
    expect(formatTaskTime(at(2026, 11, 6, 9, 5), NOW)).toBe('09:05')
    expect(formatTaskTime(at(2026, 11, 5, 9, 5), NOW)).toBe('11-05 09:05')
  })

  it('uses the latest of thread updatedAt and task endedAt/createdAt', () => {
    const endedLater = task('bg', {
      sessionId: 's',
      createdAt: iso(2026, 11, 6, 9, 0),
      endedAt: iso(2026, 11, 6, 9, 50)
    })
    expect(taskRecentTime(
      thread('t', { sessionId: 's', updatedAt: at(2026, 11, 6, 9, 40) }),
      endedLater,
      NOW
    )).toBe('09:50')
    const olderTask = task('bg2', {
      sessionId: 's',
      createdAt: iso(2026, 11, 6, 8, 0),
      endedAt: iso(2026, 11, 6, 8, 10)
    })
    expect(taskRecentTime(thread('t2', { sessionId: 's', updatedAt: at(2026, 11, 6, 9, 20) }), olderTask, NOW)).toBe('09:20')
  })
})
