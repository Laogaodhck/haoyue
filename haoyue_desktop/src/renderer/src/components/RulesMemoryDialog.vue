<script setup lang="ts">
import { daemonRequest } from '../daemon-rpc'
import { ArrowLeft, Brain, FolderOpen, LoaderCircle, Plus, RefreshCw, Save, ScrollText, Trash2, X } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
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

/** 结构化规则（全局 / 项目作用域）。 */
interface ManagedRule {
  id: string
  title: string
  content: string
  enabled: boolean
  priority: number
  scope: 'global' | 'workspace'
  createdAt: string
  updatedAt: string
}

interface RuleConflict {
  title: string
  globalId: string
  workspaceId: string
  winner: 'global' | 'workspace'
}

interface EffectiveRule {
  id: string
  title: string
  scope: 'global' | 'workspace'
  priority: number
}

interface RuleHit {
  ruleId: string
  title: string
  scope: string
  sessionId: string | null
  at: string
  action?: string | null
  actor?: string | null
}

interface RuleResolution {
  ruleId: string
  title: string
  scope: string
  action: string
  actor: string
  at: string
}

interface RulesPayload {
  workspace: string
  isGlobal: boolean
  files: RuleFile[]
  managed?: ManagedRule[]
  effective?: EffectiveRule[]
  conflicts?: RuleConflict[]
  hits?: RuleHit[]
  resolutions?: RuleResolution[]
}

/** 记忆事实（含来源、时间、置信度与仲裁状态）。 */
interface FactEntry {
  id: string
  content: string
  topic: string | null
  source: 'user' | 'agent'
  confidence: number
  createdAt: string
  expiresAt: string | null
  supersededBy: string | null
  supersededAt: string | null
  pinned: boolean
  sourceSessionId: string | null
  kind: 'auto' | 'manual'
  confirmed: boolean
}

interface FactConflict {
  topic: string | null
  keptId: string
  keptContent: string
  replacedIds: string[]
  replacedContents: string[]
  at: string
}

interface FactsPayload {
  workspace: string
  activeCount: number
  capacity: number
  facts: FactEntry[]
  conflicts: FactConflict[]
  entry?: FactEntry
  superseded?: FactEntry[]
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
/** 新建规则文件的内联表单状态。 */
const creating = ref(false)
const newPath = ref('')
const newPathInput = ref<HTMLInputElement | null>(null)
const deleting = ref(false)

/** 结构化规则管理面。 */
const managedRules = ref<ManagedRule[]>([])
const ruleConflicts = ref<RuleConflict[]>([])
const effectiveRules = ref<EffectiveRule[]>([])
const ruleHits = ref<RuleHit[]>([])
const ruleResolutions = ref<RuleResolution[]>([])
const ruleEditing = ref<ManagedRule | null>(null)
const ruleSaving = ref(false)
const ruleError = ref('')

/** 事实库管理面。 */
const facts = ref<FactEntry[]>([])
const factConflicts = ref<FactConflict[]>([])
const factsLoading = ref(false)
const factsError = ref('')
const factEditing = ref<Partial<FactEntry> | null>(null)
const factSaving = ref(false)
const factNotice = ref('')
const factUndoId = ref('')

const isRules = computed(() => tab.value === 'rules')
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
  const response = await daemonRequest(method, params)
  return JSON.parse(response.data) as T
}

async function loadRules(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    const payload = await requestJson<RulesPayload>('rules.list')
    files.value = payload.files
    workspaceRoot.value = payload.workspace
    managedRules.value = payload.managed ?? []
    ruleConflicts.value = payload.conflicts ?? []
    effectiveRules.value = payload.effective ?? []
    ruleHits.value = payload.hits ?? []
    ruleResolutions.value = payload.resolutions ?? []
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
    await loadFacts()
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    loading.value = false
  }
}

async function loadFacts(): Promise<void> {
  factsLoading.value = true
  factsError.value = ''
  try {
    const payload = await requestJson<FactsPayload>('facts.list')
    facts.value = payload.facts
    factConflicts.value = payload.conflicts
  } catch (reason) {
    factsError.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    factsLoading.value = false
  }
}

// ---- 结构化规则 ----

function beginRuleCreate(): void {
  ruleError.value = ''
  ruleEditing.value = {
    id: '', title: '', content: '', enabled: true, priority: 50,
    scope: 'workspace', createdAt: '', updatedAt: ''
  }
}

function beginRuleEdit(rule: ManagedRule): void {
  ruleError.value = ''
  ruleEditing.value = { ...rule }
}

function cancelRuleEdit(): void {
  ruleEditing.value = null
  ruleError.value = ''
}

async function saveManagedRule(): Promise<void> {
  if (!ruleEditing.value || ruleSaving.value) return
  const draft = ruleEditing.value
  if (!draft.title.trim() || !draft.content.trim()) {
    ruleError.value = '请填写规则标题与内容'
    return
  }
  ruleSaving.value = true
  ruleError.value = ''
  try {
    await requestJson('rules.save', {
      kind: 'managed',
      id: draft.id || undefined,
      title: draft.title.trim(),
      content: draft.content,
      enabled: draft.enabled,
      priority: Number(draft.priority) || 50,
      scope: draft.scope
    })
    ruleEditing.value = null
    await loadRules()
    flashSaved('规则已保存，下一回合生效')
  } catch (reason) {
    ruleError.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    ruleSaving.value = false
  }
}

async function toggleManagedRule(rule: ManagedRule): Promise<void> {
  ruleError.value = ''
  try {
    await requestJson('rules.save', {
      kind: 'managed', id: rule.id, title: rule.title, content: rule.content,
      enabled: !rule.enabled, priority: rule.priority, scope: rule.scope
    })
    await loadRules()
  } catch (reason) {
    ruleError.value = reason instanceof Error ? reason.message : String(reason)
  }
}

async function resolveRuleConflict(
  conflict: RuleConflict,
  mode: 'workspace' | 'global' | 'merge'
): Promise<void> {
  if (ruleSaving.value) return
  ruleSaving.value = true
  ruleError.value = ''
  try {
    await requestJson('rules.resolve', { title: conflict.title, mode })
    await loadRules()
    flashSaved(
      mode === 'merge' ? '已合并到项目规则' : mode === 'workspace' ? '已选择项目版本' : '已选择全局版本'
    )
  } catch (reason) {
    ruleError.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    ruleSaving.value = false
  }
}

async function deleteManagedRule(rule: ManagedRule): Promise<void> {
  const confirmed = await confirmAction({
    title: '删除规则？',
    message: `「${rule.title}」将被删除，下一回合起不再注入。`,
    confirmLabel: '删除',
    danger: true
  })
  if (!confirmed) return
  ruleError.value = ''
  try {
    await requestJson('rules.delete', { id: rule.id, scope: rule.scope })
    await loadRules()
    flashSaved('规则已删除')
  } catch (reason) {
    ruleError.value = reason instanceof Error ? reason.message : String(reason)
  }
}

// ---- 事实库 ----

function beginFactCreate(): void {
  factsError.value = ''
  factEditing.value = { content: '', topic: '', confidence: 0.9, pinned: false }
}

function beginFactEdit(fact: FactEntry): void {
  factsError.value = ''
  factEditing.value = { ...fact }
}

function cancelFactEdit(): void {
  factEditing.value = null
  factsError.value = ''
}

async function saveFact(): Promise<void> {
  const draft = factEditing.value
  if (!draft || factSaving.value) return
  if (!draft.content?.trim()) {
    factsError.value = '事实内容不能为空'
    return
  }
  factSaving.value = true
  factsError.value = ''
  factNotice.value = ''
  try {
    const payload = draft.id
      ? await requestJson<FactsPayload>('facts.update', {
          id: draft.id,
          content: draft.content,
          topic: draft.topic || undefined,
          confidence: draft.confidence,
          pinned: draft.pinned
        })
      : await requestJson<FactsPayload>('facts.add', {
          content: draft.content,
          topic: draft.topic || undefined,
          confidence: draft.confidence
        })
    facts.value = payload.facts
    factConflicts.value = payload.conflicts
    factEditing.value = null
    const superseded = payload.superseded ?? []
    if (superseded.length > 0) {
      factUndoId.value = payload.entry?.id ?? ''
      factNotice.value = `已用新事实取代 ${superseded.length} 条旧事实：${superseded
        .map(item => item.content).join('；')}`
    } else {
      factUndoId.value = ''
      factNotice.value = '事实已保存'
    }
  } catch (reason) {
    factsError.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    factSaving.value = false
  }
}

async function toggleFactPin(fact: FactEntry): Promise<void> {
  await runFactAction('facts.pin', { id: fact.id, pinned: !fact.pinned })
}

async function confirmFact(fact: FactEntry): Promise<void> {
  await runFactAction('facts.confirm', { id: fact.id, confirmed: true })
}

async function restoreFact(fact: FactEntry): Promise<void> {
  const confirmed = await confirmAction({
    title: '撤销这次仲裁？',
    message: `恢复旧事实「${fact.content}」，当前取代它的事实将转为失效。`,
    confirmLabel: '撤销仲裁'
  })
  if (!confirmed) return
  await runFactAction('facts.restore', { id: fact.id })
}

async function deleteFact(fact: FactEntry): Promise<void> {
  const confirmed = await confirmAction({
    title: '删除事实？',
    message: `「${fact.content}」将被永久删除，Agent 之后不会再记住它。`,
    confirmLabel: '删除',
    danger: true
  })
  if (!confirmed) return
  await runFactAction('facts.delete', { id: fact.id })
}

async function undoConsolidation(): Promise<void> {
  const group = factConflicts.value.find(item => item.keptId === factUndoId.value)
  if (!group) {
    factUndoId.value = ''
    factNotice.value = ''
    await loadFacts()
    return
  }
  factsError.value = ''
  try {
    for (const id of group.replacedIds) {
      await requestJson('facts.restore', { id })
    }
    factUndoId.value = ''
    factNotice.value = '已撤销本次自动仲裁'
    await loadFacts()
  } catch (reason) {
    factsError.value = reason instanceof Error ? reason.message : String(reason)
  }
}

async function runFactAction(method: string, params: Record<string, unknown>): Promise<void> {
  factsError.value = ''
  try {
    const payload = await requestJson<FactsPayload>(method, params)
    facts.value = payload.facts
    factConflicts.value = payload.conflicts
    factNotice.value = ''
    factUndoId.value = ''
  } catch (reason) {
    factsError.value = reason instanceof Error ? reason.message : String(reason)
  }
}

function factState(fact: FactEntry): string {
  if (fact.supersededBy) return '已被取代'
  if (fact.expiresAt && new Date(fact.expiresAt).getTime() < Date.now()) return '已过期'
  if (fact.kind === 'auto' && !fact.confirmed) return '待确认'
  return '生效中'
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

/** 与 runtime 侧一致的轻量归一化：反斜杠转正斜杠、去掉首部斜杠与空白。 */
function normalizeRulePath(input: string): string {
  return input.trim().replace(/\\/g, '/').replace(/^\/+/, '').replace(/\/+$/, '')
}

function beginCreate(): void {
  creating.value = true
  newPath.value = ''
  void nextTick(() => newPathInput.value?.focus())
}

function cancelCreate(): void {
  creating.value = false
  newPath.value = ''
  error.value = ''
}

async function confirmCreate(): Promise<void> {
  const path = normalizeRulePath(newPath.value) || 'AGENTS.md'
  if (!path.toUpperCase().endsWith('AGENTS.MD') || path.includes('..')) {
    error.value = '规则文件必须命名为 AGENTS.md（可在其前加子目录，如 src/AGENTS.md）'
    return
  }
  if (files.value.some((file) => file.path.toLowerCase() === path.toLowerCase())) {
    // 已存在的文件直接选中，不报错也不覆盖。
    creating.value = false
    activePath.value = path
    return
  }
  saving.value = true
  error.value = ''
  try {
    const directory = path === 'AGENTS.md' ? '工作区根目录' : path.replace(/\/AGENTS\.md$/i, '')
    await requestJson('rules.save', {
      path,
      content: `# ${directory} 规则\n\n- 在这里写下仅适用于 ${directory} 的工作区指令\n`
    })
    creating.value = false
    newPath.value = ''
    await loadRules()
    activePath.value = path
    flashSaved(`已创建 ${path}`)
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    saving.value = false
  }
}

async function deleteRuleFile(): Promise<void> {
  if (!activeRuleFile.value || deleting.value) return
  const confirmed = await confirmAction({
    title: '删除规则文件？',
    message: dirty.value
      ? `「${activePath.value}」将被删除，其中未保存的修改一并丢弃。`
      : `「${activePath.value}」将被删除，此操作不可撤销。`,
    confirmLabel: '删除',
    danger: true
  })
  if (!confirmed) return
  deleting.value = true
  error.value = ''
  try {
    await requestJson('rules.delete', { path: activePath.value })
    // 先清空缓冲再切回根规则，避免 activePath 侦听器弹出二次放弃确认。
    content.value = ''
    baseline.value = ''
    activePath.value = 'AGENTS.md'
    await loadRules()
    flashSaved(`已删除规则文件`)
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    deleting.value = false
  }
}

/** 在系统文件管理器中定位当前编辑的规则/记忆文件；尚未落盘时退回打开父目录。 */
function openLocation(): void {
  if (isRules.value) {
    if (!workspaceRoot.value) return
    const base = workspaceRoot.value.replace(/[\\/]+$/, '')
    const target = activeRuleFile.value ? `${base}/${activePath.value}` : base
    void window.haoyue.showItemInFolder(target)
    return
  }
  if (memoryPath.value) void window.haoyue.showItemInFolder(memoryPath.value)
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
  creating.value = false
  newPath.value = ''
  ruleEditing.value = null
  ruleError.value = ''
  factEditing.value = null
  factsError.value = ''
  void loadRules()
})

function closeOnEscape(event: KeyboardEvent): void {
  if (!props.open) return
  if (event.key === 'Escape') {
    // Esc 优先退出内联新建表单，其次才关闭整页。
    if (creating.value) {
      event.preventDefault()
      cancelCreate()
      return
    }
    void requestClose()
  }
  // Ctrl/Cmd+S 快速保存，与设置编辑器的习惯一致。
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
    event.preventDefault()
    if (dirty.value && !saving.value && !loading.value) void save()
  }
  // Ctrl/Cmd+N 新建规则文件（仅规则页签）。
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'n' && isRules.value) {
    event.preventDefault()
    if (!creating.value) beginCreate()
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
      <button v-if="isRules" class="icon-button" :title="creating ? '取消新建（Esc）' : '新建规则文件（Ctrl+N）'"
        @click="creating ? cancelCreate() : beginCreate()">
        <X v-if="creating" :size="16" />
        <Plus v-else :size="16" />
      </button>
      <button v-if="isRules" class="icon-button" title="删除当前规则文件" :disabled="deleting || saving || !activeRuleFile"
        @click="deleteRuleFile">
        <Trash2 :size="16" />
      </button>
      <button class="icon-button" title="打开所在文件夹" @click="openLocation">
        <FolderOpen :size="16" />
      </button>
      <button class="icon-button" title="重新加载" :disabled="loading" @click="load">
        <RefreshCw :size="16" :class="{ spinning: loading }" />
      </button>
      <button class="secondary-button primary-action" :disabled="saving || loading || !dirty" @click="save">
        <LoaderCircle v-if="saving" class="spin" :size="14" />
        <Save v-else :size="14" /> 保存
      </button>
    </div>

    <div v-if="creating && isRules" class="rules-memory-create">
      <input ref="newPathInput" v-model="newPath" class="rules-memory-input" spellcheck="false"
        placeholder="新规则路径：src/AGENTS.md；留空则创建根 AGENTS.md" @keydown.enter="confirmCreate" />
      <button class="secondary-button primary-action" :disabled="saving" @click="confirmCreate">
        <LoaderCircle v-if="saving" class="spin" :size="14" />
        <Plus v-else :size="14" /> 创建
      </button>
    </div>

    <p v-if="isRules" class="rules-memory-hint">
      AGENTS.md 是用户写给 Agent 的工作区指令，子目录规则在涉及该目录的文件时优先生效；直接对话指令始终优先于规则文件。
    </p>
    <p v-else class="rules-memory-hint">
      记忆文件（MEMORY.md）会注入每次会话：Agent 在对话中沉淀长期事实，也可以在这里手动维护；越靠前越容易被记住。
    </p>

    <section v-if="isRules" class="rm-managed">
      <div class="rm-managed-head">
        <div>
          <strong>结构化规则</strong>
          <small>启用后按优先级从高到低注入；项目规则覆盖同标题的全局规则</small>
        </div>
        <button class="secondary-button" :disabled="ruleSaving" @click="beginRuleCreate">
          <Plus :size="14" /> 新建规则
        </button>
      </div>

      <div v-for="conflict in ruleConflicts" :key="conflict.title" class="rm-conflict">
        <span>
          「{{ conflict.title }}」在全局与项目重复，当前以{{ conflict.winner === 'workspace' ? '项目' : '全局' }}版本生效
        </span>
        <div class="rm-rule-actions">
          <button class="secondary-button" :disabled="ruleSaving" @click="resolveRuleConflict(conflict, 'workspace')">
            项目胜出
          </button>
          <button class="secondary-button" :disabled="ruleSaving" @click="resolveRuleConflict(conflict, 'global')">
            全局胜出
          </button>
          <button class="secondary-button" :disabled="ruleSaving" @click="resolveRuleConflict(conflict, 'merge')">
            合并到项目
          </button>
        </div>
      </div>

      <div v-if="managedRules.length > 0" class="rm-rule-list">
        <div v-for="rule in managedRules" :key="rule.id" class="rm-rule-card" :class="{ off: !rule.enabled }">
          <div class="rm-rule-main">
            <strong>{{ rule.title }}</strong>
            <span class="rm-badge">{{ rule.scope === 'global' ? '全局' : '项目' }}</span>
            <span class="rm-badge">优先级 {{ rule.priority }}</span>
            <span v-if="!rule.enabled" class="rm-badge off">已停用</span>
          </div>
          <small class="rm-rule-content" :title="rule.content">{{ rule.content }}</small>
          <div class="rm-rule-actions">
            <button class="secondary-button" :disabled="ruleSaving" @click="toggleManagedRule(rule)">
              {{ rule.enabled ? '停用' : '启用' }}
            </button>
            <button class="secondary-button" :disabled="ruleSaving" @click="beginRuleEdit(rule)">编辑</button>
            <button class="icon-button compact" title="删除规则" :disabled="ruleSaving" @click="deleteManagedRule(rule)">
              <Trash2 :size="14" />
            </button>
          </div>
        </div>
      </div>
      <p v-else class="rules-memory-hint subtle">还没有结构化规则——点击「新建规则」添加第一条。</p>

      <p v-if="effectiveRules.length > 0" class="rm-effective">
        生效顺序：{{ effectiveRules.map(rule => `${rule.title}（${rule.scope === 'global' ? '全局' : '项目'}·P${rule.priority}）`).join(' → ') }}
      </p>
      <p v-if="ruleHits.length > 0" class="rm-effective subtle">
        最近命中：{{ ruleHits.slice(0, 3).map(hit => `${hit.title} @ ${hit.at.slice(0, 16).replace('T', ' ')}`).join('；') }}
      </p>
      <p v-if="ruleResolutions.length > 0" class="rm-effective subtle">
        冲突解决：{{ ruleResolutions.slice(0, 3).map(resolution => `${resolution.title} → ${resolution.action.replace('resolve:', '')}（${resolution.actor}）`).join('；') }}
      </p>

      <div v-if="ruleEditing" class="rm-form">
        <label class="field-label">
          标题
          <input v-model="ruleEditing.title" class="text-input" placeholder="例如：提交规范" :disabled="ruleSaving" />
        </label>
        <label class="field-label">
          规则内容
          <textarea v-model="ruleEditing.content" class="rm-textarea" rows="3"
            placeholder="写给 Agent 的规则正文" :disabled="ruleSaving" />
        </label>
        <div class="rm-form-row">
          <label class="field-label">
            作用域
            <SelectMenu v-model="ruleEditing.scope" :options="SCOPE_OPTIONS" label="规则作用域"
              class="settings-select" :disabled="ruleSaving" />
          </label>
          <label class="field-label">
            优先级
            <input v-model.number="ruleEditing.priority" class="text-input" type="number" min="0" max="100"
              :disabled="ruleSaving" />
          </label>
          <label class="rm-check">
            <input v-model="ruleEditing.enabled" type="checkbox" :disabled="ruleSaving" /> 启用
          </label>
        </div>
        <div class="rm-form-actions">
          <button class="secondary-button" :disabled="ruleSaving" @click="cancelRuleEdit">取消</button>
          <button class="secondary-button primary-action" :disabled="ruleSaving" @click="saveManagedRule">
            <LoaderCircle v-if="ruleSaving" class="spin" :size="14" />
            <Save v-else :size="14" /> 保存规则
          </button>
        </div>
      </div>
      <p v-if="ruleError" class="rules-memory-error">{{ ruleError }}</p>
    </section>

    <section v-else class="rm-facts">
      <div class="rm-managed-head">
        <div>
          <strong>事实库</strong>
          <small>共 {{ facts.length }} 条 · 置顶事实每次注入；自动固化的结果可确认或撤销</small>
        </div>
        <button class="secondary-button" :disabled="factSaving || factsLoading" @click="beginFactCreate">
          <Plus :size="14" /> 新增事实
        </button>
      </div>

      <p v-if="factNotice" class="rm-notice">
        {{ factNotice }}
        <button v-if="factUndoId" class="secondary-button" @click="undoConsolidation">撤销</button>
      </p>
      <p v-if="factsError" class="rules-memory-error">{{ factsError }}</p>

      <div v-if="facts.length > 0" class="rm-fact-list">
        <div v-for="fact in facts.slice(0, 100)" :key="fact.id" class="rm-fact-card"
          :class="{ retired: Boolean(fact.supersededBy), pinned: fact.pinned }">
          <div class="rm-fact-main">
            <strong>{{ fact.content }}</strong>
          </div>
          <small class="rm-fact-meta">
            {{ fact.kind === 'manual' ? '手动' : '自动' }}
            · {{ fact.source === 'user' ? '用户' : 'Agent' }}
            · 置信度 {{ Math.round(fact.confidence * 100) }}%
            · {{ fact.createdAt.slice(0, 16).replace('T', ' ') }}
            <template v-if="fact.sourceSessionId"> · 会话 {{ fact.sourceSessionId }}</template>
            <template v-if="fact.topic"> · 主题 {{ fact.topic }}</template>
          </small>
          <div class="rm-fact-actions">
            <span class="rm-badge" :class="{ off: Boolean(fact.supersededBy) }">{{ factState(fact) }}</span>
            <span v-if="fact.pinned" class="rm-badge">已置顶</span>
            <button v-if="fact.kind === 'auto' && !fact.confirmed && !fact.supersededBy"
              class="secondary-button" :disabled="factSaving" @click="confirmFact(fact)">确认</button>
            <button class="secondary-button" :disabled="factSaving" @click="toggleFactPin(fact)">
              {{ fact.pinned ? '取消置顶' : '置顶' }}
            </button>
            <button class="secondary-button" :disabled="factSaving || Boolean(fact.supersededBy)"
              @click="beginFactEdit(fact)">编辑</button>
            <button v-if="fact.supersededBy" class="secondary-button" :disabled="factSaving"
              @click="restoreFact(fact)">撤销仲裁</button>
            <button class="icon-button compact" title="删除事实" :disabled="factSaving" @click="deleteFact(fact)">
              <Trash2 :size="14" />
            </button>
          </div>
        </div>
      </div>
      <p v-else-if="!factsLoading" class="rules-memory-hint subtle">还没有事实——Agent 会自动沉淀，也可以手动新增。</p>

      <div v-if="factEditing" class="rm-form">
        <label class="field-label">
          内容
          <textarea v-model="factEditing.content" class="rm-textarea" rows="2"
            placeholder="例如：部署前必须跑全量测试" :disabled="factSaving" />
        </label>
        <div class="rm-form-row">
          <label class="field-label">
            主题
            <input v-model="factEditing.topic" class="text-input" placeholder="可选，例如：流程" :disabled="factSaving" />
          </label>
          <label class="field-label">
            置信度
            <input v-model.number="factEditing.confidence" class="text-input" type="number" min="0" max="1" step="0.1"
              :disabled="factSaving" />
          </label>
          <label class="rm-check">
            <input v-model="factEditing.pinned" type="checkbox" :disabled="factSaving" /> 置顶
          </label>
        </div>
        <div class="rm-form-actions">
          <button class="secondary-button" :disabled="factSaving" @click="cancelFactEdit">取消</button>
          <button class="secondary-button primary-action" :disabled="factSaving" @click="saveFact">
            <LoaderCircle v-if="factSaving" class="spin" :size="14" />
            <Save v-else :size="14" /> 保存事实
          </button>
        </div>
      </div>
    </section>

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
      <template v-if="isRules">
        规则在每次会话开始时注入（PromptSlot.Workspace），修改只影响之后的会话 · 快捷键：Ctrl+S 保存，Ctrl+N 新建规则文件
      </template>
      <template v-else>
        记忆在每次会话开始时注入（PromptSlot.Memory），Agent 与你都可以读写这份文件 · 快捷键：Ctrl+S 保存
      </template>
    </footer>
  </section>
</template>

<style scoped>
.rules-memory-dialog.embedded-page {
  display: flex;
  width: 100%;
  height: 100%;
  flex-direction: column;
  min-height: 0;
  overflow: hidden;
  background: var(--bg);
}

.rules-memory-header,
.rules-memory-toolbar,
.rules-memory-create,
.rules-memory-hint,
.rules-memory-error,
.rules-memory-saved,
.rules-memory-footer,
.rm-managed,
.rm-facts {
  flex: 0 0 auto;
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

.rules-memory-create {
  display: flex;
  gap: 8px;
  margin: 10px 22px 0;
}

.rules-memory-input {
  flex: 1;
  padding: 8px 12px;
  color: var(--text);
  font-family: ui-monospace, 'Cascadia Mono', Consolas, 'Courier New', monospace;
  font-size: 12.5px;
  background: var(--surface-raised);
  border: 1px solid var(--border-strong);
  border-radius: 8px;
  outline: none;
  transition: border-color 140ms ease, box-shadow 140ms ease;
}

.rules-memory-input:focus {
  border-color: color-mix(in srgb, var(--accent) 66%, var(--border));
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 20%, transparent);
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
  flex: 1 1 auto;
  min-height: 96px;
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

/* ---- 结构化规则 / 事实库 ---- */

.rm-managed,
.rm-facts {
  box-sizing: border-box;
  max-height: 42vh;
  margin: 10px 22px 0;
  padding: 12px 14px;
  overflow-y: auto;
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 10px;
}

.rm-managed-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 8px;
}

.rm-managed-head strong {
  display: block;
  font-size: 13px;
  font-weight: 650;
}

.rm-managed-head small {
  display: block;
  margin-top: 2px;
  color: var(--text-muted);
  font-size: 11.5px;
  line-height: 1.5;
}

.rm-rule-list,
.rm-fact-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.rm-rule-card,
.rm-fact-card {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 10px 12px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 9px;
}

.rm-rule-card.off,
.rm-fact-card.retired {
  opacity: .62;
}

.rm-fact-card.pinned {
  border-color: color-mix(in srgb, var(--accent) 40%, var(--border));
}

.rm-rule-main,
.rm-fact-main {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  font-size: 12.5px;
}

.rm-rule-main strong,
.rm-fact-main strong {
  font-weight: 600;
  word-break: break-word;
}

.rm-rule-content {
  overflow: hidden;
  color: var(--text-secondary);
  font-size: 11.5px;
  line-height: 1.55;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.rm-fact-meta {
  color: var(--text-muted);
  font-size: 11px;
  line-height: 1.5;
  word-break: break-word;
}

.rm-rule-actions,
.rm-fact-actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}

.rm-rule-actions .secondary-button,
.rm-fact-actions .secondary-button {
  padding: 3px 9px;
  font-size: 11.5px;
}

.rm-badge {
  padding: 1px 7px;
  color: var(--accent);
  font-size: 10.5px;
  font-weight: 600;
  background: var(--accent-soft);
  border-radius: 999px;
}

.rm-badge.off {
  color: var(--text-muted);
  background: color-mix(in srgb, var(--text-muted) 14%, transparent);
}

.rm-conflict,
.rm-notice,
.rm-effective {
  margin: 8px 0 0;
  color: var(--text-secondary);
  font-size: 11.5px;
  line-height: 1.55;
}

.rm-conflict {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 7px 10px;
  color: var(--warning, #b58900);
  background: color-mix(in srgb, #b58900 10%, transparent);
  border-radius: 7px;
}

.rm-effective.subtle {
  color: var(--text-muted);
}

.rm-notice {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 7px 10px;
  color: var(--accent);
  background: var(--accent-soft);
  border-radius: 7px;
}

.rm-form {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-top: 10px;
  padding: 10px 12px;
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 9px;
}

.rm-form-row {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 10px;
}

.rm-form-row .field-label {
  flex: 0 1 180px;
}

.rm-form-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}

.rm-textarea {
  box-sizing: border-box;
  width: 100%;
  padding: 8px 10px;
  color: var(--text);
  font-family: inherit;
  font-size: 12.5px;
  line-height: 1.6;
  background: var(--surface-raised);
  border: 1px solid var(--border-strong);
  border-radius: 8px;
  outline: none;
  resize: vertical;
}

.rm-textarea:focus {
  border-color: color-mix(in srgb, var(--accent) 66%, var(--border));
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 20%, transparent);
}

.rm-check {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding-bottom: 8px;
  color: var(--text-secondary);
  font-size: 12px;
}

.rm-check input {
  accent-color: var(--accent);
}
</style>
