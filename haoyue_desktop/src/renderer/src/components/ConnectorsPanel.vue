<script setup lang="ts">
import {
  Bell,
  CalendarDays,
  ChevronDown,
  ClipboardList,
  Database,
  GitBranch,
  GitFork,
  Globe2,
  LoaderCircle,
  Mail,
  NotebookText,
  PackageOpen,
  Plug,
  RefreshCw,
  Rss,
  Search
} from '@lucide/vue'
import { computed, onMounted, ref, type Component } from 'vue'
import { confirmAction } from '../confirmation'

const emit = defineEmits<{
  /** 目录发生变化（安装/卸载/启停），宿主可刷新 MCP 状态。 */
  changed: []
}>()

/** 连接器包条目，由 daemon 的 connectors.list 接口返回。 */
interface ConnectorEntry {
  id: string
  name: string
  description: string
  icon?: string
  category: string
  tags?: string[]
  offline: boolean
  homepage?: string
  /** 6.3 生效来源（经完整性清单校验）：official / verified / community / local。 */
  source?: string
  /** 声明的来源未通过校验时的降级说明。 */
  sourceWarning?: string
  /** 最低运行时版本（x.y.z）；低于当前运行时由 daemon 给出提示时使用。 */
  minRuntimeVersion?: string
  installed: boolean
  enabled: boolean
  inputs: ConnectorInputMeta[]
}

/** 安装时需收集的配置项（只给键名与元数据，不给已存值）。 */
interface ConnectorInputMeta {
  key: string
  title: string
  description?: string
  required: boolean
  secret: boolean
  hasDefault: boolean
}

const catalog = ref<ConnectorEntry[]>([])
const catalogAvailable = ref(true)
const loading = ref(false)
const error = ref('')
/** 安装结果提示（未填写的配置项：已从配置剔除，功能可能降级）。 */
const notice = ref('')
const query = ref('')
/** 正在安装/卸载/启停中的连接器 id。 */
const pending = ref<string[]>([])
/** 展开安装表单的连接器 id 与其表单值。 */
const expanded = ref<string | null>(null)
const formValues = ref<Record<string, string>>({})
const formError = ref('')

const SOURCE_LABELS: Record<string, string> = {
  official: '官方',
  verified: '已认证',
  community: '社区',
  local: '本地',
}
/** 6.3 来源标签：daemon 已按完整性清单校验（official 需 SHA256SUMS 登记为证）。 */
function sourceLabel(source: string | undefined): string {
  return SOURCE_LABELS[source ?? 'local'] ?? source ?? '本地'
}

/**
 * P0-1：连接器图标统一渲染为项目锁定的 SVG 图标（@lucide/vue）。
 * connector.json 数据层的 icon 字段是历史 emoji，仅保留兼容不再渲染；
 * 未登记的连接器回退到 Plug 语义图标。
 */
const CONNECTOR_ICONS: Record<string, Component> = {
  browser: Globe2,
  calendar: CalendarDays,
  clipboard: ClipboardList,
  email: Mail,
  git: GitBranch,
  github: GitFork,
  'http-api': Plug,
  notify: Bell,
  notion: NotebookText,
  rss: Rss,
  sqlite: Database
}

function connectorIcon(connector: ConnectorEntry): Component {
  return CONNECTOR_ICONS[connector.id] ?? Plug
}

const filtered = computed(() => {
  const normalized = query.value.trim().toLocaleLowerCase()
  if (!normalized) return catalog.value
  return catalog.value.filter((connector) =>
    connector.name.toLocaleLowerCase().includes(normalized) ||
    connector.description.toLocaleLowerCase().includes(normalized) ||
    connector.tags?.some((tag) => tag.toLocaleLowerCase().includes(normalized)))
})

const installedCount = computed(() => catalog.value.filter((item) => item.installed).length)

function isPending(connector: ConnectorEntry): boolean {
  return pending.value.includes(connector.id)
}

function statusLabel(connector: ConnectorEntry): string {
  if (!connector.installed) return '未安装'
  return connector.enabled ? '已启用' : '已停用'
}

async function requestJson<T>(method: string, params: Record<string, unknown> = {}): Promise<T> {
  const response = await window.haoyue.daemon.request(method, params)
  return JSON.parse(response.data) as T
}

async function loadCatalog(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    const payload = await requestJson<{ catalogAvailable: boolean, connectors: ConnectorEntry[] }>('connectors.list')
    catalog.value = payload.connectors
    catalogAvailable.value = payload.catalogAvailable
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    loading.value = false
  }
}

function toggleForm(connector: ConnectorEntry): void {
  formError.value = ''
  if (expanded.value === connector.id) {
    expanded.value = null
    return
  }
  expanded.value = connector.id
  formValues.value = {}
}

/** 一键安装：无需收集项直接装；有 inputs 时展开表单收集后再装。 */
async function install(connector: ConnectorEntry): Promise<void> {
  // 6.3 信任分级：非官方来源首次安装必须显式确认（它会以 MCP 服务器身份在本机运行）。
  if (connector.source && connector.source !== 'official' && !await confirmAction({
    title: '安装来源未经验证',
    message: `「${connector.name}」的来源为「${sourceLabel(connector.source)}」，不是随分发包分发的官方连接器。`
      + '安装后它会以 MCP 服务器身份在你的环境运行，请确认来源可信后再继续。',
    confirmLabel: '仍然安装',
    danger: true,
  })) return

  const needed = connector.inputs.filter((input) => input.required && !input.hasDefault)
  if (needed.length > 0 && expanded.value !== connector.id) {
    toggleForm(connector)
    return
  }
  if (expanded.value === connector.id) {
    const missing = needed.filter((input) => !(formValues.value[input.key] ?? '').trim())
    if (missing.length > 0) {
      formError.value = `请填写：${missing.map((input) => input.title).join('、')}`
      return
    }
  }

  pending.value = [...pending.value, connector.id]
  error.value = ''
  notice.value = ''
  formError.value = ''
  try {
    const withValues = expanded.value === connector.id
    const payload = await requestJson<{ connectors: ConnectorEntry[], unfilledInputs?: string[] }>(
      'connectors.install',
      withValues ? { id: connector.id, values: { ...formValues.value } } : { id: connector.id })
    catalog.value = payload.connectors
    notice.value = payload.unfilledInputs?.length
      ? `未填写的配置项：${payload.unfilledInputs.join('、')}。相关功能可能不可用，重新安装并填写即可补齐。`
      : ''
    expanded.value = null
    formValues.value = {}
    emit('changed')
  } catch (reason) {
    const message = reason instanceof Error ? reason.message : String(reason)
    if (expanded.value === connector.id) formError.value = message
    else error.value = message
  } finally {
    pending.value = pending.value.filter((id) => id !== connector.id)
  }
}

async function uninstall(connector: ConnectorEntry): Promise<void> {
  if (isPending(connector)) return
  pending.value = [...pending.value, connector.id]
  error.value = ''
  try {
    await requestJson('connectors.uninstall', { id: connector.id })
    await loadCatalog()
    emit('changed')
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    pending.value = pending.value.filter((id) => id !== connector.id)
  }
}

/** 启停复用 mcp.upsert 的合并语义：只改 enabled，其余配置原样保留。 */
async function toggleEnabled(connector: ConnectorEntry): Promise<void> {
  if (isPending(connector)) return
  pending.value = [...pending.value, connector.id]
  error.value = ''
  try {
    await requestJson('mcp.upsert', {
      name: connector.id,
      scope: 'global',
      server: { enabled: !connector.enabled },
    })
    await loadCatalog()
    emit('changed')
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    pending.value = pending.value.filter((id) => id !== connector.id)
  }
}

onMounted(() => { void loadCatalog() })
</script>

<template>
  <div class="connectors-panel">
    <div class="connectors-toolbar">
      <label class="connectors-search">
        <Search :size="16" />
        <input v-model="query" placeholder="搜索连接器" aria-label="搜索连接器" />
      </label>
      <span class="connectors-count">{{ installedCount }}/{{ catalog.length }} 已安装</span>
      <button class="icon-button" title="刷新" :disabled="loading" @click="loadCatalog">
        <RefreshCw :size="16" :class="{ spinning: loading }" />
      </button>
    </div>

    <p v-if="error" class="connectors-error">{{ error }}</p>
    <p v-if="notice" class="connectors-notice">{{ notice }}</p>
    <p v-if="!catalogAvailable && !loading" class="connectors-error">
      连接器目录不可用（缺少随包分发的 connectors/ 目录）。
    </p>

    <div class="connectors-grid-wrap">
      <div v-if="loading && catalog.length === 0" class="connectors-empty">
        <RefreshCw :size="30" class="spinning" />
        <strong>正在加载连接器目录…</strong>
      </div>
      <div v-else-if="filtered.length === 0 && catalog.length > 0" class="connectors-empty">
        <Search :size="30" />
        <strong>没有匹配的连接器</strong>
        <span>试试其他搜索词。</span>
      </div>
      <div v-else-if="catalog.length > 0" class="connectors-grid">
        <div v-for="connector in filtered" :key="connector.id" class="connector-card"
          :class="{ installed: connector.installed }">
          <div class="connector-head">
            <span class="connector-icon" aria-hidden="true">
              <component :is="connectorIcon(connector)" :size="20" />
            </span>
            <div class="connector-title">
              <strong :title="connector.id">{{ connector.name }}</strong>
              <span class="connector-status" :class="{ on: connector.installed && connector.enabled }">
                {{ statusLabel(connector) }}
              </span>
            </div>
          </div>
          <p class="connector-desc">{{ connector.description }}</p>
          <div class="connector-badges">
            <span class="inline-badge">{{ connector.category }}</span>
            <span v-if="connector.offline" class="inline-badge">离线可用</span>
            <!-- 6.3 来源角标：official 经清单校验；声明的 official 未通过校验时显示降级说明 -->
            <span v-if="connector.source" class="inline-badge source-badge" :class="`source-${connector.source}`"
              :title="connector.sourceWarning ?? sourceLabel(connector.source)">
              {{ sourceLabel(connector.source) }}
            </span>
            <span v-for="tag in connector.tags" :key="tag" class="inline-badge">{{ tag }}</span>
          </div>
          <p v-if="connector.sourceWarning" class="connector-source-warning">{{ connector.sourceWarning }}</p>

          <div v-if="expanded === connector.id" class="connector-form">
            <label v-for="input in connector.inputs" :key="input.key" class="connector-field">
              <span>{{ input.title }}<em v-if="input.required && !input.hasDefault">*</em></span>
              <input v-model="formValues[input.key]" :type="input.secret ? 'password' : 'text'"
                :placeholder="input.hasDefault ? `默认值已内置` : (input.description || input.key)"
                :autocomplete="input.secret ? 'new-password' : 'off'" />
            </label>
            <p v-if="formError" class="connector-form-error">{{ formError }}</p>
          </div>

          <div class="connector-actions">
            <template v-if="!connector.installed">
              <button class="primary-button compact" :disabled="isPending(connector)"
                @click="install(connector)">
                <LoaderCircle v-if="isPending(connector)" class="spin" :size="14" />
                {{ expanded === connector.id ? '确认安装' : '一键安装' }}
              </button>
              <button v-if="expanded === connector.id" class="secondary-button compact" @click="toggleForm(connector)">
                取消
              </button>
              <button v-else-if="connector.inputs.length > 0" class="icon-button compact"
                :aria-label="`配置 ${connector.name}`" @click="toggleForm(connector)">
                <ChevronDown :size="15" />
              </button>
            </template>
            <template v-else>
              <button class="switch-control" :class="{ active: connector.enabled, pending: isPending(connector) }"
                :disabled="isPending(connector)"
                :aria-label="connector.enabled ? '停用' : '启用'" @click="toggleEnabled(connector)">
                <LoaderCircle v-if="isPending(connector)" class="spin" :size="12" /><span v-else />
              </button>
              <button class="secondary-button compact danger" :disabled="isPending(connector)"
                @click="uninstall(connector)">
                卸载
              </button>
            </template>
          </div>
        </div>
      </div>
      <div v-else class="connectors-empty">
        <PackageOpen :size="34" />
        <strong>{{ error ? '连接器目录加载失败' : '暂无可用连接器' }}</strong>
        <span>{{ error || '请点击右上角刷新重试。' }}</span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.connectors-panel {
  display: grid;
  min-height: 0;
  grid-template-rows: auto minmax(0, 1fr);
}

.connectors-toolbar {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 4px 0 12px;
}

.connectors-search {
  display: flex;
  min-width: 0;
  height: 38px;
  flex: 1;
  align-items: center;
  gap: 9px;
  padding: 0 13px;
  color: var(--text-muted);
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 10px;
}

.connectors-search:focus-within {
  border-color: color-mix(in srgb, var(--accent) 55%, var(--border));
}

.connectors-search input {
  min-width: 0;
  flex: 1;
  color: var(--text);
  background: transparent;
  border: 0;
  outline: 0;
}

.connectors-search input::placeholder {
  color: var(--text-muted);
}

.connectors-count {
  flex: none;
  color: var(--text-muted);
  font-size: 12px;
  white-space: nowrap;
}

.connectors-error {
  margin: 0 0 12px;
  padding: 9px 12px;
  color: var(--danger);
  font-size: 12px;
  background: color-mix(in srgb, var(--danger) 9%, transparent);
  border: 1px solid color-mix(in srgb, var(--danger) 25%, transparent);
  border-radius: 8px;
}

.connectors-notice {
  margin: 0 0 12px;
  padding: 9px 12px;
  color: var(--warning, #b45309);
  font-size: 12px;
  background: color-mix(in srgb, var(--warning, #b45309) 9%, transparent);
  border: 1px solid color-mix(in srgb, var(--warning, #b45309) 25%, transparent);
  border-radius: 8px;
}

.connectors-grid-wrap {
  min-height: 0;
  overflow-y: auto;
  padding-bottom: 8px;
}

.connectors-empty {
  display: grid;
  min-height: 260px;
  place-content: center;
  justify-items: center;
  gap: 7px;
  color: var(--text-muted);
  text-align: center;
}

.connectors-empty svg {
  margin-bottom: 6px;
  color: var(--text-muted);
  opacity: .7;
}

.connectors-empty strong {
  color: var(--text);
  font-size: 15px;
  font-weight: 600;
}

.connectors-empty span {
  font-size: 12px;
}

.connectors-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
  gap: 12px;
}

.connector-card {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 9px;
  padding: 14px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 12px;
}

.connector-card.installed {
  border-color: color-mix(in srgb, var(--accent) 32%, var(--border));
}

.connector-head {
  display: flex;
  align-items: center;
  gap: 10px;
}

.connector-icon {
  display: grid;
  width: 38px;
  height: 38px;
  flex: none;
  place-items: center;
  color: var(--accent);
  background: var(--accent-soft);
  border-radius: 10px;
}

.connector-title {
  display: grid;
  min-width: 0;
  gap: 2px;
}

.connector-title strong {
  overflow: hidden;
  font-size: 13px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.connector-status {
  color: var(--text-muted);
  font-size: 11px;
}

.connector-status.on {
  color: var(--accent);
}

.connector-desc {
  display: -webkit-box;
  margin: 0;
  overflow: hidden;
  color: var(--text-secondary);
  font-size: 12px;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
}

.connector-badges {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
}

.connector-form {
  display: grid;
  gap: 8px;
  padding: 10px;
  background: color-mix(in srgb, var(--surface) 60%, transparent);
  border: 1px dashed var(--border);
  border-radius: 9px;
}

.connector-field {
  display: grid;
  gap: 4px;
  font-size: 12px;
}

.connector-field span {
  color: var(--text-secondary);
}

.connector-field em {
  color: var(--danger);
  font-style: normal;
}

.connector-field input {
  height: 30px;
  padding: 0 9px;
  color: var(--text);
  font-size: 12px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 7px;
  outline: 0;
}

.connector-field input:focus {
  border-color: color-mix(in srgb, var(--accent) 55%, var(--border));
}

.connector-form-error {
  margin: 0;
  color: var(--danger);
  font-size: 11px;
}

.connector-actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  margin-top: auto;
}

.primary-button.compact,
.secondary-button.compact {
  height: 28px;
  padding: 0 12px;
  font-size: 12px;
}

.secondary-button.compact.danger {
  color: var(--danger);
}

.primary-button.compact {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.spinning {
  animation: connectors-spin .8s linear infinite;
}

/* 6.3 来源角标：official 最可信（成功色），community/local 用中性色，降级警告单独一行。 */
.source-badge.source-official {
  color: var(--color-success, #0f6e56);
  border-color: currentColor;
}

.source-badge.source-community,
.source-badge.source-local {
  opacity: .75;
}

.connector-source-warning {
  margin: 4px 0 0;
  font-size: 12px;
  color: var(--color-warning, #854f0b);
}

@keyframes connectors-spin {
  to {
    transform: rotate(360deg);
  }
}
</style>
