<script setup lang="ts">
import { Braces, Globe2, History, ListChecks, RefreshCw, TerminalSquare, Wrench, X } from '@lucide/vue'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import type { ProjectItem, ThreadItem, WorkDockTab } from '../types'
import { collectActivities } from '../tools-activity'
import BackgroundTasksPanel from './BackgroundTasksPanel.vue'
import BrowserPanel from './BrowserPanel.vue'
import GitWorkspacePanel from './GitWorkspacePanel.vue'
import ToolsPanel from './ToolsPanel.vue'

/**
 * 右侧统一工作区面板（WorkBuddy 式）：对话区永远至多让出一列宽度，
 * Git 更改 / 提交记录 / 工具活动 / 内置浏览器 / 后台任务 以页签切换，互不叠开。五个内容
 * 组件以 embedded 模式宿主在此，只渲染主体；外壳（手柄/头部/页签）由本
 * 组件统一提供，宽度与关闭动作也统一收口。
 */
const props = withDefaults(defineProps<{
  open: boolean
  tab: WorkDockTab
  width?: number
  project?: ProjectItem
  thread?: ThreadItem
  diffOverride?: { path: string; diff: string } | null
}>(), {
  width: 560
})

const emit = defineEmits<{
  close: []
  resize: [width: number]
  'update:tab': [tab: WorkDockTab]
  openTerminal: []
  openSession: [payload: { sessionId: string; workspace?: string }]
}>()

const MIN_PANEL_WIDTH = 300
const MAX_PANEL_WIDTH = 720

const gitPanel = ref<InstanceType<typeof GitWorkspacePanel> | null>(null)
const resizing = ref(false)
let resizeStartX = 0
let resizeStartWidth = 0

const isGitTab = computed(() => props.tab === 'changes' || props.tab === 'history')
const panelStyle = computed(() => ({ '--work-dock-width': `${props.width}px` }))
/** 工具页签的执行中徽标：跨消息聚合当前任务的运行中工具数。 */
const runningCount = computed(() =>
  collectActivities(props.thread?.messages ?? []).filter((entry) => entry.state === 'running').length)

function selectTab(tab: WorkDockTab): void {
  emit('update:tab', tab)
}

function refresh(): void {
  void gitPanel.value?.refresh()
}

function stopResize(): void {
  if (!resizing.value) return
  resizing.value = false
  window.removeEventListener('pointermove', handleResize)
  window.removeEventListener('pointerup', stopResize)
  window.removeEventListener('pointercancel', stopResize)
  document.body.classList.remove('is-resizing-git-panel')
}

function handleResize(event: PointerEvent): void {
  if (!resizing.value) return
  const maxWidth = Math.min(MAX_PANEL_WIDTH, Math.max(MIN_PANEL_WIDTH, Math.floor(window.innerWidth * 0.66)))
  const nextWidth = resizeStartWidth + resizeStartX - event.clientX
  if (nextWidth <= MIN_PANEL_WIDTH) {
    stopResize()
    emit('close')
    return
  }
  emit('resize', Math.min(maxWidth, Math.max(MIN_PANEL_WIDTH, nextWidth)))
}

function startResize(event: PointerEvent): void {
  if (event.button !== 0) return
  event.preventDefault()
  resizing.value = true
  resizeStartX = event.clientX
  resizeStartWidth = props.width
  document.body.classList.add('is-resizing-git-panel')
  window.addEventListener('pointermove', handleResize)
  window.addEventListener('pointerup', stopResize)
  window.addEventListener('pointercancel', stopResize)
}

watch(() => props.open, (open) => {
  if (!open) stopResize()
})

onBeforeUnmount(stopResize)
</script>

<template>
  <Transition name="side-panel">
    <aside
      v-if="open"
      class="work-dock"
      :class="{ 'is-resizing': resizing }"
      :style="panelStyle"
      aria-label="工作区"
    >
      <div
        class="work-dock-resize-handle"
        role="separator"
        aria-label="调整工作区面板宽度"
        aria-orientation="vertical"
        @pointerdown="startResize"
      />
      <header class="work-dock-header">
        <div class="work-dock-tabs" role="tablist">
          <button :class="{ active: tab === 'changes' }" role="tab" @click="selectTab('changes')">
            <Braces :size="15" />更改
          </button>
          <button v-if="!diffOverride" :class="{ active: tab === 'history' }" role="tab" @click="selectTab('history')">
            <History :size="15" />提交记录
          </button>
          <button :class="{ active: tab === 'tools' }" role="tab" @click="selectTab('tools')">
            <Wrench :size="15" />工具活动
            <span v-if="runningCount > 0" class="work-dock-tab-badge">{{ runningCount }}</span>
          </button>
          <button :class="{ active: tab === 'browser' }" role="tab" @click="selectTab('browser')">
            <Globe2 :size="15" />浏览
          </button>
          <button :class="{ active: tab === 'tasks' }" role="tab" @click="selectTab('tasks')">
            <ListChecks :size="15" />后台任务
          </button>
        </div>
        <div class="work-dock-actions">
          <button v-if="isGitTab && !diffOverride" class="icon-button" title="刷新"
            :disabled="!project" @click="refresh">
            <RefreshCw :size="17" />
          </button>
          <button v-if="isGitTab" class="icon-button" title="在项目目录打开终端"
            :disabled="!project" @click="emit('openTerminal')">
            <TerminalSquare :size="17" />
          </button>
          <button class="icon-button" title="关闭" @click="emit('close')"><X :size="17" /></button>
        </div>
      </header>

      <div class="work-dock-body">
        <div v-show="isGitTab" class="work-dock-pane">
          <GitWorkspacePanel ref="gitPanel" :open="open && isGitTab" :project="project"
            :initial-tab="tab === 'history' ? 'history' : 'diff'" :diff-override="diffOverride" embedded />
        </div>
        <div v-show="tab === 'tools'" class="work-dock-pane">
          <ToolsPanel :open="open && tab === 'tools'" :thread="thread" embedded />
        </div>
        <div v-show="tab === 'browser'" class="work-dock-pane">
          <BrowserPanel :open="open && tab === 'browser'" />
        </div>
        <div v-show="tab === 'tasks'" class="work-dock-pane">
          <BackgroundTasksPanel :open="open && tab === 'tasks'" :workspace-path="project?.path"
            @open-session="(sessionId) => emit('openSession', { sessionId, workspace: project?.path })" />
        </div>
      </div>
    </aside>
  </Transition>
</template>
