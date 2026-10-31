import { daemonRequest } from './daemon-rpc'
import { reactive, type Ref } from 'vue'
import type { DaemonState } from '../../shared/ipc'
import type { ImageAttachment, ProjectItem, ThreadItem } from './types'
import {
  hydrateMessages,
  normalizeReasoningLevel,
  sessionScope,
  sessionStats,
  type RuntimeSession,
  type RuntimeSessionHeader
} from './app-helpers'

export interface SessionSyncContext {
  daemonState: Ref<DaemonState>
  threads: Ref<ThreadItem[]>
}

export async function refreshProjectSessions(context: SessionSyncContext, project: ProjectItem): Promise<void> {
  const { daemonState, threads } = context
  if (!daemonState.value.connected) return
  project.loaded = false
  try {
    const response = await daemonRequest('session.list', {
      workspace: project.path,
      includeArchived: true
    })
    const savedSessions = JSON.parse(response.data) as RuntimeSessionHeader[]
    const sessionIds = new Set(savedSessions.map((session) => session.id))
    threads.value = threads.value.filter((thread) =>
      thread.projectId !== project.id || !thread.sessionId || sessionIds.has(thread.sessionId))

    for (const saved of savedSessions) {
      const existing = threads.value.find((thread) =>
        thread.projectId === project.id && thread.sessionId === saved.id)
      const fallbackTitle = `任务 ${new Date(saved.createdAt).toLocaleString()}`
      if (existing) {
        existing.title = saved.title || existing.title || fallbackTitle
        existing.updatedAt = new Date(saved.updatedAt).getTime()
        existing.archived = Boolean(saved.archived)
        existing.reasoningLevel = normalizeReasoningLevel(saved.reasoningLevel)
        existing.networkEnabled = saved.networkEnabled ?? true
      } else {
        threads.value.push({
          id: `${project.id}:session:${saved.id}`,
          title: saved.title || fallbackTitle,
          projectId: project.id,
          updatedAt: new Date(saved.updatedAt).getTime(),
          messages: [],
          sessionId: saved.id,
          sessionLoaded: false,
          reasoningLevel: normalizeReasoningLevel(saved.reasoningLevel),
          networkEnabled: saved.networkEnabled ?? true,
          archived: Boolean(saved.archived)
        })
      }
    }
  } finally {
    project.loaded = true
  }
}

export async function refreshGlobalSessions(context: SessionSyncContext): Promise<void> {
  const { daemonState, threads } = context
  if (!daemonState.value.connected) return
  const response = await daemonRequest('session.list', {
    global: true,
    includeArchived: true
  })
  const savedSessions = JSON.parse(response.data) as RuntimeSessionHeader[]
  const sessionIds = new Set(savedSessions.map((session) => session.id))
  threads.value = threads.value.filter((thread) =>
    thread.projectId || !thread.sessionId || sessionIds.has(thread.sessionId))

  for (const saved of savedSessions) {
    const existing = threads.value.find((thread) => !thread.projectId && thread.sessionId === saved.id)
    const fallbackTitle = `任务 ${new Date(saved.createdAt).toLocaleString()}`
    if (existing) {
      existing.title = saved.title || existing.title || fallbackTitle
      existing.updatedAt = new Date(saved.updatedAt).getTime()
      existing.archived = Boolean(saved.archived)
      existing.reasoningLevel = normalizeReasoningLevel(saved.reasoningLevel)
      existing.networkEnabled = saved.networkEnabled ?? true
    } else {
      threads.value.push({
        id: `global:session:${saved.id}`,
        title: saved.title || fallbackTitle,
        updatedAt: new Date(saved.updatedAt).getTime(),
        messages: [],
        sessionId: saved.id,
        sessionLoaded: false,
        reasoningLevel: normalizeReasoningLevel(saved.reasoningLevel),
        networkEnabled: saved.networkEnabled ?? true,
        archived: Boolean(saved.archived)
      })
    }
  }
}

export async function refreshAllProjectSessions(
  context: SessionSyncContext,
  projects: ProjectItem[]
): Promise<void> {
  await Promise.all([
    refreshGlobalSessions(context).catch(() => undefined),
    ...projects.map((project) => refreshProjectSessions(context, project).catch(() => undefined))
  ])
}

export async function reloadThreadSession(
  context: Pick<SessionSyncContext, 'daemonState'>,
  thread: ThreadItem,
  project?: ProjectItem
): Promise<void> {
  if (!thread.sessionId || thread.running || !context.daemonState.value.connected) return
  try {
    const saved = await fetchSessionPage(thread, project, {})
    // 让出事件循环，避免后台同步任务卡顿前台动画与交互
    await new Promise((resolve) => setTimeout(resolve, 0))
    // 等待期间用户可能已在该线程发起新回合：整体替换 messages 会使回合事件
    // 失去锚点（消息丢失/气泡悬空），此时丢弃本次后台重载结果。
    if (thread.running || thread.assistantId) return
    applySessionPage(thread, saved, 'replace')
  } catch {
    thread.sessionLoaded = false
  }
}

/**
 * 首屏只拉最近这么多条消息：会话可以长到几千条，全量读取（连图片字节一起）
 * 会把首屏卡在命名管道上。更早的部分由「加载更早」按页向前翻。
 */
export const SESSION_PAGE_SIZE = 80

/** 向前翻页：把 before 之前的又一页拼到现有消息前面。 */
export async function loadEarlierMessages(
  context: Pick<SessionSyncContext, 'daemonState'>,
  thread: ThreadItem,
  project?: ProjectItem
): Promise<void> {
  if (!thread.sessionId || thread.loadingEarlier || !context.daemonState.value.connected) return
  if (!thread.sessionHasMore) return
  thread.loadingEarlier = true
  try {
    const saved = await fetchSessionPage(thread, project, {
      limit: SESSION_PAGE_SIZE,
      before: thread.sessionFirstIndex ?? 0
    })
    applySessionPage(thread, saved, 'prepend')
  } catch {
    // 保留按钮可重试：游标不变，下一次点击仍从同一位置向前取。
  } finally {
    thread.loadingEarlier = false
  }
}

async function fetchSessionPage(
  thread: ThreadItem,
  project: ProjectItem | undefined,
  page: Record<string, unknown>
): Promise<RuntimeSession> {
  const response = await daemonRequest('session.get', {
    id: thread.sessionId,
    limit: SESSION_PAGE_SIZE,
    ...sessionScope(thread, project),
    ...page
  })
  return JSON.parse(response.data) as RuntimeSession
}

function applySessionPage(thread: ThreadItem, saved: RuntimeSession, mode: 'replace' | 'prepend'): void {
  const offset = saved.firstIndex ?? 0
  const hydrated = hydrateMessages(saved, offset)
  if (mode === 'prepend') {
    // 页边界若正好切在「assistant + 它的工具结果行」之间，这一页开头的工具行没有
    // 可挂载的 assistant，按原语义丢弃——那条 assistant 在更早的一页里，本来也没渲染。
    thread.messages = [...hydrated, ...thread.messages]
  } else {
    thread.messages = hydrated
    thread.sessionLoaded = true
    thread.title = saved.title || thread.title
    thread.archived = Boolean(saved.archived)
    thread.reasoningLevel = normalizeReasoningLevel(saved.reasoningLevel)
    thread.expertId = saved.expertId || undefined
    if (!saved.expertId) thread.expertLabel = undefined
    thread.stats = sessionStats(saved)
  }
  thread.sessionTotal = saved.total ?? thread.messages.length
  thread.sessionFirstIndex = offset
  thread.sessionHasMore = Boolean(saved.hasMore)
}

/**
 * `${sessionId}:${imageId}` → data URL。`session.get` 只回图片元数据（base64 会把
 * 一页消息的载荷撑到几十 MB），字节由渲染端按需取回，因此这里做一层会话级缓存。
 */
export const sessionImageSources = reactive<Record<string, string>>({})
const pendingImageBytes = new Set<string>()

export function imageSourceKey(sessionId: string, imageId: string): string {
  return `${sessionId}:${imageId}`
}

/**
 * 取回一张历史图片的字节。同一张图重复调用只会发一次 RPC；失败后键仍留在 pending
 * 之外，下一次渲染（如用户点击预览）可以重试，也不会刷屏。
 */
export function requestSessionImageBytes(
  context: Pick<SessionSyncContext, 'daemonState'>,
  thread: ThreadItem,
  project: ProjectItem | undefined,
  image: ImageAttachment,
): void {
  if (!thread.sessionId || image.data || !context.daemonState.value.connected) return
  const key = imageSourceKey(thread.sessionId, image.id)
  if (sessionImageSources[key] || pendingImageBytes.has(key)) return
  pendingImageBytes.add(key)
  void daemonRequest('session.image', { id: thread.sessionId, imageId: image.id, ...sessionScope(thread, project) })
    .then((response) => {
      const stored = JSON.parse(response.data) as { mediaType?: string; data?: string | null }
      if (stored.data) {
        sessionImageSources[key] = `data:${stored.mediaType || image.mediaType};base64,${stored.data}`
      }
    })
    .catch(() => undefined)
    .finally(() => pendingImageBytes.delete(key))
}
