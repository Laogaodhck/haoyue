import type { ProjectItem, SessionSearchHit, ThreadItem } from './types'
import { ReasoningLevel } from './types'

/**
 * 打开「不在本地列表里」的会话：按 sessionId 重建一个懒加载 ThreadItem 再选中它。
 *
 * 三个来源共用这段逻辑——侧栏跨会话检索命中、后台任务的「打开对应会话」、以及
 * 归档会话跳转。抽成模块既避免三份复制，也让 App.vue 少 50 行（行数预算所迫）。
 */
export interface SessionOpenerDeps {
  threads: ThreadItem[]
  projects: ProjectItem[]
  select: (threadId: string) => Promise<void>
  notify: (title: string, body: string) => void
}

export async function openSessionHit(hit: SessionSearchHit, deps: SessionOpenerDeps): Promise<void> {
  const existing = deps.threads.find((thread) => thread.sessionId === hit.id)
  if (existing) {
    await deps.select(existing.id)
    return
  }
  const project = hit.workspace
    ? deps.projects.find((item) => item.path === hit.workspace)
    : undefined
  if (hit.workspace && !project) {
    deps.notify('无法打开会话', `会话所属项目尚未添加：${hit.workspace}`)
    return
  }
  const thread: ThreadItem = {
    id: project ? `${project.id}:session:${hit.id}` : `global:session:${hit.id}`,
    title: hit.title || '未命名会话',
    projectId: project?.id,
    updatedAt: hit.updatedAt ? new Date(hit.updatedAt).getTime() : Date.now(),
    messages: [],
    sessionId: hit.id,
    sessionLoaded: false,
    reasoningLevel: ReasoningLevel.High,
    networkEnabled: true,
    archived: Boolean(hit.archived)
  }
  deps.threads.unshift(thread)
  await deps.select(thread.id)
}

/** 后台任务面板的「打开对应会话」：任务快照带 workspaceRoot，据此落到正确的项目。 */
export async function openBackgroundTaskSession(
  payload: { sessionId: string; workspace?: string },
  deps: SessionOpenerDeps
): Promise<void> {
  await openSessionHit({ id: payload.sessionId, title: '', workspace: payload.workspace }, deps)
}
