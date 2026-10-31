import type { Ref } from 'vue'
import type { ProjectItem, WorkDockTab } from './types'

interface WorkDockDeps {
  activeProject: Ref<ProjectItem | undefined>
  workDockOpen: Ref<boolean>
  workDockTab: Ref<WorkDockTab>
  workDockWidth: Ref<number>
  toolDiff: Ref<{ path: string; diff: string } | null>
}

/**
 * 右侧工作区面板的开关语义：页签切换、按项目可用性回退、宽度钳制。
 * 与组件状态一一对应，拆出后可单测且 App.vue 不再堆积纯 UI 规则。
 */
export function createWorkDockController({
  activeProject,
  workDockOpen,
  workDockTab,
  workDockWidth,
  toolDiff
}: WorkDockDeps) {
  function showActiveProject(): void {
    if (activeProject.value) void window.haoyue.showItemInFolder(activeProject.value.path)
  }

  /** Git 两类页签依赖工作区路径；工具活动 / 浏览 / 后台任务与会话无关。 */
  function needsProject(tab: WorkDockTab): boolean {
    return tab === 'changes' || tab === 'history'
  }

  /** 打开工作区面板的指定页签；再次点击已激活页签视为关闭（WorkBuddy 式切换语义）。 */
  function openWorkDock(tab: WorkDockTab): void {
    if (needsProject(tab) && !activeProject.value) {
      // 旧实现是静默 return：标题栏菜单和 header 按钮看起来都能点，实际毫无反应。
      void window.haoyue.notify?.('需要先绑定项目',
        '代码更改与提交记录只对绑定了项目的任务可用。请在欢迎页绑定项目，或从左侧项目里新建任务。')
      return
    }
    if (tab !== 'tools' && tab !== 'browser') toolDiff.value = null
    workDockOpen.value = !(workDockOpen.value && workDockTab.value === tab)
    workDockTab.value = tab
  }

  /**
   * header 的单一「面板」开关：header 不再为每个页签各放一个按钮（那与面板自己的页签重复），
   * 只保留一个开关。记住的页签不可用时退回「工具活动」，避免点了没用。
   */
  function toggleWorkDock(): void {
    if (workDockOpen.value) {
      closeWorkDock()
      return
    }
    openWorkDock(needsProject(workDockTab.value) && !activeProject.value ? 'tools' : workDockTab.value)
  }

  function openDevTools(): void {
    void window.haoyue.openDevTools()
  }

  function openToolDiff(path: string, diff: string): void {
    toolDiff.value = { path, diff }
    workDockTab.value = 'changes'
    workDockOpen.value = true
  }

  function closeWorkDock(): void {
    workDockOpen.value = false
    toolDiff.value = null
  }

  function resizeWorkDock(width: number): void {
    if (!Number.isFinite(width)) return
    const maxWidth = Math.min(720, Math.max(280, Math.floor(window.innerWidth * 0.66)))
    workDockWidth.value = Math.min(maxWidth, Math.max(280, Math.round(width)))
  }

  async function openProjectTerminal(): Promise<void> {
    if (!activeProject.value) return
    await window.haoyue.project.openTerminal(activeProject.value.path)
  }

  return {
    showActiveProject,
    needsProject,
    openWorkDock,
    toggleWorkDock,
    openDevTools,
    openToolDiff,
    closeWorkDock,
    resizeWorkDock,
    openProjectTerminal
  }
}
