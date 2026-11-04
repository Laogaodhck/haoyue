<script setup lang="ts">
import {
  Blocks,
  Brain,
  Check,
  CircleAlert,
  Database,
  Eye,
  FileText,
  Globe2,
  Link2,
  ListChecks,
  LoaderCircle,
  MessagesSquare,
  MonitorPlay,
  Network,
  Target,
  Users
} from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { requestJson } from '../daemon-json'
import { collectActivities, formatDuration, toolLabel } from '../tools-activity'
import {
  collectDemo,
  collectGoal,
  collectKnowledge,
  collectMemoryUpdates,
  collectOutputs,
  collectPlan,
  collectReview,
  collectSideChat,
  collectSources,
  collectSubAgents,
  collectWebVisits,
  collectSkillMcp,
  sortBackgroundTasks,
  type MemoryUpdate
} from '../task-monitor'
import { defaultTaskMonitorConfig, type TaskMonitorConfig, type ThreadItem } from '../types'

/**
 * 右栏 → 任务监控。
 *
 * 按设置里的四组开关（进度与上下文 / 执行活动 / 结果与来源 / 辅助入口）聚合当前任务的
 * 实时上下文：回顾、目标、计划、委派、后台进程、其它会话、技能/MCP、产出、网页、来源、
 * 知识、记忆更新与演示画面。数据来自 thread / 工具活动 / task.list / memory.consolidated
 * 广播；固定模式嵌在 WorkDock 页签，浮动模式由 FloatingTaskMonitor 包一层外壳。
 */
const props = withDefaults(defineProps<{
  open: boolean
  thread?: ThreadItem
  /** 全部会话（侧边聊天区块展示其它运行中的会话）。 */
  threads?: ThreadItem[]
  embedded?: boolean
}>(), {
  embedded: false
})

const emit = defineEmits<{
  openSession: [payload: { sessionId: string; workspace?: string }]
  selectThread: [threadId: string]
}>()

interface BackgroundTask {
  id: string
  prompt: string
  status: 'running' | 'completed' | 'failed' | 'cancelled'
  sessionId?: string
  createdAt?: string
  endedAt?: string
  error?: string
}

const config = ref<TaskMonitorConfig>(defaultTaskMonitorConfig())
const configLoaded = ref(false)
const tasks = ref<BackgroundTask[]>([])
const memoryBroadcasts = ref<MemoryUpdate[]>([])
const expanded = ref<string[]>([])

const activities = computed(() => collectActivities(props.thread?.messages ?? []))
const review = computed(() => collectReview(props.thread))
const goal = computed(() => collectGoal(props.thread))
const plan = computed(() => collectPlan(props.thread))
const subAgents = computed(() => collectSubAgents(activities.value))
const sideChat = computed(() => collectSideChat(props.threads ?? [], props.thread?.id))
const skillMcp = computed(() => collectSkillMcp(activities.value))
const outputs = computed(() => collectOutputs(props.thread, activities.value))
const webVisits = computed(() => collectWebVisits(activities.value))
const sources = computed(() => collectSources(activities.value))
const knowledge = computed(() => collectKnowledge(activities.value))
const memoryUpdates = computed(() => collectMemoryUpdates(memoryBroadcasts.value, activities.value))
const demo = computed(() => collectDemo(props.thread))
const sortedTasks = computed(() => sortBackgroundTasks(tasks.value))
const runningTasks = computed(() => tasks.value.filter((task) => task.status === 'running').length)
const runningTools = computed(() => activities.value.filter((entry) => entry.state === 'running').length)

const activeGroups = computed(() => {
  const count: Array<{ icon: typeof Eye; label: string; count: number; on: boolean }> = []
  if (config.value.progress.review) count.push({ icon: Eye, label: '任务回顾', count: review.value ? 1 : 0, on: true })
  if (config.value.progress.goal) count.push({ icon: Target, label: '任务目标', count: goal.value ? 1 : 0, on: true })
  if (config.value.progress.plan) count.push({ icon: ListChecks, label: '计划', count: plan.value?.length ?? 0, on: true })
  if (config.value.activity.subAgents) count.push({ icon: Network, label: '子智能体', count: subAgents.value.length, on: true })
  if (config.value.activity.backgroundProcesses) count.push({ icon: LoaderCircle, label: '后台进程', count: tasks.value.length, on: true })
  if (config.value.activity.sideChat) count.push({ icon: MessagesSquare, label: '侧边聊天', count: sideChat.value.length, on: true })
  if (config.value.activity.skillMcp) count.push({ icon: Blocks, label: 'Skill 与 MCP', count: skillMcp.value.length, on: true })
  if (config.value.results.outputs) count.push({ icon: FileText, label: '产出', count: outputs.value.length, on: true })
  if (config.value.results.webVisits) count.push({ icon: Globe2, label: '网页查阅', count: webVisits.value.length, on: true })
  if (config.value.results.sources) count.push({ icon: Link2, label: '来源', count: sources.value.length, on: true })
  if (config.value.results.knowledge) count.push({ icon: Brain, label: '知识', count: knowledge.value.length, on: true })
  if (config.value.access.memoryUpdate) count.push({ icon: Database, label: '记忆更新', count: memoryUpdates.value.length, on: true })
  if (config.value.access.demoScreen) count.push({ icon: MonitorPlay, label: '演示画面', count: demo.value ? demo.value.images.length + demo.value.files.length : 0, on: true })
  return count
})

async function load(): Promise<void> {
  if (!props.open) return
  try {
    const advanced = await requestJson<{ taskMonitor?: TaskMonitorConfig }>('advanced.get')
    config.value = { ...defaultTaskMonitorConfig(), ...advanced.taskMonitor }
    configLoaded.value = true
  } catch {
    config.value = defaultTaskMonitorConfig()
    configLoaded.value = true
  }
  try {
    tasks.value = await requestJson<BackgroundTask[]>('task.list')
  } catch {
    tasks.value = []
  }
}

function isOpen(id: string): boolean {
  return expanded.value.includes(id)
}

function toggle(id: string): void {
  expanded.value = isOpen(id) ? expanded.value.filter((item) => item !== id) : [...expanded.value, id]
}

function openTaskSession(task: BackgroundTask): void {
  if (task.sessionId) emit('openSession', { sessionId: task.sessionId })
}

function shortTime(ms: number): string {
  const date = new Date(ms)
  if (Number.isNaN(date.getTime())) return '—'
  const pad = (value: number): string => String(value).padStart(2, '0')
  return `${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`
}

function shortIso(iso?: string): string {
  if (!iso) return '—'
  return shortTime(new Date(iso).getTime())
}

function taskStatusLabel(status: BackgroundTask['status']): string {
  return { running: '运行中', completed: '已完成', failed: '失败', cancelled: '已取消' }[status]
}

function fileKindIcon(path: string): typeof FileText {
  const ext = path.split('.').pop()?.toLowerCase() ?? ''
  if (ext === 'png' || ext === 'jpg' || ext === 'jpeg' || ext === 'gif' || ext === 'webp' || ext === 'svg') return MonitorPlay
  return FileText
}

let unsubscribe: (() => void) | null = null

onMounted(() => {
  unsubscribe = window.haoyue.daemon.onEvent((message) => {
    if (message.event === 'task.updated') void load()
    if (message.event === 'memory.consolidated') {
      const details = message.details ?? {}
      const factsAdded = Number(details.factsAdded) || 0
      const memoryPromoted = Number(details.memoryPromoted) || 0
      const note = typeof details.memoryNote === 'string' ? details.memoryNote : undefined
      const error = typeof details.error === 'string' ? details.error : undefined
      memoryBroadcasts.value = [
        { at: Date.now(), factsAdded, memoryPromoted, note, error },
        ...memoryBroadcasts.value
      ].slice(0, 12)
    }
  })
})

onBeforeUnmount(() => {
  unsubscribe?.()
  unsubscribe = null
})

watch(() => props.open, (open) => { if (open) void load() }, { immediate: true })
watch(() => props.thread?.id, () => { expanded.value = [] })
</script>

<template>
  <div class="task-monitor">
    <template v-if="!configLoaded">
      <div class="task-monitor-state"><LoaderCircle class="spin" :size="16" /> 正在读取任务监控…</div>
    </template>

    <template v-else>
      <header class="task-monitor-head">
        <div>
          <strong>{{ thread ? thread.title : '未选择任务' }}</strong>
          <small>
            <template v-if="thread?.running">
              <span class="tm-running-dot" /> {{ thread.phase ?? '运行中' }}
              <template v-if="runningTools > 0"> · {{ runningTools }} 个工具执行中</template>
            </template>
            <template v-else>空闲</template>
            <template v-if="thread?.stats?.llmRounds"> · {{ thread.stats.llmRounds }} 轮模型</template>
            <template v-if="runningTasks > 0"> · {{ runningTasks }} 个后台任务</template>
          </small>
        </div>
        <div class="task-monitor-groups" :title="activeGroups.map((g) => `${g.label} ${g.count}`).join('；')">
          <span v-for="group in activeGroups" :key="group.label" class="tm-group-chip">
            <component :is="group.icon" :size="12" /> {{ group.label }} {{ group.count }}
          </span>
        </div>
      </header>

      <!-- 进度与上下文 -->
      <section v-if="config.progress.review" class="tm-section">
        <div class="tm-section-title"><Eye :size="14" />任务回顾</div>
        <div v-if="!review" class="tm-empty">选择任务后展示回合节奏与最近消息时间线</div>
        <template v-else>
          <div class="tm-stats">
            <span>执行步数 <strong>{{ review.steps }}</strong></span>
            <span>模型轮次 <strong>{{ review.llmRounds }}</strong></span>
            <span v-if="review.inputTokens != null">输入 <strong>{{ review.inputTokens.toLocaleString() }}</strong></span>
            <span v-if="review.outputTokens != null">输出 <strong>{{ review.outputTokens.toLocaleString() }}</strong></span>
          </div>
          <ol class="tm-timeline">
            <li v-for="(item, index) in review.timeline" :key="index" :class="`tm-role-${item.role}`">
              <span class="tm-timeline-time">{{ shortTime(item.at) }}</span>
              <span class="tm-timeline-role">{{ item.role === 'user' ? '用户' : '助手' }}</span>
              <span class="tm-timeline-text">{{ item.snippet || '…' }}</span>
            </li>
          </ol>
        </template>
      </section>

      <section v-if="config.progress.goal" class="tm-section">
        <div class="tm-section-title"><Target :size="14" />任务目标</div>
        <div v-if="!goal" class="tm-empty">选择任务后展示会话标题与原始请求</div>
        <div v-else class="tm-goal">
          <strong>{{ goal.title }}</strong>
          <p v-if="goal.request">{{ goal.request }}</p>
        </div>
      </section>

      <section v-if="config.progress.plan" class="tm-section">
        <div class="tm-section-title"><ListChecks :size="14" />计划</div>
        <div v-if="!plan || plan.length === 0" class="tm-empty">模型尚未拆分步骤（update_plan 推来后显示）</div>
        <ol v-else class="tm-plan">
          <li v-for="step in plan" :key="step.id" :data-state="step.state">
            <span class="tm-plan-state">
              <LoaderCircle v-if="step.state === 'running'" :size="12" class="spin" />
              <Check v-else-if="step.state === 'done'" :size="12" />
              <CircleAlert v-else-if="step.state === 'error'" :size="12" />
              <span v-else class="tm-plan-pending" />
            </span>
            <span class="tm-plan-title">{{ step.title }}</span>
          </li>
        </ol>
      </section>

      <!-- 执行活动 -->
      <section v-if="config.activity.subAgents" class="tm-section">
        <div class="tm-section-title"><Network :size="14" />子智能体</div>
        <div v-if="subAgents.length === 0" class="tm-empty">本轮没有委派子任务</div>
        <ol v-else class="tm-tool-list">
          <li v-for="entry in subAgents" :key="entry.id" class="tm-tool-row">
            <Users :size="14" />
            <span class="tm-tool-name">{{ entry.name === 'delegate_task' ? '委派任务' : '并行委派' }}</span>
            <span class="tm-tool-args">{{ entry.detail || entry.args || '' }}</span>
            <span class="tm-tool-state" :data-state="entry.state">
              {{ entry.state === 'running' ? '执行中' : entry.state === 'error' ? '失败' : '完成' }}
            </span>
          </li>
        </ol>
      </section>

      <section v-if="config.activity.backgroundProcesses" class="tm-section">
        <div class="tm-section-title"><LoaderCircle :size="14" />后台进程</div>
        <div v-if="sortedTasks.length === 0" class="tm-empty">没有后台任务（task.start 派发的独立回合）</div>
        <ol v-else class="tm-task-list">
          <li v-for="task in sortedTasks" :key="task.id" class="tm-task-row">
            <span class="tm-task-status" :data-status="task.status">{{ taskStatusLabel(task.status) }}</span>
            <button v-if="task.sessionId" type="button" class="tm-task-open" @click="openTaskSession(task)">
              {{ task.prompt.length > 40 ? `${task.prompt.slice(0, 40)}…` : task.prompt }}
            </button>
            <span v-else class="tm-task-prompt">{{ task.prompt.length > 40 ? `${task.prompt.slice(0, 40)}…` : task.prompt }}</span>
            <span class="tm-task-time">{{ shortIso(task.createdAt) }}</span>
          </li>
        </ol>
      </section>

      <section v-if="config.activity.sideChat" class="tm-section">
        <div class="tm-section-title"><MessagesSquare :size="14" />侧边聊天</div>
        <div v-if="sideChat.length === 0" class="tm-empty">没有其它正在进行的会话</div>
        <ol v-else class="tm-chat-list">
          <li v-for="chat in sideChat" :key="chat.id">
            <button type="button" class="tm-chat-row" @click="emit('selectThread', chat.id)">
              <span class="tm-chat-title">{{ chat.title || '未命名任务' }}</span>
              <span class="tm-chat-meta">{{ chat.messages.length }} 条消息{{ chat.running ? ' · 运行中' : '' }}</span>
            </button>
          </li>
        </ol>
      </section>

      <section v-if="config.activity.skillMcp" class="tm-section">
        <div class="tm-section-title"><Blocks :size="14" />Skill 与 MCP</div>
        <div v-if="skillMcp.length === 0" class="tm-empty">未调用技能或连接器工具</div>
        <ol v-else class="tm-tool-list">
          <li v-for="entry in skillMcp" :key="entry.id" class="tm-tool-row">
            <Blocks :size="14" />
            <span class="tm-tool-name">{{ toolLabel(entry.name) }}</span>
            <span class="tm-tool-args">{{ entry.args || entry.detail || '' }}</span>
            <span class="tm-tool-state" :data-state="entry.state">
              {{ entry.state === 'running' ? '执行中' : entry.state === 'error' ? '失败' : '完成' }}
            </span>
          </li>
        </ol>
      </section>

      <!-- 结果与来源 -->
      <section v-if="config.results.outputs" class="tm-section">
        <div class="tm-section-title"><FileText :size="14" />产出</div>
        <div v-if="outputs.length === 0" class="tm-empty">本轮没有改写文件</div>
        <ol v-else class="tm-file-list">
          <li v-for="output in outputs" :key="output.path">
            <button type="button" class="tm-file-row" @click="toggle(output.path)">
              <component :is="fileKindIcon(output.path)" :size="14" />
              <span class="tm-file-path">{{ output.path }}</span>
              <span v-if="output.addedLines || output.removedLines" class="tm-file-diff">
                +{{ output.addedLines ?? 0 }} -{{ output.removedLines ?? 0 }}
              </span>
            </button>
            <div v-if="isOpen(output.path) && output.detail" class="tm-file-detail">{{ output.detail }}</div>
          </li>
        </ol>
      </section>

      <section v-if="config.results.webVisits" class="tm-section">
        <div class="tm-section-title"><Globe2 :size="14" />网页查阅</div>
        <div v-if="webVisits.length === 0" class="tm-empty">本轮未联网查阅</div>
        <ol v-else class="tm-tool-list">
          <li v-for="entry in webVisits" :key="entry.id" class="tm-tool-row">
            <Globe2 :size="14" />
            <span class="tm-tool-name">{{ toolLabel(entry.name) }}</span>
            <span class="tm-tool-args">{{ entry.args || entry.detail || '' }}</span>
            <span class="tm-tool-state" :data-state="entry.state">
              {{ entry.state === 'running' ? '执行中' : entry.state === 'error' ? '失败' : formatDuration(entry.durationMs) || '完成' }}
            </span>
          </li>
        </ol>
      </section>

      <section v-if="config.results.sources" class="tm-section">
        <div class="tm-section-title"><Link2 :size="14" />来源</div>
        <div v-if="sources.length === 0" class="tm-empty">网页查阅未提取到链接</div>
        <ol v-else class="tm-source-list">
          <li v-for="url in sources" :key="url">
            <a :href="url" target="_blank" rel="noreferrer" class="tm-source-url">{{ url }}</a>
          </li>
        </ol>
      </section>

      <section v-if="config.results.knowledge" class="tm-section">
        <div class="tm-section-title"><Brain :size="14" />知识</div>
        <div v-if="knowledge.length === 0" class="tm-empty">未调用知识库或事实记忆工具</div>
        <ol v-else class="tm-tool-list">
          <li v-for="entry in knowledge" :key="entry.id" class="tm-tool-row">
            <Brain :size="14" />
            <span class="tm-tool-name">{{ toolLabel(entry.name) }}</span>
            <span class="tm-tool-args">{{ entry.args || entry.detail || '' }}</span>
            <span class="tm-tool-state" :data-state="entry.state">完成</span>
          </li>
        </ol>
      </section>

      <!-- 辅助入口 -->
      <section v-if="config.access.memoryUpdate" class="tm-section">
        <div class="tm-section-title"><Database :size="14" />记忆更新</div>
        <div v-if="memoryUpdates.length === 0" class="tm-empty">暂无记忆固化记录（memory.consolidated 广播）</div>
        <ol v-else class="tm-memory-list">
          <li v-for="(update, index) in memoryUpdates" :key="index">
            <span class="tm-memory-time">{{ shortTime(update.at) }}</span>
            <span v-if="update.error" class="tm-memory-error">固化失败：{{ update.error }}</span>
            <template v-else>
              <span class="tm-memory-text">新增 {{ update.factsAdded }} 条事实</span>
              <template v-if="update.memoryPromoted > 0"> · 提升 {{ update.memoryPromoted }} 条长期记忆</template>
              <span v-if="update.note" class="tm-memory-note">{{ update.note }}</span>
            </template>
          </li>
        </ol>
      </section>

      <section v-if="config.access.demoScreen" class="tm-section">
        <div class="tm-section-title"><MonitorPlay :size="14" />演示画面</div>
        <div v-if="!demo" class="tm-empty">会话中还没有图片或可视化产物</div>
        <template v-else>
          <div v-if="demo.images.length > 0" class="tm-demo-images">
            <span v-for="image in demo.images" :key="image.name" class="tm-demo-item" :title="image.name">
              <MonitorPlay :size="16" /> {{ image.name }}
            </span>
          </div>
          <div v-if="demo.files.length > 0" class="tm-demo-files">
            <span v-for="file in demo.files" :key="file.path" class="tm-demo-item" :title="file.path">
              <FileText :size="16" /> {{ file.name }}
            </span>
          </div>
        </template>
      </section>
    </template>
  </div>
</template>

<style scoped>
.task-monitor {
  padding: 12px 14px 24px;
  display: flex;
  flex-direction: column;
  gap: 14px;
  font-size: 13px;
}
.task-monitor-state {
  color: var(--color-text-2, #8a8f98);
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 24px 0;
  justify-content: center;
}
.task-monitor-head {
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.task-monitor-head > div:first-child {
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.task-monitor-head strong {
  font-size: 14px;
}
.task-monitor-head small {
  color: var(--color-text-2, #8a8f98);
  display: flex;
  align-items: center;
  gap: 4px;
  flex-wrap: wrap;
}
.tm-running-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: #2e9e44;
  display: inline-block;
}
.task-monitor-groups {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}
.tm-group-chip {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 2px 7px;
  border-radius: 999px;
  background: var(--color-bg-2, rgba(0, 0, 0, 0.04));
  color: var(--color-text-2, #8a8f98);
  font-size: 11px;
}
.tm-section {
  border: 1px solid var(--color-border, rgba(0, 0, 0, 0.08));
  border-radius: 8px;
  overflow: hidden;
}
.tm-section-title {
  display: flex;
  align-items: center;
  gap: 6px;
  font-weight: 600;
  font-size: 12px;
  padding: 8px 10px;
  background: var(--color-bg-2, rgba(0, 0, 0, 0.03));
  border-bottom: 1px solid var(--color-border, rgba(0, 0, 0, 0.06));
}
.tm-empty {
  padding: 10px 12px;
  color: var(--color-text-3, #b0b4bc);
  font-size: 12px;
}
.tm-stats {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 12px;
  padding: 8px 12px;
  color: var(--color-text-2, #8a8f98);
  font-size: 12px;
}
.tm-stats strong {
  color: var(--color-text-1, #1f2329);
}
.tm-timeline {
  list-style: none;
  margin: 0;
  padding: 4px 12px 10px;
  display: flex;
  flex-direction: column;
  gap: 5px;
}
.tm-timeline li {
  display: flex;
  align-items: baseline;
  gap: 6px;
  font-size: 12px;
}
.tm-timeline-time {
  color: var(--color-text-3, #b0b4bc);
  font-variant-numeric: tabular-nums;
  flex-shrink: 0;
}
.tm-timeline-role {
  flex-shrink: 0;
  font-size: 11px;
  padding: 0 4px;
  border-radius: 4px;
  background: var(--color-bg-2, rgba(0, 0, 0, 0.05));
}
.tm-timeline-text {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--color-text-2, #5c6470);
}
.tm-goal {
  padding: 10px 12px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.tm-goal p {
  margin: 0;
  color: var(--color-text-2, #8a8f98);
  font-size: 12px;
  line-height: 1.5;
}
.tm-plan {
  list-style: none;
  margin: 0;
  padding: 6px 12px 10px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.tm-plan li {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
}
.tm-plan li[data-state='done'] .tm-plan-title {
  color: var(--color-text-3, #b0b4bc);
  text-decoration: line-through;
}
.tm-plan-state {
  display: inline-flex;
  width: 14px;
  justify-content: center;
  color: #2e9e44;
}
.tm-plan li[data-state='error'] .tm-plan-state {
  color: #d93026;
}
.tm-plan li[data-state='running'] .tm-plan-state {
  color: #2e9e44;
}
.tm-plan-pending {
  width: 9px;
  height: 9px;
  border-radius: 50%;
  border: 1.5px solid var(--color-text-3, #b0b4bc);
}
.tm-tool-list,
.tm-task-list,
.tm-chat-list,
.tm-file-list,
.tm-source-list,
.tm-memory-list {
  list-style: none;
  margin: 0;
  padding: 6px 12px 10px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.tm-tool-row {
  display: flex;
  align-items: center;
  gap: 7px;
  font-size: 12px;
  color: var(--color-text-2, #5c6470);
}
.tm-tool-name {
  font-weight: 600;
  color: var(--color-text-1, #1f2329);
  flex-shrink: 0;
}
.tm-tool-args {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  flex: 1;
}
.tm-tool-state {
  flex-shrink: 0;
  font-size: 11px;
  color: var(--color-text-3, #b0b4bc);
}
.tm-tool-state[data-state='running'] {
  color: #2e9e44;
}
.tm-tool-state[data-state='error'] {
  color: #d93026;
}
.tm-task-row {
  display: flex;
  align-items: center;
  gap: 7px;
  font-size: 12px;
}
.tm-task-status {
  flex-shrink: 0;
  font-size: 11px;
  padding: 1px 6px;
  border-radius: 999px;
  background: var(--color-bg-2, rgba(0, 0, 0, 0.05));
  color: var(--color-text-2, #8a8f98);
}
.tm-task-status[data-status='running'] {
  color: #2e9e44;
  background: rgba(46, 158, 68, 0.1);
}
.tm-task-status[data-status='failed'] {
  color: #d93026;
  background: rgba(217, 48, 38, 0.1);
}
.tm-task-open {
  border: none;
  background: none;
  padding: 0;
  color: var(--color-accent, #3370ff);
  cursor: pointer;
  text-align: left;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  flex: 1;
}
.tm-task-prompt {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  flex: 1;
}
.tm-task-time {
  flex-shrink: 0;
  color: var(--color-text-3, #b0b4bc);
  font-variant-numeric: tabular-nums;
}
.tm-chat-row {
  border: none;
  background: none;
  width: 100%;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 4px 0;
  cursor: pointer;
  color: var(--color-text-1, #1f2329);
  font-size: 12px;
}
.tm-chat-row:hover {
  color: var(--color-accent, #3370ff);
}
.tm-chat-meta {
  color: var(--color-text-3, #b0b4bc);
  flex-shrink: 0;
}
.tm-file-row {
  border: none;
  background: none;
  width: 100%;
  display: flex;
  align-items: center;
  gap: 7px;
  padding: 3px 0;
  cursor: pointer;
  font-size: 12px;
  color: var(--color-text-1, #1f2329);
  text-align: left;
}
.tm-file-path {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  flex: 1;
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 11px;
}
.tm-file-diff {
  flex-shrink: 0;
  font-size: 11px;
  color: #2e9e44;
}
.tm-file-detail {
  padding: 4px 8px 8px 24px;
  color: var(--color-text-2, #8a8f98);
  font-size: 11px;
  white-space: pre-wrap;
  word-break: break-all;
}
.tm-source-url {
  color: var(--color-accent, #3370ff);
  text-decoration: none;
  font-size: 12px;
  word-break: break-all;
}
.tm-memory-list li {
  display: flex;
  align-items: baseline;
  gap: 6px;
  font-size: 12px;
  flex-wrap: wrap;
}
.tm-memory-time {
  color: var(--color-text-3, #b0b4bc);
  flex-shrink: 0;
  font-variant-numeric: tabular-nums;
}
.tm-memory-error {
  color: #d93026;
}
.tm-memory-text {
  color: var(--color-text-2, #5c6470);
}
.tm-memory-note {
  color: var(--color-text-3, #b0b4bc);
}
.tm-demo-images,
.tm-demo-files {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px 12px 10px;
}
.tm-demo-item {
  display: flex;
  align-items: center;
  gap: 7px;
  font-size: 12px;
  color: var(--color-text-2, #5c6470);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
