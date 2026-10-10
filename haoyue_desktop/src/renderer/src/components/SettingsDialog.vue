<script setup lang="ts">
import {
  Activity,
  ArrowLeft,
  Blocks,
  Bot,
  Check,
  Circle,
  Clock,
  FolderOpen,
  Gauge,
  GripVertical,
  History,
  KeyRound,
  LoaderCircle,
  Moon,
  Monitor,
  Plug,
  Plus,
  RefreshCw,
  RotateCcw,
  Save,
  Settings2,
  SlidersHorizontal,
  Sparkles,
  ScrollText,
  Sun,
  Telescope,
  Trash2,
  Upload,
  Users,
  Wrench,
  X,
  Zap
} from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { confirmAction } from '../confirmation'
import ExpertsPanel from './ExpertsPanel.vue'
import McpPanel from './McpPanel.vue'
import type { ModelDetailConfig } from './ModelConfigModal.vue'
import FieldLabel from './FieldLabel.vue'
import OfficialSkillsPanel from './OfficialSkillsPanel.vue'
import ProviderEditorDialog from './ProviderEditorDialog.vue'
import AllowedSitesDialog from './AllowedSitesDialog.vue'
import SelectMenu from './SelectMenu.vue'
import UsageTrendChart, { type TimelinePoint } from './UsageTrendChart.vue'
import UsageModelBarChart from './UsageModelBarChart.vue'
import {
  EVOLUTION_INTERVAL_OPTIONS,
  TREND_KEY_LABELS,
  TREND_KEYS,
  clampIntervalMinutes,
  clampThresholdSignals,
  defectKindLabel,
  formatDateOnly,
  formatInterval,
  relativeTime,
  reflectionOutcomeText,
  severityClass,
  severityLabel,
  triggerLabel,
  trendMax,
  trendTitle,
  trendTotal,
  type EvolutionCandidate,
  type EvolutionCandidateFile,
  type EvolutionConfigValue,
  type EvolutionDefectReport,
  type EvolutionHealth,
  type EvolutionRun,
  type EvolutionStats,
  type EvolutionTrendPoint
} from '../evolution-form'

type SettingsSection = 'general' | 'models' | 'agent' | 'mcp' | 'skills' | 'experts' | 'rules-memory' | 'evolution' | 'diagnostics' | 'inference' | 'advanced'

interface ProviderInfo {
  id: string
  name: string
  kind: 'openai' | 'anthropic' | 'local'
  baseUrl: string
  modelListUrl?: string
  modelsDirectory?: string
  defaultModelsDirectory?: string
  apiKey?: string
  apiKeyConfigured: boolean
  models: string[]
  modelDetails?: ModelDetailConfig[]
  enabled: boolean
  priority: number
  timeoutSeconds: number
  proxy?: string
  promptCaching: boolean
  active: boolean
}

interface ProviderFormValue {
  id: string
  name: string
  kind: 'openai' | 'anthropic' | 'local'
  baseUrl: string
  modelListUrl: string
  apiKey: string
  models: string
  modelsDirectory: string
  modelDetails?: ModelDetailConfig[]
  enabled: boolean
  priority: number
  timeoutSeconds: number
  proxy: string
  promptCaching: boolean
}

interface ModelInfo {
  ref: string
  active: boolean
  provider: string
  providerEnabled: boolean
  id: string
  alias?: string
  contextWindow: number
  maxOutput: number
  tags: string[]
  capabilities: Record<string, boolean | string>
}

interface SkillInfo {
  name: string
  description?: string
  version?: string
  enabled: boolean
  directory: string
  scope: 'workspace' | 'global'
}

interface HealthCheck {
  name: string
  ok: boolean
  detail: string
  kind: 'runtime' | 'provider'
}

interface UsageInfo {
  provider: string
  model: string
  calls: number
  failures: number
  inputTokens: number
  totalInputTokens?: number
  cachedInputTokens?: number
  cacheCreationInputTokens?: number
  outputTokens: number
  totalTokens?: number
  avgLatencyMs: number
  successRate: number
}

const props = defineProps<{
  open: boolean
  page?: 'settings' | 'extensions'
  theme: 'system' | 'light' | 'dark'
  daemonConnected: boolean
  daemonEndpoint: string
  /** Active workspace directory, used to prefill MCP presets that serve a folder. */
  workspacePath?: string
  initialSection?: SettingsSection
}>()

const emit = defineEmits<{
  close: []
  changeTheme: [theme: 'system' | 'light' | 'dark']
  reconnect: []
  openRulesMemory: []
  runtimeChanged: []
  startExpert: [expert: { id: string, name: string, avatar: string }]
}>()

const section = ref<SettingsSection>('general')
const loading = ref(false)
const networkEnabled = ref(true)
const failoverEnabled = ref(true)
const deepSeekOptimizationEnabled = ref(false)
const computerUseEnabled = ref(false)
const computerUseDriver = ref('auto')
const networkOpsEnabled = ref(false)
const networkOpsAllowMutating = ref(true)
/** 允许的网站（外部网站访问白名单）：卡片计数与管理弹窗。 */
const allowedSitesOpen = ref(false)
const allowedSitesCount = ref<number | null>(null)
const replyLanguage = ref<'auto' | 'zh' | 'en'>('auto')
const rulesEnabled = ref(true)
const memoryMode = ref<'auto' | 'manual'>('auto')

/** 进化引擎：自动反思配置、健康概览、缺陷信号、反思历史、待审草稿与效果统计。 */
const evolutionAutoReflect = ref(false)
const evolutionIntervalMinutes = ref(360)
const evolutionThresholdEnabled = ref(false)
const evolutionThresholdSignals = ref(5)
const evolutionDefects = ref<EvolutionDefectReport[]>([])
const evolutionScanned = ref(0)
const evolutionHealth = ref<EvolutionHealth | null>(null)
const evolutionTrend = ref<EvolutionTrendPoint[]>([])
const evolutionByKind = ref<Record<string, number>>({})
const evolutionCandidates = ref<EvolutionCandidate[]>([])
const evolutionHistory = ref<EvolutionRun[]>([])
const evolutionStats = ref<EvolutionStats | null>(null)
/** 展开的草稿工作台（单选）；promptDraft 承载采纳前的提示词改写文本。 */
const expandedCandidateFingerprint = ref<string | null>(null)
const candidateActiveFile = ref<Record<string, string>>({})
const promptDraft = ref('')
const promptDirty = ref(false)

/** CUDA / local inference settings; null when no local provider is configured. */
const localInference = reactive({
  providerId: '',
  gpuLayers: 0,
  contextLength: 8192,
  kvCacheQuantization: 'none',
  flashAttention: false
})
const localInferenceAvailable = ref(false)
const kvQuantOptions = [
  { value: 'none', label: 'f16（原生，不量化）', description: '最高精度，显存占用最大' },
  { value: 'q8_0', label: 'q8_0', description: '接近 f16 的精度，显存减半' },
  { value: 'q4_0', label: 'q4_0', description: '显存占用最小，精度损失明显' }
]

const action = ref('')
const error = ref('')
const notice = ref('')
const providers = ref<ProviderInfo[]>([])
const models = ref<ModelInfo[]>([])
const skills = ref<SkillInfo[]>([])
const checks = ref<HealthCheck[]>([])
const usage = ref<UsageInfo[]>([])
const usageDays = ref<number>(14)
const timeline = ref<TimelinePoint[]>([])
const selectedModel = ref('')
const providerEditorOpen = ref(false)
const editingProviderId = ref<string | null>(null)

const providerForm = reactive<ProviderFormValue>({
  id: '', name: '', kind: 'openai', baseUrl: '',
  apiKey: '', models: '', modelsDirectory: '', modelDetails: [], enabled: true, priority: 0,
  modelListUrl: '', timeoutSeconds: 120, proxy: '', promptCaching: true
})

const activeModel = computed(() => models.value.find((model) => model.active))
const modelOptions = computed(() => models.value.map((model) => ({
  value: model.ref,
  label: model.ref,
  description: `${model.contextWindow.toLocaleString()} 上下文 · ${model.provider}`,
  disabled: !model.providerEnabled
})))

const visionModelOptions = computed(() => [
  { value: '', label: '自动', description: '使用路由链中第一个支持视觉的模型处理图像回合' },
  ...models.value
    .filter((model) => model.capabilities?.vision === true)
    .map((model) => ({
      value: model.ref,
      label: model.ref,
      description: `${model.contextWindow.toLocaleString()} 上下文 · ${model.provider}`,
      disabled: !model.providerEnabled
    }))
])

const showModelCatalog = ref(false)

/** 智能体设置：视觉子智能体模型、探索者委派与自动验证。visionModel 为空表示自动选择。 */
const agentConfig = reactive({
  visionModel: '',
  delegationEnabled: true,
  autoVerify: false
})

const CAPABILITY_LABELS: Record<string, string> = {
  streaming: '流式',
  tools: '工具调用',
  thinking: '思考',
  vision: '视觉',
  reasoning: '推理',
  mcp: 'MCP'
}

function capabilityChips(model: ModelInfo): string[] {
  return Object.entries(CAPABILITY_LABELS)
    .filter(([key]) => model.capabilities?.[key] === true)
    .map(([, label]) => label)
}

function formatTokens(value: number): string {
  if (value >= 1_000_000) return `${Number((value / 1_000_000).toFixed(1))}M`
  if (value >= 1_000) return `${Math.round(value / 1_000)}K`
  return String(value)
}

const totalUsage = computed(() => {
  let calls = 0
  let failures = 0
  let tokens = 0
  let cachedTokens = 0
  let totalLatency = 0

  for (const item of usage.value) {
    calls += item.calls
    failures += item.failures
    const itemInput = promptInputTokens(item)
    tokens += itemInput + item.outputTokens
    cachedTokens += item.cachedInputTokens ?? 0
    totalLatency += item.avgLatencyMs * item.calls
  }

  const successRate = calls > 0 ? (calls - failures) / calls : 1.0
  const cacheEfficiency = tokens > 0 ? Math.min(100, Math.round((cachedTokens / tokens) * 100)) : 0
  const avgLatencyMs = calls > 0 ? Math.round(totalLatency / calls) : 0

  return {
    calls,
    failures,
    tokens,
    cachedTokens,
    successRate,
    cacheEfficiency,
    avgLatencyMs
  }
})

function promptInputTokens(item: UsageInfo): number {
  return item.totalInputTokens && item.totalInputTokens > 0 ? item.totalInputTokens : item.inputTokens
}

function cacheHitRate(item: UsageInfo): number {
  const cached = item.cachedInputTokens ?? 0
  const total = promptInputTokens(item)
  return total > 0 ? Math.min(100, Math.round(cached / total * 100)) : 0
}

const sections: Array<{ id: SettingsSection; label: string; icon: typeof Settings2 }> = [
  { id: 'general', label: '常规', icon: Settings2 },
  { id: 'models', label: '模型与提供商', icon: Bot },
  { id: 'agent', label: '智能体', icon: Sparkles },
  { id: 'mcp', label: 'MCP', icon: Blocks },
  { id: 'skills', label: '技能', icon: Wrench },
  { id: 'rules-memory', label: '规则与记忆', icon: ScrollText },
  { id: 'evolution', label: '进化', icon: Telescope },
  { id: 'diagnostics', label: '诊断与用量', icon: Activity },
  { id: 'inference', label: '推理加速', icon: Gauge },
  { id: 'advanced', label: '高级设置', icon: SlidersHorizontal }
]

/** 扩展中心（Qoder 式）：连接器 / 技能 / 专家三大类目。 */
const extensionSections: Array<{ id: SettingsSection; label: string; icon: typeof Settings2 }> = [
  { id: 'mcp', label: '连接器', icon: Plug },
  { id: 'skills', label: '技能', icon: Wrench },
  { id: 'experts', label: '专家', icon: Users }
]

const pageTitle = computed(() => props.page === 'extensions' ? '扩展' : '设置')
const visibleSections = computed(() =>
  props.page === 'extensions' ? extensionSections : sections)

/** 扩展页技能类目内的二级视图：本地已安装 / 技能市场。 */
const skillsView = ref<'installed' | 'market'>('installed')

async function requestJson<T>(
  method: string,
  params: Record<string, unknown> = {},
  timeoutMs?: number
): Promise<T> {
  const response = await window.haoyue.daemon.request(method, params, timeoutMs ? { timeoutMs } : undefined)
  return JSON.parse(response.data) as T
}

function beginAction(name: string): void {
  action.value = name
  error.value = ''
  notice.value = ''
}

function endAction(): void {
  action.value = ''
}

function fail(reason: unknown): void {
  error.value = reason instanceof Error ? reason.message : String(reason)
}

function showPath(path: string): void {
  void window.haoyue.showItemInFolder(path)
}

async function loadCurrentSection(): Promise<void> {
  if (!props.open || !props.daemonConnected) return
  if (section.value === 'experts') return // 专家目录由 ExpertsPanel 自行加载
  loading.value = true
  error.value = ''
  notice.value = ''
  try {
    if (section.value === 'general') await loadGeneral()
    if (section.value === 'models') await loadModels()
    if (section.value === 'agent') await loadAgent()
    if (section.value === 'skills') skills.value = await requestJson<SkillInfo[]>('skill.list')
    if (section.value === 'rules-memory') await loadRulesMemory()
    if (section.value === 'evolution') await loadEvolution()
    if (section.value === 'diagnostics') await loadDiagnostics()
    if (section.value === 'inference' || section.value === 'advanced') await loadAdvanced()
  } catch (reason) {
    fail(reason)
  } finally {
    loading.value = false
  }
}

async function loadGeneral(): Promise<void> {
  const routing = await requestJson<{ failoverEnabled: boolean; deepSeekOptimizationEnabled: boolean }>('routing.get')
  failoverEnabled.value = routing.failoverEnabled
  deepSeekOptimizationEnabled.value = routing.deepSeekOptimizationEnabled
  const advanced = await requestJson<{ language?: string }>('advanced.get')
  replyLanguage.value = normalizeReplyLanguage(advanced.language)
}

function normalizeReplyLanguage(value?: string): 'auto' | 'zh' | 'en' {
  return value === 'zh' || value === 'en' ? value : 'auto'
}

async function setReplyLanguage(value: 'auto' | 'zh' | 'en'): Promise<void> {
  if (replyLanguage.value === value) return
  const previous = replyLanguage.value
  replyLanguage.value = value
  beginAction('language.set')
  try {
    const config = await requestJson<{ language: string }>('advanced.set', { language: value })
    replyLanguage.value = normalizeReplyLanguage(config.language)
    notice.value = replyLanguage.value === 'en'
      ? '回复语言已设为 English：模型将以英文回复'
      : replyLanguage.value === 'zh'
        ? '回复语言已设为中文：模型将以简体中文回复'
        : '回复语言已设为跟随系统：中文系统默认中文回复，其它系统默认英文回复'
  } catch (reason) {
    replyLanguage.value = previous
    fail(reason)
  } finally {
    endAction()
  }
}

function normalizeMemoryMode(value?: string): 'auto' | 'manual' {
  return value === 'manual' ? 'manual' : 'auto'
}

async function loadRulesMemory(): Promise<void> {
  const config = await requestJson<{ rulesEnabled?: boolean; memoryMode?: string }>('advanced.get')
  rulesEnabled.value = config.rulesEnabled ?? true
  memoryMode.value = normalizeMemoryMode(config.memoryMode)
}

async function setRulesMemory(payload: { rulesEnabled?: boolean; memoryMode?: 'auto' | 'manual' }): Promise<void> {
  const previousRules = rulesEnabled.value
  const previousMode = memoryMode.value
  if (payload.rulesEnabled !== undefined) rulesEnabled.value = payload.rulesEnabled
  if (payload.memoryMode !== undefined) memoryMode.value = payload.memoryMode
  beginAction('rulesMemory.set')
  try {
    const config = await requestJson<{ rulesEnabled: boolean; memoryMode: string }>(
      'advanced.set',
      { rulesEnabled: rulesEnabled.value, memoryMode: memoryMode.value }
    )
    rulesEnabled.value = config.rulesEnabled
    memoryMode.value = normalizeMemoryMode(config.memoryMode)
    if (!rulesEnabled.value)
      notice.value = '规则注入已停用：Agent 不再读取 AGENTS.md，仅对话中的显式指令生效'
    else if (memoryMode.value === 'manual')
      notice.value = '记忆已切换为手动管理：Agent 把 MEMORY.md 视为只读，仅你能在编辑器中维护'
    else
      notice.value = '记忆已切换为自动管理：Agent 可在会话中沉淀长期事实到 MEMORY.md'
  } catch (reason) {
    rulesEnabled.value = previousRules
    memoryMode.value = previousMode
    fail(reason)
  } finally {
    endAction()
  }
}

type EvolutionInspect = {
  scanned: number
  reports: EvolutionDefectReport[]
  health?: EvolutionHealth
  trend?: EvolutionTrendPoint[]
  distribution?: { byKind: Record<string, number> }
}

function applyInspect(inspect: EvolutionInspect): void {
  evolutionDefects.value = inspect.reports
  evolutionScanned.value = inspect.scanned
  evolutionHealth.value = inspect.health ?? null
  evolutionTrend.value = inspect.trend ?? []
  evolutionByKind.value = inspect.distribution?.byKind ?? {}
}

async function loadEvolution(): Promise<void> {
  const [config, inspect, pending, history, stats] = await Promise.all([
    requestJson<EvolutionConfigValue>('evolution.config.get'),
    requestJson<EvolutionInspect>('evolution.inspect'),
    requestJson<EvolutionCandidate[]>('evolution.pending-list'),
    requestJson<EvolutionRun[]>('evolution.history', { limit: 20 }),
    requestJson<EvolutionStats>('evolution.stats')
  ])
  evolutionAutoReflect.value = config.autoReflect
  evolutionIntervalMinutes.value = clampIntervalMinutes(config.intervalMinutes)
  evolutionThresholdEnabled.value = config.thresholdEnabled ?? false
  evolutionThresholdSignals.value = clampThresholdSignals(config.thresholdSignals ?? 5)
  applyInspect(inspect)
  evolutionCandidates.value = pending
  evolutionHistory.value = history
  evolutionStats.value = stats
}

async function setEvolutionConfig(payload: {
  autoReflect?: boolean
  intervalMinutes?: number
  thresholdEnabled?: boolean
  thresholdSignals?: number
}): Promise<void> {
  const previous = {
    autoReflect: evolutionAutoReflect.value,
    intervalMinutes: evolutionIntervalMinutes.value,
    thresholdEnabled: evolutionThresholdEnabled.value,
    thresholdSignals: evolutionThresholdSignals.value
  }
  if (payload.autoReflect !== undefined) evolutionAutoReflect.value = payload.autoReflect
  if (payload.intervalMinutes !== undefined)
    evolutionIntervalMinutes.value = clampIntervalMinutes(payload.intervalMinutes)
  if (payload.thresholdEnabled !== undefined) evolutionThresholdEnabled.value = payload.thresholdEnabled
  if (payload.thresholdSignals !== undefined)
    evolutionThresholdSignals.value = clampThresholdSignals(payload.thresholdSignals)
  beginAction('evolution.config')
  try {
    const config = await requestJson<EvolutionConfigValue>('evolution.config.set', {
      autoReflect: evolutionAutoReflect.value,
      intervalMinutes: evolutionIntervalMinutes.value,
      thresholdEnabled: evolutionThresholdEnabled.value,
      thresholdSignals: evolutionThresholdSignals.value
    })
    evolutionAutoReflect.value = config.autoReflect
    evolutionIntervalMinutes.value = clampIntervalMinutes(config.intervalMinutes)
    evolutionThresholdEnabled.value = config.thresholdEnabled ?? false
    evolutionThresholdSignals.value = clampThresholdSignals(config.thresholdSignals ?? 5)
    const parts: string[] = []
    if (evolutionAutoReflect.value)
      parts.push(`每 ${formatInterval(evolutionIntervalMinutes.value)} 复盘一次`)
    if (evolutionThresholdEnabled.value)
      parts.push(`缺陷累计 ${evolutionThresholdSignals.value} 条即提前复盘`)
    notice.value = parts.length > 0
      ? `自动反思已开启：${parts.join('，或')}；技能草稿始终等待人工审阅`
      : '自动反思已关闭：仅手动触发反思回合'
  } catch (reason) {
    evolutionAutoReflect.value = previous.autoReflect
    evolutionIntervalMinutes.value = previous.intervalMinutes
    evolutionThresholdEnabled.value = previous.thresholdEnabled
    evolutionThresholdSignals.value = previous.thresholdSignals
    fail(reason)
  } finally {
    endAction()
  }
}

async function runReflection(): Promise<void> {
  beginAction('evolution.reflect')
  try {
    await requestJson<{ started: boolean }>('evolution.reflect')
    notice.value = '反思回合已启动：正在后台复盘缺陷信号，完成后本页自动刷新'
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

/**
 * Applies a human decision. Adopt (optionally with a rewritten prompt) installs
 * the draft as a live skill; defer parks it; reject discards it permanently, so
 * only reject asks for confirmation.
 */
async function decideEvolutionCandidate(
  candidate: EvolutionCandidate,
  decision: 'adopt' | 'reject' | 'defer',
  promptOverride?: string
): Promise<void> {
  if (decision === 'reject') {
    const confirmed = await confirmAction({
      title: '丢弃该候选技能？',
      message: `「${candidate.skillName}」的草稿将被永久删除，同一缺陷不会再生成候选。`,
      confirmLabel: '丢弃',
      danger: true
    })
    if (!confirmed) return
  }
  beginAction(`evolution.decide:${candidate.fingerprint}`)
  try {
    await requestJson('evolution.decide', {
      fingerprint: candidate.fingerprint,
      decision,
      ...(promptOverride !== undefined ? { prompt: promptOverride } : {})
    })
    evolutionCandidates.value = await requestJson<EvolutionCandidate[]>('evolution.pending-list')
    notice.value =
      decision === 'adopt'
        ? `已采纳「${candidate.skillName}」：技能进入正式目录，下一次扫描即生效`
        : decision === 'defer'
          ? `已暂缓「${candidate.skillName}」：草稿保留在待审清单，随时可以继续审阅`
          : `已丢弃「${candidate.skillName}」`
    collapseDraftWorkbench()
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

// ------------------------------------------------ draft workbench (preview + rewrite)

function expandDraftWorkbench(candidate: EvolutionCandidate): void {
  expandedCandidateFingerprint.value = candidate.fingerprint
  const files = candidate.files ?? []
  if (!candidateActiveFile.value[candidate.fingerprint]) {
    candidateActiveFile.value[candidate.fingerprint] = files[0]?.name ?? ''
  }
  promptDraft.value = files.find((file) => file.name === 'prompt.txt')?.content ?? ''
  promptDirty.value = false
}

function collapseDraftWorkbench(): void {
  expandedCandidateFingerprint.value = null
  promptDirty.value = false
}

function activeDraftFile(candidate: EvolutionCandidate): EvolutionCandidateFile | null {
  const active = candidateActiveFile.value[candidate.fingerprint]
  return candidate.files?.find((file) => file.name === active) ?? candidate.files?.[0] ?? null
}

function selectDraftFile(candidate: EvolutionCandidate, name: string): void {
  candidateActiveFile.value[candidate.fingerprint] = name
}

/** Only an edited, non-blank prompt is sent as the adopt-time rewrite. */
function promptOverrideFor(): string | undefined {
  if (!promptDirty.value) return undefined
  return promptDraft.value.trim().length > 0 ? promptDraft.value : undefined
}

/** Reflection finished (manual or unattended): refresh the open page and surface the outcome. */
async function refreshEvolutionAfterReflect(details: Record<string, unknown>): Promise<void> {
  if (!props.open || section.value !== 'evolution' || action.value.startsWith('evolution.')) return
  try {
    evolutionCandidates.value = await requestJson<EvolutionCandidate[]>('evolution.pending-list')
    applyInspect(await requestJson<EvolutionInspect>('evolution.inspect'))
    evolutionHistory.value = await requestJson<EvolutionRun[]>('evolution.history', { limit: 20 })
    evolutionStats.value = await requestJson<EvolutionStats>('evolution.stats')
  } catch {
    return
  }
  const num = (key: string): number | undefined =>
    typeof details[key] === 'number' ? (details[key] as number) : undefined
  notice.value = reflectionOutcomeText({
    processed: num('processed'),
    candidates: num('candidates'),
    noAction: num('noAction'),
    skipped: num('skipped'),
    error: typeof details.error === 'string' ? details.error : undefined
  })
}

async function loadAdvanced(): Promise<void> {
  const config = await requestJson<{
    networkEnabled: boolean
    failoverEnabled: boolean
    deepSeekOptimizationEnabled: boolean
    computerUseEnabled?: boolean
    computerUseDriver?: string
    networkOpsEnabled?: boolean
    networkOpsAllowMutating?: boolean
    localInference?: {
      providerId: string
      gpuLayers: number
      contextLength: number
      kvCacheQuantization: string
      flashAttention: boolean
    } | null
  }>('advanced.get')
  networkEnabled.value = config.networkEnabled
  failoverEnabled.value = config.failoverEnabled
  deepSeekOptimizationEnabled.value = config.deepSeekOptimizationEnabled
  computerUseEnabled.value = config.computerUseEnabled ?? false
  computerUseDriver.value = config.computerUseDriver ?? 'auto'
  networkOpsEnabled.value = config.networkOpsEnabled ?? false
  networkOpsAllowMutating.value = config.networkOpsAllowMutating ?? true
  try {
    const web = await requestJson<{ allowedSites: string[] }>('web.allowedGet')
    allowedSitesCount.value = web.allowedSites?.length ?? 0
  } catch {
    allowedSitesCount.value = null // daemon 版本较旧时静默隐藏计数
  }
  if (config.localInference) {
    localInference.providerId = config.localInference.providerId
    localInference.gpuLayers = config.localInference.gpuLayers
    localInference.contextLength = config.localInference.contextLength
    localInference.kvCacheQuantization = config.localInference.kvCacheQuantization
    localInference.flashAttention = config.localInference.flashAttention
    localInferenceAvailable.value = true
  } else {
    localInferenceAvailable.value = false
  }
}

async function saveLocalInference(): Promise<void> {
  const previous = { ...localInference }
  beginAction('advanced.set:localInference')
  try {
    const config = await requestJson<{ localInference: typeof localInference | null }>('advanced.set', {
      localInference: {
        providerId: localInference.providerId,
        gpuLayers: Math.max(0, Math.floor(localInference.gpuLayers) || 0),
        contextLength: Math.max(512, Math.floor(localInference.contextLength) || 8192),
        kvCacheQuantization: localInference.kvCacheQuantization,
        flashAttention: localInference.flashAttention
      }
    })
    if (config.localInference) {
      localInference.gpuLayers = config.localInference.gpuLayers
      localInference.contextLength = config.localInference.contextLength
      localInference.kvCacheQuantization = config.localInference.kvCacheQuantization
      localInference.flashAttention = config.localInference.flashAttention
    }
    notice.value = '本地推理设置已保存，下一次本地模型请求生效'
  } catch (reason) {
    Object.assign(localInference, previous)
    fail(reason)
  } finally {
    endAction()
  }
}

async function toggleComputerUse(): Promise<void> {
  beginAction('advanced.set:computerUse')
  try {
    const config = await requestJson<{
      networkEnabled: boolean
      failoverEnabled: boolean
      deepSeekOptimizationEnabled: boolean
      computerUseEnabled: boolean
      computerUseDriver: string
    }>('advanced.set', {
      networkEnabled: networkEnabled.value,
      failoverEnabled: failoverEnabled.value,
      deepSeekOptimizationEnabled: deepSeekOptimizationEnabled.value,
      computerUseEnabled: computerUseEnabled.value,
      computerUseDriver: computerUseDriver.value
    })
    computerUseEnabled.value = config.computerUseEnabled
    notice.value = computerUseEnabled.value
      ? '已启用电脑操作智能体 (Computer Use)，Agent 获得直接操作桌面能力'
      : '已禁用电脑操作智能体，所有相关驱动与工具已注销并保持零开销'
  } catch (reason) {
    fail(reason)
    try {
      const config = await requestJson<{ computerUseEnabled: boolean }>('advanced.get')
      computerUseEnabled.value = config.computerUseEnabled ?? false
    } catch { /* keep last known state */ }
  } finally {
    endAction()
  }
}

async function toggleNetworkOps(): Promise<void> {
  beginAction('advanced.set:networkOps')
  try {
    const config = await requestJson<{ networkOpsEnabled: boolean }>('advanced.set', {
      networkOpsEnabled: networkOpsEnabled.value
    })
    networkOpsEnabled.value = config.networkOpsEnabled
    notice.value = networkOpsEnabled.value
      ? '已启用网络运维插件，Agent 获得网络诊断与运维命令能力'
      : '已禁用网络运维插件，相关工具已注销'
  } catch (reason) {
    fail(reason)
    try {
      const config = await requestJson<{ networkOpsEnabled: boolean }>('advanced.get')
      networkOpsEnabled.value = config.networkOpsEnabled ?? false
    } catch { /* keep last known state */ }
  } finally {
    endAction()
  }
}

async function toggleNetworkOpsMutating(): Promise<void> {
  const previous = networkOpsAllowMutating.value
  networkOpsAllowMutating.value = !previous
  beginAction('advanced.set:networkOpsMutating')
  try {
    const config = await requestJson<{ networkOpsAllowMutating: boolean }>('advanced.set', {
      networkOpsAllowMutating: networkOpsAllowMutating.value
    })
    networkOpsAllowMutating.value = config.networkOpsAllowMutating
    notice.value = networkOpsAllowMutating.value
      ? '网络运维已允许变更类命令（route、netsh set、DNS 缓存刷新等）'
      : '网络运维已限制为只读查询，变更类命令将被拒绝'
  } catch (reason) {
    networkOpsAllowMutating.value = previous
    fail(reason)
  } finally {
    endAction()
  }
}

async function toggleNetworkEnabled(): Promise<void> {

  beginAction('advanced.set:network')
  try {
    const config = await requestJson<{
      networkEnabled: boolean
      failoverEnabled: boolean
      deepSeekOptimizationEnabled: boolean
    }>('advanced.set', {
      networkEnabled: networkEnabled.value,
      failoverEnabled: failoverEnabled.value,
      deepSeekOptimizationEnabled: deepSeekOptimizationEnabled.value
    })
    networkEnabled.value = config.networkEnabled
    notice.value = networkEnabled.value
      ? '已开启全局网络访问（所有任务与项目可用网页搜索与抓取）'
      : '已关闭全局网络访问（强制处于离线模式，禁止外网请求）'
  } catch (reason) {
    fail(reason)
    try {
      const config = await requestJson<{ networkEnabled: boolean }>('advanced.get')
      networkEnabled.value = config.networkEnabled
    } catch { /* keep last known state */ }
  } finally {
    endAction()
  }
}

async function factoryReset(): Promise<void> {
  if (!props.daemonConnected) return
  if (!await confirmAction({
    title: '恢复出厂设置',
    message: '将删除全局配置、会话、计划任务、项目记录、用量日志、日志、全局技能与提示词，并重建数据库。项目源码文件不会删除，此操作无法撤销。',
    confirmLabel: '继续',
    danger: true
  })) return
  if (!await confirmAction({
    title: '再次确认恢复出厂设置',
    message: '这是最后一次确认。确认后将清空 Haoyue 的全部用户数据，并恢复默认设置。',
    confirmLabel: '恢复出厂设置',
    danger: true
  })) return

  beginAction('factory.reset')
  try {
    await requestJson<{ ok: boolean; home: string }>('factory.reset')
    emit('runtimeChanged')
    emit('close')
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function toggleFailover(): Promise<void> {
  beginAction('routing.set')
  try {
    const routing = await requestJson<{ failoverEnabled: boolean; deepSeekOptimizationEnabled: boolean }>('routing.set', {
      failoverEnabled: failoverEnabled.value,
      deepSeekOptimizationEnabled: deepSeekOptimizationEnabled.value
    })
    failoverEnabled.value = routing.failoverEnabled
    deepSeekOptimizationEnabled.value = routing.deepSeekOptimizationEnabled
    notice.value = failoverEnabled.value ? '已开启自动切换其他模型' : '已关闭自动切换，失败即停止'
  } catch (reason) {
    fail(reason)
    try {
      const routing = await requestJson<{ failoverEnabled: boolean; deepSeekOptimizationEnabled: boolean }>('routing.get')
      failoverEnabled.value = routing.failoverEnabled
      deepSeekOptimizationEnabled.value = routing.deepSeekOptimizationEnabled
    } catch { /* keep the last known state */ }
  } finally {
    endAction()
  }
}

async function toggleDeepSeekOptimization(): Promise<void> {
  beginAction('routing.set:deepseek')
  try {
    const routing = await requestJson<{ failoverEnabled: boolean; deepSeekOptimizationEnabled: boolean }>('routing.set', {
      failoverEnabled: failoverEnabled.value,
      deepSeekOptimizationEnabled: deepSeekOptimizationEnabled.value
    })
    failoverEnabled.value = routing.failoverEnabled
    deepSeekOptimizationEnabled.value = routing.deepSeekOptimizationEnabled
    notice.value = deepSeekOptimizationEnabled.value
      ? '已开启 DeepSeek 模型优化'
      : '已关闭 DeepSeek 模型优化'
  } catch (reason) {
    fail(reason)
    try {
      const routing = await requestJson<{ failoverEnabled: boolean; deepSeekOptimizationEnabled: boolean }>('routing.get')
      failoverEnabled.value = routing.failoverEnabled
      deepSeekOptimizationEnabled.value = routing.deepSeekOptimizationEnabled
    } catch { /* keep the last known state */ }
  } finally {
    endAction()
  }
}

async function loadModels(): Promise<void> {
  const [providerData, modelData] = await Promise.all([
    requestJson<ProviderInfo[]>('provider.list'),
    requestJson<ModelInfo[]>('model.catalog')
  ])
  providers.value = providerData
  models.value = modelData
  selectedModel.value = modelData.find((model) => model.active)?.ref ?? modelData[0]?.ref ?? ''
}

async function loadAgent(): Promise<void> {
  const [config] = await Promise.all([
    requestJson<{
      networkEnabled: boolean
      delegationEnabled: boolean
      visionModel: string
      autoVerify: boolean
    }>('agent.config.get'),
    loadModels()
  ])
  networkEnabled.value = config.networkEnabled
  agentConfig.delegationEnabled = config.delegationEnabled
  agentConfig.visionModel = config.visionModel
  agentConfig.autoVerify = config.autoVerify
}

async function saveAgentConfig(params: Record<string, unknown>): Promise<{
  networkEnabled: boolean
  delegationEnabled: boolean
  visionModel: string
  autoVerify: boolean
}> {
  return requestJson('agent.config.set', params)
}

async function toggleAgentNetwork(): Promise<void> {
  beginAction('agent.config.set:network')
  try {
    const config = await saveAgentConfig({ networkEnabled: networkEnabled.value })
    networkEnabled.value = config.networkEnabled
    notice.value = networkEnabled.value
      ? '已开启网页搜索（智能体可使用 web_search 与 web_fetch 等网络工具）'
      : '已关闭网页搜索（智能体强制处于离线模式）'
  } catch (reason) {
    fail(reason)
    try {
      const config = await requestJson<{ networkEnabled: boolean }>('agent.config.get')
      networkEnabled.value = config.networkEnabled
    } catch { /* keep last known state */ }
  } finally {
    endAction()
  }
}

async function toggleDelegation(): Promise<void> {
  beginAction('agent.config.set:delegation')
  try {
    const config = await saveAgentConfig({ delegationEnabled: agentConfig.delegationEnabled })
    agentConfig.delegationEnabled = config.delegationEnabled
    notice.value = agentConfig.delegationEnabled
      ? '已开启探索者智能体（独立子任务可委派给子智能体）'
      : '已关闭探索者智能体（子任务由主智能体自行完成）'
  } catch (reason) {
    fail(reason)
    try {
      const config = await requestJson<{ delegationEnabled: boolean }>('agent.config.get')
      agentConfig.delegationEnabled = config.delegationEnabled
    } catch { /* keep last known state */ }
  } finally {
    endAction()
  }
}

async function toggleAgentAutoVerify(): Promise<void> {
  beginAction('agent.config.set:autoVerify')
  try {
    const config = await saveAgentConfig({ autoVerify: agentConfig.autoVerify })
    agentConfig.autoVerify = config.autoVerify
    notice.value = agentConfig.autoVerify
      ? '已开启自动验证与修复（修改文件后自动运行构建验证）'
      : '已关闭自动验证与修复'
  } catch (reason) {
    fail(reason)
    try {
      const config = await requestJson<{ autoVerify: boolean }>('agent.config.get')
      agentConfig.autoVerify = config.autoVerify
    } catch { /* keep last known state */ }
  } finally {
    endAction()
  }
}

async function setVisionModel(): Promise<void> {
  beginAction('agent.config.set:vision')
  try {
    const config = await saveAgentConfig({ visionModel: agentConfig.visionModel })
    agentConfig.visionModel = config.visionModel
    notice.value = agentConfig.visionModel
      ? `图像回合将优先使用 ${agentConfig.visionModel} 处理`
      : '视觉子智能体模型已恢复自动选择'
  } catch (reason) {
    fail(reason)
    try {
      const config = await requestJson<{ visionModel: string }>('agent.config.get')
      agentConfig.visionModel = config.visionModel
    } catch { /* keep last known state */ }
  } finally {
    endAction()
  }
}

async function loadDiagnostics(): Promise<void> {
  const [healthData, usageData, timelineData] = await Promise.all([
    requestJson<HealthCheck[]>('doctor.run'),
    requestJson<UsageInfo[]>('usage.get', { days: usageDays.value }),
    requestJson<TimelinePoint[]>('usage.timeline', { days: usageDays.value })
  ])
  checks.value = healthData
  usage.value = usageData
  timeline.value = timelineData
}

watch(usageDays, () => {
  if (section.value === 'diagnostics') {
    void loadDiagnostics()
  }
})

function newProvider(): void {
  error.value = ''
  editingProviderId.value = null
  Object.assign(providerForm, {
    id: '', name: '', kind: 'openai', baseUrl: '', apiKey: '',
    models: '', modelsDirectory: '', modelDetails: [], enabled: true, priority: 0, modelListUrl: '', timeoutSeconds: 120, proxy: '', promptCaching: true
  })
  providerEditorOpen.value = true
}

function editProvider(provider: ProviderInfo): void {
  error.value = ''
  editingProviderId.value = provider.id
  Object.assign(providerForm, {
    id: provider.id,
    name: provider.name === provider.id ? '' : provider.name,
    kind: provider.kind,
    baseUrl: provider.baseUrl,
    modelListUrl: provider.modelListUrl ?? '',
    apiKey: provider.apiKey ?? '',
    models: provider.models.join('\n'),
    modelsDirectory: provider.modelsDirectory ?? '',
    modelDetails: provider.modelDetails ? provider.modelDetails.map((m) => ({ ...m })) : [],
    enabled: provider.enabled,
    priority: provider.priority,
    timeoutSeconds: provider.timeoutSeconds,
    proxy: provider.proxy ?? '',
    promptCaching: provider.promptCaching ?? true
  })
  providerEditorOpen.value = true
}

async function saveProvider(value: ProviderFormValue): Promise<void> {
  beginAction('provider.save')
  try {
    const { apiKey, ...provider } = value
    const parameters: Record<string, unknown> = {
      ...provider,
      models: value.models.split(/\r?\n|,/).map((model) => model.trim()).filter(Boolean),
      modelDetails: value.modelDetails ?? []
    }
    if (apiKey.trim()) parameters.apiKey = apiKey.trim()
    else if (editingProviderId.value) parameters.clearApiKey = true
    await window.haoyue.daemon.request('provider.upsert', parameters)
    providerEditorOpen.value = false
    await loadModels()
    emit('runtimeChanged')
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function fetchProviderModels(provider: ProviderInfo): Promise<void> {
  beginAction(`provider.models.fetch:${provider.id}`)
  try {
    const ids = await requestJson<string[]>('provider.models.fetch', { id: provider.id })
    notice.value = `${provider.id} 已获取 ${ids.length} 个模型`
    await loadModels()
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function useProvider(provider: ProviderInfo): Promise<void> {
  beginAction(`provider.use:${provider.id}`)
  try {
    await window.haoyue.daemon.request('provider.use', { id: provider.id })
    await loadModels()
    emit('runtimeChanged')
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function testProvider(provider: ProviderInfo): Promise<void> {
  beginAction(`provider.test:${provider.id}`)
  try {
    const [result] = await requestJson<Array<{ online: boolean; latencyMs: number; detail: string }>>('provider.test', { id: provider.id })
    const message = result
      ? `${provider.id}: ${result.online ? '在线' : '离线'} · ${Math.round(result.latencyMs)} ms · ${result.detail}`
      : '模型提供商测试没有返回结果'
    if (result?.online) notice.value = message
    else error.value = message
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function removeProvider(provider: ProviderInfo): Promise<void> {
  if (!await confirmAction({
    title: '删除模型提供商', message: `删除模型提供商 “${provider.id}”？`, confirmLabel: '删除', danger: true
  })) return
  beginAction('provider.remove')
  try {
    await window.haoyue.daemon.request('provider.remove', { id: provider.id })
    await loadModels()
    emit('runtimeChanged')
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function switchModel(): Promise<void> {
  if (!selectedModel.value) return
  beginAction('model.switch')
  try {
    await window.haoyue.daemon.request('model.switch', { model: selectedModel.value })
    await loadModels()
    emit('runtimeChanged')
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function testModel(): Promise<void> {
  if (!selectedModel.value) return
  beginAction('model.test')
  try {
    const result = await requestJson<{ success: boolean; detail: string; latencyMs: number }>('model.test', { model: selectedModel.value })
    const message = `${result.success ? '模型可用' : '模型不可用'} · ${Math.round(result.latencyMs)} ms · ${result.detail}`
    if (result.success) notice.value = message
    else error.value = message
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

// ---------------------------------------------------------------- local model load flow

interface ModelTestState {
  status: 'running' | 'ok' | 'fail'
  detail: string
  latencyMs?: number
}

/** Outcome of the load-verify flow for the currently selected model. */
const modelTestState = ref<ModelTestState | null>(null)

/**
 * Load-verify flow: a cheap model.status pre-flight (file exists? correct size?)
 * fails fast with a clear reason, then model.test performs a real generation.
 * Both steps run inside the daemon, so the dialog never blocks.
 */
async function verifyModel(): Promise<void> {
  if (!selectedModel.value || action.value) return
  modelTestState.value = { status: 'running', detail: '正在校验模型文件…' }
  beginAction('model.verify')
  try {
    const status = await requestJson<{
      exists: boolean
      path?: string
      reason?: string
      sizeBytes?: number
      kind: string
    }>('model.status', { model: selectedModel.value })
    if (!status.exists) {
      modelTestState.value = { status: 'fail', detail: status.reason ?? '模型文件不存在' }
      return
    }
    const sizeHint = status.sizeBytes ? `（${formatBytes(status.sizeBytes)}）` : ''
    modelTestState.value = { status: 'running', detail: `文件校验通过${sizeHint}，正在加载模型并试运行…` }
    const result = await requestJson<{ success: boolean; detail: string; latencyMs: number }>(
      'model.test', { model: selectedModel.value }, 300_000)
    modelTestState.value = result.success
      ? { status: 'ok', detail: `加载成功，试运行 ${Math.round(result.latencyMs)} ms · ${result.detail}`, latencyMs: result.latencyMs }
      : { status: 'fail', detail: `加载失败：${result.detail}` }
  } catch (reason) {
    modelTestState.value = { status: 'fail', detail: reason instanceof Error ? reason.message : String(reason) }
  } finally {
    endAction()
  }
}

function formatBytes(bytes: number): string {
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(1)} GB`
  return `${Math.round(bytes / 1024 ** 2)} MB`
}

// ---------------------------------------------------------------- inference presets

interface InferencePreset {
  name: string
  gpuLayers: number
  contextLength: number
  kvCacheQuantization: string
  flashAttention: boolean
}

const INFERENCE_PRESETS_KEY = 'haoyue.inference-presets'

/** Named load-parameter presets persisted locally for one-click switching. */
const inferencePresets = ref<InferencePreset[]>(loadInferencePresets())
const presetNameInput = ref('')

function loadInferencePresets(): InferencePreset[] {
  try {
    const raw = JSON.parse(localStorage.getItem(INFERENCE_PRESETS_KEY) ?? '[]') as InferencePreset[]
    return Array.isArray(raw) ? raw.filter((preset) => preset && typeof preset.name === 'string') : []
  } catch {
    return []
  }
}

function persistInferencePresets(): void {
  localStorage.setItem(INFERENCE_PRESETS_KEY, JSON.stringify(inferencePresets.value))
}

function saveInferencePreset(): void {
  const name = presetNameInput.value.trim() || `预设 ${inferencePresets.value.length + 1}`
  if (inferencePresets.value.some((preset) => preset.name === name)) {
    error.value = `预设「${name}」已存在，请换个名称`
    return
  }
  inferencePresets.value = [...inferencePresets.value, {
    name,
    gpuLayers: localInference.gpuLayers,
    contextLength: localInference.contextLength,
    kvCacheQuantization: localInference.kvCacheQuantization,
    flashAttention: localInference.flashAttention
  }]
  persistInferencePresets()
  presetNameInput.value = ''
  notice.value = `已保存预设「${name}」`
}

function applyInferencePreset(preset: InferencePreset): void {
  localInference.gpuLayers = preset.gpuLayers
  localInference.contextLength = preset.contextLength
  localInference.kvCacheQuantization = preset.kvCacheQuantization
  localInference.flashAttention = preset.flashAttention
  notice.value = `已应用预设「${preset.name}」，保存后生效`
}

async function removeInferencePreset(preset: InferencePreset): Promise<void> {
  // H21: preset deletion is irreversible (name can be recreated, tuned values cannot).
  if (!await confirmAction({
    title: '删除推理预设',
    message: `删除预设「${preset.name}」？此操作无法撤销。`,
    confirmLabel: '删除',
    danger: true
  })) return
  inferencePresets.value = inferencePresets.value.filter((entry) => entry !== preset)
  persistInferencePresets()
}

async function toggleSkill(skill: SkillInfo): Promise<void> {
  beginAction(`skill.toggle:${skill.name}`)
  try {
    skills.value = await requestJson<SkillInfo[]>('skill.toggle', { name: skill.name, enabled: !skill.enabled })
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function importSkills(): Promise<void> {
  if (!props.daemonConnected) return
  const selection = await window.haoyue.selectSkillFiles()
  if (selection.paths.length === 0) return

  beginAction('skill.import')
  try {
    for (const path of selection.paths)
      skills.value = await requestJson<SkillInfo[]>('skill.import', { path })
    notice.value = `已导入 ${selection.paths.length} 个全局技能`
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

function normalizedSection(value?: SettingsSection): SettingsSection {
  if (props.page === 'extensions') {
    return value === 'skills' || value === 'experts' ? value : 'mcp'
  }
  return value ?? 'general'
}

watch(() => props.open, (open) => {
  if (!open) return
  section.value = normalizedSection(props.initialSection)
  void loadCurrentSection()
}, { immediate: true })
watch(() => props.initialSection, (value) => {
  if (!props.open || !value) return
  section.value = normalizedSection(value)
  void loadCurrentSection()
})
watch(() => props.page, () => {
  if (!props.open) return
  section.value = normalizedSection(props.initialSection)
  void loadCurrentSection()
})
watch(section, (next) => {
  if (next === 'skills') skillsView.value = 'installed'
  void loadCurrentSection()
})

let unsubscribeDaemonEvents: (() => void) | null = null

onMounted(() => {
  unsubscribeDaemonEvents = window.haoyue.daemon.onEvent((message) => {
    if (message.event === 'evolution.reflected') {
      void refreshEvolutionAfterReflect(message.details ?? {})
    }
  })
})

onBeforeUnmount(() => {
  unsubscribeDaemonEvents?.()
  unsubscribeDaemonEvents = null
})
</script>

<template>
  <section v-if="open" class="settings-dialog settings-workbench embedded-page" role="region" :aria-label="pageTitle">
    <nav class="settings-nav" aria-label="页面导航">
      <div class="settings-nav-header">
        <button class="page-back-button" type="button" title="返回应用" @click="emit('close')">
          <ArrowLeft :size="16" />
          <span>返回应用</span>
        </button>
      </div>

      <div class="settings-nav-group-title">
        {{ pageTitle }}
      </div>

      <div class="settings-nav-list">
        <button v-for="item in visibleSections" :key="item.id" class="settings-nav-item"
          :class="{ active: section === item.id }" @click="section = item.id">
          <component :is="item.icon" :size="17" />
          <span>{{ item.label }}</span>
        </button>
      </div>

      <!--
      <div class="settings-nav-footer">
        <span class="settings-connection" :class="{ online: daemonConnected }" :title="daemonEndpoint">
          <Circle :size="8" fill="currentColor" />
          <span>{{ daemonConnected ? '运行时已连接' : '运行时离线' }}</span>
        </span>
        <button v-if="!daemonConnected" class="icon-button compact reconnect-btn" title="重新连接"
          @click="emit('reconnect')">
          <RefreshCw :size="13" />
        </button>
      </div>
      -->

    </nav>

    <main class="settings-main">
      <div class="settings-content">
        <div v-if="loading" class="settings-loading">
          <LoaderCircle class="spin" :size="20" /> 正在加载
        </div>

        <template v-else-if="section === 'general'">
          <div class="settings-section-heading">
            <div>
              <h3>常规</h3>
            </div>
          </div>

          <section class="settings-group">
            <div class="settings-row">
              <div><strong>外观</strong><small>界面主题</small></div>
              <div class="segmented-control">
                <button :class="{ active: theme === 'system' }" title="跟随系统" @click="emit('changeTheme', 'system')">
                  <Monitor :size="16" />
                </button>
                <button :class="{ active: theme === 'light' }" title="浅色" @click="emit('changeTheme', 'light')">
                  <Sun :size="16" />
                </button>
                <button :class="{ active: theme === 'dark' }" title="深色" @click="emit('changeTheme', 'dark')">
                  <Moon :size="16" />
                </button>
              </div>
            </div>
            <div class="settings-row">
              <div>
                <strong>回复语言</strong>
                <small>模型先识别输入语言，再以目标语言回复；跟随系统 = 中文系统默认中文</small>
              </div>
              <div class="segmented-control">
                <button :class="{ active: replyLanguage === 'auto' }" :disabled="action === 'language.set'"
                  title="跟随系统语言（中文系统默认中文）" @click="setReplyLanguage('auto')">自动</button>
                <button :class="{ active: replyLanguage === 'zh' }" :disabled="action === 'language.set'"
                  title="简体中文" @click="setReplyLanguage('zh')">中文</button>
                <button :class="{ active: replyLanguage === 'en' }" :disabled="action === 'language.set'"
                  title="English" @click="setReplyLanguage('en')">English</button>
              </div>
            </div>
            <div class="settings-row">
              <div><strong>运行时Runtime</strong><small>{{ daemonEndpoint }}</small></div>
              <button class="secondary-button" @click="emit('reconnect')">
                <RefreshCw :size="15" /> 重新连接
              </button>
            </div>
          </section>

          <section class="settings-group">
            <div class="settings-row">
              <div>
                <strong>恢复出厂设置</strong>
                <small>清空全局配置、会话数据并重建数据库；不会删除项目源码文件</small>
              </div>
              <button class="danger-button" :disabled="action === 'factory.reset'" @click="factoryReset">
                <LoaderCircle v-if="action === 'factory.reset'" class="spin" :size="15" />
                <RotateCcw v-else :size="15" /> 恢复出厂设置
              </button>
            </div>
          </section>
        </template>

        <template v-else-if="section === 'models'">
          <div class="settings-section-heading">
            <div>
              <h3>模型与提供商</h3>
              <p>{{ activeModel?.ref || '未选择模型' }}</p>
            </div>
            <button class="secondary-button" @click="newProvider">
              <Plus :size="15" /> 模型提供商
            </button>
          </div>

          <section class="settings-group compact-group">
            <div class="settings-row">
              <div><strong>活动模型</strong><small>{{ activeModel ? `${activeModel.contextWindow.toLocaleString()} 上下文` :
                '无可用模型' }}</small></div>
              <div class="row-actions model-actions">
                <SelectMenu v-model="selectedModel" class="settings-select model-select" :options="modelOptions"
                  label="活动模型" :menu-min-width="330" />
                <button class="secondary-button" :disabled="!selectedModel || action === 'model.verify'"
                  @click="verifyModel">
                  <LoaderCircle v-if="action === 'model.verify'" class="spin" :size="13" />
                  <template v-else>加载验证</template>
                </button>
                <button class="secondary-button primary-action" :disabled="!selectedModel"
                  @click="switchModel">使用</button>
              </div>
            </div>
            <div v-if="modelTestState" class="model-verify-state" :class="modelTestState.status" role="status">
              <LoaderCircle v-if="modelTestState.status === 'running'" class="spin" :size="14" />
              <Check v-else-if="modelTestState.status === 'ok'" :size="14" />
              <X v-else :size="14" />
              <span class="model-verify-detail">{{ modelTestState.detail }}</span>
              <button v-if="modelTestState.status === 'fail'" class="secondary-button compact-button"
                @click="verifyModel">
                <RefreshCw :size="13" /> 重试
              </button>
              <button class="icon-button compact model-verify-dismiss" title="收起"
                @click="modelTestState = null">
                <X :size="13" />
              </button>
            </div>
          </section>

          <section v-if="activeModel" class="settings-group" aria-label="模型参数">
            <div class="model-params-header">
              <strong>模型参数</strong>
              <button class="secondary-button compact-button" @click="showModelCatalog = !showModelCatalog">
                {{ showModelCatalog ? '收起全部模型' : `全部模型（${models.length}）` }}
              </button>
            </div>
            <div class="model-params-grid">
              <div class="model-param">
                <small>上下文窗口</small>
                <strong :title="`${activeModel.contextWindow.toLocaleString()} Tokens`">{{ formatTokens(activeModel.contextWindow) }}</strong>
              </div>
              <div class="model-param">
                <small>最大输出</small>
                <strong :title="`${activeModel.maxOutput.toLocaleString()} Tokens`">{{ formatTokens(activeModel.maxOutput) }}</strong>
              </div>
              <div class="model-param">
                <small>别名</small>
                <strong>{{ activeModel.alias || '—' }}</strong>
              </div>
              <div class="model-param">
                <small>提供商</small>
                <strong>{{ activeModel.provider }}</strong>
              </div>
            </div>
            <div v-if="capabilityChips(activeModel).length || activeModel.tags.length" class="model-capabilities">
              <span v-for="cap in capabilityChips(activeModel)" :key="cap" class="inline-badge cap-badge">{{ cap }}</span>
              <span v-for="tag in activeModel.tags" :key="tag" class="inline-badge tag-badge">{{ tag }}</span>
            </div>
            <div v-if="showModelCatalog" class="model-catalog" role="list">
              <div v-for="model in models" :key="model.ref" class="model-catalog-row"
                :class="{ active: model.active, disabled: !model.providerEnabled }" role="listitem">
                <div class="model-catalog-main">
                  <strong :title="model.ref">{{ model.ref }}</strong>
                  <small>{{ model.provider }}<template v-if="model.alias"> · {{ model.alias }}</template></small>
                </div>
                <div class="model-catalog-params">
                  <span class="model-param-chip" title="上下文窗口">{{ formatTokens(model.contextWindow) }} 上下文</span>
                  <span class="model-param-chip" title="最大输出">{{ formatTokens(model.maxOutput) }} 输出</span>
                  <span v-for="cap in capabilityChips(model)" :key="cap" class="inline-badge cap-badge">{{ cap }}</span>
                  <span v-for="tag in model.tags" :key="tag" class="inline-badge tag-badge">{{ tag }}</span>
                  <span v-if="model.active" class="inline-badge active-provider-badge">活动</span>
                  <span v-if="!model.providerEnabled" class="inline-badge disabled-provider-badge">提供商已禁用</span>
                </div>
              </div>
            </div>
          </section>

          <section class="provider-switch-list" aria-label="提供商列表">
            <div v-if="providers.length === 0" class="empty-settings">尚未配置提供商</div>
            <div v-for="provider in providers" :key="provider.id" class="provider-switch-card"
              :class="{ active: provider.active, disabled: !provider.enabled }"
              @click="!provider.active && useProvider(provider)">
              <div class="provider-card-left">
                <GripVertical class="provider-drag-handle" :size="15" />
                <div class="provider-card-content">
                  <div class="provider-card-header">
                    <strong class="provider-card-title">{{ provider.name }}</strong>
                    <span v-if="provider.active" class="inline-badge active-provider-badge">活动</span>
                    <span v-if="!provider.enabled" class="inline-badge disabled-provider-badge">已禁用</span>
                  </div>
                  <div class="provider-card-sub">
                    <span class="provider-url"
                      :title="provider.kind === 'local' ? provider.defaultModelsDirectory : provider.baseUrl">
                      {{ provider.kind === 'local' ? (provider.modelsDirectory || provider.defaultModelsDirectory || '默认模型目录') : provider.baseUrl }}
                    </span>
                    <span class="provider-bullet">·</span>
                    <span class="provider-meta-tag">{{ provider.models.length }} 个模型</span>
                    <span class="provider-bullet">·</span>
                    <span class="provider-meta-tag">{{ provider.kind === 'local' ? '本地 GGUF' : provider.kind === 'anthropic' ? 'Anthropic' : 'OpenAI' }}</span>
                  </div>
                </div>
              </div>

              <div class="provider-card-actions" @click.stop>
                <span v-if="provider.kind !== 'local'" class="key-indicator"
                  :title="provider.apiKeyConfigured ? '已配置 API Key' : '未配置 API Key'">
                  <KeyRound :size="15" :class="provider.apiKeyConfigured ? 'key-set' : 'key-missing'" />
                </span>
                <button class="secondary-button compact-button" :disabled="action === `provider.test:${provider.id}`"
                  @click="testProvider(provider)">
                  <LoaderCircle v-if="action === `provider.test:${provider.id}`" class="spin" :size="13" />
                  <template v-else>测试</template>
                </button>
                <button class="secondary-button compact-button"
                  :disabled="action === `provider.models.fetch:${provider.id}`" @click="fetchProviderModels(provider)">
                  <RefreshCw :size="13" :class="{ spin: action === `provider.models.fetch:${provider.id}` }" />
                  {{ provider.kind === 'local' ? '扫描注册' : '获取模型' }}
                </button>
                <button v-if="!provider.active" class="secondary-button compact-button primary-action"
                  @click="useProvider(provider)">使用</button>
                <button class="icon-button compact" title="编辑提供商与模型" @click="editProvider(provider)">
                  <Settings2 :size="15" />
                </button>
                <button class="icon-button compact danger-icon" title="删除提供商" @click="removeProvider(provider)">
                  <Trash2 :size="15" />
                </button>
              </div>
            </div>
          </section>
        </template>

        <template v-else-if="section === 'mcp'">
          <McpPanel :workspace-path="workspacePath" />
        </template>

        <template v-else-if="section === 'skills'">
          <div class="settings-section-heading">
            <div>
              <h3>技能</h3>
              <p v-if="page !== 'extensions' || skillsView === 'installed'">
                {{ skills.filter((skill) => skill.enabled).length }} 已启用
              </p>
            </div>
            <div class="row-actions">
              <div v-if="page === 'extensions'" class="segmented-control">
                <button :class="{ active: skillsView === 'installed' }" @click="skillsView = 'installed'">已安装</button>
                <button :class="{ active: skillsView === 'market' }" @click="skillsView = 'market'">技能市场</button>
              </div>
              <button class="icon-button" title="刷新" @click="loadCurrentSection">
                <RefreshCw :size="17" />
              </button>
              <button v-if="skillsView === 'installed' || page !== 'extensions'" class="secondary-button"
                :disabled="action === 'skill.import'" @click="importSkills">
                <LoaderCircle v-if="action === 'skill.import'" class="spin" :size="15" />
                <Upload v-else :size="15" />导入技能
              </button>
            </div>
          </div>

          <OfficialSkillsPanel v-if="page === 'extensions' && skillsView === 'market'"
            @changed="loadCurrentSection()" />

          <template v-else>
            <section class="settings-list">
              <div v-if="skills.length === 0" class="empty-settings">尚未发现技能，可导入 .md 或 .zip 文件</div>
              <div v-for="skill in skills" :key="skill.name" class="settings-list-row skill-row">
                <Wrench :size="17" />
                <div class="list-main">
                  <div>
                    <strong :title="skill.name">{{ skill.name }}</strong>
                    <span class="inline-badge">{{ skill.scope === 'workspace' ? '工作区' : '全局' }}</span>
                    <span v-if="skill.version" class="version-text">v{{ skill.version }}</span>
                  </div>
                  <small>{{ skill.description || skill.directory }}</small>
                </div>
                <div class="row-controls">
                  <button class="icon-button compact" title="打开位置" @click="showPath(skill.directory)">
                    <FolderOpen :size="15" />
                  </button>
                  <button class="switch-control" :class="{ active: skill.enabled }"
                    :aria-label="skill.enabled ? '禁用' : '启用'" @click="toggleSkill(skill)"><span /></button>
                </div>
              </div>
            </section>
          </template>
        </template>

        <template v-else-if="section === 'experts'">
          <div class="settings-section-heading">
            <div>
              <h3>专家</h3>
              <p>内置领域专家：一键绑定到新任务，或将提示词粘贴到对话开头</p>
            </div>
          </div>
          <ExpertsPanel @start="(expert) => emit('startExpert', expert)" />
        </template>

        <template v-else-if="section === 'rules-memory'">
          <div class="settings-section-heading">
            <div>
              <h3>规则与记忆</h3>
              <p>管理注入每次会话的工作区规则（AGENTS.md）与长期记忆（MEMORY.md）</p>
            </div>
            <div class="row-actions">
              <button class="secondary-button" @click="emit('openRulesMemory')">
                <ScrollText :size="15" /> 打开编辑器
              </button>
            </div>
          </div>

          <section class="settings-group">
            <label class="provider-enabled-row">
              <span>
                <strong>工作区规则注入</strong>
                <small>启用后，Agent 在每次会话开始时读取层级规则文件（AGENTS.md），子目录规则在涉及该目录的文件时优先；停用后仅对话中的显式指令生效，已保存的规则内容不受影响，重新开启即恢复。</small>
              </span>
              <input v-model="rulesEnabled" class="sr-only" type="checkbox"
                :disabled="action === 'rulesMemory.set'" @change="setRulesMemory({ rulesEnabled })" />
              <span class="toggle-switch" aria-hidden="true"><span /></span>
            </label>
          </section>

          <section class="settings-group">
            <div class="settings-row">
              <div>
                <strong>记忆管理方式</strong>
                <small>自动：Agent 可在会话中把长期事实沉淀到 MEMORY.md；手动：Agent 把记忆视为只读上下文，仅你能在编辑器中维护</small>
              </div>
              <div class="segmented-control">
                <button :class="{ active: memoryMode === 'auto' }" :disabled="action === 'rulesMemory.set'"
                  title="Agent 可更新 MEMORY.md" @click="setRulesMemory({ memoryMode: 'auto' })">自动</button>
                <button :class="{ active: memoryMode === 'manual' }" :disabled="action === 'rulesMemory.set'"
                  title="MEMORY.md 仅用户可编辑" @click="setRulesMemory({ memoryMode: 'manual' })">手动</button>
              </div>
            </div>
          </section>
        </template>

        <template v-else-if="section === 'evolution'">
          <div class="settings-section-heading">
            <div>
              <h3>进化</h3>
              <p>从失败信号中沉淀技能：缺陷聚合 → 反思回合 → 技能草稿 → 人工审阅后生效</p>
            </div>
            <div class="row-actions">
              <button class="icon-button" title="刷新" @click="loadCurrentSection">
                <RefreshCw :size="17" />
              </button>
              <button class="secondary-button" :disabled="action === 'evolution.reflect'" @click="runReflection">
                <LoaderCircle v-if="action === 'evolution.reflect'" class="spin" :size="15" />
                <Telescope v-else :size="15" />立即反思
              </button>
            </div>
          </div>

          <section class="settings-group">
            <div class="settings-row">
              <div>
                <strong>健康概览</strong>
                <small>缺陷信号折算成健康分（0-100）：负反馈与工具反复失败扣分最重，分数越低越值得安排一次反思</small>
              </div>
              <div v-if="evolutionHealth" class="health-summary">
                <span class="health-score"
                  :class="evolutionHealth.score >= 90 ? 'good' : evolutionHealth.score >= 60 ? 'fair' : 'poor'">
                  {{ evolutionHealth.score }}
                </span>
                <span class="health-grade">{{ evolutionHealth.grade }}</span>
              </div>
            </div>
            <div v-if="evolutionTrend.length > 0" class="trend-bars">
              <div v-for="point in evolutionTrend" :key="point.date" class="trend-bar-item" :title="trendTitle(point)">
                <div class="trend-bar" :class="{ empty: trendTotal(point) === 0 }"
                  :style="{ height: `${Math.max(4, Math.round((trendTotal(point) / trendMax(evolutionTrend)) * 56))}px` }" />
                <span class="trend-bar-label">{{ point.date.slice(5) }}</span>
              </div>
            </div>
            <div v-if="Object.keys(evolutionByKind).length > 0" class="kind-chips">
              <span v-for="(count, kind) in evolutionByKind" :key="kind" class="inline-badge">
                {{ defectKindLabel(String(kind)) }} · {{ count }}
              </span>
            </div>
          </section>

          <section class="settings-group">
            <label class="provider-enabled-row">
              <span>
                <strong>定时反思</strong>
                <small>按固定间隔在后台运行反思回合：复盘最近的失败信号并起草技能。技能草稿始终等待人工采纳，不会自动生效。</small>
              </span>
              <input v-model="evolutionAutoReflect" class="sr-only" type="checkbox"
                :disabled="action === 'evolution.config'"
                @change="setEvolutionConfig({ autoReflect: evolutionAutoReflect })" />
              <span class="toggle-switch" aria-hidden="true"><span /></span>
            </label>
            <div class="settings-row">
              <div>
                <strong>反思间隔</strong>
                <small>定时反思的触发周期，当前每 {{ formatInterval(evolutionIntervalMinutes) }} 复盘一次</small>
              </div>
              <div class="segmented-control">
                <button v-for="option in EVOLUTION_INTERVAL_OPTIONS" :key="option.value"
                  :class="{ active: evolutionIntervalMinutes === option.value }"
                  :disabled="action === 'evolution.config'"
                  @click="setEvolutionConfig({ intervalMinutes: option.value })">{{ option.label }}</button>
              </div>
            </div>
            <label class="provider-enabled-row">
              <span>
                <strong>阈值反思</strong>
                <small>与定时反思并存：待处理的缺陷信号达到阈值即提前触发反思回合（两次自动反思至少间隔 30 分钟），适合故障频发时加快进化节奏。</small>
              </span>
              <input v-model="evolutionThresholdEnabled" class="sr-only" type="checkbox"
                :disabled="action === 'evolution.config'"
                @change="setEvolutionConfig({ thresholdEnabled: evolutionThresholdEnabled })" />
              <span class="toggle-switch" aria-hidden="true"><span /></span>
            </label>
            <div v-if="evolutionThresholdEnabled" class="settings-row">
              <div>
                <strong>触发阈值</strong>
                <small>待处理的缺陷信号达到 {{ evolutionThresholdSignals }} 条即自动反思</small>
              </div>
              <input class="text-input threshold-input" type="number" min="1" max="50"
                :value="evolutionThresholdSignals" :disabled="action === 'evolution.config'"
                @change="setEvolutionConfig({ thresholdSignals: Number(($event.target as HTMLInputElement).value) })" />
            </div>
          </section>

          <section class="settings-group">
            <div class="settings-row">
              <div>
                <strong>缺陷信号（{{ evolutionDefects.length }}）</strong>
                <small>最近 {{ evolutionScanned }} 条运行事件聚合出的可改进点；「高」严重度的信号值得优先关注</small>
              </div>
            </div>
            <div v-if="evolutionDefects.length === 0" class="empty-settings">暂无缺陷信号——保持这样很好</div>
            <div v-for="report in evolutionDefects" :key="report.fingerprint" class="settings-list-row">
              <Wrench :size="17" />
              <div class="list-main">
                <div>
                  <strong>{{ defectKindLabel(report.kind) }}</strong>
                  <span class="inline-badge" :class="severityClass(report.severity)">{{ severityLabel(report.severity) }}</span>
                  <span v-if="report.occurrences > 1" class="inline-badge">{{ report.occurrences }} 次</span>
                </div>
                <small :title="report.errorSummary ?? ''">{{ report.errorSummary || report.toolName || report.fingerprint }}</small>
              </div>
              <span class="version-text" :title="report.lastSeen">{{ relativeTime(report.lastSeen) }}</span>
            </div>
          </section>

          <section class="settings-group">
            <div class="settings-row">
              <div>
                <strong>待审技能草稿（{{ evolutionCandidates.length }}）</strong>
                <small>反思回合产出的候选技能等待人工裁决：可预览草稿文件、采纳前改写提示词，或暂缓 / 丢弃</small>
              </div>
            </div>
            <div v-if="evolutionCandidates.length === 0" class="empty-settings">没有待审的技能草稿</div>
            <div v-for="candidate in evolutionCandidates" :key="candidate.fingerprint" class="candidate-block">
              <div class="settings-list-row">
                <Telescope :size="17" />
                <div class="list-main">
                  <div>
                    <strong :title="candidate.skillName">{{ candidate.skillName }}</strong>
                    <span v-if="candidate.status === 'deferred'" class="inline-badge">暂缓</span>
                  </div>
                  <small :title="candidate.summary ?? ''">{{ candidate.summary || '反思产出候选技能' }}</small>
                </div>
                <div class="row-controls">
                  <button class="secondary-button" :disabled="action.startsWith('evolution.decide')"
                    @click="expandedCandidateFingerprint === candidate.fingerprint
                      ? collapseDraftWorkbench()
                      : expandDraftWorkbench(candidate)">
                    {{ expandedCandidateFingerprint === candidate.fingerprint ? '收起' : '查看草稿' }}
                  </button>
                  <button v-if="candidate.status !== 'deferred'" class="icon-button compact" title="暂缓：保留草稿稍后审阅"
                    :disabled="action.startsWith('evolution.decide')"
                    @click="decideEvolutionCandidate(candidate, 'defer')">
                    <Clock :size="15" />
                  </button>
                  <button class="icon-button compact" title="丢弃该草稿"
                    :disabled="action.startsWith('evolution.decide')"
                    @click="decideEvolutionCandidate(candidate, 'reject')">
                    <X :size="15" />
                  </button>
                </div>
              </div>
              <div v-if="expandedCandidateFingerprint === candidate.fingerprint" class="draft-workbench">
                <div v-if="(candidate.files?.length ?? 0) > 0" class="draft-file-tabs" role="tablist">
                  <button v-for="file in candidate.files" :key="file.name" role="tab"
                    :class="{ active: activeDraftFile(candidate)?.name === file.name }"
                    @click="selectDraftFile(candidate, file.name)">{{ file.name }}</button>
                </div>
                <pre v-if="activeDraftFile(candidate)" class="draft-file-preview">{{ activeDraftFile(candidate)?.content }}</pre>
                <div class="draft-prompt-editor">
                  <label class="field-label" for="draft-prompt-rewrite">提示词改写（可选）</label>
                  <textarea id="draft-prompt-rewrite" v-model="promptDraft" class="text-input" rows="5"
                    :placeholder="candidate.files?.some((file) => file.name === 'prompt.txt')
                      ? '修改后点「保存并采纳」，改写内容会替换草稿的 prompt.txt'
                      : '该草稿没有 prompt.txt 文件'"
                    @input="promptDirty = true" />
                </div>
                <div class="draft-workbench-actions">
                  <button class="primary-button" :disabled="action.startsWith('evolution.decide')"
                    @click="decideEvolutionCandidate(candidate, 'adopt', promptOverrideFor())">
                    <Check :size="14" /> {{ promptDirty ? '保存并采纳' : '采纳' }}
                  </button>
                </div>
              </div>
            </div>
          </section>

          <section class="settings-group">
            <div class="settings-row">
              <div>
                <strong>反思历史</strong>
                <small>每次反思回合的触发方式与处理结果：手动、定时或缺陷阈值触发</small>
              </div>
            </div>
            <div v-if="evolutionHistory.length === 0" class="empty-settings">还没有反思回合记录</div>
            <div v-for="run in evolutionHistory" :key="run.id" class="settings-list-row">
              <History :size="17" />
              <div class="list-main">
                <div>
                  <strong>{{ triggerLabel(run.trigger) }}</strong>
                  <span class="inline-badge">处理 {{ run.processed }}</span>
                  <span v-if="run.candidates > 0" class="inline-badge">产出 {{ run.candidates }}</span>
                  <span v-if="run.failed > 0" class="inline-badge severity-high">失败 {{ run.failed }}</span>
                </div>
                <small v-if="run.error">{{ run.error }}</small>
              </div>
              <span class="version-text" :title="run.createdAt">{{ relativeTime(run.createdAt) }}</span>
            </div>
          </section>

          <section class="settings-group">
            <div class="settings-row">
              <div>
                <strong>效果追踪</strong>
                <small>已采纳技能的实际使用次数，以及对应缺陷是否复发——衡量进化是否真正解决了问题</small>
              </div>
            </div>
            <div v-if="!evolutionStats" class="empty-settings">暂无统计数据</div>
            <template v-else>
              <div class="stats-summary">
                <span class="inline-badge">反思 {{ evolutionStats.runs }}</span>
                <span class="inline-badge">草稿 {{ evolutionStats.candidatesProduced }}</span>
                <span class="inline-badge">采纳 {{ evolutionStats.adopted }}</span>
                <span class="inline-badge">拒绝 {{ evolutionStats.rejected }}</span>
                <span class="inline-badge">暂缓 {{ evolutionStats.deferred }}</span>
                <span class="inline-badge">采纳率 {{ evolutionStats.adoptionRate }}%</span>
              </div>
              <div v-if="evolutionStats.skills.length === 0" class="empty-settings">还没有已采纳的进化技能</div>
              <div v-for="skill in evolutionStats.skills" :key="skill.fingerprint" class="settings-list-row">
                <Zap :size="17" />
                <div class="list-main">
                  <div>
                    <strong>{{ skill.skillName }}</strong>
                    <span class="inline-badge">使用 {{ skill.usageCount }} 次</span>
                  </div>
                  <small>缺陷类型：{{ defectKindLabel(skill.kind) }} · 采纳于 {{ formatDateOnly(skill.adoptedAt) }}</small>
                </div>
                <span class="version-text" :class="{ unresolved: !skill.resolved }">
                  {{ skill.resolved ? '缺陷未复发' : '缺陷有复发' }}
                </span>
              </div>
            </template>
          </section>
        </template>

        <template v-else-if="section === 'diagnostics'">
          <div class="settings-section-heading">
            <div>
              <h3>诊断与用量</h3>
              <p>运行时健康体检与智能体调用效能看板</p>
            </div>
            <div class="row-actions">
              <div class="days-segmented-filter">
                <button type="button" class="filter-chip" :class="{ active: usageDays === 7 }" @click="usageDays = 7">
                  近 7 天
                </button>
                <button type="button" class="filter-chip" :class="{ active: usageDays === 14 }" @click="usageDays = 14">
                  近 14 天
                </button>
                <button type="button" class="filter-chip" :class="{ active: usageDays === 30 }" @click="usageDays = 30">
                  近 30 天
                </button>
              </div>
              <button class="secondary-button" :disabled="loading" @click="loadDiagnostics">
                <RefreshCw :size="15" :class="{ spin: loading }" /> 重新检查
              </button>
            </div>
          </div>

          <!-- KPI Metric Cards (No Cost) -->
          <section class="usage-kpi-grid">
            <div class="kpi-card">
              <div class="kpi-icon-badge kpi-indigo">
                <Gauge :size="18" />
              </div>
              <div class="kpi-data">
                <span class="kpi-title">总调用量</span>
                <div class="kpi-main-metric">
                  <strong>{{ totalUsage.calls.toLocaleString() }}</strong>
                  <span class="kpi-tag" :class="{ 'tag-success': totalUsage.successRate >= 0.95 }">
                    {{ Math.round(totalUsage.successRate * 100) }}% 成功
                  </span>
                </div>
                <small class="kpi-subtext">异常失败 {{ totalUsage.failures }} 次</small>
              </div>
            </div>

            <div class="kpi-card">
              <div class="kpi-icon-badge kpi-purple">
                <Activity :size="18" />
              </div>
              <div class="kpi-data">
                <span class="kpi-title">消耗 Tokens</span>
                <div class="kpi-main-metric">
                  <strong>{{ totalUsage.tokens.toLocaleString() }}</strong>
                </div>
                <small class="kpi-subtext">输入 + 输出累计上下文处理量</small>
              </div>
            </div>

            <div class="kpi-card">
              <div class="kpi-icon-badge kpi-teal">
                <Zap :size="18" />
              </div>
              <div class="kpi-data">
                <span class="kpi-title">缓存节省效率</span>
                <div class="kpi-main-metric">
                  <strong>{{ totalUsage.cacheEfficiency }}%</strong>
                  <span class="kpi-tag tag-cyan">Prompt Cache</span>
                </div>
                <small class="kpi-subtext">
                  命中 {{ totalUsage.cachedTokens.toLocaleString() }} Tokens
                </small>
              </div>
            </div>

            <div class="kpi-card">
              <div class="kpi-icon-badge kpi-amber">
                <Clock :size="18" />
              </div>
              <div class="kpi-data">
                <span class="kpi-title">平均响应延迟</span>
                <div class="kpi-main-metric">
                  <strong>{{ totalUsage.avgLatencyMs }}</strong>
                  <span class="kpi-unit">ms</span>
                </div>
                <small class="kpi-subtext">端到端网络与生成延迟均值</small>
              </div>
            </div>
          </section>

          <!-- Visual Charts Dashboard -->
          <section class="usage-charts-dashboard">
            <UsageTrendChart :data="timeline" />
            <UsageModelBarChart :items="usage" />
          </section>

          <!-- System Doctor Diagnostics -->
          <div class="diagnostics-sub-heading">
            <div>
              <h4>系统健康体检</h4>
              <small>{{checks.filter(c => c.ok).length}} / {{ checks.length }} 项检查通过</small>
            </div>
          </div>

          <section class="settings-group diagnostic-list">
            <div v-for="check in checks" :key="check.name" class="settings-row">
              <div><strong>{{ check.name }}</strong><small>{{ check.detail }}</small></div>
              <span class="check-status" :class="{ ok: check.ok }">
                <Check v-if="check.ok" :size="15" />
                <X v-else :size="15" />
                {{ check.ok ? '正常' : '异常' }}
              </span>
            </div>
          </section>

          <!-- Model Usage Detail Table -->
          <div v-if="usage.length > 0" class="diagnostics-sub-heading">
            <div>
              <h4>模型用量明细</h4>
              <small>共 {{ usage.length }} 个活跃模型配置记录</small>
            </div>
          </div>

          <section v-if="usage.length > 0" class="usage-table-wrap">
            <table class="usage-table">
              <thead>
                <tr>
                  <th style="text-align: left;">模型</th>
                  <th>调用</th>
                  <th>成功率</th>
                  <th>缓存命中</th>
                  <th>普通输入</th>
                  <th>生成输出</th>
                  <th>总 Tokens</th>
                  <th>平均延迟</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="item in usage" :key="`${item.provider}/${item.model}`">
                  <td style="text-align: left;">
                    <div class="model-row-identity">
                      <strong>{{ item.model }}</strong>
                      <small>{{ item.provider }}</small>
                    </div>
                  </td>
                  <td>{{ item.calls.toLocaleString() }}</td>
                  <td>{{ Math.round(item.successRate * 100) }}%</td>
                  <td>
                    <span class="cache-rate">{{ cacheHitRate(item) }}%</span>
                    <small v-if="item.cachedInputTokens" class="table-sub-detail">
                      命中 {{ item.cachedInputTokens.toLocaleString() }}
                    </small>
                  </td>
                  <td>{{ Math.max(0, promptInputTokens(item) - (item.cachedInputTokens ?? 0)).toLocaleString() }}</td>
                  <td>{{ item.outputTokens.toLocaleString() }}</td>
                  <td>
                    <strong class="total-tokens-cell">
                      {{ (promptInputTokens(item) + item.outputTokens).toLocaleString() }}
                    </strong>
                  </td>
                  <td>{{ Math.round(item.avgLatencyMs) }} ms</td>
                </tr>
              </tbody>
            </table>
          </section>
        </template>

        <template v-else-if="section === 'inference'">
          <div class="settings-section-heading">
            <div>
              <h3>推理加速</h3>
            </div>
          </div>

          <section v-if="localInferenceAvailable" class="settings-group">
            <div class="settings-group-title">
              <strong>本地推理加速（CUDA）</strong>
              <small>作用于本地 GGUF 模型提供商「{{ localInference.providerId }}」；GPU 卸载需要 CUDA 后端运行时
                (LLamaSharp.Backend.Cuda12)，仅 CPU 后端时这些选项将被忽略或效果有限。</small>
            </div>
            <div class="local-inference-grid">
              <label>
                <FieldLabel en="GPU Offload" zh="GPU 层卸载 (GPU Offload)"
                  help="卸载到 GPU 的 Transformer 层数（0-999）。0 = 仅 CPU 推理；数值越大显存占用越高、速度越快。" />
                <input v-model.number="localInference.gpuLayers" type="number" min="0" max="999" step="1" />
              </label>
              <label>
                <FieldLabel en="Context Length" zh="上下文长度 (Context Length)"
                  help="推理上下文窗口大小（512-32768 token）。越大可处理越长的对话，KV 缓存显存占用也成比例增长。" />
                <input v-model.number="localInference.contextLength" type="number" min="512" max="32768" step="512" />
              </label>
              <label>
                <FieldLabel en="KV Cache Quantization" zh="KV 缓存量化"
                  help="量化 KV 缓存以降低显存占用；需要先开启 Flash Attention，否则保持 f16 不生效。" />
                <SelectMenu v-model="localInference.kvCacheQuantization" class="settings-select"
                  label="KV 缓存量化" :options="kvQuantOptions" :disabled="action === 'advanced.set:localInference'" />
              </label>
            </div>
            <label class="provider-enabled-row">
              <span>
                <strong>Flash Attention（注意力加速）</strong>
                <small>启用 llama.cpp flash attention 内核加速注意力计算，并解锁 KV 缓存量化；建议 GPU 推理时开启。</small>
              </span>
              <input v-model="localInference.flashAttention" class="sr-only" type="checkbox"
                :disabled="action === 'advanced.set:localInference'" />
              <span class="toggle-switch" aria-hidden="true"><span /></span>
            </label>
            <button class="secondary-button primary-action save-inference-button"
              :disabled="action === 'advanced.set:localInference'" @click="saveLocalInference">
              {{ action === 'advanced.set:localInference' ? '保存中…' : '保存本地推理设置' }}
            </button>

            <div v-if="inferencePresets.length > 0" class="inference-presets">
              <div class="inference-presets-header">
                <strong>常用配置</strong>
                <small>点击应用，保存后对本地推理生效</small>
              </div>
              <div class="inference-preset-chips">
                <span v-for="preset in inferencePresets" :key="preset.name" class="inference-preset-chip">
                  <button type="button" class="preset-apply" :title="`GPU ${preset.gpuLayers} 层 · 上下文 ${preset.contextLength}`"
                    @click="applyInferencePreset(preset)">
                    {{ preset.name }}
                  </button>
                  <button type="button" class="preset-remove" :title="`删除预设 ${preset.name}`"
                    @click="removeInferencePreset(preset)">
                    <X :size="12" />
                  </button>
                </span>
              </div>
            </div>
            <div class="inference-preset-save">
              <input v-model="presetNameInput" placeholder="将当前参数存为预设，例如：GPU 全量卸载" @keydown.enter.prevent="saveInferencePreset" />
              <button type="button" class="secondary-button compact-button" @click="saveInferencePreset">
                <Plus :size="13" /> 存为预设
              </button>
            </div>
          </section>

          <section v-else class="settings-group">
            <label class="provider-enabled-row">
              <span>
                <strong>暂无可配置的本地模型提供商</strong>
                <small>推理加速参数仅对本地 GGUF 模型提供商生效；请先在「模型与提供商」中添加 kind 为
                  local 的提供商并注册 GGUF 模型，此区域将自动出现。</small>
              </span>
            </label>
          </section>
        </template>

        <template v-else-if="section === 'agent'">
          <div class="settings-section-heading">
            <div>
              <h3>智能体</h3>
              <p>配置智能体的模型选择与自主行为边界</p>
            </div>
          </div>

          <section class="settings-group" aria-label="模型">
            <div class="settings-row">
              <div><strong>根模型</strong><small>智能体日常对话与任务执行使用的活动模型</small></div>
              <div class="row-actions model-actions">
                <SelectMenu v-model="selectedModel" class="settings-select model-select" :options="modelOptions"
                  label="根模型" :menu-min-width="330" />
                <button class="secondary-button primary-action" :disabled="!selectedModel || action === 'model.switch'"
                  @click="switchModel">使用</button>
              </div>
            </div>
            <div class="settings-row">
              <div><strong>视觉子智能体模型</strong><small>处理图像回合（聊天附件、屏幕截图）的模型；仅列出支持视觉能力的模型，自动 = 使用路由链中第一个支持视觉的模型</small></div>
              <div class="row-actions model-actions">
                <SelectMenu v-model="agentConfig.visionModel" class="settings-select model-select"
                  :options="visionModelOptions" label="视觉子智能体模型" :menu-min-width="330" />
                <button class="secondary-button primary-action" :disabled="action === 'agent.config.set:vision'"
                  @click="setVisionModel">保存</button>
              </div>
            </div>
          </section>

          <section class="settings-group" aria-label="行为">
            <label class="provider-enabled-row">
              <span>
                <strong>网页搜索</strong>
                <small>允许智能体使用网页搜索 (web_search) 与网页抓取 (web_fetch)
                  等网络工具；关闭后所有任务强制处于离线模式，禁止外部网络请求。</small>
              </span>
              <input v-model="networkEnabled" class="sr-only" type="checkbox"
                :disabled="action === 'agent.config.set:network'" @change="toggleAgentNetwork" />
              <span class="toggle-switch" aria-hidden="true"><span /></span>
            </label>
            <label class="provider-enabled-row">
              <span>
                <strong>探索者智能体</strong>
                <small>允许智能体通过 delegate_task 把独立子任务委派给子智能体并行探索；
                  关闭后 delegate_task 将从工具列表隐藏，所有子任务由主智能体自行完成。</small>
              </span>
              <input v-model="agentConfig.delegationEnabled" class="sr-only" type="checkbox"
                :disabled="action === 'agent.config.set:delegation'" @change="toggleDelegation" />
              <span class="toggle-switch" aria-hidden="true"><span /></span>
            </label>
          </section>

          <section class="settings-group" aria-label="自动验证">
            <label class="provider-enabled-row">
              <span>
                <strong>自动验证与修复</strong>
                <small>项目任务修改文件后自动运行构建/测试验证命令，失败时把错误反馈给智能体修复。</small>
              </span>
              <input v-model="agentConfig.autoVerify" class="sr-only" type="checkbox"
                :disabled="action === 'agent.config.set:autoVerify'" @change="toggleAgentAutoVerify" />
              <span class="toggle-switch" aria-hidden="true"><span /></span>
            </label>
          </section>
        </template>

        <template v-else-if="section === 'advanced'">
          <div class="settings-section-heading">
            <div>
              <h3>高级设置</h3>
            </div>
          </div>

          <section class="settings-group">
            <label class="provider-enabled-row">
              <span>
                <strong>全局网络访问（联网搜索与抓取）</strong>
                <small>允许 Agent 使用网络搜索 (web_search) 与网页抓取 (web_fetch)
                  等网络工具。默认对所有项目开启；若关闭，所有项目和任务将强制处于离线模式，禁止外部网络请求。</small>
              </span>
              <input v-model="networkEnabled" class="sr-only" type="checkbox"
                :disabled="action === 'advanced.set:network'" @change="toggleNetworkEnabled" />
              <span class="toggle-switch" aria-hidden="true"><span /></span>
            </label>
          </section>

          <section class="settings-group">
            <div class="provider-enabled-row">
              <span>
                <strong>允许的网站（外部网站访问）</strong>
                <small v-if="allowedSitesCount && allowedSitesCount > 0">已允许 {{ allowedSitesCount }} 个网站。仍可浏览本地主机。web_fetch 将按白名单拦截列表外的站点。</small>
                <small v-else>未设置白名单：当前不限制外部网站访问。添加条目后，仅列表中的站点与本地主机可访问。</small>
              </span>
              <button class="secondary-button" type="button" @click="allowedSitesOpen = true">管理…</button>
            </div>
          </section>

          <section class="settings-group">
            <label class="provider-enabled-row">
              <span>
                <strong>电脑操作智能体 (Computer Use)</strong>
                <small>允许 Agent 获得跨平台桌面直接操作能力（点击、输入、移动、滚动、快捷键与原生窗口UI元素解析）。内置双层故障沙盒与熔断防护，安全解耦，零残留。</small>
              </span>
              <input v-model="computerUseEnabled" class="sr-only" type="checkbox"
                :disabled="action === 'advanced.set:computerUse'" @change="toggleComputerUse" />
              <span class="toggle-switch" aria-hidden="true"><span /></span>
            </label>
          </section>

          <section class="settings-group">
            <label class="provider-enabled-row">
              <span>
                <strong>网络运维插件 (Network Ops)</strong>
                <small>允许 Agent 运行网络诊断与运维命令：一键诊断（接口 / 路由 / 连接 / ARP / DNS）、ping、traceroute、netstat、nslookup、netsh、ip 等全命令集（Windows 与 Linux 自动映射）。命令经白名单目录执行，参数不经 Shell，杜绝注入。</small>
              </span>
              <input v-model="networkOpsEnabled" class="sr-only" type="checkbox"
                :disabled="action === 'advanced.set:networkOps'" @change="toggleNetworkOps" />
              <span class="toggle-switch" aria-hidden="true"><span /></span>
            </label>
            <label v-if="networkOpsEnabled" class="provider-enabled-row">
              <span>
                <strong>允许变更类网络命令</strong>
                <small>关闭后仅允许只读查询（ping、netstat、nslookup 等）；route add、netsh set、DNS 缓存刷新等变更命令将被拒绝。</small>
              </span>
              <input v-model="networkOpsAllowMutating" class="sr-only" type="checkbox"
                :disabled="action === 'advanced.set:networkOpsMutating'" @change="toggleNetworkOpsMutating" />
              <span class="toggle-switch" aria-hidden="true"><span /></span>
            </label>
          </section>


          <section class="settings-group">
            <label class="provider-enabled-row">
              <span>
                <strong>自动切换其他模型（故障转移）</strong>
                <small>激活模型请求失败时自动尝试路由链中的其他模型；关闭后失败即停止，并显示真实错误</small>
              </span>
              <input v-model="failoverEnabled" class="sr-only" type="checkbox" :disabled="action === 'routing.set'"
                @change="toggleFailover" />
              <span class="toggle-switch" aria-hidden="true"><span /></span>
            </label>
            <label class="provider-enabled-row">
              <span>
                <strong>优化 DeepSeek 模型</strong>
                <small>针对 DeepSeek 启用思维链选择性回传、空响应重试与空工具结果兜底；策略集中管理，可随模型变化调整</small>
              </span>
              <input v-model="deepSeekOptimizationEnabled" class="sr-only" type="checkbox"
                :disabled="action === 'routing.set:deepseek'" @change="toggleDeepSeekOptimization" />
              <span class="toggle-switch" aria-hidden="true"><span /></span>
            </label>
          </section>
        </template>

      </div>
    </main>
  </section>
  <Teleport to="body">
    <Transition name="global-toast">
      <div v-if="open && (error || notice)" class="global-toast-layer" role="status"
        :aria-live="error ? 'assertive' : 'polite'">
        <div class="global-toast" :class="{ error: error, success: !error && notice }">
          <X v-if="error" :size="15" />
          <Check v-else :size="15" />
          <span>{{ error || notice }}</span>
        </div>
      </div>
    </Transition>
  </Teleport>
  <ProviderEditorDialog :open="providerEditorOpen" :editing-id="editingProviderId" :value="providerForm"
    :saving="action === 'provider.save'" :error="providerEditorOpen ? error : ''"
    :default-models-directory="providers.find((provider) => provider.kind === 'local')?.defaultModelsDirectory"
    @close="providerEditorOpen = false" @save="saveProvider" />
  <AllowedSitesDialog :open="allowedSitesOpen" @close="() => { allowedSitesOpen = false; if (section === 'advanced') void loadCurrentSection() }" />
</template>
