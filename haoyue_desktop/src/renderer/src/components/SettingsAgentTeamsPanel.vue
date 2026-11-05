<script setup lang="ts">
import { Copy, LoaderCircle, Pencil, Play, Plus, Square, Trash2, Users, X } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { confirmAction } from '../confirmation'
import { requestJson } from '../daemon-json'
import type {
  TeamDefinition,
  TeamsListResult,
  TeamRun,
  TeamRunsResult,
  TeamMember
} from '../types'
import SelectMenu from './SelectMenu.vue'

/**
 * 设置 → 智能体团体。
 *
 * 把多个角色编成一个小组，按顺序 / 并行 / 主管调度 / 评审制协作。
 * 配置按 workspace 落盘（team.*），改动下一回合热生效；运行轨迹可回看每个成员的
 * 分工、工具调用、耗时与失败原因，运行中可取消。
 */
const props = defineProps<{ connected: boolean; workspacePath?: string }>()

const emit = defineEmits<{
  notice: [message: string]
  error: [message: string]
}>()

interface DraftMember {
  id: string
  name: string
  role: string
  responsibility: string
  model: string
  expertId: string
  tools: string[]
  enabled: boolean
}

interface TeamDraft {
  name: string
  description: string
  mode: TeamDefinition['mode']
  coordinatorId: string
  enabled: boolean
  maxRounds: number
  timeoutSeconds: number
  maxRetries: number
  failureStrategy: TeamDefinition['failureStrategy']
  members: DraftMember[]
}

const MODE_OPTIONS = [
  { value: 'sequential', label: '顺序执行', description: '按成员顺序执行，上游结论作为下游上下文' },
  { value: 'parallel', label: '并行执行', description: '所有成员同时执行，结果汇总' },
  { value: 'supervisor', label: '主管调度', description: '协调者分派 → 成员执行 → 协调者汇总，按轮次推进' },
  { value: 'review', label: '评审制', description: '执行者产出 → 审核者评审，未通过带反馈重做' }
]

const FAILURE_OPTIONS = [
  { value: 'stop', label: '立即停止', description: '任一成员失败即终止整次运行' },
  { value: 'continue', label: '跳过继续', description: '失败成员跳过，其余成员继续' },
  { value: 'fallback', label: '降级汇总', description: '继续执行，并在结论中标注降级运行' }
]

const TOOL_OPTIONS = [
  { name: 'read_file', label: '读取文件' },
  { name: 'write_file', label: '写入文件' },
  { name: 'edit_file', label: '编辑文件' },
  { name: 'list_dir', label: '列目录' },
  { name: 'glob', label: '文件匹配' },
  { name: 'grep', label: '内容搜索' },
  { name: 'bash', label: 'Bash' },
  { name: 'terminal', label: '终端' },
  { name: 'web_search', label: '网页搜索' },
  { name: 'web_fetch', label: '网页抓取' },
  { name: 'knowledge_search', label: '知识检索' },
  { name: 'fact_search', label: '事实检索' },
  { name: 'update_plan', label: '更新计划' },
  { name: 'browser', label: '浏览器' }
]

const teams = ref<TeamDefinition[]>([])
const runs = ref<TeamRun[]>([])
const agents = ref<Array<{ name: string; description: string }>>([])
const modelOptions = ref<Array<{ value: string; label: string; description?: string }>>([
  { value: '', label: '继承当前会话模型', description: '不额外指定模型' }
])

const loading = ref(false)
const loaded = ref(false)
const busy = ref('')
const editing = ref<TeamDraft | null>(null)
const editingId = ref('')
const expandedRunId = ref('')

const runTeamId = ref('')
const runTask = ref('')
const runSessionId = ref('')
const runLoading = ref(false)
const activeRun = ref<TeamRun | null>(null)
let pollTimer: ReturnType<typeof setInterval> | undefined

const canUse = computed(() => props.connected && Boolean(props.workspacePath))

const expertOptions = computed(() => [
  { value: '', label: '自定义（不绑定专家）', description: '直接按角色定位与职责执行' },
  ...agents.value.map((agent) => ({
    value: agent.name,
    label: agent.name,
    description: agent.description
  }))
])

const coordinatorOptions = computed(() => {
  const draft = editing.value
  if (!draft) return []
  return [
    { value: '', label: '未指定', description: '主管调度 / 评审制必须指定协调者' },
    ...draft.members
      .filter((member) => member.enabled && member.name.trim())
      .map((member) => ({
        value: member.id,
        label: member.name.trim(),
        description: member.role || undefined
      }))
  ]
})

const activeRunVisible = computed<TeamRun | null>(() =>
  activeRun.value ?? runs.value.find((run) => run.teamId === runTeamId.value) ?? null)

const runningRun = computed(() =>
  activeRun.value?.status === 'running' ? activeRun.value : null)

function workspaceParams(): Record<string, unknown> {
  return props.workspacePath ? { workspace: props.workspacePath } : {}
}

function nextMemberId(): string {
  return `member-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`
}

function blankMember(): DraftMember {
  return { id: nextMemberId(), name: '', role: '', responsibility: '', model: '', expertId: '', tools: [], enabled: true }
}

function blankDraft(): TeamDraft {
  return {
    name: '',
    description: '',
    mode: 'sequential',
    coordinatorId: '',
    enabled: true,
    maxRounds: 3,
    timeoutSeconds: 300,
    maxRetries: 1,
    failureStrategy: 'stop',
    members: [blankMember(), blankMember()]
  }
}

function applyTeams(result: TeamsListResult): void {
  teams.value = result.teams
}

async function load(): Promise<void> {
  if (!canUse.value) return
  loading.value = true
  try {
    applyTeams(await requestJson<TeamsListResult>('team.list', workspaceParams()))
    await refreshRuns()
    loaded.value = true
  } catch (reason) {
    emit('error', reason instanceof Error ? reason.message : String(reason))
  } finally {
    loading.value = false
  }
}

async function refreshRuns(): Promise<void> {
  if (!canUse.value) return
  try {
    const result = await requestJson<TeamRunsResult>('team.runs', { ...workspaceParams(), limit: 20 })
    runs.value = result.runs ?? []
    if (activeRun.value && activeRun.value.status === 'running') {
      const latest = runs.value.find((run) => run.runId === activeRun.value?.runId)
      if (latest) activeRun.value = latest
    }
  } catch {
    // 轨迹读取失败不覆盖配置列表；下次轮询会重试。
  }
}

async function loadOptions(): Promise<void> {
  try {
    const result = await requestJson<{ agents: Array<{ name: string; description: string }> }>(
      'agent.subagents.list', workspaceParams())
    agents.value = (result.agents ?? []).map((agent) => ({
      name: agent.name,
      description: agent.description ?? ''
    }))
  } catch {
    agents.value = []
  }
  try {
    const catalog = await requestJson<Array<{ ref: string; providerEnabled: boolean }>>('model.catalog')
    modelOptions.value = [
      { value: '', label: '继承当前会话模型', description: '不额外指定模型' },
      ...catalog.filter((model) => model.providerEnabled)
        .map((model) => ({ value: model.ref, label: model.ref }))
    ]
  } catch {
    // 模型目录不可用时保留继承选项。
  }
}

function openCreate(): void {
  editing.value = blankDraft()
  editingId.value = ''
}

function openEdit(team: TeamDefinition): void {
  editing.value = {
    name: team.name,
    description: team.description,
    mode: team.mode,
    coordinatorId: team.coordinatorId ?? '',
    enabled: team.enabled,
    maxRounds: team.maxRounds,
    timeoutSeconds: team.timeoutSeconds,
    maxRetries: team.maxRetries,
    failureStrategy: team.failureStrategy,
    members: team.members.map((member) => ({
      id: member.id,
      name: member.name,
      role: member.role,
      responsibility: member.responsibility,
      model: member.model ?? '',
      expertId: member.expertId ?? '',
      tools: [...member.tools],
      enabled: member.enabled
    }))
  }
  editingId.value = team.id
}

function closeEditor(): void {
  editing.value = null
  editingId.value = ''
}

function addMember(): void {
  editing.value?.members.push(blankMember())
}

function removeMember(index: number): void {
  const draft = editing.value
  if (!draft) return
  const [removed] = draft.members.splice(index, 1)
  if (removed && draft.coordinatorId === removed.id) draft.coordinatorId = ''
}

function toggleTool(member: DraftMember, tool: string): void {
  member.tools = member.tools.includes(tool)
    ? member.tools.filter((item) => item !== tool)
    : [...member.tools, tool]
}

function memberName(member: TeamMember | undefined): string {
  return member ? (member.name || member.role || member.id) : '未知成员'
}

function teamOf(teamId: string): TeamDefinition | undefined {
  return teams.value.find((team) => team.id === teamId)
}

function statusLabel(status: TeamRun['status']): string {
  return {
    running: '运行中',
    succeeded: '已完成',
    partial: '部分完成',
    failed: '失败',
    cancelled: '已取消'
  }[status]
}

async function saveDraft(): Promise<void> {
  const draft = editing.value
  if (!draft) return
  const members = draft.members.filter((member) => member.name.trim() || member.role.trim())
  if (!draft.name.trim()) return emit('error', '团体名称不能为空')
  if (members.length === 0) return emit('error', '至少需要一个成员')
  const enabledMembers = members.filter((member) => member.enabled)
  if (enabledMembers.length === 0) return emit('error', '至少需要一个启用成员')
  if ((draft.mode === 'supervisor' || draft.mode === 'review')
    && !enabledMembers.some((member) => member.id === draft.coordinatorId)) {
    return emit('error', `${draft.mode === 'supervisor' ? '主管调度' : '评审制'}需要指定一位启用成员作为协调者`)
  }
  if (draft.mode === 'review' && enabledMembers.length < 2) {
    return emit('error', '评审制至少需要执行者与审核者两个启用成员')
  }

  busy.value = 'team.save'
  try {
    const payload: Record<string, unknown> = {
      ...workspaceParams(),
      name: draft.name.trim(),
      description: draft.description.trim(),
      mode: draft.mode,
      coordinatorId: draft.coordinatorId || undefined,
      enabled: draft.enabled,
      maxRounds: Math.min(10, Math.max(1, Number(draft.maxRounds) || 3)),
      timeoutSeconds: Math.min(1800, Math.max(30, Number(draft.timeoutSeconds) || 300)),
      maxRetries: Math.min(3, Math.max(0, Number(draft.maxRetries) || 0)),
      failureStrategy: draft.failureStrategy,
      members: members.map((member) => ({
        id: member.id || undefined,
        name: member.name.trim() || member.role.trim(),
        role: member.role.trim(),
        responsibility: member.responsibility.trim(),
        model: member.model || undefined,
        expertId: member.expertId || undefined,
        tools: member.tools,
        enabled: member.enabled
      }))
    }
    if (editingId.value) payload.id = editingId.value
    applyTeams(await requestJson<TeamsListResult>('team.save', payload))
    emit('notice', editingId.value ? `团体「${draft.name.trim()}」已更新` : `团体「${draft.name.trim()}」已创建`)
    closeEditor()
  } catch (reason) {
    emit('error', reason instanceof Error ? reason.message : String(reason))
  } finally {
    busy.value = ''
  }
}

async function toggleEnabled(team: TeamDefinition, checked: boolean): Promise<void> {
  const previous = team.enabled
  team.enabled = checked
  busy.value = `toggle.${team.id}`
  try {
    applyTeams(await requestJson<TeamsListResult>('team.save', {
      ...workspaceParams(),
      id: team.id,
      name: team.name,
      description: team.description,
      mode: team.mode,
      coordinatorId: team.coordinatorId ?? undefined,
      enabled: checked,
      maxRounds: team.maxRounds,
      timeoutSeconds: team.timeoutSeconds,
      maxRetries: team.maxRetries,
      failureStrategy: team.failureStrategy,
      members: team.members.map((member) => ({
        id: member.id,
        name: member.name,
        role: member.role,
        responsibility: member.responsibility,
        model: member.model || undefined,
        expertId: member.expertId || undefined,
        tools: member.tools,
        enabled: member.enabled
      }))
    }))
    emit('notice', `团体「${team.name}」已${checked ? '启用' : '停用'}`)
  } catch (reason) {
    team.enabled = previous
    emit('error', reason instanceof Error ? reason.message : String(reason))
  } finally {
    busy.value = ''
  }
}

async function copyTeam(team: TeamDefinition): Promise<void> {
  busy.value = `copy.${team.id}`
  try {
    const result = await requestJson<{ teams: TeamDefinition[] }>('team.copy', {
      ...workspaceParams(), id: team.id
    })
    teams.value = result.teams
    emit('notice', `已复制团体「${team.name}」`)
  } catch (reason) {
    emit('error', reason instanceof Error ? reason.message : String(reason))
  } finally {
    busy.value = ''
  }
}

async function deleteTeam(team: TeamDefinition): Promise<void> {
  if (!await confirmAction({
    title: '删除智能体团体',
    message: `确定删除「${team.name}」？该操作不影响其他工作区。`,
    confirmLabel: '删除',
    danger: true
  })) return
  busy.value = `delete.${team.id}`
  try {
    applyTeams(await requestJson<TeamsListResult>('team.delete', {
      ...workspaceParams(), id: team.id
    }))
    emit('notice', `团体「${team.name}」已删除`)
  } catch (reason) {
    emit('error', reason instanceof Error ? reason.message : String(reason))
  } finally {
    busy.value = ''
  }
}

function openRun(team: TeamDefinition): void {
  runTeamId.value = team.id
  runTask.value = ''
  runSessionId.value = ''
  activeRun.value = null
}

async function startRun(): Promise<void> {
  const team = teamOf(runTeamId.value)
  if (!team) return
  if (!runTask.value.trim()) return emit('error', '请先填写要交给团体的任务')
  runLoading.value = true
  busy.value = `run.${team.id}`
  activeRun.value = null
  startPolling()
  try {
    const run = await requestJson<TeamRun>('team.run', {
      ...workspaceParams(),
      id: team.id,
      task: runTask.value.trim(),
      sessionId: runSessionId.value.trim() || undefined
    }, 1_800_000)
    activeRun.value = run
    emit('notice', run.status === 'succeeded'
      ? `团体「${team.name}」已完成`
      : `团体「${team.name}」${statusLabel(run.status)}${run.error ? `：${run.error}` : ''}`)
    await refreshRuns()
  } catch (reason) {
    emit('error', reason instanceof Error ? reason.message : String(reason))
  } finally {
    stopPolling()
    runLoading.value = false
    busy.value = ''
  }
}

function startPolling(): void {
  stopPolling()
  pollTimer = setInterval(() => {
    void refreshRuns().then(() => {
      if (!activeRun.value) {
        activeRun.value = runs.value.find(
          (run) => run.teamId === runTeamId.value && run.status === 'running') ?? null
      }
    })
  }, 1200)
}

function stopPolling(): void {
  if (pollTimer) clearInterval(pollTimer)
  pollTimer = undefined
}

async function cancelRun(): Promise<void> {
  const run = runningRun.value
  if (!run) return
  busy.value = 'team.cancel'
  try {
    const result = await requestJson<{ cancelled: boolean }>('team.cancel', { runId: run.runId })
    emit('notice', result.cancelled ? '已请求取消，运行状态将收敛为已取消' : '运行已结束，无需取消')
    await refreshRuns()
  } catch (reason) {
    emit('error', reason instanceof Error ? reason.message : String(reason))
  } finally {
    busy.value = ''
  }
}

function toggleRunDetails(runId: string): void {
  expandedRunId.value = expandedRunId.value === runId ? '' : runId
}

onMounted(() => {
  void load()
  void loadOptions()
})
watch(() => props.workspacePath, () => {
  loaded.value = false
  closeEditor()
  activeRun.value = null
  void load()
})
watch(() => props.connected, (connected) => { if (connected) void load() })
onBeforeUnmount(stopPolling)
</script>

<template>
  <div v-if="loading && !loaded" class="settings-loading">
    <LoaderCircle class="spin" :size="18" /> 正在读取智能体团体…
  </div>

  <template v-else>
    <div class="settings-section-heading">
      <div>
        <h3>智能体团体</h3>
        <p>把多个角色编成一个小组：顺序、并行、主管调度或评审制协作，结果汇总成一条普通回复</p>
      </div>
    </div>

    <div v-if="!workspacePath" class="teams-hint">
      <Users :size="16" />
      <span>智能体团体按项目工作区隔离，请先打开一个项目再配置。</span>
    </div>

    <section class="settings-group" aria-label="团体配置">
      <div class="settings-group-title">
        <strong>已配置的团体</strong>
        <small>配置改动保存后下一回合生效，无需重启；其他项目不可见</small>
        <button class="secondary-button teams-add" :disabled="busy !== '' || editing !== null || !canUse"
          @click="openCreate">
          <Plus :size="14" /> 新建团体
        </button>
      </div>

      <div v-if="editing" class="team-editor">
        <div class="team-editor-title">
          <strong>{{ editingId ? '编辑团体' : '新建团体' }}</strong>
          <button class="icon-button compact" title="取消编辑" :disabled="busy !== ''" @click="closeEditor">
            <X :size="15" />
          </button>
        </div>

        <div class="team-form-grid">
          <label class="field-label team-span">
            名称
            <input v-model="editing.name" class="text-input" placeholder="例如：发布检查小队" :disabled="busy !== ''" />
          </label>
          <label class="field-label team-span">
            描述
            <input v-model="editing.description" class="text-input" placeholder="这个团体负责什么"
              :disabled="busy !== ''" />
          </label>
          <label class="field-label">
            协作模式
            <SelectMenu :model-value="editing.mode" class="settings-select select-compact" :options="MODE_OPTIONS"
              label="协作模式" :disabled="busy !== ''"
              @update:model-value="editing.mode = $event as TeamDefinition['mode']" />
          </label>
          <label class="field-label">
            协调者
            <SelectMenu :model-value="editing.coordinatorId" class="settings-select select-compact"
              :options="coordinatorOptions" label="协调者" :disabled="busy !== ''"
              @update:model-value="editing.coordinatorId = $event as string" />
          </label>
          <label class="field-label">
            轮次上限
            <input v-model.number="editing.maxRounds" class="text-input" type="number" min="1" max="10"
              :disabled="busy !== ''" />
          </label>
          <label class="field-label">
            整体超时（秒）
            <input v-model.number="editing.timeoutSeconds" class="text-input" type="number" min="30" max="1800"
              :disabled="busy !== ''" />
          </label>
          <label class="field-label">
            单成员重试
            <input v-model.number="editing.maxRetries" class="text-input" type="number" min="0" max="3"
              :disabled="busy !== ''" />
          </label>
          <label class="field-label">
            失败策略
            <SelectMenu :model-value="editing.failureStrategy" class="settings-select select-compact"
              :options="FAILURE_OPTIONS" label="失败策略" :disabled="busy !== ''"
              @update:model-value="editing.failureStrategy = $event as TeamDefinition['failureStrategy']" />
          </label>
        </div>

        <div class="team-members-head">
          <strong>成员</strong>
          <small>角色定位、职责、模型与工具权限范围；协调者需从启用成员中选择</small>
          <button class="secondary-button" :disabled="busy !== ''" @click="addMember">
            <Plus :size="13" /> 添加成员
          </button>
        </div>

        <div v-for="(member, index) in editing.members" :key="member.id" class="team-member-card">
          <div class="team-member-head">
            <span class="inline-badge">{{ index + 1 }}</span>
            <input v-model="member.name" class="text-input member-name" placeholder="成员名称"
              :disabled="busy !== ''" />
            <button class="icon-button compact" title="移除成员" :disabled="busy !== ''" @click="removeMember(index)">
              <Trash2 :size="14" />
            </button>
          </div>
          <div class="team-form-grid">
            <label class="field-label">
              角色定位
              <input v-model="member.role" class="text-input" placeholder="架构师 / 实现者 / 审核者"
                :disabled="busy !== ''" />
            </label>
            <label class="field-label">
              可用模型
              <SelectMenu :model-value="member.model" class="settings-select select-compact" :options="modelOptions"
                label="可用模型" :disabled="busy !== ''" :menu-min-width="300"
                @update:model-value="member.model = $event as string" />
            </label>
            <label class="field-label team-span">
              职责说明
              <input v-model="member.responsibility" class="text-input" placeholder="这个成员负责什么，会拼进提示词"
                :disabled="busy !== ''" />
            </label>
            <label class="field-label team-span">
              关联专家
              <SelectMenu :model-value="member.expertId" class="settings-select select-compact"
                :options="expertOptions" label="关联专家" :disabled="busy !== ''" :menu-min-width="300"
                @update:model-value="member.expertId = $event as string" />
            </label>
          </div>
          <div class="team-tools">
            <span class="team-tools-label">工具权限</span>
            <button v-for="tool in TOOL_OPTIONS" :key="tool.name" type="button"
              class="tool-chip" :class="{ active: member.tools.includes(tool.name) }" :disabled="busy !== ''"
              @click="toggleTool(member, tool.name)">
              {{ tool.label }}
            </button>
          </div>
          <label class="provider-enabled-row team-member-enabled">
            <span>
              <strong>参与运行</strong>
              <small>关闭后该成员不参与编排</small>
            </span>
            <input :checked="member.enabled" class="sr-only" type="checkbox" :disabled="busy !== ''"
              @change="member.enabled = Boolean(($event.target as HTMLInputElement).checked)" />
            <span class="toggle-switch" aria-hidden="true"><span /></span>
          </label>
        </div>

        <div class="team-editor-actions">
          <label class="provider-enabled-row team-member-enabled">
            <span>
              <strong>启用团体</strong>
              <small>停用后不参与运行</small>
            </span>
            <input :checked="editing.enabled" class="sr-only" type="checkbox" :disabled="busy !== ''"
              @change="editing.enabled = Boolean(($event.target as HTMLInputElement).checked)" />
            <span class="toggle-switch" aria-hidden="true"><span /></span>
          </label>
          <div class="row-controls">
            <button class="secondary-button" :disabled="busy !== ''" @click="closeEditor">取消</button>
            <button class="secondary-button primary-action" :disabled="busy !== ''" @click="saveDraft">
              <LoaderCircle v-if="busy === 'team.save'" class="spin" :size="14" />
              {{ editingId ? '保存更改' : '创建' }}
            </button>
          </div>
        </div>
      </div>

      <div v-if="teams.length === 0 && editing === null" class="empty-settings">
        {{ canUse ? '尚未创建智能体团体：点击「新建团体」组建第一个小组' : '打开项目后可创建智能体团体' }}
      </div>

      <div v-for="team in teams" :key="team.id" class="team-card">
        <div class="settings-list-row">
          <Users :size="17" />
          <div class="list-main">
            <div>
              <strong :title="team.name">{{ team.name }}</strong>
              <span class="inline-badge">{{ MODE_OPTIONS.find((mode) => mode.value === team.mode)?.label ?? team.mode }}</span>
              <span class="inline-badge">{{ team.members.filter((member) => member.enabled).length }} 名成员</span>
              <span class="inline-badge">{{ team.maxRounds }} 轮上限</span>
              <span class="inline-badge">{{ team.timeoutSeconds }}s</span>
              <span class="inline-badge">重试 {{ team.maxRetries }}</span>
              <span v-if="team.coordinatorId" class="inline-badge">
                协调者 {{ memberName(team.members.find((member) => member.id === team.coordinatorId)) }}
              </span>
              <span v-if="!team.enabled" class="inline-badge problem-badge">已停用</span>
            </div>
            <small>{{ team.description || '暂无描述' }}</small>
            <small class="team-members-line">
              {{ team.members.map((member) => `${member.name}${member.role ? `（${member.role}）` : ''}`).join('、') }}
            </small>
          </div>
          <div class="row-controls">
            <button class="secondary-button" :disabled="busy !== '' || !team.enabled" title="运行一次团体任务"
              @click="openRun(team)">
              <Play :size="13" /> 运行
            </button>
            <button class="icon-button compact" title="复制团体" :disabled="busy !== ''" @click="copyTeam(team)">
              <Copy :size="15" />
            </button>
            <button class="icon-button compact" title="编辑团体" :disabled="busy !== '' || editing !== null"
              @click="openEdit(team)">
              <Pencil :size="15" />
            </button>
            <button class="icon-button compact" title="删除团体" :disabled="busy !== ''" @click="deleteTeam(team)">
              <Trash2 :size="15" />
            </button>
          </div>
        </div>
        <label class="provider-enabled-row team-enable-row">
          <span>
            <strong>启用</strong>
            <small>停用后不参与运行</small>
          </span>
          <input :checked="team.enabled" class="sr-only" type="checkbox" :disabled="busy !== '' || editing !== null"
            @change="toggleEnabled(team, Boolean(($event.target as HTMLInputElement).checked))" />
          <span class="toggle-switch" aria-hidden="true"><span /></span>
        </label>
      </div>
    </section>

    <section v-if="runTeamId" class="settings-group" aria-label="运行团体">
      <div class="settings-group-title">
        <strong>运行「{{ teamOf(runTeamId)?.name ?? runTeamId }}」</strong>
        <small>整体结果会汇总成一条普通助手消息；运行中可取消</small>
        <button class="icon-button compact" title="收起运行面板" :disabled="runLoading" @click="runTeamId = ''">
          <X :size="15" />
        </button>
      </div>

      <label class="field-label team-span">
        交给团体的任务
        <textarea v-model="runTask" class="text-input team-task" rows="4"
          placeholder="描述目标、约束与验收标准" :disabled="runLoading"></textarea>
      </label>
      <label class="field-label team-span">
        写回会话（可选）
        <input v-model="runSessionId" class="text-input" placeholder="填写会话 id 时，整体结果会追加为该会话的一条助手消息"
          :disabled="runLoading" />
      </label>
      <div class="row-controls team-run-actions">
        <button class="secondary-button primary-action" :disabled="runLoading || !runTask.trim()" @click="startRun">
          <LoaderCircle v-if="runLoading" class="spin" :size="14" />
          <Play v-else :size="14" />
          {{ runLoading ? '运行中…' : '开始运行' }}
        </button>
        <button v-if="runningRun" class="secondary-button danger-action" :disabled="busy !== ''" @click="cancelRun">
          <Square :size="13" /> 取消运行
        </button>
      </div>

      <div v-if="activeRunVisible" class="team-run">
        <div class="team-run-head">
          <span class="inline-badge" :class="{ 'problem-badge': activeRunVisible.status === 'failed' }">
            {{ statusLabel(activeRunVisible.status) }}
          </span>
          <span class="inline-badge">{{ activeRunVisible.rounds }} 轮</span>
          <span class="inline-badge">{{ activeRunVisible.runId }}</span>
          <span class="team-run-time">{{ activeRunVisible.startedAt.slice(0, 19).replace('T', ' ') }}</span>
        </div>
        <small v-if="activeRunVisible.error" class="team-run-error">{{ activeRunVisible.error }}</small>
        <div v-for="member in activeRunVisible.members" :key="`${activeRunVisible.runId}-${member.memberId}`"
          class="team-run-member">
          <span class="team-run-role">{{ memberName(teamOf(activeRunVisible.teamId)?.members.find((item) => item.id === member.memberId)) }}</span>
          <span class="inline-badge">{{ member.role || '成员' }}</span>
          <span :class="member.status === 'failed' ? 'team-run-fail' : 'team-run-ok'">
            {{ { pending: '待执行', running: '执行中', succeeded: '成功', failed: '失败' }[member.status] }}
          </span>
          <span class="team-run-ms">{{ Math.round(member.durationMs) }}ms</span>
          <span class="team-run-tools">{{ member.toolCalls }} 次工具调用</span>
          <span v-if="member.error" class="team-run-fail">{{ member.error }}</span>
        </div>
        <pre v-if="activeRunVisible.result" class="team-run-result">{{ activeRunVisible.result }}</pre>
      </div>
    </section>

    <section v-if="runs.length > 0" class="settings-group" aria-label="运行轨迹">
      <div class="settings-group-title">
        <strong>运行轨迹</strong>
        <small>最近 {{ runs.length }} 次运行（每个成员的分工、工具调用、耗时与失败原因）</small>
      </div>
      <div v-for="run in runs" :key="run.runId" class="team-run-card">
        <button class="team-run-summary" type="button" @click="toggleRunDetails(run.runId)">
          <span class="inline-badge" :class="{ 'problem-badge': run.status === 'failed' }">
            {{ statusLabel(run.status) }}
          </span>
          <strong>{{ teamOf(run.teamId)?.name ?? run.teamId }}</strong>
          <span class="team-run-time">{{ run.startedAt.slice(0, 19).replace('T', ' ') }}</span>
          <span class="inline-badge">{{ run.rounds }} 轮</span>
          <span v-if="run.error" class="team-run-error">{{ run.error }}</span>
        </button>
        <div v-if="expandedRunId === run.runId" class="team-run-details">
          <div v-for="member in run.members" :key="`${run.runId}-${member.memberId}`" class="team-run-member">
            <span class="team-run-role">{{ memberName(teamOf(run.teamId)?.members.find((item) => item.id === member.memberId)) }}</span>
            <span class="inline-badge">{{ member.role || '成员' }}</span>
            <span :class="member.status === 'failed' ? 'team-run-fail' : 'team-run-ok'">
              {{ { pending: '待执行', running: '执行中', succeeded: '成功', failed: '失败' }[member.status] }}
            </span>
            <span class="team-run-ms">{{ Math.round(member.durationMs) }}ms</span>
            <span class="team-run-tools">{{ member.toolCalls }} 次工具调用</span>
            <span v-if="member.error" class="team-run-fail">{{ member.error }}</span>
          </div>
          <pre v-if="run.result" class="team-run-result">{{ run.result }}</pre>
        </div>
      </div>
    </section>
  </template>
</template>

<style scoped>
.teams-hint {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 12px;
  border: 1px solid color-mix(in srgb, var(--warning, #d29922) 45%, var(--border));
  border-radius: 8px;
  background: color-mix(in srgb, var(--warning, #d29922) 10%, transparent);
  color: color-mix(in srgb, var(--warning, #d29922) 75%, var(--text));
  font-size: 13px;
  margin: 4px 0 16px;
}

.settings-group-title {
  align-items: flex-start;
}

.teams-add {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.team-editor {
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 12px 14px;
  margin: 10px 0 14px;
  background: var(--accent-soft);
}

.team-editor-title {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 10px;
}

.team-form-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 10px 12px;
}

.team-span {
  grid-column: 1 / -1;
}

.team-members-head {
  display: flex;
  align-items: center;
  gap: 10px;
  margin: 16px 0 8px;
}

.team-members-head small {
  color: var(--muted);
}

.team-members-head .secondary-button {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.team-member-card {
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 10px 12px;
  margin: 8px 0;
}

.team-member-head {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 8px;
}

.member-name {
  flex: 1;
}

.team-tools {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  margin-top: 8px;
}

.team-tools-label {
  font-size: 12px;
  color: var(--muted);
  margin-right: 2px;
}

.tool-chip {
  border: 1px solid var(--border);
  border-radius: 999px;
  background: transparent;
  color: var(--text);
  font-size: 12px;
  padding: 2px 10px;
  cursor: pointer;
}

.tool-chip.active {
  border-color: var(--accent);
  background: color-mix(in srgb, var(--accent) 14%, transparent);
  color: var(--accent);
}

.team-member-enabled {
  margin: 8px 0 0;
  padding: 0;
}

.team-editor-actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-top: 14px;
}

.team-card {
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 10px 12px;
  margin: 10px 0;
}

.team-enable-row {
  margin: 8px 0 0;
  padding: 0;
}

.team-members-line {
  color: var(--muted);
}

.inline-badge.problem-badge {
  background: color-mix(in srgb, var(--danger) 14%, transparent);
  color: var(--danger);
}

.team-task {
  resize: vertical;
  min-height: 84px;
  font-family: inherit;
}

.team-run-actions {
  margin-top: 10px;
  display: flex;
  gap: 10px;
}

.danger-action {
  color: var(--danger);
  border-color: color-mix(in srgb, var(--danger) 45%, var(--border));
}

.team-run {
  margin-top: 12px;
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 10px 12px;
}

.team-run-head {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  margin-bottom: 6px;
}

.team-run-time {
  color: var(--muted);
  font-size: 12px;
  margin-left: auto;
  font-variant-numeric: tabular-nums;
}

.team-run-member {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  padding: 6px 2px;
  border-bottom: 1px dashed color-mix(in srgb, var(--border) 70%, transparent);
  font-size: 12px;
}

.team-run-role {
  font-weight: 600;
}

.team-run-ok { color: var(--success, #3fb950); }
.team-run-fail { color: var(--danger); }
.team-run-error { color: var(--danger); font-size: 12px; }
.team-run-ms, .team-run-tools { color: var(--muted); }

.team-run-result {
  margin: 10px 0 0;
  padding: 10px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--bg-elevated, #111) 60%, transparent);
  border: 1px solid var(--border);
  font-size: 12px;
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 320px;
  overflow: auto;
}

.team-run-card {
  border: 1px solid var(--border);
  border-radius: 8px;
  margin: 8px 0;
  padding: 6px 10px;
}

.team-run-summary {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  border: none;
  background: transparent;
  color: var(--text);
  font-size: 13px;
  padding: 4px 0;
  cursor: pointer;
  text-align: left;
}

.team-run-details {
  padding: 4px 0 6px;
}

@media (max-width: 720px) {
  .team-form-grid {
    grid-template-columns: 1fr;
  }
  .team-editor-actions {
    flex-direction: column;
    align-items: stretch;
  }
}
</style>
