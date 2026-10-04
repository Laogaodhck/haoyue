<script setup lang="ts">
import { ArrowLeft, Brain, LoaderCircle, RefreshCw, Save, ScrollText } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { confirmAction } from '../confirmation'
import SelectMenu from './SelectMenu.vue'

const props = defineProps<{
  open: boolean
}>()

const emit = defineEmits<{
  close: []
}>()

/** rules.list 返回的层级规则文件。 */
interface RuleFile {
  path: string
  isRoot: boolean
  content: string
}

interface RulesPayload {
  workspace: string
  isGlobal: boolean
  files: RuleFile[]
}

interface MemoryPayload {
  path: string
  isGlobal: boolean
  exists: boolean
  content: string
}

const SCOPE_OPTIONS = [
  { value: 'workspace', label: '当前工作区', description: '仅此工作区的会话加载' },
  { value: 'global', label: '全局', description: '跨工作区共享' }
]

const tab = ref<'rules' | 'memory'>('rules')
const scope = ref<'workspace' | 'global'>('workspace')
const files = ref<RuleFile[]>([])
const activePath = ref('AGENTS.md')
const workspaceRoot = ref('')
const memoryPath = ref('')
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const savedMessage = ref('')
/** 编辑缓冲与加载时的原始内容，用于脏检查。 */
const content = ref('')
const baseline = ref('')

const isRules = computed(() => tab.value === 'rules')
const isGlobal = computed(() => !isRules.value && scope.value === 'global')
const dirty = computed(() => content.value !== baseline.value)

/** 保存成功提示的自动消失定时器。 */
let savedTimer: ReturnType<typeof setTimeout> | undefined
/** 切换确认取消后回滚 ref 时抑制 watch 重入。 */
let suppressSwitch = false

function clearSavedTimer(): void {
  if (savedTimer) {
    clearTimeout(savedTimer)
    savedTimer = undefined
  }
}

function flashSaved(message: string): void {
  clearSavedTimer()
  savedMessage.value = message
  savedTimer = setTimeout(() => {
    savedMessage.value = ''
  }, 3000)
}

/**
 * Tab / 范围 / 规则文件切换前的脏检查：有未保存修改时先确认，
 * 取消则回滚切换（回滚引发的 watch 触发由 suppressSwitch 抑制）。
 */
async function guardSwitch(rollback: () => void, reload: () => void): Promise<void> {
  if (suppressSwitch) {
    suppressSwitch = false
    return
  }
  if (dirty.value) {
    const confirmed = await confirmAction({
      title: '放弃未保存的修改？',
      message: '当前内容已修改但尚未保存，切换后将丢失这些修改。',
      confirmLabel: '放弃修改',
      danger: true
    })
    if (!confirmed) {
      suppressSwitch = true
      rollback()
      return
    }
  }
  clearSavedTimer()
  savedMessage.value = ''
  reload()
}

const ruleOptions = computed(() => {
  const options = files.value.map(file => ({
    value: file.path,
    label: file.isRoot ? 'AGENTS.md（根规则）' : file.path
  }))
  // 根规则始终可编辑（即使文件尚未创建，保存即新建）。
  if (!options.some(option => option.value === 'AGENTS.md'))
    options.unshift({ value: 'AGENTS.md', label: 'AGENTS.md（根规则）' })
  return options
})

const activeRuleFile = computed(() => files.value.find(file => file.path === activePath.value))
const displayPath = computed(() => {
  if (isRules.value) {
    if (!workspaceRoot.value) return activePath.value
    return `${workspaceRoot.value.replace(/[\\/]+$/, '')}/${activePath.value}`
  }
  return memoryPath.value
})

async function requestJson<T>(method: string, params: Record<string, unknown> = {}): Promise<T> {
  const response = await window.haoyue.daemon.request(method, params)
  return JSON.parse(response.data) as T
}

async function loadRules(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    const payload = await requestJson<RulesPayload>('rules.list')
    files.value = payload.files
    workspaceRoot.value = payload.workspace
    if (!payload.files.some(file => file.path === activePath.value))
      activePath.value = 'AGENTS.md'
    const current = payload.files.find(file => file.path === activePath.value)
    content.value = current?.content ?? ''
    baseline.value = content.value
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    loading.value = false
  }
}

async function loadMemory(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    const payload = await requestJson<MemoryPayload>('memory.get', scope.value === 'global' ? { global: true } : {})
    memoryPath.value = payload.path
    content.value = payload.content
    baseline.value = payload.content
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    loading.value = false
  }
}

function load(): Promise<void> {
  return isRules.value ? loadRules() : loadMemory()
}

async function save(): Promise<void> {
  if (saving.value) return
  saving.value = true
  error.value = ''
  try {
    if (isRules.value) {
      await requestJson('rules.save', { path: activePath.value, content: content.value })
      await loadRules()
    } else {
      const payload = await requestJson<MemoryPayload>('memory.save', {
        ...(scope.value === 'global' ? { global: true } : {}),
        content: content.value
      })
      memoryPath.value = payload.path
      baseline.value = content.value
    }
    flashSaved(isRules.value ? '规则已保存，新会话即生效' : '记忆已保存')
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    saving.value = false
  }
}

async function requestClose(): Promise<void> {
  if (dirty.value) {
    const confirmed = await confirmAction({
      title: '放弃未保存的修改？',
      message: '当前内容已修改但尚未保存，关闭后将丢失这些修改。',
      confirmLabel: '放弃修改',
      danger: true
    })
    if (!confirmed) return
  }
  emit('close')
}

// SelectMenu 的 v-model 直接更新 scope；这里只负责切换前脏检查与重新加载。
watch(scope, (_next, previous) => {
  if (!props.open || isRules.value) return
  void guardSwitch(() => { scope.value = previous }, () => void loadMemory())
})

watch(activePath, (_next, previous) => {
  if (!props.open || !isRules.value) return
  void guardSwitch(() => { activePath.value = previous }, () => {
    const current = files.value.find(file => file.path === activePath.value)
    content.value = current?.content ?? ''
    baseline.value = content.value
  })
})

watch(tab, (_next, previous) => {
  if (!props.open) return
  void guardSwitch(() => { tab.value = previous }, () => void load())
})

watch(() => props.open, (open) => {
  if (!open) return
  tab.value = 'rules'
  scope.value = 'workspace'
  activePath.value = 'AGENTS.md'
  content.value = ''
  baseline.value = ''
  error.value = ''
  savedMessage.value = ''
  suppressSwitch = false
  void loadRules()
})

function closeOnEscape(event: KeyboardEvent): void {
  if (!props.open) return
  if (event.key === 'Escape') void requestClose()
  // Ctrl/Cmd+S 快速保存，与设置编辑器的习惯一致。
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
    event.preventDefault()
    if (dirty.value && !saving.value && !loading.value) void save()
  }
}

onMounted(() => document.addEventListener('keydown', closeOnEscape))
onBeforeUnmount(() => {
  document.removeEventListener('keydown', closeOnEscape)
  clearSavedTimer()
})
</script>

<template>
  <section v-if="open" class="rules-memory-dialog embedded-page" role="region" aria-labelledby="rules-memory-title">
    <header class="rules-memory-header">
      <div class="rules-memory-heading">
        <button class="page-back-button" type="button" @click="requestClose">
          <ArrowLeft :size="18" />
          <span>返回应用</span>
        </button>
        <div class="rules-memory-title-copy">
          <h2 id="rules-memory-title">
            <ScrollText :size="17" /> 规则与记忆
          </h2>
          <p v-if="displayPath" :title="displayPath">{{ displayPath }}</p>
        </div>
      </div>
      <div class="rules-memory-tabs" role="tablist">
        <button type="button" role="tab" :aria-selected="isRules" :class="{ active: isRules }" @click="tab = 'rules'">
          <ScrollText :size="14" /> 规则
        </button>
        <button type="button" role="tab" :aria-selected="!isRules" :class="{ active: !isRules }" @click="tab = 'memory'">
          <Brain :size="14" /> 记忆
        </button>
      </div>
    </header>

    <div class="rules-memory-toolbar">
      <SelectMenu v-if="isRules" v-model="activePath" :options="ruleOptions" label="规则文件" class="rules-memory-picker" />
      <SelectMenu v-else v-model="scope" :options="SCOPE_OPTIONS" label="记忆范围" class="rules-memory-picker" />
      <span v-if="dirty" class="rules-memory-dirty">未保存</span>
      <span class="rules-memory-spacer" />
      <button class="icon-button" title="重新加载" :disabled="loading" @click="load">
        <RefreshCw :size="16" :class="{ spinning: loading }" />
      </button>
      <button class="secondary-button primary-action" :disabled="saving || loading || !dirty" @click="save">
        <LoaderCircle v-if="saving" class="spin" :size="14" />
        <Save v-else :size="14" /> 保存
      </button>
    </div>

    <p v-if="isRules" class="rules-memory-hint">
      AGENTS.md 是用户写给 Agent 的工作区指令，子目录规则在涉及该目录的文件时优先生效；直接对话指令始终优先于规则文件。
    </p>
    <p v-else class="rules-memory-hint">
      记忆文件（MEMORY.md）会注入每次会话：Agent 在对话中沉淀长期事实，也可以在这里手动维护；越靠前越容易被记住。
    </p>

    <p v-if="error" class="rules-memory-error">{{ error }}</p>
    <p v-else-if="savedMessage" class="rules-memory-saved">{{ savedMessage }}</p>
    <p v-else-if="isRules && files.length === 0" class="rules-memory-hint subtle">
      还没有规则文件——在下方写下第一条工作区规则，保存即创建 AGENTS.md。
    </p>

    <div class="rules-memory-editor">
      <div v-if="loading && !content" class="rules-memory-loading">
        <RefreshCw :size="26" class="spinning" />
        <strong>正在加载…</strong>
      </div>
      <textarea v-else v-model="content" class="rules-memory-textarea" spellcheck="false"
        :placeholder="isRules
          ? '# AGENTS.md\n\n- 构建命令：pnpm build\n- 提交信息使用中文\n- 修改 src/ 前先跑 pnpm typecheck'
          : '# 项目记忆\n\n- 数据库迁移固定用 pnpm db:migrate\n- 用户偏好：回复简洁，先结论后细节'" />
    </div>

    <footer class="rules-memory-footer">
      {{
        isRules
          ? '规则在每次会话开始时注入（PromptSlot.Workspace），修改只影响之后的会话'
          : '记忆在每次会话开始时注入（PromptSlot.Memory），Agent 与你都可以读写这份文件'
      }}
    </footer>
  </section>
</template>

<style scoped>
.rules-memory-dialog.embedded-page {
  display: grid;
  width: 100%;
  height: 100%;
  grid-template-rows: auto auto auto minmax(0, 1fr) auto;
  min-height: 0;
  overflow: hidden;
  background: var(--bg);
}

.rules-memory-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 20px;
  min-height: 62px;
  padding: 10px 22px;
  background: var(--surface-raised);
  border-bottom: 1px solid var(--border);
}

.rules-memory-heading {
  display: flex;
  min-width: 0;
  align-items: center;
  gap: 12px;
}

.rules-memory-title-copy {
  min-width: 0;
}

.rules-memory-title-copy h2 {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0;
  font-size: 16px;
  font-weight: 650;
  letter-spacing: -.01em;
}

.rules-memory-title-copy p {
  max-width: 480px;
  margin: 2px 0 0;
  overflow: hidden;
  color: var(--text-secondary);
  font-size: 11px;
  text-overflow: ellipsis;
  white-space: nowrap;
  direction: rtl;
  text-align: left;
}

.rules-memory-tabs {
  display: flex;
  flex: 0 0 auto;
  gap: 2px;
  padding: 3px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 10px;
}

.rules-memory-tabs button {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 6px 14px;
  color: var(--text-secondary);
  font-size: 12.5px;
  font-weight: 600;
  background: transparent;
  border: none;
  border-radius: 8px;
  cursor: pointer;
  transition: background 120ms ease, color 120ms ease;
}

.rules-memory-tabs button:hover {
  color: var(--text);
}

.rules-memory-tabs button.active {
  color: var(--accent);
  background: var(--accent-soft);
}

.rules-memory-toolbar {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 14px 22px 0;
}

.rules-memory-picker {
  width: 280px;
  flex: 0 0 auto;
}

.rules-memory-dirty {
  padding: 3px 9px;
  color: var(--warning, #b58900);
  font-size: 11px;
  font-weight: 600;
  background: color-mix(in srgb, #b58900 12%, transparent);
  border-radius: 999px;
}

.rules-memory-spacer {
  flex: 1;
}

.rules-memory-hint {
  margin: 8px 22px 0;
  color: var(--text-secondary);
  font-size: 12px;
  line-height: 1.6;
}

.rules-memory-hint.subtle {
  margin-top: 6px;
  color: var(--text-muted);
}

.rules-memory-error {
  margin: 8px 22px 0;
  padding: 9px 12px;
  color: var(--danger);
  font-size: 12px;
  background: color-mix(in srgb, var(--danger) 9%, transparent);
  border: 1px solid color-mix(in srgb, var(--danger) 25%, transparent);
  border-radius: 8px;
}

.rules-memory-saved {
  margin: 8px 22px 0;
  padding: 9px 12px;
  color: var(--accent);
  font-size: 12px;
  background: color-mix(in srgb, var(--accent) 9%, transparent);
  border: 1px solid color-mix(in srgb, var(--accent) 25%, transparent);
  border-radius: 8px;
}

.rules-memory-editor {
  display: flex;
  min-height: 0;
  padding: 10px 22px 14px;
}

.rules-memory-textarea {
  box-sizing: border-box;
  width: 100%;
  padding: 14px 16px;
  color: var(--text);
  font-family: ui-monospace, 'Cascadia Mono', Consolas, 'Courier New', monospace;
  font-size: 12.5px;
  line-height: 1.7;
  background: var(--surface-raised);
  border: 1px solid var(--border-strong);
  border-radius: 10px;
  outline: none;
  resize: none;
  transition: border-color 140ms ease, box-shadow 140ms ease;
}

.rules-memory-textarea:focus {
  border-color: color-mix(in srgb, var(--accent) 66%, var(--border));
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 20%, transparent);
}

.rules-memory-loading {
  display: flex;
  flex: 1;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  color: var(--text-muted);
}

.rules-memory-footer {
  padding: 11px 22px;
  color: var(--text-muted);
  font-size: 11.5px;
  background: var(--surface-raised);
  border-top: 1px solid var(--border);
}
</style>
