<script setup lang="ts">
import { GripVertical, Monitor, X } from '@lucide/vue'
import { ref } from 'vue'
import type { ThreadItem } from '../types'
import TaskMonitorPanel from './TaskMonitorPanel.vue'

/**
 * 任务监控 · 浮动模式外壳：独立悬浮窗（默认右下角，可拖拽），收起为胶囊，
 * 内部复用 TaskMonitorPanel（embedded）。位置只在本次会话内记忆。
 */
withDefaults(defineProps<{
  open: boolean
  thread?: ThreadItem
  threads?: ThreadItem[]
}>(), {
  open: false
})

const emit = defineEmits<{
  close: []
  openSession: [payload: { sessionId: string; workspace?: string }]
  selectThread: [threadId: string]
}>()

const collapsed = ref(false)
const dragging = ref(false)
const position = ref<{ x: number; y: number } | null>(null)
let startX = 0
let startY = 0
let startLeft = 0
let startTop = 0

function startDrag(event: PointerEvent): void {
  if (event.button !== 0) return
  event.preventDefault()
  dragging.value = true
  const el = panelEl.value
  if (el) {
    const rect = el.getBoundingClientRect()
    startLeft = rect.left
    startTop = rect.top
  }
  startX = event.clientX
  startY = event.clientY
  window.addEventListener('pointermove', handleDrag)
  window.addEventListener('pointerup', stopDrag)
  window.addEventListener('pointercancel', stopDrag)
}

function handleDrag(event: PointerEvent): void {
  if (!dragging.value) return
  position.value = {
    x: startLeft + event.clientX - startX,
    y: startTop + event.clientY - startY
  }
}

function stopDrag(): void {
  dragging.value = false
  window.removeEventListener('pointermove', handleDrag)
  window.removeEventListener('pointerup', stopDrag)
  window.removeEventListener('pointercancel', stopDrag)
}

const panelEl = ref<HTMLElement | null>(null)
</script>

<template>
  <Teleport to="body">
    <Transition name="tm-floating">
      <div v-if="open" class="tm-floating" :class="{ 'is-collapsed': collapsed, 'is-dragging': dragging }"
        :style="position ? { left: `${position.x}px`, top: `${position.y}px` } : undefined"
        ref="panelEl">
        <div class="tm-floating-bar" :class="{ 'is-collapsed-bar': collapsed }" @pointerdown="startDrag">
          <GripVertical :size="14" class="tm-floating-grip" aria-hidden="true" />
          <span class="tm-floating-title"><Monitor :size="14" />任务监控</span>
          <span v-if="!collapsed" class="tm-floating-actions">
            <button type="button" class="tm-floating-min" title="收起为胶囊" @click.stop="collapsed = true">
              <span class="tm-floating-min-line" />
            </button>
            <button type="button" class="tm-floating-close" title="关闭任务监控" @click.stop="emit('close')">
              <X :size="14" />
            </button>
          </span>
          <button v-else type="button" class="tm-floating-restore" title="展开任务监控" @click.stop="collapsed = false">
            展开
          </button>
        </div>
        <div v-if="!collapsed" class="tm-floating-body">
          <TaskMonitorPanel :open="open" :thread="thread" :threads="threads"
            @open-session="(payload) => emit('openSession', payload)"
            @select-thread="(id) => emit('selectThread', id)" />
        </div>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.tm-floating {
  position: fixed;
  right: 20px;
  bottom: 20px;
  width: 340px;
  max-height: min(560px, 70vh);
  display: flex;
  flex-direction: column;
  background: var(--color-bg-1, #ffffff);
  border: 1px solid var(--color-border, rgba(0, 0, 0, 0.1));
  border-radius: 12px;
  box-shadow: 0 8px 30px rgba(0, 0, 0, 0.18);
  overflow: hidden;
  z-index: 200;
  font-size: 13px;
}
.tm-floating.is-collapsed {
  width: auto;
  max-height: none;
  box-shadow: 0 4px 16px rgba(0, 0, 0, 0.14);
}
.tm-floating.is-dragging {
  user-select: none;
}
.tm-floating-bar {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 10px;
  cursor: grab;
  background: var(--color-bg-2, rgba(0, 0, 0, 0.03));
  border-bottom: 1px solid var(--color-border, rgba(0, 0, 0, 0.07));
}
.tm-floating-bar.is-collapsed-bar {
  border-bottom: none;
}
.tm-floating-bar:active {
  cursor: grabbing;
}
.tm-floating-grip {
  color: var(--color-text-3, #b0b4bc);
}
.tm-floating-title {
  display: flex;
  align-items: center;
  gap: 5px;
  font-weight: 600;
  flex: 1;
}
.tm-floating-actions {
  display: flex;
  align-items: center;
  gap: 4px;
}
.tm-floating-min,
.tm-floating-close,
.tm-floating-restore {
  border: none;
  background: none;
  padding: 4px;
  border-radius: 6px;
  cursor: pointer;
  color: var(--color-text-2, #8a8f98);
  display: inline-flex;
  align-items: center;
  justify-content: center;
}
.tm-floating-min:hover,
.tm-floating-close:hover,
.tm-floating-restore:hover {
  background: var(--color-bg-3, rgba(0, 0, 0, 0.06));
  color: var(--color-text-1, #1f2329);
}
.tm-floating-min-line {
  width: 12px;
  height: 2px;
  border-radius: 1px;
  background: currentColor;
}
.tm-floating-body {
  overflow-y: auto;
  flex: 1;
  min-height: 120px;
  max-height: min(500px, 60vh);
}
</style>
