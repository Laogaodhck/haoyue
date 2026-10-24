<script setup lang="ts">
import { Ban, ExternalLink, LoaderCircle, Play, Plus, RefreshCw, X } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { requestJson } from '../daemon-json'

/**
 * 右栏 → 后台任务。
 *
 * `BackgroundTaskService` 早已完整实现（task.start / task.get / task.list / task.cancel，
 * 终态迁移经 `task.updated` 广播），但渲染层此前对这些方法的引用数是 **0**：用户既不能
 * 派发后台回合，也看不到/取消正在跑的后台任务。这是审查里最大的能力黑洞——它不是配置项，
 * 而是一整块已就绪的功能面。
 *
 * 交互与「工具活动」同构：列表 + 状态 + 取消 + 点进对应会话。
 */
const props = defineProps<{
  open: boolean
  /** 当前任务所属项目路径；派发后台任务时作为默认工作区。 */
  workspacePath?: string
}>()

const emit = defineEmits<{ openSession: [sessionId: string] }>()

interface BackgroundTask {
  id: string
  prompt: string
  workspaceRoot?: string
  status: 'running' | 'completed' | 'failed' | 'cancelled'
  sessionId?: string
  createdAt?: string
  endedAt?: string
  error?: string
  output?: string
}

const tasks = ref<BackgroundTask[]>([])
const loading = ref(false)
const busy = ref('')
const error = ref('')
const note = ref('')
const composerOpen = ref(false)
const draft = ref('')
const expanded = ref<string>('')

const runningCount = computed(() => tasks.value.filter((task) => task.status === 'running').length)

const statusLabel: Record<BackgroundTask['status'], string> = {
  running: '运行中',
  completed: '已完成',
  failed: '失败',
  cancelled: '已取消'
}

async function load(): Promise<void> {
  if (!props.open) return
  loading.value = true
  error.value = ''
  try {
    tasks.value = await requestJson<BackgroundTask[]>('task.list')
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    loading.value = false
  }
}

async function dispatch(): Promise<void> {
  const prompt = draft.value.trim()
  if (!prompt) return
  busy.value = 'start'
  error.value = ''
  note.value = ''
  try {
    await requestJson<BackgroundTask>('task.start', {
      message: prompt,
      ...(props.workspacePath ? { workspace: props.workspacePath } : {})
    })
    draft.value = ''
    composerOpen.value = false
    note.value = '后台任务已提交：回合在运行时侧独立执行，不占用当前对话的推理槽'
    await load()
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    busy.value = ''
  }
}

async function cancel(task: BackgroundTask): Promise<void> {
  busy.value = `cancel:${task.id}`
  error.value = ''
  try {
    await requestJson<BackgroundTask>('task.cancel', { id: task.id })
    await load()
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    busy.value = ''
  }
}

function toggle(task: BackgroundTask): void {
  expanded.value = expanded.value === task.id ? '' : task.id
}

function shortTime(iso?: string): string {
  if (!iso) return '—'
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return '—'
  const pad = (value: number): string => String(value).padStart(2, '0')
  return `${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`
}

let unsubscribe: (() => void) | null = null

onMounted(() => {
  unsubscribe = window.haoyue.daemon.onEvent((message) => {
    if (message.event === 'task.updated') void load()
  })
})

onBeforeUnmount(() => {
  unsubscribe?.()
  unsubscribe = null
})

watch(() => props.open, (open) => { if (open) void load() }, { immediate: true })
</script>

<template>
  <div class="background-tasks">
    <header class="background-tasks-head">
      <div>
        <strong>后台任务</strong>
        <small>运行时侧独立执行的回合：不占用当前对话，适合长跑批处理与并行调研。</small>
      </div>
      <div class="background-tasks-actions">
        <button type="button" class="icon-button compact" title="刷新" :disabled="loading" @click="load">
          <LoaderCircle v-if="loading" class="spin" :size="14" />
          <RefreshCw v-else :size="14" />
        </button>
        <button type="button" class="secondary-button compact-button" @click="composerOpen = !composerOpen">
          <Plus :size="13" /> 新建
        </button>
      </div>
    </header>

    <p v-if="error" class="background-tasks-note is-error">{{ error }}</p>
    <p v-else-if="note" class="background-tasks-note is-ok">{{ note }}</p>

    <div v-if="composerOpen" class="background-tasks-composer">
      <textarea v-model="draft" rows="3" placeholder="描述要让智能体在后台完成的事，例如：把 docs/ 下所有过期的接口说明更新到最新实现" />
      <div class="background-tasks-composer-actions">
        <small>{{ workspacePath ? `工作区：${workspacePath}` : '未绑定项目：使用全局工作区' }}</small>
        <button type="button" class="secondary-button" @click="composerOpen = false">取消</button>
        <button type="button" class="secondary-button primary-action"
          :disabled="!draft.trim() || busy === 'start'" @click="dispatch">
          <LoaderCircle v-if="busy === 'start'" class="spin" :size="14" />
          <Play v-else :size="14" /> 派发
        </button>
      </div>
    </div>

    <p v-if="tasks.length === 0 && !loading" class="background-tasks-empty">
      还没有后台任务。点「新建」派发一个，或让智能体在对话里用后台任务工具自行派发。
    </p>

    <ul v-else class="background-tasks-list">
      <li v-for="task in tasks" :key="task.id" class="background-task-row"
        :class="{ 'is-running': task.status === 'running' }">
        <button type="button" class="background-task-main" @click="toggle(task)">
          <span class="background-task-status" :class="`is-${task.status}`">{{ statusLabel[task.status] }}</span>
          <span class="background-task-prompt">{{ task.prompt }}</span>
          <span class="background-task-time">{{ shortTime(task.createdAt) }}</span>
        </button>
        <div class="background-task-controls">
          <button v-if="task.status === 'running'" type="button" class="icon-button compact danger-icon"
            title="取消该后台任务" :disabled="busy === `cancel:${task.id}`" @click="cancel(task)">
            <LoaderCircle v-if="busy === `cancel:${task.id}`" class="spin" :size="14" />
            <Ban v-else :size="14" />
          </button>
          <button v-if="task.sessionId" type="button" class="icon-button compact" title="打开对应会话"
            @click="emit('openSession', task.sessionId!)">
            <ExternalLink :size="14" />
          </button>
          <button type="button" class="icon-button compact" :title="expanded === task.id ? '收起' : '展开详情'"
            @click="toggle(task)">
            <X v-if="expanded === task.id" :size="14" />
            <span v-else class="background-task-more">···</span>
          </button>
        </div>

        <div v-if="expanded === task.id" class="background-task-detail">
          <p v-if="task.workspaceRoot" class="background-task-detail-row">
            <span>工作区</span><code>{{ task.workspaceRoot }}</code>
          </p>
          <p v-if="task.sessionId" class="background-task-detail-row">
            <span>会话</span><code>{{ task.sessionId }}</code>
          </p>
          <p class="background-task-detail-row">
            <span>时间</span><code>{{ shortTime(task.createdAt) }} → {{ shortTime(task.endedAt) }}</code>
          </p>
          <p v-if="task.error" class="background-task-detail-error">{{ task.error }}</p>
          <pre v-if="task.output" class="background-task-output">{{ task.output }}</pre>
          <p v-else-if="task.status !== 'running'" class="background-task-detail-empty">该任务没有留下输出摘要。</p>
        </div>
      </li>
    </ul>

    <p v-if="runningCount > 0" class="background-tasks-footer">{{ runningCount }} 个任务正在运行</p>
  </div>
</template>

<style scoped>
.background-tasks {
  display: flex;
  flex-direction: column;
  gap: 10px;
  height: 100%;
  padding: 12px 14px;
  overflow-y: auto;
}

.background-tasks-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 10px;
}

.background-tasks-head > div:first-child {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}

.background-tasks-head small {
  color: var(--text-muted);
  font-size: 11px;
  line-height: 1.5;
}

.background-tasks-actions {
  display: flex;
  flex: 0 0 auto;
  align-items: center;
  gap: 6px;
}

.background-tasks-note {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 0;
  font-size: 12px;
}

.background-tasks-note.is-ok { color: var(--accent); }
.background-tasks-note.is-error { color: var(--danger); }

.background-tasks-composer {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px;
  background: color-mix(in srgb, var(--surface-hover) 55%, transparent);
  border: 1px solid var(--border);
  border-radius: 10px;
}

.background-tasks-composer textarea {
  width: 100%;
  padding: 8px 10px;
  color: var(--text);
  font-size: 13px;
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 8px;
  resize: vertical;
}

.background-tasks-composer-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.background-tasks-composer-actions small {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--text-muted);
  font-size: 11px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.background-tasks-empty {
  margin: 12px 0;
  color: var(--text-muted);
  font-size: 12px;
  line-height: 1.6;
}

.background-tasks-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.background-task-row {
  display: grid;
  grid-template-columns: 1fr auto;
  gap: 6px;
  padding: 8px 10px;
  background: color-mix(in srgb, var(--surface-hover) 45%, transparent);
  border: 1px solid var(--border);
  border-radius: 10px;
}

.background-task-row.is-running {
  border-color: color-mix(in srgb, var(--accent) 34%, var(--border));
}

.background-task-main {
  display: flex;
  align-items: baseline;
  gap: 8px;
  min-width: 0;
  padding: 0;
  color: inherit;
  text-align: left;
  background: none;
  border: 0;
  cursor: pointer;
}

.background-task-status {
  flex: 0 0 auto;
  padding: 1px 7px;
  font-size: 10px;
  border-radius: 999px;
}

.background-task-status.is-running { color: var(--accent); background: color-mix(in srgb, var(--accent) 13%, transparent); }
.background-task-status.is-completed { color: var(--success); background: color-mix(in srgb, var(--success) 12%, transparent); }
.background-task-status.is-failed { color: var(--danger); background: color-mix(in srgb, var(--danger) 12%, transparent); }
.background-task-status.is-cancelled { color: var(--text-muted); background: var(--surface-hover); }

.background-task-prompt {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.background-task-time {
  flex: 0 0 auto;
  color: var(--text-muted);
  font-size: 10px;
  font-variant-numeric: tabular-nums;
}

.background-task-controls {
  display: flex;
  align-items: center;
  gap: 4px;
}

.background-task-more {
  font-size: 12px;
  line-height: 1;
}

.background-task-detail {
  grid-column: 1 / -1;
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding-top: 6px;
  border-top: 1px dashed var(--border);
}

.background-task-detail-row {
  display: flex;
  gap: 8px;
  margin: 0;
  font-size: 11px;
}

.background-task-detail-row span { flex: 0 0 44px; color: var(--text-muted); }

.background-task-detail-row code {
  min-width: 0;
  overflow: hidden;
  font-family: var(--font-mono, monospace);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.background-task-detail-error { margin: 0; color: var(--danger); font-size: 11px; }

.background-task-output {
  max-height: 220px;
  margin: 0;
  padding: 8px 10px;
  overflow: auto;
  color: var(--text-secondary);
  font-family: var(--font-mono, monospace);
  font-size: 11px;
  line-height: 1.6;
  white-space: pre-wrap;
  background: var(--surface);
  border-radius: 8px;
}

.background-task-detail-empty { margin: 0; color: var(--text-muted); font-size: 11px; }

.background-tasks-footer {
  margin: 0;
  color: var(--text-muted);
  font-size: 11px;
}
</style>
