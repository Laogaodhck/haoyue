<script setup lang="ts">
import {
  ArrowDown, Braces, Check, ChevronDown, ChevronUp, Circle, CornerDownLeft,
  Folder, FolderOpen, Globe2, History, LoaderCircle, MoreHorizontal, PanelRight,
  RefreshCw, Search, ShieldAlert, Telescope, TerminalSquare, Trash2, Sparkles,
  Undo2, Wrench, X
} from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch, watchEffect } from 'vue'
import type { AppearanceTheme, AppInfo, DaemonMessage, DaemonState } from '../../shared/ipc'
import AppTitleBar from './components/AppTitleBar.vue'
import AboutDialog from './components/AboutDialog.vue'
import ArchivedTasksDialog from './components/ArchivedTasksDialog.vue'
import ScheduledTasksDialog from './components/ScheduledTasksDialog.vue'
import Composer from './components/Composer.vue'
import ConfigAnomalyDialog from './components/ConfigAnomalyDialog.vue'
import ConfirmDialog from './components/ConfirmDialog.vue'
import ConversationMessage from './components/ConversationMessage.vue'
import EditMessageDialog from './components/EditMessageDialog.vue'
import WorkDock from './components/WorkDock.vue'
import KnowledgeBaseDialog from './components/KnowledgeBaseDialog.vue'
import RulesMemoryDialog from './components/RulesMemoryDialog.vue'
import ProjectPropertiesDialog from './components/ProjectPropertiesDialog.vue'
import RuntimeReconnectDialog from './components/RuntimeReconnectDialog.vue'
import SettingsDialog from './components/SettingsDialog.vue'
import Sidebar from './components/Sidebar.vue'
import TaskSettingsDialog from './components/TaskSettingsDialog.vue'
import TaskStepList from './components/TaskStepList.vue'
import WelcomeEmpty from './components/WelcomeEmpty.vue'
import { computeTurnTaskSteps, type TaskStep } from './task-planner'
import { confirmAction } from './confirmation'
import { createWorkspaceFileSuggester } from './workspace-files'
import { DeletionUndoBuffer, finalizeAssistantBubbles, markThreadInterrupted, preserveTruncatedVersions, restoreAssistantVersion } from './conversation-state'
import { journalCursor, openTurnsOf, recoverTurns, type JournalRow, type RecoveredTurn } from './turn-recovery'
import { isForbiddenProjectPath } from './project-paths'
import { retryRuntimeConnection, RUNTIME_RECONNECT_ATTEMPTS } from './runtime-reconnect'
import { ReasoningLevel } from './types'
import type { ChatMessage, EvolutionCandidate, FileAttachment, ImageAttachment, ProjectItem, QueuedMessage, SessionSearchHit, ThreadItem, ThreadStats, ToolActivity, WorkDockTab } from './types'
import {
  cancelParams, formatContextRatio, formatInferenceFootnote,
  formatPreloadLabel, formatPromptWithFiles, formatTurnFailure,
  hydrateMessages, makeId, messageMatches, normalizePath, normalizeReasoningLevel,
  pathName, phaseLabel, plainImages, queuedImageUrl, queuedMessagePreview,
  samePath, sessionScope, sessionStats, updateThreadTitle,
} from './app-helpers'
import { createChatActions } from './app-actions'
import { clearScrollPositions, readScrollPosition, saveScrollPosition } from './scroll-memory'
import type { RuntimeModelCatalogItem, RuntimeProject, RuntimeSession, RuntimeWorkspace } from './app-helpers'
import {
  imageSourceKey, loadEarlierMessages as loadEarlierMessagesFromStore,
  refreshAllProjectSessions as refreshAllProjectSessionsFromStore,
  refreshGlobalSessions as refreshGlobalSessionsFromStore,
  refreshProjectSessions as refreshProjectSessionsFromStore,
  reloadThreadSession as reloadThreadSessionFromStore,
  requestSessionImageBytes, SESSION_PAGE_SIZE, sessionImageSources
} from './session-sync'
import { createThreadActions } from './thread-actions'
import { createDaemonEventHandler } from './daemon-event-handler'

const PROJECTS_STORAGE_KEY = 'haoyue-projects-v2'
const IMPLICIT_DOCUMENTS_MIGRATION_KEY = 'haoyue-projects-remove-implicit-documents-v2'
// The daemon starts a chat turn within milliseconds of receiving the request,
// so a request that produces no event at all within this window is stuck (for
// example the payload never reached the daemon). Reject it instead of leaving
// the task loading forever; once the first event arrives the turn is confirmed
// running and may legitimately take minutes.
const CHAT_FIRST_EVENT_TIMEOUT_MS = 30_000
// prompt.optimize is model-backed and non-streaming: no daemon event arrives
// until the model answers, so allow the runtime's full retry chain instead of
// the generic 60s daemon-client default. Worst case before the daemon reports
// its own error: 3 retryable attempts x provider timeout (120s) + backoff
// ~= 364s, so 420s guarantees the descriptive daemon error wins the race.
const PROMPT_OPTIMIZE_TIMEOUT_MS = 420_000
const appInfo = ref<AppInfo>({
  version: '0.1.0',
  platform: 'win32',
  supportsMica: false,
  logDir: '',
  daemonLogFile: '',
  defaultWorkspace: '',
  documentsPath: '',
  userProfilePath: ''
})
type AppPage = 'main' | 'settings' | 'extensions' | 'archived' | 'scheduled' | 'knowledge' | 'rules-memory'

const sidebarOpen = ref(true)
const activePage = ref<AppPage>('main')
const aboutOpen = ref(false)
const workDockOpen = ref(false)
const workDockTab = ref<WorkDockTab>('changes')
const workDockWidth = ref(560)
const toolDiff = ref<{ path: string; diff: string } | null>(null)

const settingsSection = ref<'general' | 'models' | 'mcp' | 'skills' | 'rules-memory' | 'diagnostics' | 'inference' | 'advanced'>('general')
const extensionsSection = ref<'mcp' | 'skills' | 'experts'>('mcp')
const taskSettingsThreadId = ref('')
const activePropertiesProject = ref<ProjectItem | null>(null)
const storedTheme = localStorage.getItem('haoyue-theme')
const theme = ref<AppearanceTheme>(
  storedTheme === 'light' || storedTheme === 'dark' || storedTheme === 'system' ? storedTheme : 'system')
const daemonState = ref<DaemonState>({ connected: false, endpoint: '' })
const reconnecting = ref(false)
const reconnectAttempt = ref(0)
const reconnectPrompt = ref<{ startup: boolean; error?: string } | null>(null)
const configAnomaly = ref<{
  hasAnomaly: boolean
  detail?: string
  configFile?: string
  backupFile?: string
} | null>(null)
// 凭据存储告警（明文 API Key 无法加密迁移）。可关闭，但每次重连都会重新拉取，
// 因为只要告警仍在，配置文件里就还有一个未加密的密钥。
const secretWarnings = ref<string[]>([])
const rebuildingConfig = ref(false)
const projects = ref<ProjectItem[]>([])
const threads = ref<ThreadItem[]>([])
const activeThreadId = ref('')
// daemon 启动预热进度：无运行回合时展示在输入区（Ready/Failed 或断连即清除）。
const modelPreload = ref<{ phase: string; percent: number } | null>(null)
const selectedProjectId = ref('')
const runtimeWorkspacePath = ref('')
const models = ref<string[]>([])
const modelCatalog = ref<RuntimeModelCatalogItem[]>([])
const activeModel = ref('')
const mode = ref('edit')
const busy = computed(() => Boolean(activeThread.value?.running))
const scrollArea = ref<HTMLElement | null>(null)
const composer = ref<InstanceType<typeof Composer> | null>(null)
const autoFollowConversation = ref(true)
const conversationLoading = ref(false)
const conversationLoadError = ref('')
const conversationQuery = ref('')
const messageHeights = new Map<string, number>()
const conversationScrollTop = ref(0)
const conversationViewportHeight = ref(600)
/** Per-task composer drafts (text + attachments), kept across task switches (H5). */
interface ComposerDraft { text: string; images: ImageAttachment[]; files: FileAttachment[] }
const composerDrafts = new Map<string, ComposerDraft>()
/** 草稿落盘 key（P1-2.3）：未发送草稿此前仅存内存 Map，刷新/崩溃即丢。 */
const DRAFT_STORAGE_KEY = 'haoyue-composer-drafts'

function loadPersistedDrafts(): void {
  try {
    const raw = localStorage.getItem(DRAFT_STORAGE_KEY)
    if (!raw) return
    const parsed = JSON.parse(raw) as Record<string, ComposerDraft>
    for (const [threadId, draft] of Object.entries(parsed))
      if (draft && typeof draft.text === 'string') composerDrafts.set(threadId, draft)
  } catch {
    // 损坏的草稿存储不影响启动：按无草稿处理。
  }
}

function persistDrafts(): void {
  try {
    const entries: Record<string, ComposerDraft> = {}
    for (const [threadId, draft] of composerDrafts) {
      // 只持久化有内容的草稿：空文本不占存储，也不该在恢复时抢焦点。
      if (draft.text.trim() || draft.images.length > 0 || draft.files.length > 0)
        entries[threadId] = draft
    }
    localStorage.setItem(DRAFT_STORAGE_KEY, JSON.stringify(entries))
  } catch {
    // 存储配额满等故障静默降级：内存 Map 仍是权威来源。
  }
}

// ---------------------------------------------------------------- H9/H10/H11/H19/H25
const conversationSearchInput = ref<HTMLInputElement | null>(null)
const sidebarPanel = ref<InstanceType<typeof Sidebar> | null>(null)
const queuedHintDismissed = ref(false)

/** H10: match count + prev/next navigation across content/thinking/tool results. */
const searchMatches = computed(() => {
  const query = conversationQuery.value.trim()
  if (!query || !activeThread.value) return []
  return activeThread.value.messages.filter((message) => messageMatches(message, query))
})
const searchCursor = ref(0)
watch(() => conversationQuery.value, () => { searchCursor.value = 0 })

function jumpSearch(delta: number): void {
  const matches = searchMatches.value
  if (matches.length === 0) return
  searchCursor.value = (searchCursor.value + delta + matches.length) % matches.length
  const target = matches[searchCursor.value]
  if (!target) return
  document.querySelector(`[data-message-id="${CSS.escape(target.id)}"]`)
    ?.scrollIntoView({ block: 'center', behavior: 'smooth' })
}

/** H9: floating "back to latest" when the user scrolled up during streaming. */
async function jumpToLatest(): Promise<void> {
  autoFollowConversation.value = true
  await scrollToBottom(true, true)
}

/** H19: long phases (compaction/verify) show elapsed seconds so they don't read as frozen. */
const phaseStartedAt = ref(0)
const phaseElapsedSeconds = ref(0)
let phaseTimer: ReturnType<typeof setInterval> | undefined
watch(() => activeThread.value?.phase, (phase) => {
  if (phaseTimer) { clearInterval(phaseTimer); phaseTimer = undefined }
  if (!phase) { phaseStartedAt.value = 0; phaseElapsedSeconds.value = 0; return }
  phaseStartedAt.value = Date.now()
  phaseElapsedSeconds.value = 0
  phaseTimer = setInterval(() => {
    phaseElapsedSeconds.value = Math.floor((Date.now() - phaseStartedAt.value) / 1000)
  }, 1000)
})

/** H11: global keyboard flow — Ctrl+K task search, Ctrl+F conversation search, Ctrl+./Esc stop. */
function handleGlobalShortcuts(event: KeyboardEvent): void {
  // 审批弹窗没人接 Esc：键盘用户只能拿鼠标点「拒绝」。这里补上同一路径——
  // 与 daemon 侧超时完全一致（按拒绝收场），Esc 绝不会被读成允许。
  if (event.key === 'Escape' && activeThread.value?.pendingApproval) {
    event.preventDefault()
    void respondApproval(false)
    return
  }
  // 模态弹窗打开时，按键归弹窗所有（与 AppTitleBar 同一约定）：否则设置里的 Esc
  // 会一边关弹窗一边把后台正在跑的回合掐掉。
  if (document.querySelector('[aria-modal="true"]')) return
  // Esc 停止活动回合（P1-2.3）：Composer 停止按钮长期标称 Esc 但只有 Ctrl+. 生效。
  if (event.key === 'Escape' && activeThread.value?.running) {
    event.preventDefault()
    stopTurn()
    return
  }
  if (!(event.ctrlKey || event.metaKey) || event.altKey) return
  const key = event.key.toLowerCase()
  if (key === 'k') {
    event.preventDefault()
    sidebarOpen.value = true
    void nextTick(() => sidebarPanel.value?.focusSearch())
  } else if (key === 'f') {
    event.preventDefault()
    void nextTick(() => conversationSearchInput.value?.focus())
  } else if (key === '.') {
    if (activeThread.value?.running) {
      event.preventDefault()
      stopTurn()
    }
  }
}

function saveComposerDraft(threadId?: string): void {
  if (threadId && composer.value) {
    const state = composer.value.getState()
    composerDrafts.set(threadId, { text: state.value, images: state.images, files: state.files })
  }
  persistDrafts()
}
const conversationSelectionToken = { value: 0 }

const activeThread = computed(() => threads.value.find((thread) => thread.id === activeThreadId.value))
const activeReasoningLevel = computed(() => activeThread.value?.reasoningLevel ?? ReasoningLevel.High)
const activeModelSupportsImages = computed(() =>
  modelCatalog.value.find((model) => model.ref === activeModel.value)?.capabilities?.vision === true)
const activeImageSources = computed<Record<string, string>>(() => {
  const sources: Record<string, string> = {}
  const thread = activeThread.value
  if (!thread) return sources
  for (const message of thread.messages)
    for (const image of message.images ?? []) {
      // 本回合刚附上的图带字节；历史页里的图只有元数据，字节由 session.image 按需填缓存。
      if (image.data) sources[image.id] = `data:${image.mediaType};base64,${image.data}`
      else if (thread.sessionId) {
        const cached = sessionImageSources[imageSourceKey(thread.sessionId, image.id)]
        if (cached) sources[image.id] = cached
      }
    }
  return sources
})
const activeProject = computed(() => {
  const projectId = activeThread.value ? activeThread.value.projectId : selectedProjectId.value
  return projects.value.find((project) => project.id === projectId)
})
/** 首屏窗口之前还有多少条历史消息（决定「加载更早」按钮的文案与可见性）。 */
const earlierMessageCount = computed(() => activeThread.value?.sessionFirstIndex ?? 0)
const globalTaskActive = computed(() => activeThread.value ? !activeThread.value.projectId : !selectedProjectId.value)

const composerCaption = computed(() => {

  if (conversationLoading.value) return '正在读取会话历史…'
  // H24: surface the offline state before the user hits send, not after.
  if (!daemonState.value.connected) return reconnecting.value ? '正在重连 Haoyue Runtime…' : 'Haoyue Runtime 未连接，请点击右上角按钮重连后再发送。'
  if (!activeThread.value) return '新任务：直接输入开始对话。'
  if (activeThread.value.archived) return '此任务已归档，发送消息后将自动恢复并继续对话。'
  // H13: an empty task has no stats yet — zero noise instead of "0 轮 · 0 步 | 缓存命中 — |…"。
  if (activeThread.value.messages.length === 0) return '新任务：直接输入开始对话。'
  const stats = activeThread.value.stats
  const number = (value?: number): string =>
    typeof value === 'number' && Number.isFinite(value) ? value.toLocaleString() : '—'
  const totalInputTokens = stats?.totalInputTokens ?? 0
  const cachedInputTokens = stats?.cachedInputTokens ?? 0
  const cacheRate = totalInputTokens > 0
    ? `${Math.min(100, Math.round(cachedInputTokens / totalInputTokens * 100))}%`
    : '—'
  const outputTokens = stats?.outputTokens ?? 0
  const outputElapsedMs = stats?.outputElapsedMs ?? 0
  const speed = outputTokens > 0 && outputElapsedMs > 0
    ? (outputTokens / (outputElapsedMs / 1000)).toFixed(1)
    : '—'
  // 上下文占用比例：只按最近一轮单次 prompt 规模 ÷ 模型窗口（累计口径会随轮数虚涨）。
  const contextWindow = modelCatalog.value.find((model) => model.ref === activeModel.value)?.contextWindow ?? 0
  const contextRatio = formatContextRatio(stats?.lastRoundInputTokens, contextWindow)
  // 本地推理读数（仅本地 provider 的 usage 事件携带）：预热 / KV 复用的效果直接可见。
  const inference = formatInferenceFootnote(stats?.lastInference)
  return `${number(stats?.llmRounds)} 轮 · ${number(stats?.executionSteps)} 步 | 缓存命中 ${cacheRate} | ${speed} tok/s | 输入 ${number(stats?.inputTokens)} tok · 输出 ${number(stats?.outputTokens)} tok${contextRatio}${inference ? ` | ${inference}` : ''}`
})
const settingsThread = computed(() => threads.value.find((thread) => thread.id === taskSettingsThreadId.value))
const settingsProject = computed(() => projects.value.find((project) => project.id === settingsThread.value?.projectId))
// 启动预热提示（Failed 时说清「不影响使用」）；断连时进度失去意义——daemon 可能已重启。
const modelPreloadLabel = computed(() => formatPreloadLabel(modelPreload.value))
watch(() => daemonState.value.connected, (connected) => { if (!connected) modelPreload.value = null })
const conversationTitle = computed(() =>
  activeThread.value?.title || activeProject.value?.name || '任务')
const runtimeConnectionLabel = computed(() => {
  if (reconnecting.value) return `正在重连 ${reconnectAttempt.value}/${RUNTIME_RECONNECT_ATTEMPTS}`
  return 'Runtime 离线'
})

const sessionSyncContext = { daemonState, threads }

// 历史消息的图片只有元数据进入渲染端（session.get 不内联 base64）：这里按当前任务
// 可见的图片排队按需取回，同一张图并发只会发一次 RPC。
watchEffect(() => {
  const thread = activeThread.value
  if (!thread?.sessionId) return
  for (const message of thread.messages)
    for (const image of message.images ?? [])
      requestSessionImageBytes(sessionSyncContext, thread, activeProject.value, image)
})

let unsubscribeEvent: (() => void) | undefined
let unsubscribeState: (() => void) | undefined
let reconnectTask: Promise<boolean> | null = null
let automaticReconnectPaused = false
let appReadyForRecovery = false

function loadStoredProjects(): ProjectItem[] {
  try {
    const saved = JSON.parse(localStorage.getItem(PROJECTS_STORAGE_KEY) ?? '[]') as ProjectItem[]
    return saved.filter((project) => project?.id && project?.path).map((project) => ({
      id: project.id,
      name: project.name || pathName(project.path),
      path: project.path,
      loaded: false
    }))
  } catch {
    return []
  }
}

function ensureProject(path: string, name?: string): ProjectItem {
  const existing = projects.value.find((project) => samePath(project.path, path))
  if (existing) {
    if (name) existing.name = name
    return existing
  }
  const project: ProjectItem = { id: makeId(), name: name || pathName(path), path, loaded: false }
  projects.value.push(project)
  return project
}

async function saveProject(project: ProjectItem): Promise<ProjectItem> {
  const oldId = project.id
  const response = await window.haoyue.daemon.request('project.upsert', {
    id: project.id,
    path: project.path,
    name: project.name
  })
  const saved = JSON.parse(response.data) as RuntimeProject
  const duplicate = projects.value.find((item) => item !== project && item.id === saved.id)
  if (duplicate) {
    duplicate.path = saved.path
    duplicate.name = saved.name
    threads.value.forEach((thread) => {
      if (thread.projectId === oldId) thread.projectId = duplicate.id
    })
    if (selectedProjectId.value === oldId) selectedProjectId.value = duplicate.id
    projects.value = projects.value.filter((item) => item !== project)
    return duplicate
  }
  project.id = saved.id
  project.path = saved.path
  project.name = saved.name
  if (oldId !== saved.id) {
    threads.value.forEach((thread) => {
      if (thread.projectId === oldId) thread.projectId = saved.id
    })
    if (selectedProjectId.value === oldId) selectedProjectId.value = saved.id
  }
  return project
}

async function migrateStoredProjects(): Promise<void> {
  if (localStorage.getItem(PROJECTS_STORAGE_KEY) === null) return
  const stored = loadStoredProjects()
  for (const project of stored) {
    if (isForbiddenProjectPath(project.path, appInfo.value.userProfilePath)) continue
    await window.haoyue.daemon.request('project.upsert', {
      id: project.id,
      path: project.path,
      name: project.name
    })
  }
  localStorage.removeItem(PROJECTS_STORAGE_KEY)
}

async function removeInvalidProjectRows(): Promise<void> {
  // Older builds could register the user profile (or ~/.haoyue) as a project, which
  // made every plain folder under the profile share one session scope. Drop those rows
  // now; sessions are preserved in the database instead of being deleted with them.
  const invalid = projects.value.filter((project) =>
    isForbiddenProjectPath(project.path, appInfo.value.userProfilePath))
  for (const project of invalid) {
    try {
      await window.haoyue.daemon.request('project.remove', { id: project.id, keepSessions: true })
    } catch {
      continue // leave the row in place; the next launch retries the cleanup
    }
    projects.value = projects.value.filter((item) => item.id !== project.id)
    if (selectedProjectId.value === project.id) selectedProjectId.value = ''
  }
}

function showActiveProject(): void {
  if (activeProject.value) void window.haoyue.showItemInFolder(activeProject.value.path)
}

/** 打开工作区面板的指定页签；再次点击已激活页签视为关闭（WorkBuddy 式切换语义）。 */
function openWorkDock(tab: WorkDockTab): void {
  if (tab !== 'tools' && !activeProject.value) return
  if (tab !== 'tools') toolDiff.value = null
  workDockOpen.value = !(workDockOpen.value && workDockTab.value === tab)
  workDockTab.value = tab
}

function openDevTools(): void {
  void window.haoyue.openDevTools()
}

function openToolDiff(path: string, diff: string): void {
  toolDiff.value = { path, diff }
  workDockTab.value = 'changes'
  workDockOpen.value = true
}

function closeWorkDock(): void {
  workDockOpen.value = false
  toolDiff.value = null
}

function resizeWorkDock(width: number): void {
  if (!Number.isFinite(width)) return
  const maxWidth = Math.min(720, Math.max(280, Math.floor(window.innerWidth * 0.66)))
  workDockWidth.value = Math.min(maxWidth, Math.max(280, Math.round(width)))
}

async function openProjectTerminal(): Promise<void> {
  if (!activeProject.value) return
  await window.haoyue.project.openTerminal(activeProject.value.path)
}

function applyTheme(value: AppearanceTheme): void {
  theme.value = value
  document.documentElement.dataset.theme = value
  localStorage.setItem('haoyue-theme', value)
  void window.haoyue.setTheme(value)
}

function isNearConversationBottom(element: HTMLElement, threshold = 48): boolean {
  return element.scrollHeight - element.scrollTop - element.clientHeight <= threshold
}

function measureConversationViewport(): void {
  if (scrollArea.value) conversationViewportHeight.value = scrollArea.value.clientHeight
}

function handleConversationScroll(): void {
  if (scrollArea.value) conversationScrollTop.value = scrollArea.value.scrollTop
  const element = scrollArea.value
  if (element) autoFollowConversation.value = isNearConversationBottom(element)
}

async function scrollToBottom(smooth = false, force = false): Promise<void> {
  await nextTick()
  const element = scrollArea.value
  if (!element || (!force && !autoFollowConversation.value)) return
  element.scrollTo({ top: element.scrollHeight, behavior: smooth ? 'smooth' : 'auto' })
}

async function handleScheduleUpdated(): Promise<void> {
  if (!daemonState.value.connected) return
  await refreshAllProjectSessions().catch(() => undefined)
}

async function refreshProjectSessions(project: ProjectItem): Promise<void> {
  await refreshProjectSessionsFromStore(sessionSyncContext, project)
}

async function refreshGlobalSessions(): Promise<void> {
  await refreshGlobalSessionsFromStore(sessionSyncContext)
}

async function refreshAllProjectSessions(): Promise<void> {
  await refreshAllProjectSessionsFromStore(sessionSyncContext, projects.value)
}

async function migrateImplicitDocumentsProject(): Promise<void> {
  if (localStorage.getItem(IMPLICIT_DOCUMENTS_MIGRATION_KEY) === '1') return
  if (!appInfo.value.documentsPath) return

  const project = projects.value.find((item) => samePath(item.path, appInfo.value.documentsPath))
  if (!project) {
    localStorage.setItem(IMPLICIT_DOCUMENTS_MIGRATION_KEY, '1')
    return
  }

  // Only remove the legacy entry after the Runtime has confirmed that it owns no sessions.
  // A failed request leaves the migration pending so a later reconnect can retry safely.
  try {
    await refreshProjectSessions(project)
  } catch {
    return
  }

  if (!threads.value.some((thread) => thread.projectId === project.id)) {
    await window.haoyue.daemon.request('project.remove', { id: project.id })
    projects.value = projects.value.filter((item) => item.id !== project.id)
    if (selectedProjectId.value === project.id) selectedProjectId.value = ''
    if (activeThread.value?.projectId === project.id) activeThreadId.value = ''
  }
  localStorage.setItem(IMPLICIT_DOCUMENTS_MIGRATION_KEY, '1')
}

async function loadRuntimeState(): Promise<void> {
  try {
    await migrateStoredProjects()
    void refreshEvolutionCandidates() // restore the candidate banner after reconnect
    const [projectResponse, modelResponse, workspaceResponse, modeResponse, catalogResponse, configStatusResponse] = await Promise.all([
      window.haoyue.daemon.request('project.list'),
      window.haoyue.daemon.request('model.list'),
      window.haoyue.daemon.request('workspace.get'),
      window.haoyue.daemon.request('agent.mode.get'),
      window.haoyue.daemon.request('model.catalog'),
      window.haoyue.daemon.request('config.status').catch(() => null)
    ])
    if (configStatusResponse) {
      try {
        const status = JSON.parse(configStatusResponse.data) as {
          hasAnomaly: boolean
          detail?: string
          configFile?: string
          backupFile?: string
          secretWarnings?: string[]
        }
        secretWarnings.value = status.secretWarnings ?? []
        if (status.hasAnomaly) {
          configAnomaly.value = status
        } else {
          configAnomaly.value = null
        }
      } catch { /* ignore parse error */ }
    }
    projects.value = (JSON.parse(projectResponse.data) as RuntimeProject[]).map((project) => ({
      id: project.id,
      name: project.name || pathName(project.path),
      path: project.path,
      loaded: false
    }))
    await removeInvalidProjectRows()
    const available = JSON.parse(modelResponse.data) as string[]
    const catalog = JSON.parse(catalogResponse.data) as RuntimeModelCatalogItem[]
    const workspace = JSON.parse(workspaceResponse.data) as RuntimeWorkspace
    const currentProject = projects.value.find((project) => samePath(project.path, workspace.path))
    if (currentProject && workspace.name) currentProject.name = workspace.name
    runtimeWorkspacePath.value = workspace.path
    if (!projects.value.some((project) => project.id === selectedProjectId.value))
      selectedProjectId.value = currentProject?.id ?? ''
    models.value = available
    modelCatalog.value = catalog
    activeModel.value = catalog.find((model) => model.active)?.ref
      ?? (available.length > 0
        ? (available.includes(activeModel.value) ? activeModel.value : available[0] ?? '')
        : '')
    mode.value = modeResponse.data
    await refreshAllProjectSessions()
    await migrateImplicitDocumentsProject()
    await replayMissedTerminals()
    await reconcileInterruptedTurns()

    if (!activeThread.value) {
      const recent = threads.value
        .filter((thread) => !thread.archived)
        .sort((left, right) => right.updatedAt - left.updatedAt)[0]
      if (recent) {
        activeThreadId.value = recent.id
        selectedProjectId.value = recent.projectId ?? ''
        await selectThread(recent.id)
      }
    } else if (activeThread.value && !activeThread.value.sessionLoaded) {
      await selectThread(activeThread.value.id)
    }
  } catch {
    models.value = []
    modelCatalog.value = []
  }
}

// N17: after a daemon crash + restart, the startup recovery appends interruption
// notices and records the recovered session ids. The client may reconnect long
// after that broadcast window, so it pulls the list via agent.interrupted and
// clears the stale "running" state that would otherwise stick forever.
async function reconcileInterruptedTurns(): Promise<void> {
  if (!daemonState.value.connected) return
  let sessionIds: string[] = []
  try {
    const response = await window.haoyue.daemon.request('agent.interrupted')
    sessionIds = JSON.parse(response.data) as string[]
  } catch {
    return
  }
  if (!Array.isArray(sessionIds) || sessionIds.length === 0) return
  const interruptedIds = new Set(sessionIds)
  const hits = threads.value.filter((thread) => thread.sessionId && interruptedIds.has(thread.sessionId))
  if (hits.length === 0) return
  for (const thread of hits) markThreadInterrupted(thread)
  void window.haoyue?.notify?.(
    '回合被中断',
    hits.length === 1
      ? `「${hits[0]!.title}」的回合因 Runtime 重启而中断，可重新发送消息继续。`
      : `${hits.length} 个任务的回合因 Runtime 重启而中断，可重新发送消息继续。`)
  const active = activeThread.value
  if (active?.sessionId && interruptedIds.has(active.sessionId)) {
    await selectThread(active.id)
  }
}

// 断线期间结束的回合收不到实时终态帧：帧带的是请求 id，重连后那条请求已经不存在了。
// 台账行是唯一线索，于是带上「见过的最大行号」只补播缺的那几行；否则任务会永远卡在运行中，
// 队列也不会继续下发。
let lastJournalId = 0

async function replayMissedTerminals(): Promise<void> {
  if (!daemonState.value.connected) return
  const opens = openTurnsOf(threads.value)
  if (opens.length === 0) return
  let rows: JournalRow[] = []
  try {
    const response = await window.haoyue.daemon.request('events.recent', { limit: 500, after: lastJournalId })
    const parsed = JSON.parse(response.data) as JournalRow[]
    if (Array.isArray(parsed)) rows = parsed
  } catch {
    return
  }
  lastJournalId = journalCursor(rows, lastJournalId)
  const recovered = recoverTurns(rows, opens)
  for (const item of recovered) {
    const thread = threads.value.find((candidate) => candidate.id === item.threadId)
    if (thread) await applyRecoveredTurn(thread, item)
  }
  if (recovered.length === 0) return
  const titles = recovered
    .map((item) => threads.value.find((candidate) => candidate.id === item.threadId)?.title ?? '任务')
  void window.haoyue?.notify?.(
    '已补回断线期间的结果',
    recovered.length === 1
      ? `「${titles[0]}」在连接中断期间已经结束，结果与失败原因已补回。`
      : `${recovered.length} 个任务（${titles.join('、')}）在连接中断期间已经结束，结果已补回。`)
}

/** 台账行没有回答正文，先回会话存储整体重载，再补终态标记——顺序反了会被水合覆盖掉。 */
async function applyRecoveredTurn(thread: ThreadItem, recovered: RecoveredTurn): Promise<void> {
  thread.running = false
  thread.requestId = undefined
  thread.assistantId = undefined
  thread.phase = undefined
  thread.activeTurnToken = undefined
  thread.pendingGuidance = 0
  finalizeAssistantBubbles(thread.messages, recovered.outcome === 'error' ? 'error' : 'done')
  thread.undoableFiles = recovered.undoableFiles
  await reloadThreadSession(thread, projects.value.find((project) => project.id === thread.projectId))
  const bubble = thread.messages.findLast((item) => item.role === 'assistant')
  if (bubble) {
    if (recovered.finishReason) bubble.finishReason = recovered.finishReason
    if (recovered.outcome === 'cancelled') bubble.interrupted = true
    if (recovered.outcome === 'error') {
      bubble.state = 'error'
      bubble.errorDetail = recovered.error ?? bubble.errorDetail
      if (!bubble.content) bubble.content = formatTurnFailure(recovered.error ?? '').content
    }
  }
  scheduleQueuedDrain(thread)
}

async function handleRebuildConfig(): Promise<void> {
  rebuildingConfig.value = true
  try {
    await window.haoyue.daemon.request('config.rebuild')
    configAnomaly.value = null
    await loadRuntimeState()
  } catch (err) {
    console.error('Failed to rebuild config:', err)
  } finally {
    rebuildingConfig.value = false
  }
}

// H24: reconnect success deserves its own feedback — previously the UI just silently
// returned to normal and users could not tell whether their running tasks survived.
let hasBeenConnected = false
function handleDaemonState(state: DaemonState): void {
  const wasConnected = daemonState.value.connected
  daemonState.value = state
  if (state.connected) {
    automaticReconnectPaused = false
    reconnectPrompt.value = null
    if (wasConnected === false && hasBeenConnected) {
      const runningCount = threads.value.filter((thread) => thread.running).length
      void window.haoyue?.notify?.(
        '已重新连接',
        runningCount > 0 ? `连接已恢复，${runningCount} 个运行中的任务已恢复跟踪。` : '连接已恢复。')
    }
    hasBeenConnected = true
    return
  }
  if (conversationLoading.value) {
    conversationLoading.value = false
    conversationLoadError.value = '已与 Haoyue Runtime 断开连接，正在尝试重连…'
  }
  if (!appReadyForRecovery || reconnecting.value || reconnectTask || automaticReconnectPaused || reconnectPrompt.value) return
  void runReconnectCycle(false)
}

async function runReconnectCycle(startup: boolean): Promise<boolean> {
  if (reconnectTask) return reconnectTask
  reconnectTask = (async () => {
    reconnecting.value = true
    reconnectPrompt.value = null
    const state = await retryRuntimeConnection(
      () => window.haoyue.daemon.connect(),
      { onAttempt: (attempt) => { reconnectAttempt.value = attempt } })
    daemonState.value = state
    if (state.connected) {
      automaticReconnectPaused = false
      await loadRuntimeState()
      return true
    }
    reconnectPrompt.value = { startup, error: state.error }
    return false
  })().finally(() => {
    reconnecting.value = false
    reconnectAttempt.value = 0
    reconnectTask = null
  })
  return reconnectTask
}

async function reconnectDaemon(): Promise<void> {
  automaticReconnectPaused = false
  reconnectPrompt.value = null
  if (daemonState.value.connected) {
    await loadRuntimeState()
    return
  }
  await runReconnectCycle(false)
}

async function refreshRuntimeState(): Promise<void> {
  if (daemonState.value.connected) await loadRuntimeState()
  else await reconnectDaemon()
}

function continueRuntimeReconnect(): void {
  const startup = reconnectPrompt.value?.startup ?? false
  reconnectPrompt.value = null
  automaticReconnectPaused = false
  void runReconnectCycle(startup)
}

function cancelRuntimeReconnect(): void {
  if (reconnectPrompt.value?.startup) {
    void window.haoyue.closeApp()
    return
  }
  reconnectPrompt.value = null
  automaticReconnectPaused = true
}

async function openWorkspace(): Promise<void> {
  const path = await window.haoyue.selectWorkspace()
  if (!path) return
  if (isForbiddenProjectPath(path, appInfo.value.userProfilePath)) {
    // H14: window.alert is disabled in this Electron build — use the notification channel.
    void window.haoyue?.notify?.('无法添加项目', '不能把用户主目录或 Haoyue 数据目录添加为项目，请选择具体的项目文件夹。')
    return
  }
  const project = await saveProject(ensureProject(path))
  selectedProjectId.value = project.id
  activeThreadId.value = ''
  conversationSelectionToken.value++
  conversationLoading.value = false
  conversationLoadError.value = ''
  await refreshProjectSessions(project).catch(() => undefined)
}

function openSettings(section: typeof settingsSection.value = 'general'): void {
  activePropertiesProject.value = null
  taskSettingsThreadId.value = ''
  settingsSection.value = section
  activePage.value = 'settings'
}

function openExtensions(section: 'mcp' | 'skills' | 'experts' = 'mcp'): void {
  activePropertiesProject.value = null
  taskSettingsThreadId.value = ''
  extensionsSection.value = section
  activePage.value = 'extensions'
}

const expertNames = ref<Record<string, string>>({})

async function expertLabelFor(id: string): Promise<string> {
  if (expertNames.value[id]) return expertNames.value[id]
  try {
    const response = await window.haoyue.daemon.request('expert.list', {})
    const experts = JSON.parse(response.data) as Array<{ id: string, name: string, avatar: string }>
    const map: Record<string, string> = {}
    for (const expert of experts) map[expert.id] = `${expert.avatar} ${expert.name}`
    expertNames.value = map
  } catch {
    // 目录拉取失败时回退为原始 id 展示
  }
  return expertNames.value[id] ?? id
}

function startExpertTask(expert: { id: string, name: string, avatar: string }): void {
  closePage()
  newTask(selectedProjectId.value || undefined)
  const thread = activeThread.value
  if (!thread) return
  thread.expertId = expert.id
  thread.expertLabel = `${expert.avatar} ${expert.name}`
  void nextTick(() => composer.value?.focus())
}

async function removeExpert(thread: ThreadItem): Promise<void> {
  const hadSession = Boolean(thread.sessionId)
  thread.expertId = undefined
  thread.expertLabel = undefined
  if (hadSession && daemonState.value.connected) {
    try {
      await window.haoyue.daemon.request('session.update', {
        id: thread.sessionId,
        ...sessionScope(thread, projects.value.find((item) => item.id === thread.projectId)),
        expertId: ''
      })
    } catch (error) {
      void window.haoyue?.notify?.('解除专家失败', error instanceof Error ? error.message : String(error))
    }
  }
}

function openArchivedTasks(): void {
  activePropertiesProject.value = null
  taskSettingsThreadId.value = ''
  activePage.value = 'archived'
}

function openScheduledTasks(): void {
  activePropertiesProject.value = null
  taskSettingsThreadId.value = ''
  activePage.value = 'scheduled'
}

function openKnowledge(): void {
  activePropertiesProject.value = null
  taskSettingsThreadId.value = ''
  activePage.value = 'knowledge'
}

function openRulesMemory(): void {
  activePropertiesProject.value = null
  taskSettingsThreadId.value = ''
  activePage.value = 'rules-memory'
}

function closePage(): void {
  activePage.value = 'main'
}

function selectArchivedThread(id: string): void {
  activePage.value = 'main'
  void selectThread(id)
}

function newTask(projectId?: string): void {
  const project = projectId ? projects.value.find((item) => item.id === projectId) : undefined
  if (projectId && !project) return
  // 当前任务已是「未开始的新任务」且目标作用域一致时直接复用，避免连点堆积空线程。
  const current = activeThread.value
  if (current && !current.sessionId && !current.running && current.messages.length === 0
    && (current.projectId ?? undefined) === (project?.id ?? undefined)
    && current.id === activeThreadId.value) {
    selectedProjectId.value = project?.id ?? ''
    void nextTick(() => composer.value?.focus())
    return
  }
  // 先把当前输入框内容归档为旧任务草稿，再清空输入框，避免文字归属到新任务。
  saveComposerDraft(current?.id)
  composer.value?.setValue('')
  const thread: ThreadItem = {
    id: makeId(),
    title: '新任务',
    projectId: project?.id,
    updatedAt: Date.now(),
    messages: [],
    reasoningLevel: ReasoningLevel.High,
    networkEnabled: true,
    archived: false,
    stats: {
      llmRounds: 0,
      executionSteps: 0,
      inputTokens: 0,
      outputTokens: 0,
      totalInputTokens: 0,
      cachedInputTokens: 0,
      outputElapsedMs: 0
    }
  }
  threads.value.unshift(thread)
  selectedProjectId.value = project?.id ?? ''
  activeThreadId.value = thread.id
  // Invalidate an in-flight session read for the previously selected task.
  conversationSelectionToken.value++
  conversationLoading.value = false
  conversationLoadError.value = ''
  void nextTick(() => composer.value?.focus())
}

async function branchFromMessage(message: ChatMessage): Promise<void> {
  const currentThread = activeThread.value
  if (!currentThread) return
  const messageIndex = currentThread.messages.findIndex((m) => m.id === message.id)
  if (messageIndex < 0) return

  const project = projects.value.find((item) => item.id === currentThread.projectId)
  const slicedMessages = currentThread.messages.slice(0, messageIndex + 1)
  const branchTitle = currentThread.title.includes('分支')
    ? currentThread.title
    : `${currentThread.title} (分支)`

  let newSessionId: string | undefined
  if (currentThread.sessionId && daemonState.value.connected) {
    // 后端按会话原始行序分叉：只有携带持久化行号后缀的消息才能安全分叉。
    const match = /^.*:(\d+)$/.exec(message.id)
    if (!match?.[1]) {
      void window.haoyue?.notify?.('无法分叉', '该消息尚未与后端会话同步，请等待会话加载完成后重试。')
      return
    }
    const keepCount = parseInt(match[1], 10) + 1
    try {
      const response = await window.haoyue.daemon.request('session.fork', {
        id: currentThread.sessionId,
        ...sessionScope(currentThread, project),
        keepCount,
        title: branchTitle
      })
      const data = JSON.parse(response.data) as { id: string }
      newSessionId = data.id
    } catch (err) {
      // 分叉失败时不得本地伪造分支（会出现前端有历史、后端无会话的幽灵任务）。
      void window.haoyue?.notify?.('分叉失败', err instanceof Error ? err.message : String(err))
      return
    }
  }

  const clonedMessages: ChatMessage[] = slicedMessages.map((m, idx) => ({
    ...m,
    id: newSessionId ? `${newSessionId}:${idx}` : makeId(),
    tools: m.tools?.map((t) => ({ ...t })),
    images: m.images ? [...m.images] : undefined,
    files: m.files ? [...m.files] : undefined,
    viewedImages: m.viewedImages ? [...m.viewedImages] : undefined,
    warnings: m.warnings ? [...m.warnings] : undefined
  }))

  const newThreadId = newSessionId
    ? (project ? `${project.id}:session:${newSessionId}` : `global:session:${newSessionId}`)
    : makeId()

  const newThread: ThreadItem = {
    id: newThreadId,
    title: branchTitle,
    projectId: project?.id,
    updatedAt: Date.now(),
    messages: clonedMessages,
    sessionId: newSessionId,
    sessionLoaded: true,
    reasoningLevel: currentThread.reasoningLevel ?? ReasoningLevel.High,
    networkEnabled: currentThread.networkEnabled ?? true,
    archived: false,
    stats: {
      llmRounds: 0,
      executionSteps: 0,
      inputTokens: 0,
      outputTokens: 0,
      totalInputTokens: 0,
      cachedInputTokens: 0,
      outputElapsedMs: 0
    }
  }

  threads.value.unshift(newThread)
  await selectThread(newThread.id)
  void nextTick(() => composer.value?.focus())
}

const editingUserMessage = ref<ChatMessage | null>(null)
const editMessageModifiedFiles = ref<string[]>([])
const editMessagePatches = ref<Array<{ filePath: string; diff: string }>>([])
const editMessageDialogOpen = ref(false)

function onEditUserMessage(message: ChatMessage): void {
  const thread = activeThread.value
  if (!thread || thread.archived) return
  if (thread.running || thread.queueDraining) {
    void window.haoyue?.notify?.('无法编辑', '当前回合正在执行，请先等待或停止生成后再进行编辑。')
    return
  }

  const promptIndex = thread.messages.findIndex((item) => item.id === message.id)
  if (promptIndex < 0) return

  // Collect subsequent file patches & modified files
  const patches: Array<{ filePath: string; diff: string }> = []
  const filesSet = new Set<string>()

  for (let i = promptIndex + 1; i < thread.messages.length; i++) {
    const msg = thread.messages[i]
    if (!msg) continue
    for (const tool of msg.tools ?? []) {
      if (tool.filePath && tool.diff) {
        patches.push({ filePath: tool.filePath, diff: tool.diff })
        filesSet.add(tool.filePath)
      }
    }
  }

  editingUserMessage.value = message
  editMessageModifiedFiles.value = Array.from(filesSet)
  editMessagePatches.value = patches
  editMessageDialogOpen.value = true
}

async function handleEditConfirm(revertFiles: boolean): Promise<void> {
  const thread = activeThread.value
  const message = editingUserMessage.value
  const patches = editMessagePatches.value
  editMessageDialogOpen.value = false
  if (!thread || !message) return

  const promptIndex = thread.messages.findIndex((item) => item.id === message.id)
  if (promptIndex < 0) return

  const project = projects.value.find((item) => item.id === thread.projectId)
  const workspace = project?.path || appInfo.value.defaultWorkspace

  // Step 1: If reverting file modifications, apply diffs in reverse
  if (revertFiles && patches.length > 0) {
    try {
      const res = await window.haoyue.project.revertFileDiffs(workspace, patches)
      if (res.failed.length > 0) {
        const failedSummary = res.failed
          .map((f) => `• ${f.filePath} (${f.reason})`)
          .join('\n')
        await confirmAction({
          title: '部分文件撤销失败',
          message: `以下文件未能成功自动撤回（可能已被外部修改或存在冲突），建议您手动检查：\n${failedSummary}`,
          confirmLabel: '我知道了',
          danger: true
        })
      }
    } catch (err) {
      await confirmAction({
        title: '撤销文件修改失败',
        message: `撤回文件修改时出错：${err instanceof Error ? err.message : String(err)}`,
        confirmLabel: '我知道了',
        danger: true
      })
    }
  }

  // Step 2: Truncate backend session in SQLite
  if (thread.sessionId) {
    const match = message.id ? /^.*:(\d+)$/.exec(message.id) : null
    if (!match?.[1]) {
      // 无持久化行号时禁止用本地索引猜测后端行号（tool 行合并会导致错位）。
      void window.haoyue?.notify?.('无法编辑', '该消息尚未与后端会话同步，请等待会话加载完成后重试。')
      return
    }
    try {
      await window.haoyue.daemon.request('session.truncate', {
        id: thread.sessionId,
        ...sessionScope(thread, project),
        keepCount: parseInt(match[1], 10)
      })
    } catch (err) {
      // 截断失败必须中止：否则前端删了消息、后端还在，下次 reload 全部复活。
      void window.haoyue?.notify?.('无法编辑', `会话截断失败：${err instanceof Error ? err.message : String(err)}`)
      return
    }
  }

  // Step 3: Truncate frontend messages（1.2：截断前把被替换的整段历史压入版本链）
  preserveTruncatedVersions(thread, promptIndex, 'edit')
  thread.messages = thread.messages.slice(0, promptIndex)
  thread.phase = undefined
  thread.assistantId = undefined

  // Step 4: Populate Composer and focus
  if (typeof composer.value?.populate === 'function') {
    composer.value.populate(message.content || '', message.images, message.files)
  } else {
    composer.value?.setValue(message.content || '')
    composer.value?.focus()
  }
}

async function ensureRuntimeProject(project: ProjectItem): Promise<void> {
  if (samePath(runtimeWorkspacePath.value, project.path)) return
  const response = await window.haoyue.daemon.request('workspace.open', { path: project.path })
  const opened = JSON.parse(response.data) as RuntimeWorkspace
  project.path = opened.path
  project.name = opened.name || project.name
  await saveProject(project)
  runtimeWorkspacePath.value = opened.path
  mode.value = opened.mode
}

async function reloadThreadSession(thread: ThreadItem, project?: ProjectItem): Promise<void> {
  await reloadThreadSessionFromStore(sessionSyncContext, thread, project)
}

/** 向前翻页并把视口锚回原来那条消息：前插会让旧内容整体下移。 */
async function loadEarlier(): Promise<void> {
  const thread = activeThread.value
  if (!thread) return
  const element = scrollArea.value
  const heightBefore = element?.scrollHeight ?? 0
  const topBefore = element?.scrollTop ?? 0
  await loadEarlierMessagesFromStore(sessionSyncContext, thread, activeProject.value)
  await nextTick()
  const delta = (element?.scrollHeight ?? 0) - heightBefore
  if (element && delta > 0) element.scrollTop = topBefore + delta
}

async function selectThread(id: string): Promise<void> {
  const thread = threads.value.find((item) => item.id === id)
  if (!thread) return
  if (activeThread.value) saveComposerDraft(activeThread.value.id)
  // 1.6 切走前记住阅读位；任务切换同时释放虚拟列表高度缓存（否则内存单调增长）。
  if (activeThread.value) saveScrollPosition(activeThread.value.id, conversationScrollTop.value)
  messageHeights.clear()
  const project = projects.value.find((item) => item.id === thread.projectId)
  if (thread.projectId && !project) return
  const selectionToken = ++conversationSelectionToken.value
  const needsLoad = Boolean(thread.sessionId && (!thread.sessionLoaded || !thread.running))
  activeThreadId.value = id
  selectedProjectId.value = project?.id ?? ''
  conversationLoadError.value = ''

  if (needsLoad && !daemonState.value.connected) {
    conversationLoading.value = false
    conversationLoadError.value = '未连接到 Haoyue Runtime，请检查运行时状态或点击重试。'
    return
  }

  conversationLoading.value = needsLoad
  try {
    if (project) await ensureRuntimeProject(project)
    const scope = sessionScope(thread, project)
    if (thread.sessionId && (!thread.sessionLoaded || !thread.running)) {
      const response = await window.haoyue.daemon.request('session.get', {
        id: thread.sessionId,
        limit: SESSION_PAGE_SIZE,
        ...scope
      })
      if (selectionToken !== conversationSelectionToken.value) return
      // 让出事件循环，确保骨架屏平滑渲染、侧栏点击与拖拽无卡顿
      await new Promise((resolve) => setTimeout(resolve, 0))
      if (selectionToken !== conversationSelectionToken.value) return

      const saved = JSON.parse(response.data) as RuntimeSession
      const offset = saved.firstIndex ?? 0
      thread.messages = hydrateMessages(saved, offset)
      thread.sessionLoaded = true
      thread.sessionTotal = saved.total ?? thread.messages.length
      thread.sessionFirstIndex = offset
      thread.sessionHasMore = Boolean(saved.hasMore)
      thread.title = saved.title || thread.title
      thread.archived = Boolean(saved.archived)
      thread.reasoningLevel = normalizeReasoningLevel(saved.reasoningLevel)
      thread.networkEnabled = saved.networkEnabled ?? true
      thread.expertId = saved.expertId || undefined
      if (saved.expertId) void expertLabelFor(saved.expertId).then((label) => { thread.expertLabel = label })
      else thread.expertLabel = undefined
      thread.stats = sessionStats(saved)
    }
  } catch (error) {
    thread.sessionLoaded = false
    if (selectionToken === conversationSelectionToken.value) {
      conversationLoadError.value = error instanceof Error
        ? error.message
        : '无法读取此会话，请检查 Runtime 连接后重试。'
    }
  } finally {
    if (selectionToken === conversationSelectionToken.value) {
      conversationLoading.value = false
    }
  }
  if (selectionToken !== conversationSelectionToken.value) return
  // 无条件按目标任务的草稿回填（无草稿则清空），附件与文字一起恢复，避免上一个任务
  // 的输入串扰到当前任务，也不再出现「切个任务附件就没了」的静默丢失（H5）。
  const draft = composerDrafts.get(thread.id)
  composer.value?.populate(draft?.text ?? '', draft?.images, draft?.files)
  // 1.6 阅读位恢复：有记忆时回到原位置（原值直存，偏差 <1px）而不是无条件回底。
  const restoreTop = readScrollPosition(thread.id)
  if (restoreTop > 0) {
    autoFollowConversation.value = false
    await nextTick()
    if (scrollArea.value) scrollArea.value.scrollTop = restoreTop
  } else {
    autoFollowConversation.value = true
    await scrollToBottom(false, true)
  }
}

// 跨会话检索命中：本地列表没有该会话（未加载项目或已归档）时，按检索结果重建一个
// 懒加载 ThreadItem（sessionLoaded=false，selectThread 会走 session.get 拉取）。
async function openSearchSession(hit: SessionSearchHit): Promise<void> {
  const existing = threads.value.find((thread) => thread.sessionId === hit.id)
  if (existing) {
    await selectThread(existing.id)
    return
  }
  const project = hit.workspace
    ? projects.value.find((item) => item.path === hit.workspace)
    : undefined
  if (hit.workspace && !project) {
    void window.haoyue?.notify?.('无法打开会话', `会话所属项目尚未添加：${hit.workspace}`)
    return
  }
  const thread: ThreadItem = {
    id: project ? `${project.id}:session:${hit.id}` : `global:session:${hit.id}`,
    title: hit.title || '未命名会话',
    projectId: project?.id,
    updatedAt: hit.updatedAt ? new Date(hit.updatedAt).getTime() : Date.now(),
    messages: [],
    sessionId: hit.id,
    sessionLoaded: false,
    reasoningLevel: ReasoningLevel.High,
    networkEnabled: true,
    archived: Boolean(hit.archived)
  }
  threads.value.unshift(thread)
  await selectThread(thread.id)
}

interface ConversationItem {
  message: ChatMessage
  showFooter: boolean
}

const conversationItems = computed<ConversationItem[]>(() => {
  const thread = activeThread.value
  const messages = (thread?.messages ?? []).filter(
    (message) => !message.content?.startsWith('>>> [output truncated]')
  )
  const isThreadRunning = thread?.running === true
  const lastUserIndex = messages.findLastIndex((m) => m.role === 'user')

  return messages.map((message, index) => {
    let showFooter = false
    if (message.role === 'assistant' && Boolean(message.content?.trim())) {
      // If this message belongs to the actively running turn, do not show footer
      const inActiveRunningTurn = isThreadRunning && (lastUserIndex < 0 || index >= lastUserIndex)
      if (!inActiveRunningTurn) {
        // Must be the last assistant message in this turn
        let hasLaterAssistant = false
        for (let j = index + 1; j < messages.length; j++) {
          const next = messages[j]
          if (!next || next.role === 'user') break
          if (next.role === 'assistant') {
            hasLaterAssistant = true
            break
          }
        }
        showFooter = !hasLaterAssistant
      }
    }
    return { message, showFooter }
  })
})

/**
 * The plan shown above the composer, straight from the Agent's own `update_plan`
 * calls. Turns where the Agent did not create a plan render nothing.
 */
const activeThreadTurnSteps = computed<TaskStep[]>(() => {
  const thread = activeThread.value
  if (!thread) return []

  const messages = thread.messages ?? []
  const lastUserIndex = messages.findLastIndex((m) => m.role === 'user')
  const turnAssistants = (lastUserIndex >= 0 ? messages.slice(lastUserIndex + 1) : messages)
    .filter((m) => m.role === 'assistant')

  return computeTurnTaskSteps({
    turnAssistants,
    isTurnRunning: thread.running === true,
    customPlan: thread.customPlan
  })
})

const VIRTUAL_THRESHOLD = 60
const VIRTUAL_OVERSCAN = 12
const VIRTUAL_ESTIMATED_HEIGHT = 120

/** Windowed rendering for very long conversations: only messages near the viewport are mounted. */
const virtualWindow = computed(() => {
  const items = conversationItems.value
  const total = items.length
  if (total <= VIRTUAL_THRESHOLD || conversationQuery.value.trim()) {
    return { active: false, start: 0, end: total, topPad: 0, bottomPad: 0, items }
  }
  const heightOf = (index: number): number =>
    messageHeights.get(items[index]?.message.id ?? '') ?? VIRTUAL_ESTIMATED_HEIGHT

  const targetTop = Math.max(0, conversationScrollTop.value - VIRTUAL_OVERSCAN * VIRTUAL_ESTIMATED_HEIGHT)
  let topPad = 0
  let start = 0
  for (; start < total; start++) {
    const height = heightOf(start)
    if (topPad + height >= targetTop) break
    topPad += height
  }

  const targetBottom = conversationScrollTop.value + conversationViewportHeight.value
    + VIRTUAL_OVERSCAN * VIRTUAL_ESTIMATED_HEIGHT
  let end = start
  let acc = topPad
  while (end < total && acc < targetBottom) {
    acc += heightOf(end)
    end++
  }

  let bottomPad = 0
  for (let i = end; i < total; i++) bottomPad += heightOf(i)
  return { active: true, start, end, topPad, bottomPad, items: items.slice(start, end) }
})

const vMeasure = {
  mounted(el: HTMLElement, binding: { value: string }): void {
    const report = (): void => {
      const height = el.getBoundingClientRect().height
      if (height > 0) messageHeights.set(binding.value, height)
    }
    report()
    const observer = new ResizeObserver(report)
      ; (el as HTMLElement & { __heightObserver?: ResizeObserver }).__heightObserver = observer
    observer.observe(el)
  },
  unmounted(el: HTMLElement & { __heightObserver?: ResizeObserver }): void {
    el.__heightObserver?.disconnect()
  }
}

function continueAssistant(): void {
  const thread = activeThread.value
  if (!thread || thread.running) return
  void sendMessage('继续', [])
}

async function regenerateMessage(message: ChatMessage): Promise<void> {
  const thread = activeThread.value
  if (!thread || thread.running || !thread.sessionId) return
  if (!await resumeThreadIfArchived(thread)) return
  const index = thread.messages.findIndex((item) => item.id === message.id)
  if (index < 0) return
  // Re-run the turn from its user prompt: keep history through that prompt, then resend it.
  let promptIndex = -1
  for (let i = index; i >= 0; i--) {
    if (thread.messages[i]?.role === 'user') { promptIndex = i; break }
  }
  if (promptIndex < 0) return
  const promptMessage = thread.messages[promptIndex]
  const prompt = promptMessage?.content ?? ''
  const project = projects.value.find((item) => item.id === thread.projectId)
  // 后端 session.truncate 按会话原始行序截断；本地合并视图（tool 行并入 assistant 气泡）
  // 的索引与原始行号存在偏移，只有持久化消息 id 的后缀才携带权威行号。无法解析时中止，
  // 绝不用本地猜测值截断，否则会造成会话历史错乱。
  const match = promptMessage ? /^.*:(\d+)$/.exec(promptMessage.id) : null
  if (!match?.[1]) {
    void window.haoyue?.notify?.('无法重新生成', '该消息尚未与后端会话同步，请等待会话加载完成后重试。')
    return
  }
  const keepCount = parseInt(match[1], 10) + 1
  try {
    await window.haoyue.daemon.request('session.truncate', {
      id: thread.sessionId,
      ...sessionScope(thread, project),
      keepCount
    })
  } catch (err) {
    void window.haoyue?.notify?.('无法重新生成', `会话截断失败：${err instanceof Error ? err.message : String(err)}`)
    return
  }
  // 1.2 截断视图前保留被替换回复为会话内历史版本（可从 user 气泡「恢复历史回答」取回）。
  preserveTruncatedVersions(thread, promptIndex + 1, 'regenerate')
  thread.messages = thread.messages.slice(0, promptIndex + 1)
  thread.phase = undefined
  await sendMessage(prompt, [])
}

// 1.2 / 1.5 / 1.6 的会话级动作（删除撤销、版本恢复、slash 命令、会话导出）：
// 处理逻辑在 app-actions（依赖注入、可单测），这里只装配回调与共享状态。
const undoBarVisible = ref(false)
const chatActions = createChatActions({
  getThread: () => activeThread.value ?? null,
  setUndoVisible: (visible) => { undoBarVisible.value = visible },
  switchModel: (model) => void changeModel(model),
  switchMode: (nextMode) => void changeMode(nextMode),
  switchDepth: changeReasoningLevel,
  clearDraft: (thread) => { composerDrafts.delete(thread.id); composer.value?.setValue('') },
  saveTextFile: (name, content) => window.haoyue.saveTextFile(name, content),
  notify: (title, body) => void window.haoyue?.notify?.(title, body),
  models: () => models.value,
})

// 归档任务收到新消息时自动恢复会话（等价于手动「恢复任务」），失败时返回 false 并中止发送。
// H6：首次恢复弹轻量确认（归档语义不再被无感破坏）；用户确认过一次后记住选择，
// 之后的发送直接恢复，不再重复打扰（localStorage: haoyue.autoResumeArchived）。
const AUTO_RESUME_KEY = 'haoyue.autoResumeArchived'
async function resumeThreadIfArchived(thread: ThreadItem): Promise<boolean> {
  if (!thread.archived) return true
  const project = projects.value.find((item) => item.id === thread.projectId)
  if ((thread.projectId && !project) || !thread.sessionId) return false
  if (localStorage.getItem(AUTO_RESUME_KEY) !== '1' && !await confirmAction({
    title: '恢复已归档任务',
    message: '该任务已归档。发送消息将自动恢复任务并继续对话，是否继续？',
    confirmLabel: '恢复并继续'
  })) return false
  localStorage.setItem(AUTO_RESUME_KEY, '1')
  try {
    await window.haoyue.daemon.request('session.archive', {
      id: thread.sessionId,
      ...sessionScope(thread, project),
      archived: false
    })
  } catch (error) {
    conversationLoadError.value = error instanceof Error
      ? `恢复任务失败：${error.message}`
      : '恢复任务失败，请检查 Runtime 连接后重试。'
    return false
  }
  thread.archived = false
  thread.updatedAt = Date.now()
  return true
}

async function sendMessage(content: string, images: ImageAttachment[], files?: FileAttachment[]): Promise<void> {
  // 空状态修复：尚未创建任何任务时，直接输入会先自动新建任务再发送。
  if (!activeThread.value) newTask(selectedProjectId.value || undefined)
  const thread = activeThread.value
  const project = thread ? projects.value.find((item) => item.id === thread.projectId) : undefined
  if (!thread || (thread.projectId && !project)) return
  if (!content.trim() && images.length === 0 && (!files || files.length === 0)) return
  if (!await resumeThreadIfArchived(thread)) return
  if (thread.running || thread.queueDraining) {
    thread.queuedMessages ??= []
    thread.queuedMessages.push({ id: makeId(), content, images, files, createdAt: Date.now() })
    return
  }
  composerDrafts.delete(thread.id)
  persistDrafts()
  await runMessageTurn(thread, content, images, files)
}

/** 「本次允许」审批响应（P1-2.3）：回传后 daemon 放行/维持拒绝，事件流继续。 */
const approvalBusy = ref(false)
async function respondApproval(approve: boolean): Promise<void> {
  const thread = activeThread.value
  const approval = thread?.pendingApproval
  if (!approval || approvalBusy.value) return
  approvalBusy.value = true
  try {
    await window.haoyue.daemon.request('agent.respondApproval', {
      callId: approval.callId,
      approve
    })
    thread!.pendingApproval = undefined
  } catch {
    // 回传失败保留弹窗：daemon 侧超时自会按拒绝收场，用户也可重试。
  } finally {
    approvalBusy.value = false
  }
}

function rememberFinishedRequest(thread: ThreadItem, requestId: number): void {
  if (!Number.isFinite(requestId)) return
  thread.finishedRequestIds ??= []
  if (thread.finishedRequestIds.includes(requestId)) return
  thread.finishedRequestIds.push(requestId)
  // Keep this bounded; it only protects against delayed events from recent turns.
  if (thread.finishedRequestIds.length > 12) thread.finishedRequestIds.splice(0, thread.finishedRequestIds.length - 12)
}

// ---------------------------------------------------------------- turn undo (TurnScope)
const undoInFlight = ref(false)

/** One-shot revert of the latest finished turn's builtin file-tool changes. The daemon
 * keeps a single ledger per session and consumes it, so the button hides itself after. */
async function undoLastTurn(): Promise<void> {
  const thread = activeThread.value
  if (!thread?.sessionId || !thread.undoableFiles?.length || undoInFlight.value) return
  if (!daemonState.value.connected) return
  undoInFlight.value = true
  try {
    const response = await window.haoyue.daemon.request('agent.undo', { sessionId: thread.sessionId })
    if (response.event === 'error') {
      await window.haoyue.notify('撤销失败', response.data)
      return
    }
    thread.undoableFiles = undefined
    const project = projects.value.find((item) => item.id === thread.projectId)
    await reloadThreadSession(thread, project)
  } catch {
    await window.haoyue.notify('撤销失败', '无法连接 Runtime，请稍后重试')
  } finally {
    undoInFlight.value = false
  }
}

function isFinishedRequest(thread: ThreadItem, requestId: number): boolean {
  return thread.finishedRequestIds?.includes(requestId) === true
}

function scheduleQueuedDrain(thread: ThreadItem): void {
  if (!thread.running && !thread.queueDraining && !thread.archived && thread.queuedMessages?.length)
    void drainQueuedMessages(thread)
}

async function drainQueuedMessages(thread: ThreadItem): Promise<void> {
  if (thread.running || thread.queueDraining || thread.archived) return
  const next = thread.queuedMessages?.shift()
  if (!next) return
  thread.queueDraining = true
  try {
    await runMessageTurn(thread, next.content, next.images, next.files)
  } finally {
    thread.queueDraining = false
    scheduleQueuedDrain(thread)
    reloadBackgroundThreadIfIdle(thread)
  }
}

function removeQueuedMessage(thread: ThreadItem, id: string): void {
  if (!thread.queuedMessages) return
  thread.queuedMessages = thread.queuedMessages.filter((item) => item.id !== id)
}

async function steerQueuedMessage(thread: ThreadItem, queued: QueuedMessage): Promise<void> {
  if (!thread.running || !thread.sessionId || !daemonState.value.connected) return
  const index = thread.queuedMessages?.findIndex((item) => item.id === queued.id) ?? -1
  if (index < 0) return
  const project = projects.value.find((item) => item.id === thread.projectId)
  if (thread.projectId && !project) return
  const guidanceMessage: ChatMessage = {
    id: makeId(),
    role: 'user',
    content: queued.content,
    images: queued.images,
    files: queued.files,
    createdAt: Date.now()
  }
  // Reflect the steer immediately. The daemon request is still awaited below so
  // a rejection can put the item back into the normal queue without losing it.
  thread.queuedMessages?.splice(index, 1)
  thread.messages.push(guidanceMessage)
  thread.updatedAt = Date.now()
  thread.pendingGuidance = (thread.pendingGuidance ?? 0) + 1
  if (thread.id === activeThreadId.value) void scrollToBottom(true, true)
  try {
    await window.haoyue.daemon.request('agent.steer', {
      message: formatPromptWithFiles(queued.content, queued.files),
      images: plainImages(queued.images),
      sessionId: thread.sessionId,
      requestId: thread.requestId,
      ...sessionScope(thread, project)
    }, { timeoutMs: CHAT_FIRST_EVENT_TIMEOUT_MS })
  } catch {
    // Keep the message queued when the active turn has just finished or the Runtime rejects it.
    const messageIndex = thread.messages.findIndex((item) => item.id === guidanceMessage.id)
    if (messageIndex >= 0) thread.messages.splice(messageIndex, 1)
    thread.queuedMessages ??= []
    if (!thread.queuedMessages.some((item) => item.id === queued.id))
      thread.queuedMessages.splice(Math.min(index, thread.queuedMessages.length), 0, queued)
    thread.pendingGuidance = Math.max(0, (thread.pendingGuidance ?? 1) - 1)
  }
}

async function runMessageTurn(
  thread: ThreadItem,
  content: string,
  images: ImageAttachment[],
  files?: FileAttachment[]
): Promise<void> {
  const project = projects.value.find((item) => item.id === thread.projectId)
  if ((thread.projectId && !project) || thread.archived || thread.running) return
  const reasoningLevel = thread.reasoningLevel ?? ReasoningLevel.High
  const turnToken = makeId()

  const userMessage: ChatMessage = {
    id: makeId(),
    role: 'user',
    content,
    images,
    files,
    createdAt: Date.now()
  }
  const assistant: ChatMessage = {
    id: makeId(), role: 'assistant', content: '', thinking: '', tools: [], state: 'thinking', createdAt: Date.now()
  }
  thread.messages.push(userMessage, assistant)
  thread.updatedAt = Date.now()
  const titlePrompt = content.trim()
    || (files?.length ? `处理文件：${files.map((f) => f.name).join('、')}` : '')
    || `查看图片：${images.map((image) => image.name).join('、')}`
  const titleChanged = updateThreadTitle(thread, titlePrompt)
  thread.running = true
  thread.activeTurnToken = turnToken
  thread.assistantId = assistant.id
  thread.requestId = undefined
  let terminalAssistant = assistant
  autoFollowConversation.value = true
  await scrollToBottom(true, true)

  try {
    if (project) await ensureRuntimeProject(project)
    const scope = sessionScope(thread, project)
    let sessionCreated = false
    if (!thread.sessionId) {
      const sessionResponse = await window.haoyue.daemon.request('session.new', {
        ...scope,
        reasoningLevel,
        networkEnabled: thread.networkEnabled ?? true,
        ...(thread.expertId ? { expertId: thread.expertId } : {})
      })
      thread.sessionId = sessionResponse.data
      thread.sessionLoaded = true
      sessionCreated = true
    }
    if (sessionCreated || titleChanged) {
      await window.haoyue.daemon.request('session.update', {
        id: thread.sessionId,
        ...scope,
        title: thread.title
      })
    }
    const daemonPrompt = formatPromptWithFiles(content, files)
    const response = await window.haoyue.daemon.request('chat', {
      message: daemonPrompt,
      images: plainImages(images),
      sessionId: thread.sessionId,
      reasoningLevel,
      ...(thread.expertId ? { expertId: thread.expertId } : {}),
      ...scope
    }, { timeoutMs: CHAT_FIRST_EVENT_TIMEOUT_MS })
    rememberFinishedRequest(thread, response.id)
    const finalAssistant = thread.messages.findLast((item) => item.role === 'assistant') ?? assistant
    terminalAssistant = finalAssistant
    if (finalAssistant.state !== 'error') {
      finalAssistant.state = 'done'
      // Streamed delta events can lose the IPC race to the request's terminal
      // response, leaving the bubble empty even though the daemon finished.
      // Fall back to the latest assistant bubble: a mid-turn model step or steer
      // may have superseded the original placeholder captured above.
      if (!finalAssistant.content && response.data) finalAssistant.content = response.data
    }
  } catch (error) {
    const failedAssistant = thread.messages.findLast((item) => item.role === 'assistant') ?? assistant
    terminalAssistant = failedAssistant
    failedAssistant.state = 'error'
    // 一条文案不能说清三种事：后台没起来、后台起了但迟迟不回第一个字、请求被拒。
    // 以前全写成「无法连接 Haoyue Daemon」，用户照着提示去重启后台反而弄坏正在加载的回合。
    const failure = formatTurnFailure(error)
    failedAssistant.errorDetail = failure.detail
    if (!failedAssistant.content) {
      // 详情同时进 content：重开会话后只剩纯文本，「查看技术详情」靠它复原。
      failedAssistant.content = failure.content
    }
  } finally {
    // A terminal event may already have started the next queued turn. Do not let
    // this older request clear that newer turn's state.
    if (thread.activeTurnToken !== turnToken) return
    // Belt-and-braces: finalize every leftover "..." placeholder when this turn
    // settles. The captured assistant can be a stale bubble (a mid-turn steer
    // created a fresh one) and the terminal event itself may have been dropped
    // by the finished-request guard, so walk all messages instead of one.
    finalizeAssistantBubbles(thread.messages, terminalAssistant.state === 'error' ? 'error' : 'done')
    thread.activeTurnToken = undefined
    thread.running = false
    thread.requestId = undefined
    thread.assistantId = undefined
    if (thread.sessionId && terminalAssistant.state !== 'error') {
      await reloadThreadSession(thread, project).catch(() => undefined)
    }
    if (thread.id === activeThreadId.value) await scrollToBottom(true)
    scheduleQueuedDrain(thread)
  }
}

function reloadBackgroundThreadIfIdle(thread: ThreadItem): void {
  if (thread.id === activeThreadId.value
    || thread.running
    || thread.queueDraining
    || thread.queuedMessages?.length
    || thread.pendingGuidance)
    return
  void reloadThreadSession(thread, projects.value.find((project) => project.id === thread.projectId))
}

function openTaskSettings(thread: ThreadItem = activeThread.value!): void {
  if (thread) taskSettingsThreadId.value = thread.id
}

function openProjectProperties(project: ProjectItem): void {
  activePropertiesProject.value = project
}

async function saveTaskTitle(title: string): Promise<void> {
  const thread = settingsThread.value
  const project = settingsProject.value
  if (!thread || (thread.projectId && !project)) return
  thread.title = title
  if (thread.sessionId) {
    try {
      await window.haoyue.daemon.request('session.update', {
        id: thread.sessionId,
        ...sessionScope(thread, project),
        title
      })
    } catch (error) {
      void window.haoyue?.notify?.('重命名失败', error instanceof Error ? error.message : String(error))
    }
  }
  taskSettingsThreadId.value = ''
}

// H4: inline rename from the sidebar — optimistic update with rollback on failure.
async function renameTaskInline(thread: ThreadItem, title: string): Promise<void> {
  const project = projects.value.find((item) => item.id === thread.projectId)
  if (thread.projectId && !project) return
  const previous = thread.title
  thread.title = title
  if (!thread.sessionId) return
  try {
    await window.haoyue.daemon.request('session.update', {
      id: thread.sessionId,
      ...sessionScope(thread, project),
      title
    })
  } catch (error) {
    thread.title = previous
    void window.haoyue?.notify?.('重命名失败', error instanceof Error ? error.message : String(error))
  }
}

const {
  chooseAfterRemoval,
  archiveTask,
  restoreTask,
  archiveProjectTasks,
  archiveGlobalTasks,
  deleteTask,
  deleteGlobalTasks,
  deleteProjectTasks,
  deleteArchivedTasks,
  deleteProject
} = createThreadActions({
  projects,
  threads,
  activeThreadId,
  selectedProjectId,
  taskSettingsThreadId,
  daemonState,
  conversationLoading,
  conversationLoadError,
  conversationSelectionToken,
  selectThread,
  refreshProjectSessions,
  reconnectDaemon
})

async function initializeProjectWorkspace(project: ProjectItem): Promise<void> {
  if (!daemonState.value.connected) {
    await reconnectDaemon()
    if (!daemonState.value.connected) return
  }
  try {
    await ensureRuntimeProject(project)
    const response = await window.haoyue.daemon.request('workspace.init')
    const result = JSON.parse(response.data) as { created: string[] }
    await window.haoyue.notify(
      '工作区元数据已初始化',
      result.created.length > 0 ? `已创建 ${result.created.length} 项元数据` : '工作区元数据已就绪'
    )
  } catch (reason) {
    await window.haoyue.notify('工作区元数据初始化失败', reason instanceof Error ? reason.message : String(reason))
  }
}

const handleDaemonEvent = createDaemonEventHandler({
  threads,
  activeThreadId,
  projects,
  modelPreload,
  handleScheduleUpdated,
  handleEvolutionReflected,
  scrollToBottom,
  reloadThreadSession,
  rememberFinishedRequest,
  isFinishedRequest,
  scheduleQueuedDrain,
  reloadBackgroundThreadIfIdle
})

// ---------------------------------------------------------------- evolution engine (P3/P4)
// 进化引擎：候选技能审批横幅 + 回合负反馈。候选产生于后台反思 turn，
// 通过 evolution.reflected 广播或启动时 pending-list 恢复横幅状态。

const evolutionCandidates = ref<EvolutionCandidate[]>([])
const evolutionInFlight = ref(false)

async function refreshEvolutionCandidates(): Promise<void> {
  try {
    const list = await window.haoyue.daemon.request('evolution.pending-list')
    evolutionCandidates.value = Array.isArray(list)
      ? list.filter((item): item is EvolutionCandidate =>
        Boolean(item && typeof item.fingerprint === 'string' && typeof item.skillName === 'string'))
      : []
  } catch {
    // daemon offline / old runtime without evolution support — banner stays hidden
  }
}

async function handleEvolutionReflected(): Promise<void> {
  await refreshEvolutionCandidates()
  if (evolutionCandidates.value.length > 0) {
    void window.haoyue.notify(
      '进化引擎',
      `反思完成：${evolutionCandidates.value.length} 个技能候选待审批`)
  }
}

async function decideEvolution(candidate: EvolutionCandidate, adopt: boolean): Promise<void> {
  if (evolutionInFlight.value) return
  evolutionInFlight.value = true
  try {
    await window.haoyue.daemon.request('evolution.decide', {
      fingerprint: candidate.fingerprint,
      decision: adopt ? 'adopt' : 'reject'
    })
    evolutionCandidates.value = evolutionCandidates.value.filter(
      (item) => item.fingerprint !== candidate.fingerprint)
  } catch (reason) {
    await window.haoyue.notify(
      '候选审批失败',
      reason instanceof Error ? reason.message : String(reason))
    await refreshEvolutionCandidates()
  } finally {
    evolutionInFlight.value = false
  }
}

/** P4: explicit negative feedback on a finished turn; journaled as a defect signal. */
async function feedbackNegative(): Promise<void> {
  const thread = activeThread.value
  if (!thread?.sessionId) return
  try {
    await window.haoyue.daemon.request('feedback.turn', { sessionId: thread.sessionId, kind: 'negative' })
  } catch {
    // feedback is best-effort; never disturb the user over a failed journal write
  }
}

async function stopTurn(): Promise<void> {
  const thread = activeThread.value
  if (!thread?.running) return
  const params = cancelParams(thread)
  if (!params) return
  try {
    await window.haoyue.daemon.request('agent.cancel', params)
  } catch { /* sendMessage owns the final state */ }
}

async function changeNetwork(enabled: boolean): Promise<void> {
  const thread = activeThread.value
  if (!thread || thread.archived) return
  thread.networkEnabled = enabled
  const project = projects.value.find((item) => item.id === thread.projectId)
  if (!thread.sessionId || !daemonState.value.connected || (thread.projectId && !project)) return
  try {
    await window.haoyue.daemon.request('session.update', {
      id: thread.sessionId,
      ...sessionScope(thread, project),
      networkEnabled: enabled
    })
  } catch { /* The in-memory toggle is still applied to the next turn. */ }
}

async function changeModel(model: string): Promise<void> {
  const previousModel = activeModel.value
  activeModel.value = model
  if (!daemonState.value.connected || !model) return
  try { await window.haoyue.daemon.request('model.switch', { model }) }
  catch { activeModel.value = previousModel }
}

async function changeMode(nextMode: string): Promise<void> {
  if (nextMode === mode.value) return
  try {
    const response = await window.haoyue.daemon.request('agent.mode.switch', { mode: nextMode })
    mode.value = response.data
  } catch { /* Keep showing the active Runtime mode. */ }
}

async function changeReasoningLevel(level: ReasoningLevel): Promise<void> {
  const thread = activeThread.value
  if (!thread || thread.running || thread.archived) return
  thread.reasoningLevel = level
  const project = projects.value.find((item) => item.id === thread.projectId)
  if (!thread.sessionId || !daemonState.value.connected || (thread.projectId && !project)) return
  try {
    await window.haoyue.daemon.request('session.update', {
      id: thread.sessionId,
      ...sessionScope(thread, project),
      reasoningLevel: level
    })
  } catch { /* The selected level is still sent with the next turn. */ }
}

async function optimizePrompt(text: string): Promise<string> {
  if (!daemonState.value.connected) throw new Error('Runtime 未连接，无法优化提示词。')
  if (!activeModel.value) throw new Error('尚未配置模型，请先在设置中新建 Provider 和模型。')
  const params: Record<string, unknown> = { text }
  params.model = activeModel.value
  const response = await window.haoyue.daemon.request('prompt.optimize', params, {
    timeoutMs: PROMPT_OPTIMIZE_TIMEOUT_MS
  })
  return response.data
}

/**
 * Composer 的 `@` 候选：未绑定项目或 runtime 未连接时给空数组，选择器自己显示无候选。
 * root/daemon 都用惰性取值，避免在 setup 阶段固化到某一次 computed 结果。
 */
const suggestWorkspaceFiles = createWorkspaceFileSuggester({
  daemon: () => (daemonState.value.connected ? window.haoyue.daemon : null),
  root: () => activeProject.value?.path ?? null
})

function useStarterPrompt(prompt: string): void {
  composer.value?.setValue(prompt)
}

onMounted(async () => {
  applyTheme(theme.value)
  loadPersistedDrafts()
  appInfo.value = await window.haoyue.getAppInfo()
  document.documentElement.dataset.platform = appInfo.value.platform
  document.documentElement.dataset.material = appInfo.value.supportsMica ? 'mica' : 'solid'
  projects.value = loadStoredProjects()
  unsubscribeEvent = window.haoyue.daemon.onEvent(handleDaemonEvent)
  unsubscribeState = window.haoyue.daemon.onState(handleDaemonState)
  appReadyForRecovery = true
  await runReconnectCycle(true)
  measureConversationViewport()
  window.addEventListener('resize', measureConversationViewport)
  window.addEventListener('keydown', handleGlobalShortcuts)
  if (!daemonState.value.connected) projects.value.forEach((project) => { project.loaded = true })
  if (activeThread.value) composer.value?.focus()
})

onBeforeUnmount(() => {
  appReadyForRecovery = false
  window.removeEventListener('resize', measureConversationViewport)
  window.removeEventListener('keydown', handleGlobalShortcuts)
  if (phaseTimer) clearInterval(phaseTimer)
  unsubscribeEvent?.()
  unsubscribeState?.()
})

watch(theme, applyTheme)
</script>

<template>
  <div class="app-shell">
    <AppTitleBar :sidebar-open="sidebarOpen" :project-path="globalTaskActive ? undefined : activeProject?.path"
      @toggle-sidebar="sidebarOpen = !sidebarOpen" @new-task="newTask(selectedProjectId || undefined)"
      @open-workspace="openWorkspace" @show-project="showActiveProject" @open-settings="openSettings('general')"
      @focus-composer="composer?.focus()" @open-terminal="openProjectTerminal" @open-git-changes="openWorkDock('changes')"
      @open-git-history="openWorkDock('history')" @open-diagnostics="openSettings('diagnostics')"
      @open-dev-tools="openDevTools" @open-about="aboutOpen = true" />

    <div class="app-body" v-show="activePage === 'main'" :class="{ 'sidebar-collapsed': !sidebarOpen }">
      <Transition name="sidebar-slide">
        <Sidebar v-if="sidebarOpen" ref="sidebarPanel" :projects="projects" :threads="threads" :active-thread-id="activeThreadId"
          :active-project-id="selectedProjectId" :version="appInfo.version" @new-task="newTask"
          @open-workspace="openWorkspace" @select-thread="selectThread" @open-session="openSearchSession" @task-settings="openTaskSettings"
          @rename-task="renameTaskInline"
          @archive-task="archiveTask" @delete-task="deleteTask"
          @delete-project="deleteProject" @archive-project-tasks="archiveProjectTasks"
          @initialize-project-workspace="initializeProjectWorkspace" @open-project-properties="openProjectProperties"
          @delete-project-tasks="deleteProjectTasks" @archive-global-tasks="archiveGlobalTasks"
          @delete-global-tasks="deleteGlobalTasks" @open-archived="openArchivedTasks"
          @open-scheduled-tasks="openScheduledTasks" @open-extensions="openExtensions('mcp')"
          @open-knowledge="openKnowledge"
          @open-settings="openSettings('general')" />
      </Transition>
      <Transition name="scrim-fade">
        <button v-if="sidebarOpen" class="sidebar-scrim" title="关闭侧栏" @click="sidebarOpen = false" />
      </Transition>

      <div class="workspace-content">
        <main class="workspace-main" v-show="activePage === 'main'">
          <header class="conversation-header">
            <div class="conversation-title">
              <strong>{{ conversationTitle }}</strong>
              <!--  <small v-if="activeThread">{{ activeProject?.name || '任务' }}</small>-->
              <span v-if="activeThread?.running && activeThread?.phase" class="task-phase-chip">
                <span class="phase-dot" />{{ activeThread.phase }}<template v-if="phaseElapsedSeconds > 0"> · 已进行 {{ phaseElapsedSeconds }} 秒</template>
              </span>
            </div>
            <div class="conversation-actions">
              <label class="conversation-search" :class="{ active: Boolean(conversationQuery.trim()) }">
                <Search :size="15" />
                <input ref="conversationSearchInput" v-model="conversationQuery"
                  placeholder="搜索对话（含思考与工具结果）" aria-label="搜索对话" />
                <span v-if="conversationQuery.trim() && searchMatches.length" class="search-count">
                  {{ searchCursor + 1 }}/{{ searchMatches.length }}
                </span>
                <button v-if="conversationQuery.trim() && searchMatches.length" type="button"
                  class="conversation-search-clear" title="上一个匹配（无高亮跳转时请上下滚动）"
                  @click="jumpSearch(-1)">
                  <ChevronUp :size="13" />
                </button>
                <button v-if="conversationQuery.trim() && searchMatches.length" type="button"
                  class="conversation-search-clear" title="下一个匹配" @click="jumpSearch(1)">
                  <ChevronDown :size="13" />
                </button>
                <button v-if="conversationQuery" type="button" class="conversation-search-clear" title="清除搜索"
                  @click="conversationQuery = ''">
                  <X :size="13" />
                </button>
              </label>
              <button v-if="!daemonState.connected" class="connection-button"
                :title="daemonState.error || daemonState.endpoint" :disabled="reconnecting" @click="reconnectDaemon">
                <Circle :size="9" fill="currentColor" />
                {{ runtimeConnectionLabel }}
                <RefreshCw :class="{ spin: reconnecting }" :size="14" />
              </button>
              <button v-if="activeProject" class="open-location-button" title="打开项目所在文件夹" @click="showActiveProject">
                <FolderOpen :size="17" />
                <span>打开位置</span>
              </button>
              <button v-if="activeProject" class="icon-button project-tool-button" title="在项目目录打开终端"
                @click="openProjectTerminal">
                <TerminalSquare :size="18" />
              </button>
              <button class="icon-button project-tool-button" :class="{ 'is-active': workDockOpen && workDockTab === 'changes' }"
                title="查看代码更改" @click="openWorkDock('changes')">
                <Braces :size="18" />
              </button>
              <button class="icon-button project-tool-button" :class="{ 'is-active': workDockOpen && workDockTab === 'history' }"
                title="查看 Git 提交记录" @click="openWorkDock('history')">
                <History :size="18" />
              </button>
              <button class="icon-button project-tool-button" :class="{ 'is-active': workDockOpen && workDockTab === 'tools' }"
                title="工具活动面板" @click="openWorkDock('tools')">
                <Wrench :size="18" />
              </button>
              <button class="icon-button" title="任务设置" :disabled="!activeThread" @click="openTaskSettings()">
                <MoreHorizontal :size="18" />
              </button>
              <button class="icon-button" title="切换侧栏" @click="sidebarOpen = !sidebarOpen">
                <PanelRight :size="18" />
              </button>
            </div>
          </header>

          <section ref="scrollArea" class="conversation-scroll" @scroll="handleConversationScroll">
            <div v-if="conversationLoading" class="conversation-content conversation-skeleton" role="status" aria-label="正在加载会话">
              <div class="skeleton-bubble user" style="width: 130px;" />
              <div class="skeleton-bubble assistant" style="width: 240px;" />
              <div class="skeleton-bubble user" style="width: 85px;" />
              <div class="skeleton-bubble assistant" style="width: 190px;" />
            </div>
            <div v-else-if="conversationLoadError" class="empty-state conversation-load-error">
              <h1>会话加载失败</h1>
              <p>{{ conversationLoadError }}</p>
              <div style="display: flex; gap: 8px; justify-content: center; margin-top: 12px;">
                <button v-if="!daemonState.connected" class="primary-button empty-state-action" @click="reconnectDaemon">连接运行时</button>
                <button class="secondary-button empty-state-action" @click="selectThread(activeThreadId)">重新加载</button>
              </div>
            </div>
            <div v-else-if="activeThread && activeThread.messages.length > 0" class="conversation-content">
              <button v-if="activeThread.sessionHasMore" class="load-earlier-button" type="button"
                :disabled="activeThread.loadingEarlier" @click="loadEarlier">
                {{ activeThread.loadingEarlier ? '正在加载更早的消息…' : `加载更早的 ${earlierMessageCount} 条消息` }}
              </button>
              <template v-if="virtualWindow.active">
                <div class="virtual-pad" :style="{ height: `${virtualWindow.topPad}px` }" />
                <template v-for="item in virtualWindow.items" :key="item.message.id">
                  <div v-measure="item.message.id" class="virtual-message">
                    <ConversationMessage :message="item.message" :image-sources="activeImageSources"
                      :streaming="item.message.id === activeThread?.assistantId && activeThread?.running === true"
                      :show-footer="item.showFooter"
                      :dimmed="Boolean(conversationQuery.trim()) && !messageMatches(item.message, conversationQuery)"
                      @open-diff="openToolDiff"
                      @branch="branchFromMessage"
                      @edit="onEditUserMessage"
                      @regenerate="regenerateMessage"
                      @continue-turn="continueAssistant"
                      @delete="chatActions.deleteMessage" @restore-version="chatActions.restoreVersion"
                      @thumbs-down="feedbackNegative" />
                  </div>
                </template>
                <div class="virtual-pad" :style="{ height: `${virtualWindow.bottomPad}px` }" />
              </template>
              <template v-else>
                <template v-for="item in conversationItems" :key="item.message.id">
                  <ConversationMessage :message="item.message" :image-sources="activeImageSources"
                    :streaming="item.message.id === activeThread?.assistantId && activeThread?.running === true"
                    :show-footer="item.showFooter"
                    :dimmed="Boolean(conversationQuery.trim()) && !messageMatches(item.message, conversationQuery)"
                    @open-diff="openToolDiff"
                    @branch="branchFromMessage"
                    @edit="onEditUserMessage"
                    @regenerate="regenerateMessage"
                    @continue-turn="continueAssistant"
                    @delete="chatActions.deleteMessage" @restore-version="chatActions.restoreVersion"
                    @thumbs-down="feedbackNegative" />
                </template>
              </template>
            </div>
            <div v-else-if="activeThread" class="empty-state">
              <template v-if="models.length === 0">
                <!-- H23: first-run onboarding — previously new users hit an error and had
                     to discover the settings dialog on their own. -->
                <h1>三步开始使用 Haoyue</h1>
                <ol class="onboarding-steps">
                  <li><span class="step-no">1</span>在设置中添加并启用一个模型服务商</li>
                  <li><span class="step-no">2</span>选择或新建一个任务</li>
                  <li><span class="step-no">3</span>输入问题、附加文件或截图开始对话</li>
                </ol>
                <button class="primary-button empty-state-action" @click="openSettings('models')">
                  前往设置添加模型
                </button>
              </template>
              <template v-else>
                <WelcomeEmpty :project="activeProject" :thread="activeThread" :show-bind-action="globalTaskActive"
                  :daemon-connected="daemonState.connected" @use-starter-prompt="useStarterPrompt"
                  @open-workspace="openWorkspace" />
              </template>
            </div>
            <div v-else class="empty-state no-task-state">
              <h1>还没有任务</h1>
              <p>新建一个任务以开始使用 Haoyue</p>
              <div class="empty-state-actions">
                <button class="secondary-button empty-state-action" @click="newTask(selectedProjectId || undefined)">
                  新建任务
                </button>
                <button v-if="projects.length === 0" class="secondary-button empty-state-action"
                  @click="openWorkspace">
                  选择文件夹，添加项目
                </button>
              </div>
            </div>
          </section>

          <Transition name="scrim-fade">
            <button v-if="!autoFollowConversation && activeThread?.messages.length" class="scroll-bottom-button"
              title="滚动到最新消息" @click="jumpToLatest">
              <ArrowDown :size="14" />
              <span>回到最新</span>
            </button>
          </Transition>

          <footer class="composer-region">
            <!-- P0-2: 明文凭证告警。runtime 不会把无法加密的 Key 写成明文落盘，但历史配置
                 里可能残留一个，必须由界面说清楚并指向重设入口。 -->
            <div v-if="secretWarnings.length" class="security-banner" role="alert">
              <ShieldAlert :size="14" aria-hidden="true" />
              <div class="security-list">
                <span v-for="(warning, index) in secretWarnings" :key="index" class="security-item">{{ warning }}</span>
              </div>
              <button type="button" class="pending-message-action" title="前往设置 · 模型服务重设该密钥"
                @click="openSettings('models')">
                去重设
              </button>
              <button type="button" class="pending-message-action" title="本次会话不再提示"
                @click="secretWarnings = []">
                <X :size="14" />
              </button>
            </div>
            <!-- Evolution engine (P3): candidate skills produced by reflection turns
                 await human review here — adopting moves the draft into the live
                 skills directory, rejecting discards it. -->
            <div v-if="evolutionCandidates.length" class="evolution-banner" role="status">
              <Telescope :size="14" aria-hidden="true" />
              <div class="evolution-list">
                <div v-for="candidate in evolutionCandidates" :key="candidate.fingerprint"
                  class="evolution-candidate">
                  <span class="evolution-skill">{{ candidate.skillName }}</span>
                  <span class="evolution-summary" :title="candidate.summary">{{
                    candidate.summary || '反思产出候选技能' }}</span>
                  <div class="evolution-actions">
                    <button type="button" class="undo-button" :disabled="evolutionInFlight"
                      title="采纳后技能进入正式目录，下一次扫描即生效"
                      @click="decideEvolution(candidate, true)">
                      <Check :size="14" /> 采纳
                    </button>
                    <button type="button" class="pending-message-action" :disabled="evolutionInFlight"
                      title="丢弃该候选草稿"
                      @click="decideEvolution(candidate, false)">
                      <X :size="14" /> 丢弃
                    </button>
                  </div>
                </div>
              </div>
            </div>
            <!-- TurnScope: one-click revert of the latest turn's builtin file-tool changes.
                 The daemon keeps one undo ledger per session; agent.undo is one-shot. -->
            <div v-if="activeThread?.undoableFiles?.length && activeThread.sessionId && !activeThread.running"
              class="undo-banner" role="status">
              <History :size="14" aria-hidden="true" />
              <span>本回合修改了 {{ activeThread.undoableFiles.length }} 个文件，可撤销恢复到修改前状态</span>
              <button type="button" class="undo-button" :disabled="undoInFlight"
                title="按修改的逆序恢复这些文件" @click="undoLastTurn">
                <Undo2 :size="14" />
                {{ undoInFlight ? '撤销中…' : '撤销' }}
              </button>
            </div>
            <div v-if="activeThread?.queuedMessages?.length" class="pending-message-stack" aria-label="等待发送的消息">
              <button v-if="!queuedHintDismissed" type="button" class="pending-message-hint"
                title="点击不再显示" @click="queuedHintDismissed = true">
                排队中的消息会在当前回合结束后自动发送；想立即让 AI 看到，点消息右侧的「引导」插话。
                <X :size="12" aria-hidden="true" />
              </button>
              <div v-for="queued in activeThread.queuedMessages" :key="queued.id" class="pending-message-card">
                <div class="pending-message-main">
                  <CornerDownLeft :size="15" aria-hidden="true" />
                  <img v-if="queued.images.length" class="pending-message-image" :src="queuedImageUrl(queued.images[0])"
                    :alt="queued.images[0]?.name || '图片'">
                  <span>{{ queuedMessagePreview(queued) }}</span>
                </div>
                <div class="pending-message-actions">
                  <button type="button" class="pending-message-action"
                    :disabled="!activeThread.running || !activeThread.sessionId || !daemonState.connected"
                    title="作为附加指导发送，不打断当前 AI 回合" @click="steerQueuedMessage(activeThread, queued)">
                    <CornerDownLeft :size="14" /> 引导
                  </button>
                  <button type="button" class="pending-message-action icon-only" title="删除等待中的消息"
                    @click="removeQueuedMessage(activeThread, queued.id)">
                    <Trash2 :size="14" />
                  </button>
                </div>
              </div>
            </div>

            <TaskStepList :steps="activeThreadTurnSteps" :running="activeThread?.running"
              :phase="activeThread?.phase" />
            <div v-if="activeThread?.expertId" class="expert-badge-row">
              <span class="expert-badge" :title="`Agent 将以专家「${activeThread.expertLabel || activeThread.expertId}」的角色协作`">
                <Sparkles :size="13" aria-hidden="true" />
                <span>专家：{{ activeThread.expertLabel || activeThread.expertId }}</span>
                <button type="button" class="expert-badge-remove" title="解除专家绑定"
                  @click="activeThread && removeExpert(activeThread)">
                  <X :size="12" aria-hidden="true" />
                </button>
              </span>
            </div>
            <p v-if="modelPreloadLabel" class="preload-chip"><LoaderCircle :size="13" aria-hidden="true" />{{ modelPreloadLabel }}</p>
            <p v-if="undoBarVisible" class="preload-chip undo-bar">
              已从会话视图移除 1 条消息
              <button type="button" class="action-btn" @click="chatActions.undoDeletion">撤销</button>
            </p>
            <!-- 空状态修复：无任务时输入框保持可用，发送时由 sendMessage 自动创建任务。 -->
            <Composer ref="composer" :busy="busy"
              :disabled="conversationLoading" :model="activeModel"
              :models="models" :mode="mode" :task-id="activeThread?.id" :supports-images="activeModelSupportsImages"
              :reasoning-level="activeReasoningLevel" :network-enabled="activeThread?.networkEnabled ?? true"
              :optimize-prompt="optimizePrompt" :suggest-files="suggestWorkspaceFiles" @send="sendMessage" @stop="stopTurn" @change-model="changeModel"
              @slash-command="chatActions.slashCommand"
              @change-mode="changeMode" @change-reasoning-level="changeReasoningLevel"
              @change-network="changeNetwork" />
            <p class="composer-caption">{{ composerCaption }}</p>
          </footer>
        </main>

        <WorkDock v-show="activePage === 'main'" :open="workDockOpen" v-model:tab="workDockTab"
          :width="workDockWidth" :project="activeProject" :thread="activeThread" :diff-override="toolDiff"
          @close="closeWorkDock" @resize="resizeWorkDock" @open-terminal="openProjectTerminal" />
      </div>
    </div>


    <SettingsDialog :open="activePage === 'settings' || activePage === 'extensions'"
      :page="activePage === 'extensions' ? 'extensions' : 'settings'" :theme="theme"
      :daemon-connected="daemonState.connected" :daemon-endpoint="daemonState.endpoint"
      :workspace-path="runtimeWorkspacePath"
      :initial-section="activePage === 'settings' ? settingsSection : extensionsSection" @close="closePage"
      @change-theme="applyTheme" @reconnect="reconnectDaemon"
      @open-rules-memory="openRulesMemory"
      @start-expert="startExpertTask"
      @runtime-changed="refreshRuntimeState" />

    <KnowledgeBaseDialog :open="activePage === 'knowledge'" @close="closePage" />

    <RulesMemoryDialog :open="activePage === 'rules-memory'" @close="closePage" />

    <ScheduledTasksDialog :open="activePage === 'scheduled'" :projects="projects" @close="closePage" />

    <ArchivedTasksDialog :open="activePage === 'archived'" :projects="projects" :threads="threads" @close="closePage"
      @select-thread="selectArchivedThread" @restore-task="restoreTask" @delete-task="deleteTask"
      @delete-all="deleteArchivedTasks" />

    <AboutDialog :open="aboutOpen" :app-info="appInfo" :protocol-version="daemonState.protocolVersion" @close="aboutOpen = false" />

    <!-- 「本次允许」审批（P1-2.3）：内置高危守卫挂起执行类工具调用时弹出；
         用户选择经 agent.respondApproval 回传，超时（120s）按拒绝处理。 -->
    <Teleport to="body">
      <Transition name="modal-fade">
        <div v-if="activeThread?.pendingApproval" class="modal-backdrop" @mousedown.self>
          <section class="approval-dialog" role="alertdialog" aria-modal="true" aria-labelledby="approval-title">
            <header class="approval-header">
              <h2 id="approval-title">高危操作需要你的确认</h2>
            </header>
            <div class="approval-body">
              <p class="approval-tool">
                工具 <code>{{ activeThread.pendingApproval.tool || '未知工具' }}</code> 被内置高危守卫拦截
              </p>
              <p v-if="activeThread.pendingApproval.summary" class="approval-summary">
                {{ activeThread.pendingApproval.summary }}
              </p>
              <p class="approval-reason">拦截原因：{{ activeThread.pendingApproval.reason }}</p>
              <p class="approval-note">
                「本次允许」只放行当前这一次调用；拒绝或 120 秒无响应按拒绝处理。
                如需长期放行，请在配置 <code>agent.toolPolicy.allow</code> 中添加规则。
              </p>
            </div>
            <footer class="approval-footer">
              <button class="ghost-button" :disabled="approvalBusy" @click="respondApproval(false)">拒绝</button>
              <button class="primary-button" :disabled="approvalBusy" @click="respondApproval(true)">本次允许</button>
            </footer>
          </section>
        </div>
      </Transition>
    </Teleport>


    <TaskSettingsDialog :open="Boolean(taskSettingsThreadId)" :thread="settingsThread" :project="settingsProject"
      @close="taskSettingsThreadId = ''" @save-title="saveTaskTitle"
      @archive="settingsThread && archiveTask(settingsThread)" @restore="settingsThread && restoreTask(settingsThread)"
      @delete="settingsThread && deleteTask(settingsThread)" />

    <ProjectPropertiesDialog :open="Boolean(activePropertiesProject)" :project="activePropertiesProject ?? undefined"
      :threads="threads" @close="activePropertiesProject = null" @initialize-workspace="initializeProjectWorkspace"
      @open-extensions="openExtensions" />

    <RuntimeReconnectDialog :open="Boolean(reconnectPrompt)" :startup="reconnectPrompt?.startup ?? false"
      :endpoint="daemonState.endpoint" :error="reconnectPrompt?.error" @retry="continueRuntimeReconnect"
      @cancel="cancelRuntimeReconnect" />

    <ConfigAnomalyDialog v-if="configAnomaly && configAnomaly.hasAnomaly" :open="true" :detail="configAnomaly.detail"
      :config-file="configAnomaly.configFile" :backup-file="configAnomaly.backupFile" :rebuilding="rebuildingConfig"
      @close="configAnomaly = null" @rebuild="handleRebuildConfig" />

    <EditMessageDialog :open="editMessageDialogOpen" :message="editingUserMessage"
      :modified-files="editMessageModifiedFiles" @close="editMessageDialogOpen = false"
      @confirm="handleEditConfirm" />

    <ConfirmDialog />
  </div>
</template>
