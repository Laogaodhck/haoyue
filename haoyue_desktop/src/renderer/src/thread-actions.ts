import type { Ref } from 'vue'
import type { DaemonState } from '../../shared/ipc'
import type { ProjectItem, ThreadItem } from './types'
import { confirmAction } from './confirmation'
import { sessionScope } from './app-helpers'

export interface ThreadActionsContext {
  projects: Ref<ProjectItem[]>
  threads: Ref<ThreadItem[]>
  activeThreadId: Ref<string>
  selectedProjectId: Ref<string>
  taskSettingsThreadId: Ref<string>
  daemonState: Ref<DaemonState>
  conversationLoading: Ref<boolean>
  conversationLoadError: Ref<string>
  conversationSelectionToken: { value: number }
  selectThread: (id: string) => Promise<void> | void
  refreshProjectSessions: (project: ProjectItem) => Promise<void>
  reconnectDaemon: () => Promise<void>
}

export function createThreadActions(context: ThreadActionsContext) {
  const {
    projects,
    threads,
    activeThreadId,
    selectedProjectId,
    taskSettingsThreadId,
    daemonState,
    conversationLoading,
    conversationLoadError,
    conversationSelectionToken,
    selectThread,
    refreshProjectSessions,
    reconnectDaemon
  } = context

  function chooseAfterRemoval(projectId?: string): void {
    // The selected task is about to change; stale session reads must not update
    // the loading state for the replacement task.
    conversationSelectionToken.value++
    conversationLoading.value = false
    conversationLoadError.value = ''
    const fallback = threads.value
      .filter((thread) => thread.projectId === projectId && !thread.archived)
      .sort((left, right) => right.updatedAt - left.updatedAt)[0]
    if (fallback) {
      void selectThread(fallback.id)
      return
    }
    activeThreadId.value = ''
    selectedProjectId.value = projectId ?? ''
  }

  /**
   * H3: a silently swallowed action reads as a broken button. When any target task
   * is still running, surface one actionable notification instead of returning quietly.
   */
  function guardNotRunning(targets: ThreadItem[], action: string): boolean {
    const running = targets.filter((thread) => thread.running)
    if (running.length === 0) return true
    const subject = running.length === 1
      ? `“${running[0]!.title}”`
      : `${running.length} 个运行中的任务`
    void window.haoyue?.notify?.('操作无法执行', `${subject}正在运行中，请先停止后再${action}。`)
    return false
  }

  async function archiveTask(thread: ThreadItem): Promise<void> {
    const project = projects.value.find((item) => item.id === thread.projectId)
    if (thread.projectId && !project) return
    if (!guardNotRunning([thread], '归档')) return
    if (thread.sessionId) {
      await window.haoyue.daemon.request('session.archive', {
        id: thread.sessionId,
        ...sessionScope(thread, project),
        archived: true
      })
      thread.archived = true
    } else {
      threads.value = threads.value.filter((item) => item.id !== thread.id)
    }
    taskSettingsThreadId.value = ''
    if (activeThreadId.value === thread.id) chooseAfterRemoval(project?.id)
  }

  async function restoreTask(thread: ThreadItem): Promise<void> {
    const project = projects.value.find((item) => item.id === thread.projectId)
    if ((thread.projectId && !project) || !thread.sessionId) return
    if (!guardNotRunning([thread], '恢复')) return
    await window.haoyue.daemon.request('session.archive', {
      id: thread.sessionId,
      ...sessionScope(thread, project),
      archived: false
    })
    thread.archived = false
    thread.updatedAt = Date.now()
    taskSettingsThreadId.value = ''
  }

  // 批量会话操作的逐项保护：单条失败不中断整批，结束后汇总提示失败数量。
async function applyBatch(targets: ThreadItem[], apply: (thread: ThreadItem) => Promise<void>): Promise<void> {
  let failed = 0
  for (const thread of targets) {
    try {
      await apply(thread)
    } catch (error) {
      failed++
      console.error('Batch session action failed:', thread.title, error)
    }
  }
  if (failed > 0) {
    void window.haoyue?.notify?.('批量操作未全部完成', `${failed} 个任务处理失败，请检查 Runtime 连接后重试。`)
  }
}

async function archiveProjectTasks(project: ProjectItem): Promise<void> {
    if (!project.loaded) await refreshProjectSessions(project).catch(() => undefined)
    const targets = threads.value.filter((thread) => thread.projectId === project.id && !thread.archived)
    if (targets.length === 0 || !guardNotRunning(targets, '批量归档')) return
    if (!await confirmAction({
      title: '归档项目任务',
      message: `归档项目“${project.name}”的全部 ${targets.length} 个任务？`,
      confirmLabel: '全部归档'
    })) return

    const activeAffected = targets.some((thread) => thread.id === activeThreadId.value)
    await applyBatch(targets, async (thread) => {
      if (thread.sessionId) {
        await window.haoyue.daemon.request('session.archive', {
          id: thread.sessionId,
          workspace: project.path,
          archived: true
        })
        thread.archived = true
      } else {
        threads.value = threads.value.filter((item) => item.id !== thread.id)
      }
    })
    if (activeAffected) chooseAfterRemoval(project.id)
  }

  async function archiveGlobalTasks(): Promise<void> {
    const targets = threads.value.filter((thread) => !thread.projectId && !thread.archived)
    if (targets.length === 0 || !guardNotRunning(targets, '批量归档')) return
    if (!await confirmAction({
      title: '归档任务',
      message: `归档全部 ${targets.length} 个任务？`,
      confirmLabel: '全部归档'
    })) return

    const activeAffected = targets.some((thread) => thread.id === activeThreadId.value)
    await applyBatch(targets, async (thread) => {
      if (thread.sessionId) {
        await window.haoyue.daemon.request('session.archive', {
          id: thread.sessionId,
          global: true,
          archived: true
        })
        thread.archived = true
      } else {
        threads.value = threads.value.filter((item) => item.id !== thread.id)
      }
    })
    if (activeAffected) chooseAfterRemoval()
  }

  async function deleteTask(thread: ThreadItem): Promise<void> {
    const project = projects.value.find((item) => item.id === thread.projectId)
    if (thread.projectId && !project) return
    if (!guardNotRunning([thread], '删除')) return
    if (!await confirmAction({
      title: '删除任务',
      message: `永久删除任务“${thread.title}”？此操作无法撤销。`,
      confirmLabel: '永久删除',
      danger: true
    })) return
    if (thread.sessionId) {
      await window.haoyue.daemon.request('session.delete', {
        id: thread.sessionId,
        ...sessionScope(thread, project)
      })
    }
    threads.value = threads.value.filter((item) => item.id !== thread.id)
    taskSettingsThreadId.value = ''
    if (activeThreadId.value === thread.id) chooseAfterRemoval(project?.id)
  }

  async function deleteGlobalTasks(): Promise<void> {
    const targets = threads.value.filter((thread) => !thread.projectId)
    if (targets.length === 0 || !guardNotRunning(targets, '批量删除')) return
    if (!await confirmAction({
      title: '删除全部任务',
      message: `永久删除全部 ${targets.length} 个任务？此操作无法撤销。`,
      confirmLabel: '全部删除',
      danger: true
    })) return

    const activeAffected = targets.some((thread) => thread.id === activeThreadId.value)
    await applyBatch(targets, async (thread) => {
      if (thread.sessionId) {
        await window.haoyue.daemon.request('session.delete', { id: thread.sessionId, global: true })
      }
      threads.value = threads.value.filter((item) => item.id !== thread.id)
    })
    taskSettingsThreadId.value = ''
    if (activeAffected) chooseAfterRemoval()
  }

  async function deleteProjectTasks(project: ProjectItem): Promise<void> {
    if (!project.loaded) await refreshProjectSessions(project).catch(() => undefined)
    const targets = threads.value.filter((thread) => thread.projectId === project.id)
    if (targets.length === 0 || !guardNotRunning(targets, '批量删除')) return
    if (!await confirmAction({
      title: '删除项目全部任务',
      message: `永久删除项目“${project.name}”的全部 ${targets.length} 个任务？此操作无法撤销。`,
      confirmLabel: '全部删除',
      danger: true
    })) return

    const activeAffected = targets.some((thread) => thread.id === activeThreadId.value)
    await applyBatch(targets, async (thread) => {
      if (thread.sessionId) {
        await window.haoyue.daemon.request('session.delete', {
          id: thread.sessionId,
          workspace: project.path
        })
      }
      threads.value = threads.value.filter((item) => item.id !== thread.id)
    })
    taskSettingsThreadId.value = ''
    if (activeAffected) chooseAfterRemoval(project.id)
  }

  async function deleteArchivedTasks(): Promise<void> {
    const targets = threads.value.filter((thread) => thread.archived)
    if (targets.length === 0 || !guardNotRunning(targets, '批量删除')) return
    if (!await confirmAction({
      title: '清空已归档任务',
      message: `永久删除全部 ${targets.length} 个已归档任务？此操作无法撤销。`,
      confirmLabel: '全部删除',
      danger: true
    })) return

    const activeAffected = targets.some((thread) => thread.id === activeThreadId.value)
    await applyBatch(targets, async (thread) => {
      const project = projects.value.find((item) => item.id === thread.projectId)
      if (thread.sessionId && (project || !thread.projectId)) {
        await window.haoyue.daemon.request('session.delete', {
          id: thread.sessionId,
          ...sessionScope(thread, project)
        })
      }
      threads.value = threads.value.filter((item) => item.id !== thread.id)
    })
    taskSettingsThreadId.value = ''
    if (activeAffected) {
      activeThreadId.value = ''
      const project = projects.value.find((project) => project.id === selectedProjectId.value)
      chooseAfterRemoval(project?.id)
    }
  }

  async function deleteProject(project: ProjectItem): Promise<void> {
    if (!guardNotRunning(threads.value.filter((thread) => thread.projectId === project.id), '删除项目')) return
    if (!daemonState.value.connected) {
      await reconnectDaemon()
      if (!daemonState.value.connected) return
    }
    try {
      // Always refresh so sessions created or archived by another client are included.
      await refreshProjectSessions(project)
    } catch {
      return
    }
    const targets = threads.value.filter((thread) => thread.projectId === project.id)
    if (!guardNotRunning(targets, '删除项目')) return
    if (!await confirmAction({
      title: '删除项目',
      message: `删除项目“${project.name}”并永久删除其下全部 ${targets.length} 个会话？本地项目文件不会删除，此操作无法撤销。`,
      confirmLabel: '删除项目和会话',
      danger: true
    })) return

    try {
      await window.haoyue.daemon.request('project.remove', { id: project.id })
    } catch {
      await refreshProjectSessions(project).catch(() => undefined)
      return
    }

    const activeAffected = targets.some((thread) => thread.id === activeThreadId.value)
    projects.value = projects.value.filter((item) => item.id !== project.id)
    threads.value = threads.value.filter((thread) => thread.projectId !== project.id)
    if (targets.some((thread) => thread.id === taskSettingsThreadId.value)) taskSettingsThreadId.value = ''
    if (activeAffected) activeThreadId.value = ''
    if (selectedProjectId.value === project.id) selectedProjectId.value = projects.value[0]?.id ?? ''
    if (!threads.value.some((thread) => thread.id === activeThreadId.value)) {
      activeThreadId.value = ''
      chooseAfterRemoval(selectedProjectId.value || undefined)
    }
  }

  return {
    chooseAfterRemoval,
    archiveTask,
    restoreTask,
    archiveProjectTasks,
    archiveGlobalTasks,
    deleteTask,
    deleteGlobalTasks,
    deleteProjectTasks,
    deleteArchivedTasks,
    deleteProject
  }
}
