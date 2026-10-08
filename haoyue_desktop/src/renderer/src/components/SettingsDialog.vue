<script setup lang="ts">
import {
  Activity,
  ArrowLeft,
  Blocks,
  Bot,
  Brain,
  BrainCircuit,
  Check,
  Circle,
  Clock,
  FolderOpen,
  Gauge,
  GitBranch,
  Globe,
  GripVertical,
  KeyRound,
  LoaderCircle,
  Moon,
  Monitor,
  Plug,
  Plus,
  RefreshCw,
  RotateCcw,
  Save,
  Search,
  Settings2,
  SlidersHorizontal,
  ScrollText,
  Sun,
  Trash2,
  Upload,
  Wrench,
  X,
  Zap
} from '@lucide/vue'
import { computed, defineComponent, h, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { confirmAction } from '../confirmation'
import {
  MCP_PRESETS,
  buildMcpTogglePayload,
  mcpStatusText,
  transportLabel,
  type McpFormValue,
  type McpPreset,
  type McpScope,
  type McpServerSummary
} from '../mcp-form'
import McpEditorDialog from './McpEditorDialog.vue'
import type { ModelDetailConfig } from './ModelConfigModal.vue'
import FieldLabel from './FieldLabel.vue'
import ProviderEditorDialog from './ProviderEditorDialog.vue'
import SelectMenu from './SelectMenu.vue'
import UsageTrendChart, { type TimelinePoint } from './UsageTrendChart.vue'
import UsageModelBarChart from './UsageModelBarChart.vue'

type SettingsSection = 'general' | 'models' | 'mcp' | 'skills' | 'rules-memory' | 'diagnostics' | 'inference' | 'advanced'

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

type McpServerInfo = McpServerSummary

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
  openWorkspace: []
  openOfficialSkills: []
  openRulesMemory: []
  runtimeChanged: []
}>()

const section = ref<SettingsSection>('general')
const loading = ref(false)
const networkEnabled = ref(true)
const failoverEnabled = ref(true)
const deepSeekOptimizationEnabled = ref(false)
const computerUseEnabled = ref(false)
const computerUseDriver = ref('auto')
const replyLanguage = ref<'auto' | 'zh' | 'en'>('auto')
const rulesEnabled = ref(true)
const memoryMode = ref<'auto' | 'manual'>('auto')

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
const mcpServers = ref<McpServerInfo[]>([])
const skills = ref<SkillInfo[]>([])
const checks = ref<HealthCheck[]>([])
const usage = ref<UsageInfo[]>([])
const usageDays = ref<number>(14)
const timeline = ref<TimelinePoint[]>([])
const selectedModel = ref('')
const providerEditorOpen = ref(false)
const mcpEditorOpen = ref(false)
const editingMcpServer = ref<McpServerSummary | null>(null)
const mcpPreset = ref<McpFormValue | null>(null)
const mcpDialogError = ref('')
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

const showModelCatalog = ref(false)

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
  { id: 'mcp', label: 'MCP', icon: Blocks },
  { id: 'skills', label: '技能', icon: Wrench },
  { id: 'rules-memory', label: '规则与记忆', icon: ScrollText },
  { id: 'diagnostics', label: '诊断与用量', icon: Activity },
  { id: 'inference', label: '推理加速', icon: Gauge },
  { id: 'advanced', label: '高级设置', icon: SlidersHorizontal }
]

/** GitHub brand mark; lucide no longer ships brand icons. */
const GithubMark = defineComponent({
  props: { size: { type: Number, default: 15 } },
  setup: (props) => () => h('svg', {
    viewBox: '0 0 16 16',
    width: props.size,
    height: props.size,
    fill: 'currentColor',
    'aria-hidden': 'true'
  }, [h('path', {
    d: 'M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27s1.36.09 2 .27c1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.01 8.01 0 0 0 16 8c0-4.42-3.58-8-8-8Z'
  })])
})

/** Per-preset icons shown on the MCP one-click template buttons. */
const mcpPresetIcons: Record<string, unknown> = {
  github: GithubMark,
  filesystem: FolderOpen,
  fetch: Globe,
  memory: Brain,
  'sequential-thinking': BrainCircuit,
  git: GitBranch
}

const pageTitle = computed(() => props.page === 'extensions' ? 'MCP 与技能' : '设置')
const visibleSections = computed(() =>
  props.page === 'extensions'
    ? sections.filter((item) => item.id === 'mcp' || item.id === 'skills')
    : sections)

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
  loading.value = true
  error.value = ''
  notice.value = ''
  try {
    if (section.value === 'general') await loadGeneral()
    if (section.value === 'models') await loadModels()
    if (section.value === 'mcp') mcpServers.value = await requestJson<McpServerInfo[]>('mcp.list')
    if (section.value === 'skills') skills.value = await requestJson<SkillInfo[]>('skill.list')
    if (section.value === 'rules-memory') await loadRulesMemory()
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

async function loadAdvanced(): Promise<void> {
  const config = await requestJson<{
    networkEnabled: boolean
    failoverEnabled: boolean
    deepSeekOptimizationEnabled: boolean
    computerUseEnabled?: boolean
    computerUseDriver?: string
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

// Saving or toggling an MCP server reconnects every enabled server, so allow
// headroom for slow servers while still guaranteeing the UI never waits forever.
const MCP_ADMIN_TIMEOUT_MS = 120_000

/** Rows whose toggle request is still in flight; the switch shows progress at once. */
const pendingMcpToggles = ref<string[]>([])

function mcpServerKey(server: { scope: string; name: string }): string {
  return `${server.scope}:${server.name}`
}

function mcpServerPending(server: { scope: string; name: string; connecting?: boolean }): boolean {
  return server.connecting === true || pendingMcpToggles.value.includes(mcpServerKey(server))
}

function newMcpServer(): void {
  editingMcpServer.value = null
  mcpPreset.value = null
  mcpDialogError.value = ''
  mcpEditorOpen.value = true
}

/** Opens the editor prefilled from a one-click preset; credentials stay user-provided. */
function newPresetMcpServer(preset: McpPreset): void {
  editingMcpServer.value = null
  mcpPreset.value = preset.createForm({ workspacePath: props.workspacePath })
  mcpDialogError.value = ''
  mcpEditorOpen.value = true
}

function editMcpServer(server: McpServerInfo): void {
  editingMcpServer.value = server
  mcpDialogError.value = ''
  mcpEditorOpen.value = true
}

function closeMcpEditor(): void {
  mcpEditorOpen.value = false
  editingMcpServer.value = null
  mcpPreset.value = null
  mcpDialogError.value = ''
}

async function saveMcpServer(payload: { name: string; scope: McpScope; server: Record<string, unknown> }): Promise<void> {
  beginAction('mcp.save')
  mcpDialogError.value = ''
  try {
    mcpServers.value = await requestJson<McpServerInfo[]>('mcp.upsert', payload, MCP_ADMIN_TIMEOUT_MS)
    closeMcpEditor()
    notice.value = 'MCP 配置已保存并重载'
  } catch (reason) {
    mcpDialogError.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    endAction()
  }
}

async function toggleMcp(server: McpServerInfo): Promise<void> {
  const key = mcpServerKey(server)
  beginAction(`mcp.toggle:${server.name}`)
  pendingMcpToggles.value = [...pendingMcpToggles.value, key]
  try {
    mcpServers.value = await requestJson<McpServerInfo[]>('mcp.upsert', {
      name: server.name,
      scope: server.scope,
      server: buildMcpTogglePayload(server)
    }, MCP_ADMIN_TIMEOUT_MS)
  } catch (reason) {
    fail(reason)
  } finally {
    pendingMcpToggles.value = pendingMcpToggles.value.filter((entry) => entry !== key)
    endAction()
  }
}

async function removeMcp(server: McpServerInfo): Promise<void> {
  if (!await confirmAction({
    title: '删除 MCP 服务器', message: `删除 MCP 服务器 “${server.name}”？`, confirmLabel: '删除', danger: true
  })) return
  beginAction('mcp.remove')
  try {
    mcpServers.value = await requestJson<McpServerInfo[]>(
      'mcp.remove', { name: server.name, scope: server.scope }, MCP_ADMIN_TIMEOUT_MS)
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function reloadMcp(): Promise<void> {
  beginAction('mcp.reload')
  try {
    mcpServers.value = await requestJson<McpServerInfo[]>('mcp.reload', {}, MCP_ADMIN_TIMEOUT_MS)
    notice.value = mcpServers.value.some((server) => server.connecting) ? 'MCP 正在重新加载…' : 'MCP 已重新加载'
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
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
  if (props.page === 'extensions') return value === 'skills' ? 'skills' : 'mcp'
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
watch(section, () => { void loadCurrentSection() })

/**
 * Enabling a server returns immediately and connects in the background, so the
 * runtime broadcasts `mcp.updated` once the real status is known.
 */
async function refreshMcpServers(): Promise<void> {
  if (!props.open || section.value !== 'mcp') return
  if (action.value.startsWith('mcp.')) return // never clobber an in-flight mutation
  try {
    mcpServers.value = await requestJson<McpServerInfo[]>('mcp.list')
    if (notice.value === 'MCP 正在重新加载…' && !mcpServers.value.some((server) => server.connecting)) {
      notice.value = 'MCP 已重新加载'
    }
  } catch {
    // Transient failure: the list keeps its previous content.
  }
}

let unsubscribeMcpEvents: (() => void) | null = null

onMounted(() => {
  unsubscribeMcpEvents = window.haoyue.daemon.onEvent((message) => {
    if (message.event !== 'mcp.updated') return
    void refreshMcpServers()
  })
})

onBeforeUnmount(() => {
  unsubscribeMcpEvents?.()
  unsubscribeMcpEvents = null
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
          <div class="settings-section-heading">
            <div>
              <h3>MCP 服务器</h3>
              <div class="settings-stat-chips">
                <span class="stat-chip">
                  <span class="status-dot" :class="{ online: mcpServers.some((server) => server.connected) }" />
                  {{ mcpServers.filter((server) => server.connected).length }} / {{ mcpServers.length }} 已连接
                </span>
                <span class="stat-chip">{{ mcpServers.reduce((sum, server) => sum + server.toolCount, 0) }} 个工具</span>
              </div>
            </div>
            <div class="row-actions">
              <button class="icon-button" title="重新加载" :disabled="action === 'mcp.reload'" @click="reloadMcp">
                <RefreshCw :class="{ spin: action === 'mcp.reload' }" :size="17" />
              </button>
              <button v-for="preset in MCP_PRESETS" :key="preset.id" class="secondary-button"
                :title="preset.description" @click="newPresetMcpServer(preset)">
                <component :is="mcpPresetIcons[preset.id] ?? Plug" :size="15" /> {{ preset.label }}
              </button>
              <button class="secondary-button" @click="newMcpServer">
                <Plus :size="15" /> 服务器
              </button>
            </div>
          </div>

          <section class="settings-list">
            <div v-if="mcpServers.length === 0" class="empty-settings">尚未配置 MCP 服务器</div>
            <div v-for="server in mcpServers" :key="`${server.scope}:${server.name}`" class="settings-list-row">
              <span class="status-dot" :class="{ online: server.connected }" />
              <div class="list-main">
                <div>
                  <strong :title="server.name">{{ server.name }}</strong>
                  <span class="inline-badge">{{ server.scope === 'workspace' ? '工作区' : '全局' }}</span>
                </div>
                <small :title="server.error">{{ mcpStatusText(server) }} · {{ transportLabel(server.transport) }}</small>
              </div>
              <div class="row-controls">
                <button class="switch-control" :class="{ active: server.enabled, pending: mcpServerPending(server) }"
                  :disabled="mcpServerPending(server)"
                  :aria-label="mcpServerPending(server) ? '正在连接' : (server.enabled ? '禁用' : '启用')"
                  @click="toggleMcp(server)">
                  <LoaderCircle v-if="mcpServerPending(server)" class="spin" :size="12" /><span v-else />
                </button>
                <button class="icon-button compact" title="编辑" @click="editMcpServer(server)">
                  <Settings2 :size="15" />
                </button>
                <button class="icon-button compact danger-icon" title="删除" @click="removeMcp(server)">
                  <Trash2 :size="15" />
                </button>
              </div>
            </div>
          </section>

          <McpEditorDialog :open="mcpEditorOpen" :server="editingMcpServer" :preset="mcpPreset"
            :saving="action === 'mcp.save'" :error="mcpDialogError" @close="closeMcpEditor" @save="saveMcpServer" />
        </template>

        <template v-else-if="section === 'skills'">
          <div class="settings-section-heading">
            <div>
              <h3>技能</h3>
              <p>{{skills.filter((skill) => skill.enabled).length}} 已启用</p>
            </div>
            <div class="row-actions">
              <button class="icon-button" title="刷新" @click="loadCurrentSection">
                <RefreshCw :size="17" />
              </button>
              <button class="secondary-button" :disabled="action === 'skill.import'" @click="importSkills">
                <LoaderCircle v-if="action === 'skill.import'" class="spin" :size="15" />
                <Upload v-else :size="15" />导入技能
              </button>
            </div>
          </div>
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
</template>
