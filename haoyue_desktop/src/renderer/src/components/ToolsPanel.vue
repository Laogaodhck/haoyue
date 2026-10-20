<script setup lang="ts">
import {
  Check,
  ChevronDown,
  CircleAlert,
  FileText,
  Globe2,
  ListTodo,
  LoaderCircle,
  Search,
  TerminalSquare,
  Wrench,
  X
} from '@lucide/vue'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import type { ThreadItem } from '../types'
import type { ToolActivityEntry, ToolKind } from '../tools-activity'
import { collectActivities, formatDuration, toolKind, toolLabel, uniqueToolNames } from '../tools-activity'

/**
 * 右侧「工具活动」面板：跨消息聚合当前任务的工具调用（最新在前），
 * 提供按工具筛选、展开入参/结果、定位来源消息。与 GitWorkspacePanel
 * 同一滑入式面板模型（可拖宽，逻辑全部内聚在本组件，App.vue 零膨胀）。
 */
const props = withDefaults(defineProps<{
  open: boolean
  thread?: ThreadItem
  width?: number
  /** 嵌入 WorkDock：隐藏自身外壳（手柄/头部），由 dock 统一提供。 */
  embedded?: boolean
}>(), {
  width: 520,
  embedded: false
})

const emit = defineEmits<{
  close: []
  resize: [width: number]
}>()

const MIN_PANEL_WIDTH = 300
const MAX_PANEL_WIDTH = 720

const filter = ref<string>('all')
const openIds = ref<string[]>([])
const resizing = ref(false)
const flashMessageId = ref<string | null>(null)
let resizeStartX = 0
let resizeStartWidth = 0
let flashTimer: ReturnType<typeof setTimeout> | null = null

const activities = computed<ToolActivityEntry[]>(() => collectActivities(props.thread?.messages ?? []))
const toolNames = computed(() => uniqueToolNames(activities.value))
const filtered = computed(() =>
  filter.value === 'all' ? activities.value : activities.value.filter((entry) => entry.name === filter.value))
const runningCount = computed(() => activities.value.filter((entry) => entry.state === 'running').length)
const errorCount = computed(() => activities.value.filter((entry) => entry.state === 'error').length)
const panelStyle = computed(() => ({ '--tools-panel-width': `${props.width}px` }))

function summary(entry: ToolActivityEntry): string {
  const text = entry.args ?? entry.detail ?? ''
  return text.length > 96 ? `${text.slice(0, 96)}…` : text
}

function isOpen(id: string): boolean {
  return openIds.value.includes(id)
}

function toggle(entry: ToolActivityEntry): void {
  if (!entry.detail && !entry.args) return
  openIds.value = isOpen(entry.id)
    ? openIds.value.filter((id) => id !== entry.id)
    : [...openIds.value, entry.id]
}

function selectFilter(name: string): void {
  filter.value = name
}

/** 定位来源消息：虚拟窗口外的消息可能未渲染，尽力滚动 + 闪烁提示。 */
function locate(entry: ToolActivityEntry): void {
  const node = document.querySelector(`[data-message-id="${CSS.escape(entry.messageId)}"]`)
  if (!node) return
  node.scrollIntoView({ behavior: 'smooth', block: 'center' })
  flashMessageId.value = entry.messageId
  if (flashTimer) clearTimeout(flashTimer)
  flashTimer = setTimeout(() => { flashMessageId.value = null }, 1400)
}

const KIND_ICONS: Record<ToolKind, typeof Wrench> = {
  shell: TerminalSquare,
  browser: Globe2,
  file: FileText,
  search: Search,
  web: Globe2,
  plan: ListTodo,
  other: Wrench
}

function kindIcon(name: string): typeof Wrench {
  return KIND_ICONS[toolKind(name)] ?? Wrench
}

function stopResize(): void {
  if (!resizing.value) return
  resizing.value = false
  window.removeEventListener('pointermove', handleResize)
  window.removeEventListener('pointerup', stopResize)
  window.removeEventListener('pointercancel', stopResize)
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
  window.addEventListener('pointermove', handleResize)
  window.addEventListener('pointerup', stopResize)
  window.addEventListener('pointercancel', stopResize)
}

// 切换任务时重置筛选与展开态，避免上一个任务的上下文泄漏。
watch(() => props.thread?.id, () => {
  filter.value = 'all'
  openIds.value = []
})

watch(() => props.open, (open) => {
  if (!open) stopResize()
})

onBeforeUnmount(stopResize)
</script>

<template>
  <Transition name="side-panel">
    <aside
      v-if="open || embedded"
      class="tools-panel"
      :class="{ 'is-resizing': resizing, 'is-embedded': embedded }"
      :style="panelStyle"
      aria-label="工具活动"
    >
      <div
        v-if="!embedded"
        class="tools-panel-resize-handle"
        role="separator"
        aria-label="调整工具面板宽度"
        aria-orientation="vertical"
        @pointerdown="startResize"
      />
      <header v-if="!embedded" class="tools-panel-header">
        <div class="tools-panel-title">
          <Wrench :size="19" />
          <div>
            <strong>工具活动</strong>
            <small>
              {{ thread ? thread.title : '未选择任务' }}
              <template v-if="runningCount > 0"> · {{ runningCount }} 个执行中</template>
              <template v-if="errorCount > 0"> · {{ errorCount }} 个失败</template>
            </small>
          </div>
        </div>
        <div class="tools-panel-actions">
          <button class="icon-button" title="关闭" @click="emit('close')"><X :size="18" /></button>
        </div>
      </header>

      <div v-if="toolNames.length > 1" class="tools-panel-filters" role="tablist">
        <button :class="{ active: filter === 'all' }" role="tab" @click="selectFilter('all')">
          全部 {{ activities.length }}
        </button>
        <button v-for="name in toolNames" :key="name" :class="{ active: filter === name }" role="tab"
          @click="selectFilter(name)">
          {{ toolLabel(name) }}
        </button>
      </div>

      <div class="tools-panel-body">
        <div v-if="!thread" class="tools-panel-state">选择一个任务后查看工具调用。</div>
        <div v-else-if="activities.length === 0" class="tools-panel-state">
          这个任务还没有工具调用。工具（Shell、浏览器、文件编辑等）的执行记录会在这里汇总展示。
        </div>
        <div v-else-if="filtered.length === 0" class="tools-panel-state">没有符合筛选条件的工具调用。</div>
        <ol v-else class="tools-activity-list">
          <li v-for="entry in filtered" :key="entry.id" class="tools-activity-row" :data-state="entry.state">
            <button type="button" class="tools-activity-summary" :aria-expanded="isOpen(entry.id)"
              @click="toggle(entry)">
              <component :is="kindIcon(entry.name)" :size="15" class="tools-activity-icon" aria-hidden="true" />
              <span class="tools-activity-name">{{ toolLabel(entry.name) }}</span>
              <span class="tools-activity-args">{{ summary(entry) }}</span>
              <span v-if="entry.state === 'running'" class="tools-activity-state">
                <LoaderCircle :size="13" class="spin" />执行中
              </span>
              <span v-else-if="entry.state === 'error'" class="tools-activity-state is-error">
                <CircleAlert :size="13" />失败
              </span>
              <span v-else class="tools-activity-state is-done">
                <Check :size="13" />{{ formatDuration(entry.durationMs) || '完成' }}
              </span>
              <ChevronDown v-if="entry.detail || entry.args" :size="14" class="tools-activity-chevron"
                :class="{ rotated: isOpen(entry.id) }" />
            </button>
            <div v-if="isOpen(entry.id)" class="tools-activity-detail">
              <template v-if="entry.args">
                <span class="tools-activity-detail-label">入参</span>
                <pre>{{ entry.args }}</pre>
              </template>
              <template v-if="entry.detail">
                <span class="tools-activity-detail-label">结果</span>
                <pre>{{ entry.detail }}</pre>
              </template>
              <div class="tools-activity-detail-meta">
                <span v-if="entry.durationMs != null">耗时 {{ formatDuration(entry.durationMs) }}</span>
                <button type="button" class="tools-activity-locate" @click="locate(entry)">定位来源消息</button>
              </div>
            </div>
          </li>
        </ol>
      </div>
    </aside>
  </Transition>
</template>
