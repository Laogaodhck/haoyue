import type { Ref } from 'vue'
import type { AppInfo } from '../../shared/ipc'
import {
  makeId,
  pathName,
  samePath,
  type RuntimeProject
} from './app-helpers'
import { isForbiddenProjectPath } from './project-paths'
import { daemonRequest } from './daemon-rpc'
import {
  refreshAllProjectSessions as refreshAllProjectSessionsFromStore,
  refreshGlobalSessions as refreshGlobalSessionsFromStore,
  refreshProjectSessions as refreshProjectSessionsFromStore,
  type SessionSyncContext
} from './session-sync'
import type { ProjectItem, ThreadItem } from './types'

export const PROJECTS_STORAGE_KEY = 'haoyue-projects-v2'
export const IMPLICIT_DOCUMENTS_MIGRATION_KEY = 'haoyue-projects-remove-implicit-documents-v2'

interface ProjectStoreDeps {
  projects: Ref<ProjectItem[]>
  threads: Ref<ThreadItem[]>
  selectedProjectId: Ref<string>
  activeThreadId: Ref<string>
  activeThread: Ref<ThreadItem | undefined>
  appInfo: Ref<AppInfo>
  sessionSyncContext: SessionSyncContext
}

/**
 * 项目的本地缓存迁移与运行时同步：localStorage 旧数据 → daemon 行、重复行合并、
 * 非法路径清理、会话列表刷新。全部状态通过 refs 读写，调用方与组件共享同一份数据。
 */
export function createProjectStore({
  projects,
  threads,
  selectedProjectId,
  activeThreadId,
  activeThread,
  appInfo,
  sessionSyncContext
}: ProjectStoreDeps) {
  function loadStoredProjects(): ProjectItem[] {
    try {
      const saved = JSON.parse(localStorage.getItem(PROJECTS_STORAGE_KEY) ?? '[]') as ProjectItem[]
      return saved.filter((project) => project?.id && project?.path).map((project) => ({
        id: project.id,
        name: project.name || pathName(project.path),
        path: project.path,
        loaded: false
      }))
    } catch {
      return []
    }
  }

  function ensureProject(path: string, name?: string): ProjectItem {
    const existing = projects.value.find((project) => samePath(project.path, path))
    if (existing) {
      if (name) existing.name = name
      return existing
    }
    const project: ProjectItem = { id: makeId(), name: name || pathName(path), path, loaded: false }
    projects.value.push(project)
    return project
  }

  async function saveProject(project: ProjectItem): Promise<ProjectItem> {
    const oldId = project.id
    const response = await daemonRequest('project.upsert', {
      id: project.id,
      path: project.path,
      name: project.name
    })
    const saved = JSON.parse(response.data) as RuntimeProject
    const duplicate = projects.value.find((item) => item !== project && item.id === saved.id)
    if (duplicate) {
      duplicate.path = saved.path
      duplicate.name = saved.name
      threads.value.forEach((thread) => {
        if (thread.projectId === oldId) thread.projectId = duplicate.id
      })
      if (selectedProjectId.value === oldId) selectedProjectId.value = duplicate.id
      projects.value = projects.value.filter((item) => item !== project)
      return duplicate
    }
    project.id = saved.id
    project.path = saved.path
    project.name = saved.name
    if (oldId !== saved.id) {
      threads.value.forEach((thread) => {
        if (thread.projectId === oldId) thread.projectId = saved.id
      })
      if (selectedProjectId.value === oldId) selectedProjectId.value = saved.id
    }
    return project
  }

  async function migrateStoredProjects(): Promise<void> {
    if (localStorage.getItem(PROJECTS_STORAGE_KEY) === null) return
    const stored = loadStoredProjects()
    for (const project of stored) {
      if (isForbiddenProjectPath(project.path, appInfo.value.userProfilePath)) continue
      await daemonRequest('project.upsert', {
        id: project.id,
        path: project.path,
        name: project.name
      })
    }
    localStorage.removeItem(PROJECTS_STORAGE_KEY)
  }

  async function removeInvalidProjectRows(): Promise<void> {
    // Older builds could register the user profile (or ~/.haoyue) as a project, which
    // made every plain folder under the profile share one session scope. Drop those rows
    // now; sessions are preserved in the database instead of being deleted with them.
    const invalid = projects.value.filter((project) =>
      isForbiddenProjectPath(project.path, appInfo.value.userProfilePath))
    for (const project of invalid) {
      try {
        await daemonRequest('project.remove', { id: project.id, keepSessions: true })
      } catch {
        continue // leave the row in place; the next launch retries the cleanup
      }
      projects.value = projects.value.filter((item) => item.id !== project.id)
      if (selectedProjectId.value === project.id) selectedProjectId.value = ''
    }
  }

  async function refreshProjectSessions(project: ProjectItem): Promise<void> {
    await refreshProjectSessionsFromStore(sessionSyncContext, project)
  }

  async function refreshGlobalSessions(): Promise<void> {
    await refreshGlobalSessionsFromStore(sessionSyncContext)
  }

  async function refreshAllProjectSessions(): Promise<void> {
    await refreshAllProjectSessionsFromStore(sessionSyncContext, projects.value)
  }

  async function migrateImplicitDocumentsProject(): Promise<void> {
    if (localStorage.getItem(IMPLICIT_DOCUMENTS_MIGRATION_KEY) === '1') return
    if (!appInfo.value.documentsPath) return

    const project = projects.value.find((item) => samePath(item.path, appInfo.value.documentsPath))
    if (!project) {
      localStorage.setItem(IMPLICIT_DOCUMENTS_MIGRATION_KEY, '1')
      return
    }

    // Only remove the legacy entry after the Runtime has confirmed that it owns no sessions.
    // A failed request leaves the migration pending so a later reconnect can retry safely.
    try {
      await refreshProjectSessions(project)
    } catch {
      return
    }

    if (!threads.value.some((thread) => thread.projectId === project.id)) {
      await daemonRequest('project.remove', { id: project.id })
      projects.value = projects.value.filter((item) => item.id !== project.id)
      if (selectedProjectId.value === project.id) selectedProjectId.value = ''
      if (activeThread.value?.projectId === project.id) activeThreadId.value = ''
    }
    localStorage.setItem(IMPLICIT_DOCUMENTS_MIGRATION_KEY, '1')
  }

  return {
    loadStoredProjects,
    ensureProject,
    saveProject,
    migrateStoredProjects,
    removeInvalidProjectRows,
    refreshProjectSessions,
    refreshGlobalSessions,
    refreshAllProjectSessions,
    migrateImplicitDocumentsProject
  }
}
