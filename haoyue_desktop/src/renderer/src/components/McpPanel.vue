<script setup lang="ts">
import {
  ArrowLeft,
  BookOpen,
  Brain,
  BrainCircuit,
  Clock,
  Database,
  ExternalLink,
  FolderOpen,
  GitBranch,
  Globe,
  KeyRound,
  LoaderCircle,
  MessageSquare,
  MonitorPlay,
  Plug,
  Plus,
  RefreshCw,
  Save,
  Search,
  Settings2,
  Trash2,
  X
} from '@lucide/vue'
import { computed, defineComponent, h, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { confirmAction } from '../confirmation'
import {
  MCP_PRESETS,
  MCP_SCOPE_OPTIONS,
  MCP_TRANSPORT_OPTIONS,
  buildMcpServerPayload,
  buildMcpTogglePayload,
  classifyTransport,
  createMcpFormValue,
  credentialRowsToText,
  envNeedsGithubToken,
  githubTokenCreateUrl,
  inferMcpName,
  isRemoteTransport,
  mcpFormError,
  mcpFormFromServer,
  normalizeConnection,
  parseCredentialRows,
  parseMcpJsonConfig,
  transportLabel,
  type CredentialRow,
  type McpFormValue,
  type McpPreset,
  type McpServerSummary
} from '../mcp-form'
import FieldLabel from './FieldLabel.vue'
import SelectMenu from './SelectMenu.vue'

const props = defineProps<{
  /** Active workspace directory, used by presets that serve a folder on disk. */
  workspacePath?: string
}>()

const MCP_ADMIN_TIMEOUT_MS = 120_000

// ---------------------------------------------------------------- state

const servers = ref<McpServerSummary[]>([])
const loading = ref(false)
const action = ref('')
const error = ref('')
const notice = ref('')
const pendingToggles = ref<string[]>([])

/** null = list view; a server = edit it; 'new' = blank form; 'preset:<id>' = prefilled form. */
const detail = ref<McpServerSummary | 'new' | { preset: McpPreset } | null>(null)
const form = reactive<McpFormValue>(createMcpFormValue())
const validationError = ref('')
const nameTouched = ref(false)
const advancedOpen = ref(false)
/** 身份验证 for remote servers: OAuth auto vs manual token header. */
const authMode = ref<'auto' | 'token'>('auto')
const newEnvKey = ref('')
const newHeaderKey = ref('')
const detailIcon = ref<unknown>(Plug)
const detailDescription = ref('')

const editing = computed(() => detail.value instanceof Object && 'name' in detail.value)
const remote = computed(() => isRemoteTransport(form.transport))
const envRows = computed(() => parseCredentialRows(form.env))
const headerRows = computed(() => parseCredentialRows(form.headers))
const message = computed(() => validationError.value || error.value || '')
const githubTokenUrl = githubTokenCreateUrl()
const showGithubHint = computed(() => !remote.value && envNeedsGithubToken(form.env))
const connectionSummary = computed(() => {
  if (remote.value) return ''
  const value = form.connection.trim()
  if (!value) return ''
  if (parseMcpJsonConfig(value)) return '检测到 JSON 配置，已自动填充各字段'
  return `本地命令：${value}`
})
const detailTitle = computed(() => {
  if (detail.value === 'new') return form.name.trim() || '添加自定义 MCP'
  if (detail.value && typeof detail.value === 'object' && 'preset' in detail.value) return detail.value.preset.label
  return form.name
})

/** GitHub brand mark; lucide no longer ships brand icons. */
const GithubMark = defineComponent({
  props: { size: { type: Number, default: 15 } },
  setup: (iconProps) => () => h('svg', {
    viewBox: '0 0 16 16',
    width: iconProps.size,
    height: iconProps.size,
    fill: 'currentColor',
    'aria-hidden': 'true'
  }, [h('path', {
    d: 'M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27s1.36.09 2 .27c1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.01 8.01 0 0 0 16 8c0-4.42-3.58-8-8-8Z'
  })])
})

const presetIcons: Record<string, unknown> = {
  github: GithubMark,
  filesystem: FolderOpen,
  fetch: Globe,
  memory: Brain,
  'sequential-thinking': BrainCircuit,
  git: GitBranch,
  playwright: MonitorPlay,
  'brave-search': Search,
  context7: BookOpen,
  postgres: Database,
  slack: MessageSquare,
  time: Clock
}

function presetIcon(preset: McpPreset): unknown {
  return presetIcons[preset.id] ?? Plug
}

function isPresetInstalled(preset: McpPreset): boolean {
  const presetName = preset.createForm({ workspacePath: props.workspacePath }).name
  return servers.value.some((server) => server.name === presetName)
}

function serverKey(server: { scope: string; name: string }): string {
  return `${server.scope}:${server.name}`
}

function isTogglePending(server: McpServerSummary): boolean {
  return server.connecting === true || pendingToggles.value.includes(serverKey(server))
}

/** LM Studio style status line: 已连接 · N 个工具已就绪. */
function statusLine(server: Pick<McpServerSummary, 'connected' | 'connecting' | 'enabled' | 'toolCount' | 'error'>): string {
  if (server.connected) return `已连接 · ${server.toolCount} 个工具已就绪`
  if (server.connecting) return '连接中…'
  if (!server.enabled) return '已停用'
  return server.error?.trim() || '未连接'
}

// ---------------------------------------------------------------- data

async function requestJson<T>(method: string, params: Record<string, unknown> = {}, timeoutMs?: number): Promise<T> {
  const response = await window.haoyue.daemon.request(method, params, timeoutMs ? { timeoutMs } : undefined)
  return JSON.parse(response.data) as T
}

async function load(): Promise<void> {
  loading.value = true
  try {
    servers.value = await requestJson<McpServerSummary[]>('mcp.list', {}, MCP_ADMIN_TIMEOUT_MS)
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  void load()
  unsubscribe = window.haoyue.daemon.onEvent((evt) => {
    if (evt.event === 'mcp.updated') void refreshAfterEvent()
  })
})

let unsubscribe: (() => void) | null = null
onBeforeUnmount(() => unsubscribe?.())

/**
 * Enabling a server connects in the background; the runtime broadcasts
 * `mcp.updated` once the real status is known. In-flight mutations win.
 */
async function refreshAfterEvent(): Promise<void> {
  if (action.value.startsWith('mcp.')) return
  try {
    servers.value = await requestJson<McpServerSummary[]>('mcp.list', {}, MCP_ADMIN_TIMEOUT_MS)
    if (notice.value === 'MCP 正在重新加载…' && !servers.value.some((server) => server.connecting)) {
      notice.value = 'MCP 已重新加载'
    }
    syncDetailWithList()
  } catch {
    // Transient failure: the list keeps its previous content.
  }
}

function syncDetailWithList(): void {
  const current = detail.value
  if (!current || current === 'new' || !(current instanceof Object) || !('name' in current)) return
  const fresh = servers.value.find((server) => serverKey(server) === serverKey(current))
  if (fresh) detail.value = fresh
  else closeDetail()
}

// ---------------------------------------------------------------- list actions

async function toggleServer(server: McpServerSummary): Promise<void> {
  const key = serverKey(server)
  action.value = `mcp.toggle:${server.name}`
  error.value = ''
  notice.value = ''
  pendingToggles.value = [...pendingToggles.value, key]
  try {
    servers.value = await requestJson<McpServerSummary[]>('mcp.upsert', {
      name: server.name,
      scope: server.scope,
      server: buildMcpTogglePayload(server)
    }, MCP_ADMIN_TIMEOUT_MS)
    syncDetailWithList()
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    pendingToggles.value = pendingToggles.value.filter((entry) => entry !== key)
    action.value = ''
  }
}

async function reloadAll(): Promise<void> {
  action.value = 'mcp.reload'
  error.value = ''
  notice.value = ''
  try {
    servers.value = await requestJson<McpServerSummary[]>('mcp.reload', {}, MCP_ADMIN_TIMEOUT_MS)
    notice.value = servers.value.some((server) => server.connecting) ? 'MCP 正在重新加载…' : 'MCP 已重新加载'
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    action.value = ''
  }
}

// ---------------------------------------------------------------- detail navigation

function openServer(server: McpServerSummary): void {
  Object.assign(form, mcpFormFromServer(server))
  detail.value = server
  detailIcon.value = presetIconForServer(server)
  detailDescription.value = describeServer(server)
  resetDetailState()
}

function presetIconForServer(server: McpServerSummary): unknown {
  const preset = MCP_PRESETS.find((entry) => entry.createForm({ workspacePath: props.workspacePath }).name === server.name)
  return preset ? presetIcon(preset) : Plug
}

function describeServer(server: McpServerSummary): string {
  const preset = MCP_PRESETS.find((entry) => entry.createForm({ workspacePath: props.workspacePath }).name === server.name)
  if (preset) return preset.description
  return remote
    ? '远程 MCP 服务器，通过 HTTP 与 Agent 共享工具。'
    : '本地 MCP 服务器，通过子进程与 Agent 共享工具。'
}

function openPreset(preset: McpPreset): void {
  Object.assign(form, preset.createForm({ workspacePath: props.workspacePath }))
  detail.value = { preset }
  detailIcon.value = presetIcon(preset)
  detailDescription.value = preset.description
  resetDetailState()
}

function openBlank(): void {
  Object.assign(form, createMcpFormValue())
  detail.value = 'new'
  detailIcon.value = Plug
  detailDescription.value = '添加本地命令或远程 MCP 服务器。'
  resetDetailState()
}

function resetDetailState(): void {
  validationError.value = ''
  error.value = ''
  notice.value = ''
  nameTouched.value = editing.value
  newEnvKey.value = ''
  newHeaderKey.value = ''
  advancedOpen.value = Boolean(
    form.connectTimeoutSeconds ||
    form.mutatingTools ||
    form.readOnlyTools ||
    form.trustReadOnly ||
    form.oauthDisabled ||
    (editing.value && detail.value instanceof Object && 'name' in detail.value &&
      ((detail.value.readOnlyTools?.length ?? 0) > 0 || (detail.value.mutatingTools?.length ?? 0) > 0))
  )
  authMode.value = headerRows.value.length > 0 ? 'token' : 'auto'
  if (form.scope !== 'global') advancedOpen.value = true
  document.addEventListener('keydown', handleKeydown)
}

function closeDetail(): void {
  detail.value = null
  document.removeEventListener('keydown', handleKeydown)
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') closeDetail()
  if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
    event.preventDefault()
    void save()
  }
}

onBeforeUnmount(() => document.removeEventListener('keydown', handleKeydown))

// ---------------------------------------------------------------- detail actions

function setConnectionMode(mode: 'stdio' | 'remote'): void {
  const target = mode === 'remote'
  if (remote.value === target) return
  // 切换连接方式时清空不再合法的连接串，避免带着命令去保存远程表单。
  const current = form.connection.trim()
  const isUrl = /^https?:\/\//i.test(current)
  if (target && !isUrl) form.connection = ''
  if (!target && isUrl) form.connection = ''
  form.transport = target ? 'http' : 'stdio'
}

function updateEnvRows(rows: CredentialRow[]): void {
  form.env = credentialRowsToText(rows)
}

function updateHeaderRows(rows: CredentialRow[]): void {
  form.headers = credentialRowsToText(rows)
}

function addEnvKey(): void {
  const key = newEnvKey.value.trim()
  if (!key) return
  const rows = envRows.value
  if (rows.some((row) => row.key === key)) return
  updateEnvRows([...rows, { key, value: '' }])
  newEnvKey.value = ''
}

function addHeaderKey(): void {
  const key = newHeaderKey.value.trim()
  if (!key) return
  const rows = headerRows.value
  if (rows.some((row) => row.key === key)) return
  updateHeaderRows([...rows, { key, value: '' }])
  newHeaderKey.value = ''
}

/** 自动 = OAuth 浏览器授权，手动请求头一并清空；访问令牌 = 预置 Authorization 行。 */
function setAuthMode(mode: 'auto' | 'token'): void {
  authMode.value = mode
  if (mode === 'auto') {
    form.headers = ''
    newHeaderKey.value = ''
    return
  }
  if (headerRows.value.length === 0) form.headers = 'Authorization='
}

async function save(): Promise<void> {
  if (action.value) return
  const invalid = mcpFormError(form)
  if (invalid) {
    validationError.value = invalid
    return
  }
  action.value = 'mcp.save'
  validationError.value = ''
  error.value = ''
  try {
    servers.value = await requestJson<McpServerSummary[]>('mcp.upsert', {
      name: form.name.trim(),
      scope: form.scope,
      server: buildMcpServerPayload(form)
    }, MCP_ADMIN_TIMEOUT_MS)
    notice.value = 'MCP 配置已保存并重载'
    closeDetail()
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    action.value = ''
  }
}

async function removeServer(): Promise<void> {
  if (!editing.value || detail.value instanceof Object === false) return
  const target = detail.value as McpServerSummary
  if (!await confirmAction({
    title: '移除 MCP 服务器', message: `移除 MCP 服务器 “${target.name}”？`, confirmLabel: '移除', danger: true
  })) return
  action.value = 'mcp.remove'
  error.value = ''
  try {
    servers.value = await requestJson<McpServerSummary[]>(
      'mcp.remove', { name: target.name, scope: target.scope }, MCP_ADMIN_TIMEOUT_MS)
    notice.value = `已移除 ${target.name}`
    closeDetail()
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    action.value = ''
  }
}

// ---------------------------------------------------------------- connection recognition

function applyJsonConfig(): void {
  const parsed = parseMcpJsonConfig(form.connection)
  if (!parsed) return
  const server = parsed.server
  const transport = typeof server.transport === 'string' ? server.transport : undefined
  const command = typeof server.command === 'string' ? server.command : ''
  const args = Array.isArray(server.args) ? server.args.map(String) : []
  const url = typeof server.url === 'string' ? server.url : ''

  if (parsed.name && !nameTouched.value) form.name = parsed.name
  form.transport = transport
    ? (transport === 'sse' ? 'sse' : transport === 'stdio' ? 'stdio' : 'http')
    : classifyTransport(url || command)
  form.connection = url || [command, ...args].join(' ')
  const toText = (value: unknown) =>
    value && typeof value === 'object' && !Array.isArray(value)
      ? Object.entries(value as Record<string, unknown>).map(([k, v]) => `${k}=${String(v ?? '')}`).join('\n')
      : ''
  form.env = toText(server.env)
  form.headers = toText(server.headers)
  if (parseCredentialRows(form.headers).length > 0) authMode.value = 'token'
}

watch(() => form.connection, (value) => {
  if (detail.value === null) return
  if (parseMcpJsonConfig(value)) {
    applyJsonConfig()
    return
  }
  const isUrl = /^https?:\/\//i.test(value.trim())
  if (remote.value && !isUrl) {
    // 用户把 URL 换成了本地内容——切回本地命令。
    form.transport = 'stdio'
  }
  if (!remote.value && !isUrl) {
    const normalized = normalizeConnection(value)
    if (normalized !== value) {
      form.connection = normalized
      return
    }
  }
  form.transport = classifyTransport(value)
  if (!nameTouched.value) form.name = inferMcpName(value)
})

watch(form, () => {
  if (validationError.value) validationError.value = ''
})
</script>

<template>
  <div class="mcp-panel">
    <!-- ============================== 列表视图 ============================== -->
    <template v-if="!detail">
      <section class="mcp-list-heading">
        <h3>已连接的 MCP 服务器 <span class="mcp-count-chip">{{ servers.length }}</span></h3>
        <button class="icon-button" title="重新加载全部" :disabled="action === 'mcp.reload'" @click="reloadAll">
          <LoaderCircle v-if="action === 'mcp.reload'" class="spin" :size="15" />
          <RefreshCw v-else :size="15" />
        </button>
      </section>

      <p v-if="message" class="mcp-panel-message" :class="{ error: !!error }">{{ message }}</p>

      <section v-if="loading && servers.length === 0" class="mcp-empty-card">正在加载 MCP 服务器…</section>
      <section v-else-if="servers.length === 0" class="mcp-empty-card">
        尚未添加 MCP 服务器——从下方热门目录一键添加，或手动配置。
      </section>
      <section v-else class="mcp-server-list">
        <div v-for="server in servers" :key="serverKey(server)" class="mcp-server-card"
          role="button" tabindex="0" @click="openServer(server)" @keydown.enter.prevent="openServer(server)">
          <span class="mcp-server-icon"><component :is="presetIconForServer(server)" :size="20" /></span>
          <div class="mcp-server-main">
            <strong :title="server.name">{{ server.name }}</strong>
            <small>
              <span class="mcp-status-dot" :class="{
                online: server.connected,
                pending: server.connecting,
                off: !server.connected && !server.connecting
              }" /> {{ statusLine(server) }}
            </small>
          </div>
          <div class="mcp-server-actions" @click.stop>
            <button class="icon-button" :title="`配置 ${server.name}`" @click="openServer(server)">
              <Settings2 :size="16" />
            </button>
            <label class="mcp-toggle-row">
              <input class="sr-only" type="checkbox" :checked="server.enabled" :disabled="isTogglePending(server)"
                :aria-label="isTogglePending(server) ? '正在连接' : (server.enabled ? '停用' : '启用')"
                @change="toggleServer(server)" />
              <span class="toggle-switch" aria-hidden="true">
                <LoaderCircle v-if="isTogglePending(server)" class="spin mcp-toggle-spinner" :size="13" />
                <span v-else />
              </span>
            </label>
          </div>
        </div>
      </section>

      <section class="mcp-catalog">
        <div class="mcp-catalog-heading">
          <h3>热门 MCP</h3>
          <p>精心挑选、设置简单的 MCP 服务器。</p>
        </div>
        <div class="mcp-catalog-grid">
          <div v-for="preset in MCP_PRESETS" :key="preset.id" class="mcp-preset-card">
            <span class="mcp-server-icon large"><component :is="presetIcon(preset)" :size="22" /></span>
            <strong>{{ preset.label }}</strong>
            <p>{{ preset.description }}</p>
            <div class="mcp-preset-footer">
              <small>发布者：{{ preset.publisher ?? 'MCP 官方' }}</small>
              <button class="secondary-button primary-action compact-button" @click="openPreset(preset)">
                {{ isPresetInstalled(preset) ? '已添加' : '设置' }}
              </button>
            </div>
          </div>
        </div>
      </section>

      <section class="mcp-manual-card">
        <div>
          <strong>手动设置 MCP 服务器</strong>
          <small>添加本地命令或远程 MCP 服务器。</small>
        </div>
        <button class="secondary-button" @click="openBlank">
          <Plus :size="15" /> 添加自定义 MCP
        </button>
      </section>
    </template>

    <!-- ============================== 详情视图 ============================== -->
    <template v-else>
      <button class="mcp-back" @click="closeDetail">
        <ArrowLeft :size="15" /> 返回列表
      </button>

      <header class="mcp-detail-header">
        <span class="mcp-server-icon large"><component :is="detailIcon" :size="22" /></span>
        <div>
          <h3>{{ detailTitle }}</h3>
          <p>{{ detailDescription }}</p>
        </div>
      </header>

      <p v-if="message" class="mcp-panel-message" :class="{ error: !!error }">{{ message }}</p>
      <p v-else-if="notice" class="mcp-panel-message">{{ notice }}</p>

      <!-- 状态 -->
      <section class="mcp-detail-card">
        <div class="mcp-detail-card-title"><strong>状态</strong></div>
        <div class="mcp-status-row">
          <small v-if="editing">
            <span class="mcp-status-dot" :class="{
              online: form.enabled && (detail as McpServerSummary).connected,
              pending: (detail as McpServerSummary).connecting,
              off: !((detail as McpServerSummary).connected) && !((detail as McpServerSummary).connecting)
            }" />
            {{ statusLine(detail as McpServerSummary) }}
            <span v-if="(detail as McpServerSummary).error" class="mcp-error-text">· {{ (detail as McpServerSummary).error }}</span>
          </small>
          <small v-else>保存后将立即连接并注册工具</small>
          <label class="mcp-toggle-row">
            <input v-model="form.enabled" class="sr-only" type="checkbox" />
            <span class="toggle-switch" aria-hidden="true"><span /></span>
          </label>
        </div>
      </section>

      <!-- 配置 -->
      <section class="mcp-detail-card">
        <div class="mcp-detail-card-title"><strong>配置</strong></div>
        <div class="mcp-form-grid">
          <label class="form-field full-width">
            <FieldLabel en="Name" zh="名称" help="此 MCP 服务器显示的名称。创建后不可修改。" required />
            <input v-model="form.name" class="form-input" placeholder="github" :disabled="editing"
              autocomplete="off" spellcheck="false" @input="nameTouched = true" />
          </label>

          <div class="form-field full-width">
            <FieldLabel en="Connection" zh="连接" help="选择连接到此服务器的方式。" required />
            <div class="segmented-control mcp-connection-segmented">
              <button type="button" :class="{ active: !remote }" @click="setConnectionMode('stdio')">在此电脑上</button>
              <button type="button" :class="{ active: remote }" @click="setConnectionMode('remote')">网址</button>
            </div>
            <input v-model="form.connection" class="form-input" autocomplete="off" spellcheck="false"
              :placeholder="remote ? 'https://example.com/mcp' : '@modelcontextprotocol/server-github 或启动命令'" />
            <small v-if="connectionSummary" class="mcp-connection-summary">{{ connectionSummary }}</small>
          </div>

          <div class="form-field full-width">
            <FieldLabel en="Authentication" zh="身份验证" :help="remote
              ? '自动使用浏览器 OAuth 授权（支持的服务器）；或改用访问令牌。'
              : '通过环境变量传递凭据，值加密存储，保存后不会回显；留空表示保持原值不变。'" />
            <div v-if="remote" class="segmented-control mcp-connection-segmented">
              <button type="button" :class="{ active: authMode === 'auto' }" @click="setAuthMode('auto')">
                自动<span v-if="form.oauthDisabled" class="mcp-auth-mode-note">（已禁用）</span>
              </button>
              <button type="button" :class="{ active: authMode === 'token' }" @click="setAuthMode('token')">访问令牌</button>
            </div>
            <small v-if="remote && authMode === 'auto' && (detail as McpServerSummary)?.oauthConfigured" class="mcp-auth-note">
              此服务器已通过浏览器授权，令牌会自动续期。
            </small>

            <div v-if="remote && authMode === 'token'" class="mcp-credential-list">
              <div v-for="(row, index) in headerRows" :key="row.key" class="mcp-credential-row">
                <code class="mcp-credential-key">{{ row.key }}</code>
                <input class="form-input" type="password" :placeholder="editing ? '留空保持原值' : 'Bearer <token>'"
                  :value="row.value" autocomplete="new-password"
                  @input="updateHeaderRows(headerRows.map((r, i) => i === index ? { ...r, value: ($event.target as HTMLInputElement).value } : r))" />
                <button class="icon-button" type="button" title="删除" @click="updateHeaderRows(headerRows.filter((_, i) => i !== index))">
                  <X :size="14" />
                </button>
              </div>
              <div class="mcp-credential-add">
                <input v-model="newHeaderKey" class="form-input" placeholder="请求头名称，如 X-Api-Key"
                  autocomplete="off" spellcheck="false" @keydown.enter.prevent="addHeaderKey" />
                <button class="secondary-button" type="button" :disabled="!newHeaderKey.trim()" @click="addHeaderKey">
                  <Plus :size="14" /> 添加
                </button>
              </div>
            </div>

            <div v-if="!remote" class="mcp-credential-list">
              <div v-for="(row, index) in envRows" :key="row.key" class="mcp-credential-row">
                <code class="mcp-credential-key">{{ row.key }}</code>
                <input class="form-input" type="password" :placeholder="editing ? '留空保持原值' : '输入值'"
                  :value="row.value" autocomplete="new-password"
                  @input="updateEnvRows(envRows.map((r, i) => i === index ? { ...r, value: ($event.target as HTMLInputElement).value } : r))" />
                <button class="icon-button" type="button" title="删除" @click="updateEnvRows(envRows.filter((_, i) => i !== index))">
                  <X :size="14" />
                </button>
              </div>
              <div class="mcp-credential-add">
                <input v-model="newEnvKey" class="form-input" placeholder="环境变量名，如 GITHUB_PERSONAL_ACCESS_TOKEN"
                  autocomplete="off" spellcheck="false" @keydown.enter.prevent="addEnvKey" />
                <button class="secondary-button" type="button" :disabled="!newEnvKey.trim()" @click="addEnvKey">
                  <Plus :size="14" /> 添加
                </button>
              </div>
            </div>

            <div v-if="showGithubHint" class="mcp-token-hint">
              <div class="mcp-token-hint-copy">
                <strong><KeyRound :size="13" /> 获取 GitHub 令牌</strong>
                <ol>
                  <li>点「去 GitHub 生成」打开令牌页，所需权限已预选（repo、read:org 等）</li>
                  <li>在页面底部点 Generate token，复制 ghp_ 开头的令牌</li>
                  <li>粘贴到上方 GITHUB_PERSONAL_ACCESS_TOKEN 的值输入框</li>
                </ol>
              </div>
              <a class="secondary-button" :href="githubTokenUrl" target="_blank" rel="noreferrer">
                <ExternalLink :size="14" /> 去 GitHub 生成
              </a>
            </div>
          </div>
        </div>
      </section>

      <!-- 高级设置 -->
      <section class="mcp-detail-card">
        <div class="mcp-advanced-row">
          <div>
            <strong>显示高级选项</strong>
            <small>配置可选的连接详细信息。</small>
          </div>
          <label class="mcp-toggle-row">
            <input v-model="advancedOpen" class="sr-only" type="checkbox" />
            <span class="toggle-switch" aria-hidden="true"><span /></span>
          </label>
        </div>
        <div v-if="advancedOpen" class="mcp-form-grid mcp-advanced-grid">
          <label class="form-field">
            <FieldLabel en="Scope" zh="范围" help="全局对所有工作区生效；工作区仅对当前项目生效。" />
            <SelectMenu v-model="form.scope" :options="MCP_SCOPE_OPTIONS" label="MCP 范围" />
          </label>
          <label v-if="remote" class="form-field">
            <FieldLabel en="Transport" zh="传输协议" help="网址默认使用 Streamable HTTP；老旧服务器可选 SSE。" />
            <SelectMenu v-model="form.transport" :options="MCP_TRANSPORT_OPTIONS.filter((o) => o.value !== 'stdio')" label="MCP 连接方式" />
          </label>
          <label v-if="remote" class="form-field">
            <FieldLabel en="Connect timeout" zh="连接超时（秒）" help="1~120；留空使用默认 10 秒，内网慢服务器可调大。" />
            <input v-model="form.connectTimeoutSeconds" class="form-input" type="number" min="1" max="120"
              placeholder="10" autocomplete="off" />
          </label>
          <label class="form-field full-width">
            <FieldLabel en="Mutating tools" zh="视为变更类工具" help="每行一个工具名；这些工具始终按「会改动真实世界」处理，只读模式下默认拒绝执行。" />
            <textarea v-model="form.mutatingTools" class="form-input" rows="2" placeholder="send_email&#10;deploy" />
          </label>
          <label class="form-field full-width">
            <FieldLabel en="Read-only tools" zh="只读工具白名单" help="每行一个工具名；优先级高于变更类清单，命中即视为安全只读。" />
            <textarea v-model="form.readOnlyTools" class="form-input" rows="2" placeholder="search&#10;get_weather" />
          </label>
          <label class="mcp-inline-toggle full-width">
            <span>
              <strong>信任只读工具</strong>
              <small>勾选后，名称不含变更关键词的工具一律视为只读；仅对已知安全的服务器使用</small>
            </span>
            <input v-model="form.trustReadOnly" class="sr-only" type="checkbox" />
            <span class="toggle-switch" aria-hidden="true"><span /></span>
          </label>
          <label v-if="remote" class="mcp-inline-toggle full-width">
            <span>
              <strong>禁用 OAuth 自动授权 <span v-if="editing && (detail as McpServerSummary)?.oauthConfigured" class="mcp-transport-tag">已授权</span></strong>
              <small>默认自动发起浏览器授权并静默刷新令牌；勾选后仅使用手动配置的凭证</small>
            </span>
            <input v-model="form.oauthDisabled" class="sr-only" type="checkbox" />
            <span class="toggle-switch" aria-hidden="true"><span /></span>
          </label>
        </div>
      </section>

      <!-- 安装 -->
      <section v-if="editing" class="mcp-detail-card">
        <div class="mcp-advanced-row">
          <div>
            <strong>移除 MCP</strong>
            <small>移除此 MCP 服务器及其存储的身份验证信息。</small>
          </div>
          <button class="secondary-button mcp-remove-button" :disabled="action === 'mcp.remove'" @click="removeServer">
            <LoaderCircle v-if="action === 'mcp.remove'" class="spin" :size="14" />
            <Trash2 v-else :size="14" /> 移除 MCP
          </button>
        </div>
      </section>

      <footer class="mcp-detail-footer">
        <span>按 Ctrl + Enter 保存</span>
        <div>
          <button class="secondary-button" :disabled="action === 'mcp.save'" @click="closeDetail">取消</button>
          <button class="secondary-button primary-action" :disabled="action === 'mcp.save'" @click="save">
            <LoaderCircle v-if="action === 'mcp.save'" class="spin" :size="15" />
            <Save v-else :size="15" /> {{ action === 'mcp.save' ? '正在连接…' : '保存并重载' }}
          </button>
        </div>
      </footer>
    </template>
  </div>
</template>

<style scoped>
.mcp-panel {
  display: flex;
  flex-direction: column;
  gap: 18px;
}

.mcp-list-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.mcp-list-heading h3 {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  margin: 0;
  font-size: 14.5px;
  font-weight: 650;
  color: var(--text);
}

.mcp-count-chip {
  min-width: 20px;
  padding: 1px 7px;
  color: var(--text-secondary);
  font-size: 11.5px;
  font-weight: 600;
  text-align: center;
  background: color-mix(in srgb, var(--surface-hover) 82%, transparent);
  border: 1px solid var(--border);
  border-radius: 999px;
}

.mcp-panel-message {
  margin: 0;
  padding: 9px 12px;
  color: var(--text-secondary);
  font-size: 12px;
  background: color-mix(in srgb, var(--accent) 7%, transparent);
  border: 1px solid color-mix(in srgb, var(--accent) 22%, transparent);
  border-radius: 8px;
}

.mcp-panel-message.error {
  color: var(--danger);
  background: color-mix(in srgb, var(--danger) 9%, transparent);
  border-color: color-mix(in srgb, var(--danger) 25%, transparent);
}

.mcp-empty-card {
  padding: 26px 18px;
  color: var(--text-muted);
  font-size: 12.5px;
  text-align: center;
  border: 1px dashed var(--border-strong);
  border-radius: 12px;
}

/* ---------------- server cards ---------------- */

.mcp-server-list {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.mcp-server-card {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 13px 14px;
  cursor: pointer;
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 12px;
  transition: border-color 140ms ease, background-color 140ms ease;
}

.mcp-server-card:hover {
  background: color-mix(in srgb, var(--surface-hover) 42%, var(--surface-raised));
  border-color: var(--border-strong);
}

.mcp-server-icon {
  display: grid;
  width: 38px;
  height: 38px;
  flex: 0 0 auto;
  place-items: center;
  color: var(--text);
  background: color-mix(in srgb, var(--surface-hover) 55%, var(--surface-raised));
  border: 1px solid var(--border);
  border-radius: 10px;
}

.mcp-server-icon.large {
  width: 44px;
  height: 44px;
  border-radius: 11px;
}

.mcp-server-main {
  display: flex;
  min-width: 0;
  flex: 1;
  flex-direction: column;
  gap: 3px;
}

.mcp-server-main strong {
  overflow: hidden;
  font-size: 13.5px;
  font-weight: 620;
  color: var(--text);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mcp-server-main small {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--text-muted);
  font-size: 11.5px;
}

.mcp-status-dot {
  width: 8px;
  height: 8px;
  flex: 0 0 auto;
  background: var(--text-muted);
  border-radius: 50%;
}

.mcp-status-dot.online {
  background: #2ea44f;
}

.mcp-status-dot.pending {
  background: color-mix(in srgb, var(--accent) 70%, transparent);
}

.mcp-server-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

/* ---------------- toggle switch ---------------- */

.mcp-toggle-row {
  display: inline-flex;
  flex: 0 0 auto;
  cursor: pointer;
}

.mcp-toggle-row input:checked + .toggle-switch {
  background: var(--accent);
}

.mcp-toggle-row input:checked + .toggle-switch > span {
  transform: translateX(16px);
}

.mcp-toggle-row input:focus-visible + .toggle-switch {
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 28%, transparent);
}

.mcp-toggle-spinner {
  color: var(--text-secondary);
}

.toggle-switch {
  display: flex;
  width: 38px;
  height: 22px;
  align-items: center;
  padding: 2px;
  background: var(--border-strong);
  border-radius: 999px;
  transition: background-color 160ms ease;
}

.toggle-switch > span {
  width: 18px;
  height: 18px;
  background: white;
  border-radius: 50%;
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.2);
  transition: transform 180ms cubic-bezier(0.2, 0.8, 0.2, 1);
}

/* ---------------- catalog ---------------- */

.mcp-catalog {
  display: flex;
  flex-direction: column;
  gap: 12px;
  margin-top: 6px;
}

.mcp-catalog-heading h3 {
  margin: 0;
  font-size: 14.5px;
  font-weight: 650;
  color: var(--text);
}

.mcp-catalog-heading p {
  margin: 3px 0 0;
  color: var(--text-muted);
  font-size: 12px;
}

.mcp-catalog-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(218px, 1fr));
  gap: 12px;
}

.mcp-preset-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 15px;
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 12px;
  transition: border-color 140ms ease, box-shadow 140ms ease;
}

.mcp-preset-card:hover {
  border-color: var(--border-strong);
  box-shadow: 0 3px 14px rgba(0, 0, 0, 0.06);
}

.mcp-preset-card strong {
  margin-top: 4px;
  font-size: 13.5px;
  font-weight: 640;
  color: var(--text);
}

.mcp-preset-card p {
  display: -webkit-box;
  min-height: 48px;
  margin: 0;
  overflow: hidden;
  color: var(--text-muted);
  font-size: 11.5px;
  line-height: 1.55;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 3;
}

.mcp-preset-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  margin-top: auto;
}

.mcp-preset-footer small {
  overflow: hidden;
  color: var(--text-muted);
  font-size: 11px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mcp-preset-footer .primary-action {
  flex: 0 0 auto;
  padding: 5px 14px;
}

/* ---------------- manual add ---------------- */

.mcp-manual-card {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 14px;
  padding: 14px 16px;
  background: color-mix(in srgb, var(--surface-hover) 36%, var(--surface-raised));
  border: 1px solid var(--border);
  border-radius: 12px;
}

.mcp-manual-card div {
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.mcp-manual-card strong {
  font-size: 13px;
  color: var(--text);
}

.mcp-manual-card small {
  color: var(--text-muted);
  font-size: 11.5px;
}

/* ---------------- detail view ---------------- */

.mcp-back {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  align-self: flex-start;
  padding: 5px 10px 5px 6px;
  color: var(--text-secondary);
  font-size: 12.5px;
  background: transparent;
  border: none;
  border-radius: 8px;
  cursor: pointer;
}

.mcp-back:hover {
  color: var(--text);
  background: color-mix(in srgb, var(--surface-hover) 65%, transparent);
}

.mcp-detail-header {
  display: flex;
  align-items: center;
  gap: 13px;
}

.mcp-detail-header h3 {
  margin: 0;
  font-size: 16px;
  font-weight: 660;
  color: var(--text);
}

.mcp-detail-header p {
  margin: 3px 0 0;
  color: var(--text-muted);
  font-size: 12px;
}

.mcp-detail-card {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 15px 16px;
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 12px;
}

.mcp-detail-card-title strong {
  font-size: 13px;
  font-weight: 640;
  color: var(--text);
}

.mcp-status-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 14px;
}

.mcp-status-row small {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--text-muted);
  font-size: 12px;
}

.mcp-error-text {
  color: var(--danger);
  word-break: break-all;
}

.mcp-form-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 14px 16px;
}

.form-field {
  display: flex;
  min-width: 0;
  width: 100%;
  flex-direction: column;
  gap: 7px;
}

.form-field.full-width {
  grid-column: 1 / -1;
}

.form-input {
  width: 100%;
  box-sizing: border-box;
  min-height: 37px;
  padding: 8px 12px;
  color: var(--text);
  font-size: 13px;
  font-family: inherit;
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 8px;
  outline: none;
  transition: border-color 140ms ease, box-shadow 140ms ease;
}

.form-input:hover:not(:disabled) {
  border-color: color-mix(in srgb, var(--text-muted) 58%, var(--border));
}

.form-input:focus {
  border-color: color-mix(in srgb, var(--accent) 66%, var(--border));
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 20%, transparent);
}

.form-input:disabled {
  opacity: .62;
}

.mcp-connection-segmented {
  width: fit-content;
}

.mcp-auth-mode-note {
  font-size: 10.5px;
  opacity: .75;
}

.mcp-auth-note {
  color: var(--text-muted);
  font-size: 11.5px;
}

.mcp-connection-summary {
  color: var(--text-muted);
  font-size: 11.5px;
  font-family: var(--font-mono, monospace);
  word-break: break-all;
}

.mcp-credential-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.mcp-credential-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.mcp-credential-key {
  flex: 0 0 auto;
  max-width: 40%;
  overflow: hidden;
  padding: 8px 10px;
  color: var(--text-secondary);
  font-size: 12px;
  font-family: var(--font-mono, monospace);
  text-overflow: ellipsis;
  white-space: nowrap;
  background: color-mix(in srgb, var(--surface-hover) 70%, transparent);
  border: 1px solid var(--border);
  border-radius: 8px;
}

.mcp-credential-row .form-input {
  flex: 1;
  min-height: 34px;
}

.mcp-credential-add {
  display: flex;
  gap: 8px;
  margin-top: 2px;
}

.mcp-credential-add .form-input {
  flex: 1;
  min-height: 34px;
}

.mcp-token-hint {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  margin-top: 2px;
  padding: 12px 14px;
  background: color-mix(in srgb, var(--accent) 6%, transparent);
  border: 1px solid color-mix(in srgb, var(--accent) 20%, transparent);
  border-radius: 9px;
}

.mcp-token-hint-copy {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 6px;
}

.mcp-token-hint-copy strong {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--text);
  font-size: 12.5px;
}

.mcp-token-hint-copy ol {
  display: flex;
  flex-direction: column;
  gap: 3px;
  margin: 0;
  padding-left: 17px;
  color: var(--text-muted);
  font-size: 11.5px;
  line-height: 1.5;
}

.mcp-token-hint a.secondary-button {
  align-self: center;
  color: var(--text-secondary);
  text-decoration: none;
}

.mcp-advanced-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 14px;
}

.mcp-advanced-row > div {
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.mcp-advanced-row strong {
  font-size: 13px;
  color: var(--text);
}

.mcp-advanced-row small {
  color: var(--text-muted);
  font-size: 11.5px;
}

.mcp-remove-button {
  color: var(--danger);
}

.mcp-remove-button:hover:not(:disabled) {
  color: var(--danger);
  border-color: color-mix(in srgb, var(--danger) 45%, var(--border));
}

.mcp-advanced-grid {
  padding-top: 12px;
  border-top: 1px solid var(--border);
}

.mcp-inline-toggle {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 18px;
  padding: 11px 13px;
  cursor: pointer;
  background: color-mix(in srgb, var(--surface-hover) 60%, transparent);
  border: 1px solid var(--border);
  border-radius: 9px;
}

.mcp-inline-toggle > span:first-child {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 3px;
}

.mcp-inline-toggle strong {
  font-size: 12.5px;
  color: var(--text);
}

.mcp-inline-toggle small {
  color: var(--text-muted);
  font-size: 11px;
}

.mcp-inline-toggle input:checked + .toggle-switch {
  background: var(--accent);
}

.mcp-inline-toggle input:checked + .toggle-switch > span {
  transform: translateX(16px);
}

.mcp-transport-tag {
  padding: 1px 7px;
  color: var(--text-muted);
  font-size: 11px;
  background: color-mix(in srgb, var(--surface-hover) 80%, transparent);
  border: 1px solid var(--border);
  border-radius: 999px;
}

.mcp-detail-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding-top: 2px;
}

.mcp-detail-footer > span {
  color: var(--text-muted);
  font-size: 11.5px;
}

.mcp-detail-footer > div {
  display: flex;
  gap: 8px;
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  white-space: nowrap;
  border: 0;
  clip: rect(0, 0, 0, 0);
}

.spin {
  animation: mcp-spin 1s linear infinite;
}

@keyframes mcp-spin {
  to { transform: rotate(360deg); }
}

.compact-button {
  padding: 5px 12px;
  font-size: 12px;
}
</style>
