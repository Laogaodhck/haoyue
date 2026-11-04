<script setup lang="ts">
import { LoaderCircle } from '@lucide/vue'
import { onMounted, ref, watch } from 'vue'
import { requestJson } from '../daemon-json'
import { defaultTaskMonitorConfig, type TaskMonitorConfig } from '../types'
import SelectMenu from './SelectMenu.vue'

/**
 * 设置 → 任务监控。
 *
 * 纯展示偏好（runtime 不读取）：展示形式（固定/浮动）与固定模式默认展示（隐藏/展示），
 * 外加四组内容开关（进度与上下文 / 执行活动 / 结果与来源 / 辅助入口），
 * 决定右栏任务监控面板展示哪些区块。读写走 advanced.get / advanced.set 的 taskMonitor 节，
 * 与 runtime 契约同构；写操作逐键 patch + 乐观更新 + 失败回滚。
 */
const props = defineProps<{ connected: boolean }>()

const emit = defineEmits<{
  notice: [message: string]
  error: [message: string]
}>()

const config = ref<TaskMonitorConfig>(defaultTaskMonitorConfig())
const loading = ref(false)
const busy = ref('')
const loaded = ref(false)

const MODE_OPTIONS = [
  { value: 'fixed', label: '固定', description: '任务监控作为右侧栏页签，与更改/工具活动等并列' },
  { value: 'floating', label: '浮动', description: '任务监控作为独立悬浮窗，可拖拽并随时收起' }
]

const SHOW_OPTIONS = [
  { value: 'show', label: '展示', description: '固定模式下打开右侧栏时默认选中任务监控页签' },
  { value: 'hidden', label: '隐藏', description: '固定模式下保持上次页签，需手动点开任务监控' }
]

async function load(): Promise<void> {
  if (!props.connected) return
  loading.value = true
  try {
    const advanced = await requestJson<{ taskMonitor?: TaskMonitorConfig }>('advanced.get')
    config.value = { ...defaultTaskMonitorConfig(), ...advanced.taskMonitor }
    loaded.value = true
  } catch (reason) {
    emit('error', reason instanceof Error ? reason.message : String(reason))
  } finally {
    loading.value = false
  }
}

/** 单键写回：乐观更新 → advanced.set 逐键 patch → 服务器完整读面回同步 → 失败回滚。 */
async function save(patch: Record<string, unknown>, busyKey: string, note: string): Promise<void> {
  busy.value = busyKey
  try {
    const advanced = await requestJson<{ taskMonitor?: TaskMonitorConfig }>('advanced.set', {
      taskMonitor: patch
    })
    config.value = { ...defaultTaskMonitorConfig(), ...advanced.taskMonitor }
    emit('notice', note)
  } catch (reason) {
    // 失败回滚由调用侧负责（调用前已保存原值并先改本地）。
    emit('error', reason instanceof Error ? reason.message : String(reason))
  } finally {
    busy.value = ''
  }
}

function changeMode(value: string): void {
  const next = value as 'fixed' | 'floating'
  if (config.value.mode === next) return
  const previous = config.value.mode
  config.value.mode = next
  void save({ mode: next }, 'mode', `任务监控形式已切换为「${next === 'fixed' ? '固定' : '浮动'}」`)
    .catch(() => { config.value.mode = previous })
}

function changeDefaultShow(value: string): void {
  const next = value as 'show' | 'hidden'
  if (config.value.fixedDefaultShow === next) return
  const previous = config.value.fixedDefaultShow
  config.value.fixedDefaultShow = next
  void save({ fixedDefaultShow: next }, 'show', `固定模式默认展示已设为「${next === 'show' ? '展示' : '隐藏'}」`)
    .catch(() => { config.value.fixedDefaultShow = previous })
}

function toggleGroup<G extends 'progress' | 'activity' | 'results' | 'access'>(
  group: G,
  key: keyof TaskMonitorConfig[G],
  checked: boolean
): void {
  const previous = config.value[group]
  const next = { ...previous, [key]: checked } as TaskMonitorConfig[G]
  config.value[group] = next
  void save({ [group]: next } as Record<string, unknown>, `${String(group)}.${String(key)}`, '任务监控显示项已更新')
    .catch(() => { config.value[group] = previous })
}

onMounted(() => { void load() })
watch(() => props.connected, (connected) => { if (connected) void load() })
</script>

<template>
  <div v-if="loading && !loaded" class="settings-loading">
    <LoaderCircle class="spin" :size="18" /> 正在读取任务监控偏好…
  </div>

  <template v-else>
    <div class="settings-section-heading">
      <div>
        <h3>任务监控</h3>
        <p>右侧栏任务监控的展示形式与内容区块开关：打开右侧栏即可实时查看当前任务的进度、活动、产出与记忆</p>
      </div>
    </div>

    <section class="settings-group">
      <div class="settings-group-title">
        <strong>展示方式</strong>
        <small>决定任务监控以页签还是悬浮窗呈现，以及固定模式下打开右侧栏的默认页签</small>
      </div>
      <div class="settings-row">
        <div>
          <strong>任务监控形式</strong>
          <small>固定：右侧栏页签；浮动：独立悬浮窗（可拖拽、可收起）</small>
        </div>
        <SelectMenu :model-value="config.mode" class="settings-select select-compact"
          :options="MODE_OPTIONS" label="任务监控形式" :disabled="busy === 'mode'"
          @update:model-value="changeMode" />
      </div>
      <div class="settings-row">
        <div>
          <strong>固定模式默认展示</strong>
          <small>固定模式下，打开右侧栏时是否默认选中任务监控页签</small>
        </div>
        <SelectMenu :model-value="config.fixedDefaultShow" class="settings-select select-compact"
          :options="SHOW_OPTIONS" label="固定模式默认展示" :disabled="busy === 'show'"
          @update:model-value="changeDefaultShow" />
      </div>
    </section>

    <section class="settings-group">
      <div class="settings-group-title">
        <strong>进度与上下文</strong>
        <small>当前任务的回顾、目标与计划步骤</small>
      </div>
      <label class="provider-enabled-row">
        <span>
          <strong>任务回顾</strong>
          <small>回合节奏：执行步数、模型轮次、token 用量与最近消息时间线</small>
        </span>
        <input :checked="config.progress.review" class="sr-only" type="checkbox"
          :disabled="busy !== ''" @change="toggleGroup('progress', 'review', Boolean(($event.target as HTMLInputElement).checked))" />
        <span class="toggle-switch" aria-hidden="true"><span /></span>
      </label>
      <label class="provider-enabled-row">
        <span>
          <strong>任务目标</strong>
          <small>会话标题与首条用户请求（任务的原始目标）</small>
        </span>
        <input :checked="config.progress.goal" class="sr-only" type="checkbox"
          :disabled="busy !== ''" @change="toggleGroup('progress', 'goal', Boolean(($event.target as HTMLInputElement).checked))" />
        <span class="toggle-switch" aria-hidden="true"><span /></span>
      </label>
      <label class="provider-enabled-row">
        <span>
          <strong>计划</strong>
          <small>模型主动拆解的步骤清单（update_plan），带运行/完成/失败状态</small>
        </span>
        <input :checked="config.progress.plan" class="sr-only" type="checkbox"
          :disabled="busy !== ''" @change="toggleGroup('progress', 'plan', Boolean(($event.target as HTMLInputElement).checked))" />
        <span class="toggle-switch" aria-hidden="true"><span /></span>
      </label>
    </section>

    <section class="settings-group">
      <div class="settings-group-title">
        <strong>执行活动</strong>
        <small>委派、后台进程、其它会话与技能/MCP 调用</small>
      </div>
      <label class="provider-enabled-row">
        <span>
          <strong>子智能体</strong>
          <small>委派给子代理的任务调用（delegate_task / delegate_tasks）</small>
        </span>
        <input :checked="config.activity.subAgents" class="sr-only" type="checkbox"
          :disabled="busy !== ''" @change="toggleGroup('activity', 'subAgents', Boolean(($event.target as HTMLInputElement).checked))" />
        <span class="toggle-switch" aria-hidden="true"><span /></span>
      </label>
      <label class="provider-enabled-row">
        <span>
          <strong>后台进程</strong>
          <small>运行时侧独立执行的回合（task.list）：不占用当前对话的推理槽</small>
        </span>
        <input :checked="config.activity.backgroundProcesses" class="sr-only" type="checkbox"
          :disabled="busy !== ''" @change="toggleGroup('activity', 'backgroundProcesses', Boolean(($event.target as HTMLInputElement).checked))" />
        <span class="toggle-switch" aria-hidden="true"><span /></span>
      </label>
      <label class="provider-enabled-row">
        <span>
          <strong>侧边聊天</strong>
          <small>其它正在运行的会话概览，可一键切换过去</small>
        </span>
        <input :checked="config.activity.sideChat" class="sr-only" type="checkbox"
          :disabled="busy !== ''" @change="toggleGroup('activity', 'sideChat', Boolean(($event.target as HTMLInputElement).checked))" />
        <span class="toggle-switch" aria-hidden="true"><span /></span>
      </label>
      <label class="provider-enabled-row">
        <span>
          <strong>Skill 与 MCP</strong>
          <small>技能与连接器工具的调用记录（不在内置工具清单中的名称）</small>
        </span>
        <input :checked="config.activity.skillMcp" class="sr-only" type="checkbox"
          :disabled="busy !== ''" @change="toggleGroup('activity', 'skillMcp', Boolean(($event.target as HTMLInputElement).checked))" />
        <span class="toggle-switch" aria-hidden="true"><span /></span>
      </label>
    </section>

    <section class="settings-group">
      <div class="settings-group-title">
        <strong>结果与来源</strong>
        <small>产出文件、网页查阅与引用的来源、知识检索</small>
      </div>
      <label class="provider-enabled-row">
        <span>
          <strong>产出</strong>
          <small>本轮改写的文件（可撤销）与文件类工具写入路径</small>
        </span>
        <input :checked="config.results.outputs" class="sr-only" type="checkbox"
          :disabled="busy !== ''" @change="toggleGroup('results', 'outputs', Boolean(($event.target as HTMLInputElement).checked))" />
        <span class="toggle-switch" aria-hidden="true"><span /></span>
      </label>
      <label class="provider-enabled-row">
        <span>
          <strong>网页查阅</strong>
          <small>web_search / web_fetch 调用记录（检索词与结果摘要）</small>
        </span>
        <input :checked="config.results.webVisits" class="sr-only" type="checkbox"
          :disabled="busy !== ''" @change="toggleGroup('results', 'webVisits', Boolean(($event.target as HTMLInputElement).checked))" />
        <span class="toggle-switch" aria-hidden="true"><span /></span>
      </label>
      <label class="provider-enabled-row">
        <span>
          <strong>来源</strong>
          <small>网页查阅中提取的 URL 列表（保序去重，最多 12 条）</small>
        </span>
        <input :checked="config.results.sources" class="sr-only" type="checkbox"
          :disabled="busy !== ''" @change="toggleGroup('results', 'sources', Boolean(($event.target as HTMLInputElement).checked))" />
        <span class="toggle-switch" aria-hidden="true"><span /></span>
      </label>
      <label class="provider-enabled-row">
        <span>
          <strong>知识</strong>
          <small>知识库检索与事实记忆工具的调用记录</small>
        </span>
        <input :checked="config.results.knowledge" class="sr-only" type="checkbox"
          :disabled="busy !== ''" @change="toggleGroup('results', 'knowledge', Boolean(($event.target as HTMLInputElement).checked))" />
        <span class="toggle-switch" aria-hidden="true"><span /></span>
      </label>
    </section>

    <section class="settings-group">
      <div class="settings-group-title">
        <strong>辅助入口</strong>
        <small>记忆固化的流水与可视化产物回看</small>
      </div>
      <label class="provider-enabled-row">
        <span>
          <strong>记忆更新</strong>
          <small>记忆固化广播（memory.consolidated）与会话内事实保存的流水</small>
        </span>
        <input :checked="config.access.memoryUpdate" class="sr-only" type="checkbox"
          :disabled="busy !== ''" @change="toggleGroup('access', 'memoryUpdate', Boolean(($event.target as HTMLInputElement).checked))" />
        <span class="toggle-switch" aria-hidden="true"><span /></span>
      </label>
      <label class="provider-enabled-row">
        <span>
          <strong>演示画面</strong>
          <small>会话中最近的图片附件与可视化产物（图片/文档/网页）</small>
        </span>
        <input :checked="config.access.demoScreen" class="sr-only" type="checkbox"
          :disabled="busy !== ''" @change="toggleGroup('access', 'demoScreen', Boolean(($event.target as HTMLInputElement).checked))" />
        <span class="toggle-switch" aria-hidden="true"><span /></span>
      </label>
    </section>
  </template>
</template>
