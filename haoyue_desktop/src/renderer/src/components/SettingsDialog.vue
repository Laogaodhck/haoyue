<script setup lang="ts">
import { daemonRequest } from '../daemon-rpc'
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
  KeyRound,
  LoaderCircle,
  Plug,
  Plus,
  RefreshCw,
  Save,
  Search,
  Settings2,
  SlidersHorizontal,
  Sparkles,
  ScrollText,
  Telescope,
  Trash2,
  Upload,
  Users,
  Wifi,
  Wrench,
  X,
  Zap,
  Monitor,
  Webhook,
  Code2
} from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import type { AppInfo } from '../../../shared/ipc'
import { confirmAction } from '../confirmation'
import ExpertsPanel from './ExpertsPanel.vue'
import McpPanel from './McpPanel.vue'
import ConnectorsPanel from './ConnectorsPanel.vue'
import type { ModelDetailConfig } from './ModelConfigModal.vue'
import OfficialSkillsPanel from './OfficialSkillsPanel.vue'
import ProviderEditorDialog from './ProviderEditorDialog.vue'
import SelectMenu from './SelectMenu.vue'
import UsageTrendChart, { type TimelinePoint } from './UsageTrendChart.vue'
import UsageModelBarChart from './UsageModelBarChart.vue'
import { buildDiagnosticsBundle, diagnosticsFileName } from '../diagnostics-bundle'
import { readSendKeyPreference } from '../app-helpers'
import { providerKindLabel, type ProviderKind } from '../provider-presets'
import { requestJson } from '../daemon-json'
import ToolPolicyPanel from './ToolPolicyPanel.vue'
import SettingsRoutingPanel from './SettingsRoutingPanel.vue'
import SettingsInferencePanel from './SettingsInferencePanel.vue'
import SettingsSubAgentsPanel from './SettingsSubAgentsPanel.vue'
import SettingsAgentExpertPanel from './SettingsAgentExpertPanel.vue'
import ConfigJsonEditor from './ConfigJsonEditor.vue'
import SettingsIntegrationsPanel from './SettingsIntegrationsPanel.vue'
import SettingsEvolutionPanel from './SettingsEvolutionPanel.vue'
import SettingsAdvancedPanel from './SettingsAdvancedPanel.vue'
import SettingsNetworkPanel from './SettingsNetworkPanel.vue'
import SettingsTaskMonitorPanel from './SettingsTaskMonitorPanel.vue'
import SettingsHooksPanel from './SettingsHooksPanel.vue'
import SettingsCodingPanel from './SettingsCodingPanel.vue'
import { useEscapeClose } from '../use-escape-close'

type SettingsSection = 'general' | 'network' | 'models' | 'agent' | 'mcp' | 'skills' | 'experts' | 'rules-memory' | 'evolution' | 'diagnostics' | 'inference' | 'advanced' | 'monitor' | 'hooks' | 'coding'

interface ProviderInfo {
  id: string
  name: string
  kind: ProviderKind
  baseUrl: string
  modelListUrl?: string
  modelsDirectory?: string
  defaultModelsDirectory?: string
  apiKeyConfigured: boolean
  models: string[]
  modelDetails?: ModelDetailConfig[]
  enabled: boolean
  priority: number
  timeoutSeconds: number
  proxy?: string
  promptCaching: boolean
  organization?: string | null
  threads?: number | null
  localPrefixReuse?: boolean
  headers?: Record<string, string> | null
  reasoningEffortMap?: Record<string, string> | null
  active: boolean
}

interface ProviderFormValue {
  id: string
  name: string
  kind: ProviderKind
  baseUrl: string
  modelListUrl: string
  apiKey: string
  clearApiKey: boolean
  models: string
  modelsDirectory: string
  modelDetails?: ModelDetailConfig[]
  organization: string
  threads: number | null
  localPrefixReuse: boolean
  /** 一行一条 `Name: Value`；保存时解析成 headers 对象。 */
  headersText: string
  /** 一行一条 `level=wire`；保存时解析成 reasoningEffortMap 对象。 */
  reasoningEffortMapText: string
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
const evolutionPanel = ref<InstanceType<typeof SettingsEvolutionPanel> | null>(null)
const loading = ref(false)
const networkEnabled = ref(true)
const replyLanguage = ref<'auto' | 'zh' | 'en'>('auto')
// 1.5 发送键偏好：localStorage 持久化，Composer 每次按键直读（改完立即生效）。
const sendKey = ref<'enter' | 'ctrl-enter'>(readSendKeyPreference())
function setSendKey(key: 'enter' | 'ctrl-enter'): void {
  sendKey.value = key
  try { localStorage.setItem('haoyue.sendKey', key) } catch { /* 隐私模式：仅本次会话生效 */ }
}
const rulesEnabled = ref(true)
const memoryMode = ref<'auto' | 'manual'>('auto')

const THEME_OPTIONS = [
  { value: 'system', label: '跟随系统' },
  { value: 'light', label: '浅色' },
  { value: 'dark', label: '深色' }
]
const REPLY_LANGUAGE_OPTIONS = [
  { value: 'auto', label: '自动（跟随系统）', triggerLabel: '自动', description: '中文系统默认中文，其它系统默认英文' },
  { value: 'zh', label: '简体中文', triggerLabel: '中文' },
  { value: 'en', label: 'English' }
]
const SEND_KEY_OPTIONS = [
  { value: 'enter', label: 'Enter 发送', description: 'Shift+Enter 换行' },
  { value: 'ctrl-enter', label: 'Ctrl+Enter 发送', description: '裸 Enter 换行，适合多行输入' }
]
const MEMORY_MODE_OPTIONS = [
  { value: 'auto', label: '自动', description: 'Agent 可在会话中把长期事实沉淀到 MEMORY.md' },
  { value: 'manual', label: '手动', description: 'Agent 把记忆视为只读上下文，仅你能在编辑器中维护' }
]

function changeTheme(value: string): void {
  emit('changeTheme', value as 'system' | 'light' | 'dark')
}
function changeReplyLanguage(value: string): void {
  void setReplyLanguage(value as 'auto' | 'zh' | 'en')
}
function changeSendKey(value: string): void {
  setSendKey(value as 'enter' | 'ctrl-enter')
}
function changeMemoryMode(value: string): void {
  void setRulesMemory({ memoryMode: value as 'auto' | 'manual' })
}

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
/** 后台（daemon）日志：路径由主进程给出，尾部行在主进程脱敏后回传。 */
const desktopInfo = ref<AppInfo | null>(null)
const logTail = ref('')
const logNote = ref('')
const logBytes = ref(0)
const logSkippedBytes = ref(0)
const logBusy = ref(false)
const selectedModel = ref('')
const providerEditorOpen = ref(false)
const editingProviderId = ref<string | null>(null)

const providerForm = reactive<ProviderFormValue>({
  id: '', name: '', kind: 'openai', baseUrl: '',
  apiKey: '', clearApiKey: false, models: '', modelsDirectory: '', modelDetails: [], enabled: true, priority: 0,
  modelListUrl: '', timeoutSeconds: 120, proxy: '', promptCaching: true,
  organization: '', threads: null, localPrefixReuse: true, headersText: '', reasoningEffortMapText: ''
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
  { id: 'network', label: '局域网连接', icon: Wifi },
  { id: 'models', label: '模型与提供商', icon: Bot },
  { id: 'agent', label: '智能体', icon: Sparkles },
  { id: 'experts', label: '专家', icon: Users },
  { id: 'mcp', label: 'MCP', icon: Blocks },
  { id: 'skills', label: '技能', icon: Wrench },
  { id: 'rules-memory', label: '规则与记忆', icon: ScrollText },
  { id: 'evolution', label: '进化', icon: Telescope },
  { id: 'diagnostics', label: '诊断与用量', icon: Activity },
  { id: 'inference', label: '推理加速', icon: Gauge },
  { id: 'monitor', label: '任务监控', icon: Monitor },
  { id: 'hooks', label: '钩子', icon: Webhook },
  { id: 'coding', label: '编码', icon: Code2 },
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

/**
 * 设置项搜索（2.8）：左侧导航原本只能靠记忆逐项点。
 * 搜的是分区名 + 该分区覆盖的关键词，命中后直接跳分区，不另做一套结果页。
 */
const SECTION_KEYWORDS: Record<SettingsSection, string> = {
  general: '外观 主题 语言 快捷键 发送 工具策略 权限 运行时 连接',
  network: '局域网 远程 连接 lan 主机 端口 令牌 配对 共享 runtime',
  models: '模型 提供商 provider api key base url 上下文 定价 路由链 活动模型',
  agent: '智能体 网页搜索 联网 委派 子智能体 自动验证 视觉',
  mcp: 'mcp 连接器 服务器 connector 工具',
  skills: '技能 skill 导入 市场 官方',
  experts: '专家 expert 角色 子代理 参数 步数 思考预算 工具输出 bash 超时 自动修复 续写 委派 事实 会话保留 记忆固化 人格 系统提示词 可读目录 回调 json 原始配置 配置文档',
  'rules-memory': '规则 记忆 AGENTS.md MEMORY.md 注入',
  evolution: '进化 反思 缺陷 草稿 趋势',
  diagnostics: '诊断 用量 日志 token 费用 健康检查 导出',
  inference: '推理 加速 GPU 卸载 显存 KV 量化 flash attention 预热 卸载 驻留 基准 回合预算 token 费用 时长 上下文压缩 保留比例',
  monitor: '任务监控 监控 固定 浮动 悬浮 默认展示 进度 上下文 回顾 目标 计划 执行 活动 子智能体 后台 进程 侧边 聊天 技能 mcp 产出 网页 来源 知识 记忆 更新 演示 画面 开关',
  hooks: '钩子 hook webhook 命令 触发 事件 任务完成 定时 自动 执行 密钥 bearer 本地',
  coding: '编码 git 分支 前缀 强制推送 force push lease 工作区 worktree 索引 安全 扫描 修复 提交',
  advanced: '高级 网络 白名单 电脑操作 computer use 网络运维 故障转移 容灾 负载均衡 重试 出厂'
}

const search = ref('')
const filteredSections = computed(() => {
  const keyword = search.value.trim().toLowerCase()
  if (!keyword) return visibleSections.value
  return visibleSections.value.filter((item) =>
    item.label.toLowerCase().includes(keyword)
    || SECTION_KEYWORDS[item.id]?.toLowerCase().includes(keyword))
})

/** 离线时空列表不能写成「尚未配置」——那会被读成"配置丢了"。 */
function emptyCopy(empty: string): string {
  return props.daemonConnected ? empty : '运行时未连接，暂时读不到数据（离线 ≠ 没有配置）'
}

/** 扩展页技能类目内的二级视图：本地已安装 / 技能市场。 */
const skillsView = ref<'installed' | 'market'>('installed')

/** 扩展页连接器类目内的二级视图：卡片商店 / MCP 高级配置。 */
const connectorsView = ref<'store' | 'advanced'>('store')

/** 连接器安装/卸载后让 MCP 面板状态随之刷新（重连由 daemon 后台完成）。 */
function onConnectorsChanged(): void {
  daemonRequest('mcp.reload', {}).catch(() => {})
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

function onPanelNotice(message: string): void {
  error.value = ''
  notice.value = message
}

function onPanelError(message: string): void {
  error.value = message
}

function showPath(path: string): void {
  void window.haoyue.showItemInFolder(path)
}

async function loadCurrentSection(): Promise<void> {
  if (!props.open || !props.daemonConnected) return
  if (section.value === 'experts') return // 专家目录与专家参数面板自行加载
  loading.value = true
  error.value = ''
  notice.value = ''
  try {
    if (section.value === 'general') await loadGeneral()
    if (section.value === 'models') await loadModels()
    if (section.value === 'agent') await loadAgent()
    if (section.value === 'skills') skills.value = await requestJson<SkillInfo[]>('skill.list')
    if (section.value === 'rules-memory') await loadRulesMemory()
    if (section.value === 'diagnostics') await loadDiagnostics()
  } catch (reason) {
    fail(reason)
  } finally {
    loading.value = false
  }
}

async function loadGeneral(): Promise<void> {
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
  void loadDaemonLogTail()
}

const logFile = computed(() => desktopInfo.value?.daemonLogFile ?? '')

/**
 * 后台日志此前只有主进程在写，产品里没有任何入口，而排障指引第一条就是「先看 daemon.log」。
 * 尾部行由主进程读取并脱敏，渲染进程拿不到文件路径以外的能力。
 */
async function loadDaemonLogTail(lines = 200): Promise<void> {
  logBusy.value = true
  try {
    if (!desktopInfo.value) desktopInfo.value = await window.haoyue.getAppInfo()
    const tail = await window.haoyue.readDaemonLogTail(lines)
    logBytes.value = tail.totalBytes
    logSkippedBytes.value = tail.skippedBytes
    logTail.value = tail.error ? `读取失败：${tail.error}` : tail.text
    if (tail.error) {
      logNote.value = tail.error
    } else if (tail.totalBytes === 0) {
      logNote.value = '日志还没有内容（后台未启动过，或刚轮转）'
    } else {
      const skipped = tail.skippedBytes > 0 ? ` · 之前 ${Math.round(tail.skippedBytes / 1024)} KB 已省略` : ''
      logNote.value = `${Math.round(tail.totalBytes / 1024)} KB · 末尾 ${lines} 行${skipped}`
    }
  } catch (reason) {
    logTail.value = ''
    logNote.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    logBusy.value = false
  }
}

function copyLogPath(): void {
  if (!logFile.value) return
  void navigator.clipboard.writeText(logFile.value).then(
    () => { notice.value = `已复制日志路径：${logFile.value}` },
    () => { notice.value = logFile.value }
  )
}

function openDaemonLogFolder(): void {
  const info = desktopInfo.value
  if (!info) return
  // 日志文件还没生成时展示目录，否则展示文件本身——两种都让人能把日志拷出来。
  showPath(logFile.value || info.logDir)
}

async function exportDiagnosticsBundle(): Promise<void> {
  beginAction('diagnostics.bundle')
  try {
    await loadDaemonLogTail(400)
    if (!desktopInfo.value) desktopInfo.value = await window.haoyue.getAppInfo()
    const generatedAt = new Date()
    const info = desktopInfo.value
    const bundle = buildDiagnosticsBundle({
      generatedAt,
      appVersion: info.version,
      platform: info.platform,
      logDir: info.logDir,
      daemonLogFile: info.daemonLogFile,
      daemonConnected: props.daemonConnected,
      daemonEndpoint: props.daemonEndpoint,
      usageDays: usageDays.value,
      checks: checks.value,
      usage: usage.value,
      totals: totalUsage.value,
      logTail: logTail.value,
      logBytes: logBytes.value,
      skippedLogBytes: logSkippedBytes.value
    })
    const saved = await window.haoyue.saveTextFile(diagnosticsFileName(generatedAt), bundle)
    notice.value = saved ? `诊断包已导出到 ${saved}` : '已取消导出'
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
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
    id: '', name: '', kind: 'openai', baseUrl: '', apiKey: '', clearApiKey: false,
    models: '', modelsDirectory: '', modelDetails: [], enabled: true, priority: 0, modelListUrl: '', timeoutSeconds: 120, proxy: '', promptCaching: true,
    organization: '', threads: null, localPrefixReuse: true, headersText: '', reasoningEffortMapText: ''
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
    apiKey: '', clearApiKey: false,
    models: provider.models.join('\n'),
    modelsDirectory: provider.modelsDirectory ?? '',
    modelDetails: provider.modelDetails ? provider.modelDetails.map((m) => ({ ...m })) : [],
    enabled: provider.enabled,
    priority: provider.priority,
    timeoutSeconds: provider.timeoutSeconds,
    proxy: provider.proxy ?? '',
    promptCaching: provider.promptCaching ?? true,
    organization: provider.organization ?? '',
    threads: provider.threads ?? null,
    localPrefixReuse: provider.localPrefixReuse ?? true,
    headersText: formatPairs(provider.headers),
    reasoningEffortMapText: formatPairs(provider.reasoningEffortMap)
  })
  providerEditorOpen.value = true
}

/** `Name: Value` / `level=wire` 的一行式编辑 ↔ 对象。空文本返回 null（= 清空）。 */
function parsePairs(text: string): Record<string, string> | null {
  const map: Record<string, string> = {}
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim()
    if (!line || line.startsWith('#')) continue
    const cut = line.search(/[:=]/)
    if (cut <= 0) continue
    const name = line.slice(0, cut).trim()
    const value = line.slice(cut + 1).trim()
    if (name) map[name] = value
  }
  return Object.keys(map).length > 0 ? map : null
}

function formatPairs(map?: Record<string, string> | null): string {
  if (!map) return ''
  return Object.entries(map).map(([name, value]) => `${name}: ${value}`).join('\n')
}

async function saveProvider(value: ProviderFormValue): Promise<void> {
  beginAction('provider.save')
  try {
    const { apiKey, clearApiKey, headersText, reasoningEffortMapText, ...provider } = value
    const parameters: Record<string, unknown> = {
      ...provider,
      models: value.models.split(/\r?\n|,/).map((model) => model.trim()).filter(Boolean),
      modelDetails: value.modelDetails ?? [],
      // 头表整表提交：密钥类值读侧被置空，提交空串表示保持已存值；全删即清空自定义头。
      headers: parsePairs(headersText),
      reasoningEffortMap: parsePairs(reasoningEffortMapText)
    }
    // 明文不再回传，表单留空即「保持已存密钥不变」；删除需要显式勾选。
    if (clearApiKey) parameters.clearApiKey = true
    else if (apiKey.trim()) parameters.apiKey = apiKey.trim()
    await daemonRequest('provider.upsert', parameters)
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
    await daemonRequest('provider.use', { id: provider.id })
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
    await daemonRequest('provider.remove', { id: provider.id })
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
    await daemonRequest('model.switch', { model: selectedModel.value })
    await loadModels()
    emit('runtimeChanged')
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
  if (next === 'mcp') connectorsView.value = 'store'
  void loadCurrentSection()
})

/**
 * 成功提示 4 秒自动消失（2.8）。
 * 此前 notice 只在开始下一个动作时被清掉，于是「已保存…」会一直挂在屏幕右下角，
 * 与之后手动改回的状态并存，读起来像当前值。错误提示不自动消失——需要用户处理。
 */
let noticeTimer: number | undefined
watch(notice, (value) => {
  window.clearTimeout(noticeTimer)
  if (!value) return
  noticeTimer = window.setTimeout(() => {
    notice.value = ''
  }, 4000)
})

let unsubscribeDaemonEvents: (() => void) | null = null

// Esc 返回应用页，与规则/知识/计划/归档四个整页页签一致。整页设置用的是 role="region"
// 而非 aria-modal，所以按键归属由栈管：嵌套模态打开时这一次 Esc 归模态，设置页不动。
useEscapeClose(() => props.open, () => emit('close'))

onMounted(() => {
  unsubscribeDaemonEvents = window.haoyue.daemon.onEvent((message) => {
    if (message.event === 'evolution.reflected' && section.value === 'evolution') {
      void evolutionPanel.value?.refreshAfterReflect(message.details ?? {})
    }
  })
})

onBeforeUnmount(() => {
  window.clearTimeout(noticeTimer)
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

      <label class="settings-nav-search">
        <Search :size="14" aria-hidden="true" />
        <input v-model="search" type="search" placeholder="搜索设置项" aria-label="搜索设置项" />
        <button v-if="search" type="button" class="settings-nav-search-clear" title="清除搜索" @click="search = ''">
          <X :size="12" />
        </button>
      </label>

      <div class="settings-nav-list">
        <button v-for="item in filteredSections" :key="item.id" class="settings-nav-item"
          :class="{ active: section === item.id }" @click="section = item.id">
          <component :is="item.icon" :size="17" />
          <span>{{ item.label }}</span>
        </button>
        <p v-if="filteredSections.length === 0" class="settings-nav-empty">
          没有匹配的设置项，试试「模型」「网络」「推理」
        </p>
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
      <!-- 2.3：运行时离线时整页只读。以前这里是直接 return，于是「尚未配置提供商」
           这类空态被读成"配置丢了"，开关显示的还是 ref 初值。 -->
      <div v-if="!daemonConnected" class="settings-offline-banner" role="status">
        <Circle :size="8" fill="currentColor" />
        <span v-if="section === 'network'">运行时离线：连接设置仍可修改；连上目标 Runtime 后，其余设置才会重新加载。</span>
        <span v-else>运行时离线：以下内容是上次读取到的旧值，修改已全部禁用；列表为空只代表读不到，不代表没有配置。</span>
        <button class="secondary-button compact-button" type="button" @click="emit('reconnect')">
          <RefreshCw :size="13" /> 重新连接
        </button>
      </div>
      <!-- 局域网连接页不依赖 daemon（离线时正是要改连接目标），因此不受只读样式约束。 -->
      <div class="settings-content" :class="{ 'is-offline': !daemonConnected && section !== 'network' }">
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
              <SelectMenu :model-value="theme" class="settings-select select-compact" :options="THEME_OPTIONS"
                label="外观" @update:model-value="changeTheme" />
            </div>
            <div class="settings-row">
              <div>
                <strong>回复语言</strong>
                <small>模型先识别输入语言，再以目标语言回复；跟随系统 = 中文系统默认中文</small>
              </div>
              <SelectMenu :model-value="replyLanguage" class="settings-select select-compact"
                :options="REPLY_LANGUAGE_OPTIONS" label="回复语言" :disabled="action === 'language.set'"
                @update:model-value="changeReplyLanguage" />
            </div>
            <div class="settings-row">
              <div>
                <strong>发送快捷键</strong>
                <small>Enter 直接发送（Shift+Enter 换行）；Ctrl+Enter 发送时裸 Enter 换行，适合多行输入</small>
              </div>
              <SelectMenu :model-value="sendKey" class="settings-select select-compact" :options="SEND_KEY_OPTIONS"
                label="发送快捷键" @update:model-value="changeSendKey" />
            </div>
            <div class="settings-row">
              <div><strong>运行时Runtime</strong><small>{{ daemonEndpoint }}</small></div>
              <button class="secondary-button" @click="emit('reconnect')">
                <RefreshCw :size="15" /> 重新连接
              </button>
            </div>
          </section>

          <section class="settings-group">
            <div class="settings-section-heading">
              <div>
                <h3>工具执行策略</h3>
              </div>
            </div>
            <ToolPolicyPanel />
          </section>

        </template>

        <template v-else-if="section === 'network'">
          <SettingsNetworkPanel :daemon-connected="daemonConnected" :daemon-endpoint="daemonEndpoint"
            @notice="onPanelNotice" @error="onPanelError" />
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
              <div><strong>视觉子智能体模型</strong><small>处理图像回合（聊天附件、屏幕截图）的模型；仅列出支持视觉能力的模型，自动 = 使用路由链中第一个支持视觉的模型</small></div>
              <div class="row-actions model-actions">
                <SelectMenu v-model="agentConfig.visionModel" class="settings-select model-select"
                  :options="visionModelOptions" label="视觉子智能体模型" :menu-min-width="330" />
                <button class="secondary-button primary-action" :disabled="action === 'agent.config.set:vision'"
                  @click="setVisionModel">保存</button>
              </div>
            </div>
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
            <div v-if="providers.length === 0" class="empty-settings">{{ emptyCopy('尚未配置提供商') }}</div>
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
                    <span class="provider-meta-tag">{{ providerKindLabel(provider.kind) }}</span>
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
          <!-- 11. 凡是「模型相关」的都归到这一页：路由容灾、图像生成、文档渲染、嵌入模型。 -->
          <SettingsRoutingPanel :connected="daemonConnected" />

          <SettingsIntegrationsPanel :connected="daemonConnected" />
        </template>

        <template v-else-if="section === 'mcp'">
          <!-- 扩展中心：连接器卡片商店为默认视图，MCP 高级配置收进二级视图；
               普通设置页维持原 MCP 面板直入。 -->
          <div v-if="page === 'extensions'" class="settings-section-heading">
            <div>
              <h3>连接器</h3>
              <p>一键接入常用外部服务；进阶配置（自定义服务器、凭据、OAuth）请切到 MCP 配置。</p>
            </div>
            <div class="row-actions">
              <div class="segmented-control">
                <button :class="{ active: connectorsView === 'store' }" @click="connectorsView = 'store'">连接器商店</button>
                <button :class="{ active: connectorsView === 'advanced' }" @click="connectorsView = 'advanced'">MCP 配置</button>
              </div>
            </div>
          </div>
          <ConnectorsPanel v-if="page === 'extensions' && connectorsView === 'store'"
            @changed="onConnectorsChanged" />
          <McpPanel v-else :workspace-path="workspacePath" />
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
              <div v-if="skills.length === 0" class="empty-settings">{{ emptyCopy('尚未发现技能，可导入 .md 或 .zip 文件') }}</div>
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
          <template v-if="page === 'extensions'">
            <div class="settings-section-heading">
              <div>
                <h3>专家</h3>
                <p>内置领域专家：一键绑定到新任务，或将提示词粘贴到对话开头</p>
              </div>
            </div>
            <ExpertsPanel @start="(expert) => emit('startExpert', expert)" />
          </template>
          <template v-else>
            <div class="settings-section-heading">
              <div>
                <h3>专家</h3>
                <p>专家参数：回合步数与工具预算、超时与重试、委派配额，以及人格、系统提示词与额外可读目录</p>
              </div>
            </div>
            <SettingsAgentExpertPanel :connected="daemonConnected" />
            <section class="settings-group">
              <div class="settings-group-title">
                <strong>直接编辑配置文档（JSON）</strong>
                <small>上面没有覆盖到的字段都在这里：只提交你实际改动的叶子节点，没动过的配置不会被写回。</small>
              </div>
              <ConfigJsonEditor :connected="daemonConnected" />
            </section>
          </template>
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
              <SelectMenu :model-value="memoryMode" class="settings-select select-compact"
                :options="MEMORY_MODE_OPTIONS" label="记忆管理方式" :disabled="action === 'rulesMemory.set'"
                @update:model-value="changeMemoryMode" />
            </div>
          </section>
        </template>

        <template v-else-if="section === 'evolution'">
          <SettingsEvolutionPanel ref="evolutionPanel" @notice="onPanelNotice" @error="onPanelError" />
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

          <!-- Daemon log & diagnostics bundle -->
          <div class="diagnostics-sub-heading">
            <div>
              <h4>后台日志与诊断包</h4>
              <small>{{ logNote || '后台原生输出落盘位置；崩溃、显存不足、首字慢的线索都在这里' }}</small>
            </div>
            <div class="diagnostics-log-actions">
              <button class="secondary-button" :disabled="!desktopInfo" @click="openDaemonLogFolder">
                <FolderOpen :size="15" /> 打开日志
              </button>
              <button class="secondary-button" :disabled="!logFile" @click="copyLogPath">
                <Save :size="15" /> 复制路径
              </button>
              <button class="secondary-button" :disabled="logBusy" @click="loadDaemonLogTail()">
                <RefreshCw :size="15" :class="{ spin: logBusy }" /> 刷新末尾
              </button>
              <button class="secondary-button" :disabled="Boolean(action)" @click="exportDiagnosticsBundle">
                <ScrollText :size="15" /> 导出诊断包
              </button>
            </div>
          </div>

          <section class="settings-group diagnostics-log-group">
            <div class="settings-row">
              <div>
                <strong>日志文件</strong>
                <small class="diagnostics-log-path">{{ logFile || '尚未取得路径' }}</small>
              </div>
            </div>
            <pre class="diagnostics-log-tail">{{ logTail || '（暂无日志内容）' }}</pre>
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
              <p>本地加载参数、驻留与显存治理，以及单回合预算与上下文压缩保留比例；加载参数的写入口统一在「模型与提供商」</p>
            </div>
          </div>
          <SettingsInferencePanel :connected="daemonConnected" />
        </template>

        <template v-else-if="section === 'agent'">
          <div class="settings-section-heading">
            <div>
              <h3>智能体</h3>
              <p>配置智能体的模型选择、自主行为边界与可委派的子智能体</p>
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

          <SettingsSubAgentsPanel :connected="daemonConnected" :workspace-path="workspacePath" />
        </template>

        <template v-else-if="section === 'monitor'">
          <SettingsTaskMonitorPanel :connected="daemonConnected" @notice="onPanelNotice" @error="onPanelError" />
        </template>

        <template v-else-if="section === 'hooks'">
          <SettingsHooksPanel :connected="daemonConnected" @notice="onPanelNotice" @error="onPanelError" />
        </template>

        <template v-else-if="section === 'coding'">
          <SettingsCodingPanel :connected="daemonConnected" :workspace-path="workspacePath"
            @notice="onPanelNotice" @error="onPanelError" />
        </template>

        <template v-else-if="section === 'advanced'">
          <SettingsAdvancedPanel :daemon-connected="daemonConnected" :network-enabled="networkEnabled"
            @notice="onPanelNotice" @error="onPanelError" @navigate-agent="section = 'agent'"
            @runtime-changed="emit('runtimeChanged')" @close="emit('close')" />
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
    :key-configured="providers.find((p) => p.id === editingProviderId)?.apiKeyConfigured ?? false"
    :saving="action === 'provider.save'" :error="providerEditorOpen ? error : ''"
    :default-models-directory="providers.find((provider) => provider.kind === 'local')?.defaultModelsDirectory"
    @close="providerEditorOpen = false" @save="saveProvider" />
</template>
