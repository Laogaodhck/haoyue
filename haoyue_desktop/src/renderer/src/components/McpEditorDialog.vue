<script setup lang="ts">
import { ExternalLink, KeyRound, Plus, Save, X } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, reactive, ref, watch } from 'vue'
import {
  MCP_SCOPE_OPTIONS,
  MCP_TRANSPORT_OPTIONS,
  buildMcpServerPayload,
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
  classifyTransport,
  type CredentialRow,
  type McpFormValue,
  type McpScope,
  type McpServerSummary
} from '../mcp-form'
import SelectMenu from './SelectMenu.vue'
import FieldLabel from './FieldLabel.vue'

const props = defineProps<{
  open: boolean
  /** null opens an empty form; a server opens the editor for that entry. */
  server: McpServerSummary | null
  /** Prefilled values for a new server, supplied by one-click presets. */
  preset?: McpFormValue | null
  saving?: boolean
  /** Error reported by the runtime for the last save attempt. */
  error?: string
}>()

const emit = defineEmits<{
  close: []
  save: [payload: { name: string; scope: McpScope; server: Record<string, unknown> }]
}>()

const form = reactive<McpFormValue>(createMcpFormValue())
const validationError = ref('')
const firstInput = ref<HTMLInputElement | null>(null)
/** False until the user edits the name field — auto-inference stops afterwards. */
const nameTouched = ref(false)
const newEnvKey = ref('')
const newHeaderKey = ref('')
/** Expert section pre-opens only when the edited server actually uses those fields. */
const expertOpen = ref(false)

const editing = computed(() => props.server !== null)
const remote = computed(() => isRemoteTransport(form.transport))
const message = computed(() => validationError.value || props.error || '')
const githubTokenUrl = githubTokenCreateUrl()
const envRows = computed(() => parseCredentialRows(form.env))
const headerRows = computed(() => parseCredentialRows(form.headers))
const showGithubHint = computed(() => !remote.value && envNeedsGithubToken(form.env))
/** Short human summary of what the connection string will do. */
const connectionSummary = computed(() => {
  const value = form.connection.trim()
  if (!value) return ''
  if (/^https?:\/\//i.test(value)) return `远程连接：POST/GET ${value}`
  if (parseMcpJsonConfig(value)) return '检测到 JSON 配置，已自动填充各字段'
  return `本地命令：${value}`
})

function close(): void {
  if (!props.saving) emit('close')
}

function save(): void {
  if (props.saving) return

  const invalid = mcpFormError(form)
  if (invalid) {
    validationError.value = invalid
    return
  }

  validationError.value = ''
  emit('save', {
    name: form.name.trim(),
    scope: form.scope,
    server: buildMcpServerPayload(form)
  })
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') close()
  if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
    event.preventDefault()
    save()
  }
}

// ---------------------------------------------------------------------------
// Credential rows: rendered as per-key password inputs. An empty value sent to
// the runtime means "keep the stored value" — the stored value itself never
// reaches the UI, so editing never requires re-typing credentials.
// ---------------------------------------------------------------------------

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
}

// Live recognition: the connection string fills name/transport and expands bare
// package names. All inference happens here at the input layer — the payload
// that gets stored is always the explicit expansion, so a wrong guess surfaces
// immediately in the connection test instead of failing silently.
watch(() => form.connection, (value) => {
  if (!props.open || editing.value) {
    // Editing keeps recognition on for JSON pastes only — the stored name/URL
    // must not be silently rewritten while the user edits another field.
    if (props.open && parseMcpJsonConfig(value)) applyJsonConfig()
    return
  }
  if (parseMcpJsonConfig(value)) {
    applyJsonConfig()
    return
  }
  const isUrl = /^https?:\/\//i.test(value.trim())
  if (remote.value && !isUrl) {
    // User replaced a URL with something local — switch back.
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

watch(() => props.open, (open) => {
  if (!open) return
  Object.assign(
    form,
    props.server ? mcpFormFromServer(props.server) : (props.preset ?? createMcpFormValue())
  )
  validationError.value = ''
  nameTouched.value = false
  newEnvKey.value = ''
  newHeaderKey.value = ''
  expertOpen.value = Boolean(
    props.server &&
    (props.server.connectTimeoutSeconds ||
      (props.server.mutatingTools?.length ?? 0) > 0 ||
      (props.server.readOnlyTools?.length ?? 0) > 0 ||
      props.server.trustReadOnly ||
      props.server.oauthConfigured)
  )
  document.addEventListener('keydown', handleKeydown)
  void nextTick(() => firstInput.value?.focus())
}, { immediate: true })

watch(() => props.open, (open, previous) => {
  if (!open && previous) document.removeEventListener('keydown', handleKeydown)
})

// Editing any field invalidates the previous validation message.
watch(form, () => {
  if (validationError.value) validationError.value = ''
})

onBeforeUnmount(() => document.removeEventListener('keydown', handleKeydown))
</script>

<template>
  <Teleport to="body">
    <Transition name="modal-fade">
      <div v-if="open" class="modal-backdrop mcp-editor-backdrop" @mousedown.self="close">
        <form class="mcp-editor-dialog" role="dialog" aria-modal="true" aria-labelledby="mcp-editor-title" @submit.prevent="save">
          <header class="mcp-editor-header">
            <div>
              <h2 id="mcp-editor-title">{{ editing ? '编辑连接器' : '新增连接器' }}</h2>
              <p>{{ editing ? form.name : '粘贴包名、命令或 URL，一步接入 MCP Server' }}</p>
            </div>
            <button class="icon-button" type="button" title="关闭" :disabled="saving" @click="close">
              <X :size="18" />
            </button>
          </header>

          <div class="mcp-editor-body">
            <section class="mcp-form-section">
              <div class="mcp-form-grid">
                <label class="form-field full-width">
                  <FieldLabel
                    en="Connection"
                    zh="连接"
                    help="粘贴 npm/uv 包名、完整启动命令或远程 URL；粘贴完整 JSON 配置也可以自动识别。"
                    required
                  />
                  <input
                    ref="firstInput"
                    v-model="form.connection"
                    class="form-input"
                    :placeholder="remote ? 'https://example.com/mcp' : '@modelcontextprotocol/server-github'"
                    autocomplete="off"
                    spellcheck="false"
                  />
                  <small v-if="connectionSummary" class="mcp-connection-summary">{{ connectionSummary }}</small>
                </label>

                <label class="form-field">
                  <FieldLabel en="Name" zh="名称" help="连接器的唯一标识；创建后不可修改。留空会根据连接内容自动推断。" required />
                  <input
                    v-model="form.name"
                    class="form-input"
                    placeholder="github"
                    :disabled="editing"
                    autocomplete="off"
                    @input="nameTouched = true"
                  />
                </label>
                <label class="form-field">
                  <FieldLabel en="Scope" zh="范围" help="全局对所有工作区生效；工作区仅对当前项目生效。" />
                  <SelectMenu v-model="form.scope" :options="MCP_SCOPE_OPTIONS" label="MCP 范围" />
                </label>
              </div>
            </section>

            <section class="mcp-form-section">
              <div class="mcp-section-heading">
                <strong>凭据 <span class="mcp-transport-tag">{{ transportLabel(form.transport) }}</span></strong>
                <small>值加密存储，保存后不会回显；留空表示保持原值不变</small>
              </div>

              <template v-if="!remote">
                <div class="mcp-credential-list">
                  <div v-for="(row, index) in envRows" :key="row.key" class="mcp-credential-row">
                    <code class="mcp-credential-key">{{ row.key }}</code>
                    <input
                      class="form-input"
                      type="password"
                      :placeholder="editing ? '留空保持原值' : '输入值'"
                      :value="row.value"
                      autocomplete="new-password"
                      @input="updateEnvRows(envRows.map((r, i) => i === index ? { ...r, value: ($event.target as HTMLInputElement).value } : r))"
                    />
                    <button class="icon-button" type="button" title="删除" @click="updateEnvRows(envRows.filter((_, i) => i !== index))">
                      <X :size="14" />
                    </button>
                  </div>
                  <div class="mcp-credential-add">
                    <input v-model="newEnvKey" class="form-input" placeholder="环境变量名，如 GITHUB_PERSONAL_ACCESS_TOKEN" autocomplete="off" spellcheck="false" @keydown.enter.prevent="addEnvKey" />
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
              </template>

              <template v-else>
                <div class="mcp-credential-list">
                  <div v-for="(row, index) in headerRows" :key="row.key" class="mcp-credential-row">
                    <code class="mcp-credential-key">{{ row.key }}</code>
                    <input
                      class="form-input"
                      type="password"
                      :placeholder="editing ? '留空保持原值' : '输入值'"
                      :value="row.value"
                      autocomplete="new-password"
                      @input="updateHeaderRows(headerRows.map((r, i) => i === index ? { ...r, value: ($event.target as HTMLInputElement).value } : r))"
                    />
                    <button class="icon-button" type="button" title="删除" @click="updateHeaderRows(headerRows.filter((_, i) => i !== index))">
                      <X :size="14" />
                    </button>
                  </div>
                  <div class="mcp-credential-add">
                    <input v-model="newHeaderKey" class="form-input" placeholder="请求头名称，如 Authorization" autocomplete="off" spellcheck="false" @keydown.enter.prevent="addHeaderKey" />
                    <button class="secondary-button" type="button" :disabled="!newHeaderKey.trim()" @click="addHeaderKey">
                      <Plus :size="14" /> 添加
                    </button>
                  </div>
                </div>
                <small class="mcp-headers-hint">常用：Authorization = Bearer &lt;token&gt;。携带请求头的远程连接必须使用 https://（127.0.0.1 本地调试除外）。支持 OAuth 的服务器保存后会自动发起浏览器授权，无需手动填请求头。</small>
              </template>
            </section>

            <section class="mcp-form-section">
              <details class="mcp-advanced" :open="expertOpen">
                <summary>
                  <strong>专家选项</strong>
                  <small>传输协议、超时与信任设置；主路径无需修改</small>
                </summary>
                <div class="mcp-form-grid mcp-advanced-grid">
                  <label class="form-field">
                    <FieldLabel en="Transport" zh="传输协议" help="默认由连接内容自动判断；仅当自动判断不符合预期时覆盖。" />
                    <SelectMenu v-model="form.transport" :options="MCP_TRANSPORT_OPTIONS" label="MCP 连接方式" />
                  </label>
                  <label v-if="remote" class="form-field">
                    <FieldLabel en="Connect timeout" zh="连接超时（秒）" help="1~120；留空使用默认 10 秒，内网慢服务器可调大。" />
                    <input
                      v-model="form.connectTimeoutSeconds"
                      class="form-input"
                      type="number"
                      min="1"
                      max="120"
                      placeholder="10"
                      autocomplete="off"
                    />
                  </label>
                  <label class="form-field full-width">
                    <FieldLabel en="Mutating tools" zh="视为变更类工具" help="每行一个工具名；这些工具始终按「会改动真实世界」处理，只读模式下默认拒绝执行。" />
                    <textarea v-model="form.mutatingTools" class="form-input" rows="2" placeholder="send_email&#10;deploy"></textarea>
                  </label>
                  <label class="form-field full-width">
                    <FieldLabel en="Read-only tools" zh="只读工具白名单" help="每行一个工具名；优先级高于变更类清单，命中即视为安全只读。" />
                    <textarea v-model="form.readOnlyTools" class="form-input" rows="2" placeholder="search&#10;get_weather"></textarea>
                  </label>
                  <label class="mcp-enabled-row mcp-trust-row">
                    <span>
                      <strong>信任只读工具</strong>
                      <small>勾选后，名称不含变更关键词的工具一律视为只读；仅对已知安全的服务器使用</small>
                    </span>
                    <input v-model="form.trustReadOnly" class="sr-only" type="checkbox" />
                    <span class="toggle-switch" aria-hidden="true"><span /></span>
                  </label>
                  <label v-if="remote" class="mcp-enabled-row mcp-trust-row">
                    <span>
                      <strong>禁用 OAuth 自动授权 <span v-if="server?.oauthConfigured" class="mcp-transport-tag">已授权</span></strong>
                      <small>默认自动发起浏览器授权并静默刷新令牌；勾选后仅使用上方手动配置的凭证</small>
                    </span>
                    <input v-model="form.oauthDisabled" class="sr-only" type="checkbox" />
                    <span class="toggle-switch" aria-hidden="true"><span /></span>
                  </label>
                </div>
              </details>
            </section>

            <section class="mcp-form-section">
              <label class="mcp-enabled-row">
                <span>
                  <strong>启用</strong>
                  <small>保存后立即连接并注册该 Server 的工具与 Prompt</small>
                </span>
                <input v-model="form.enabled" class="sr-only" type="checkbox" />
                <span class="toggle-switch" aria-hidden="true"><span /></span>
              </label>
            </section>

            <p v-if="message" class="mcp-editor-error">{{ message }}</p>
          </div>

          <footer class="mcp-editor-footer">
            <span>按 Ctrl + Enter 保存</span>
            <div>
              <button class="secondary-button" type="button" :disabled="saving" @click="close">取消</button>
              <button class="secondary-button primary-action" type="submit" :disabled="saving">
                <Save :size="15" /> {{ saving ? '正在连接…' : '保存并重载' }}
              </button>
            </div>
          </footer>
        </form>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.mcp-editor-backdrop {
  position: fixed;
  z-index: 130;
  inset: 0;
  display: grid;
  place-items: center;
  padding: 20px;
  background: rgba(0, 0, 0, 0.45);
  backdrop-filter: blur(8px);
}

.mcp-editor-dialog {
  display: flex;
  flex-direction: column;
  width: min(100%, 620px);
  max-height: min(760px, calc(100vh - 40px));
  overflow: hidden;
  background: var(--surface-raised, #ffffff);
  border: 1px solid var(--border);
  border-radius: 12px;
  box-shadow: 0 24px 70px rgba(0, 0, 0, 0.28), 0 2px 10px rgba(0, 0, 0, 0.1);
}

.mcp-editor-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 20px;
  padding: 20px 24px 16px;
  background: var(--surface-raised);
  border-bottom: 1px solid var(--border);
}

.mcp-editor-header h2 {
  margin: 0;
  font-size: 18px;
  font-weight: 650;
  color: var(--text);
  letter-spacing: -0.01em;
}

.mcp-editor-header p {
  margin: 4px 0 0;
  color: var(--text-muted);
  font-size: 12px;
}

.mcp-editor-body {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 18px;
  padding: 20px 24px;
  overflow-x: hidden;
  overflow-y: auto;
  background: var(--surface-raised);
}

.mcp-form-section {
  display: flex;
  flex-direction: column;
  padding-bottom: 18px;
  border-bottom: 1px solid var(--border);
}

.mcp-form-section:last-of-type {
  padding-bottom: 0;
  border-bottom: none;
}

.mcp-section-heading {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 12px;
}

.mcp-section-heading strong {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  font-size: 13px;
  font-weight: 600;
  color: var(--text);
}

.mcp-transport-tag {
  padding: 1px 7px;
  color: var(--text-muted);
  font-size: 11px;
  font-family: var(--font-mono, monospace);
  background: color-mix(in srgb, var(--surface-hover) 80%, transparent);
  border: 1px solid var(--border);
  border-radius: 999px;
}

.mcp-section-heading small {
  color: var(--text-muted);
  font-size: 11.5px;
}

.mcp-form-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 14px 16px;
  width: 100%;
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

.form-field > span {
  color: var(--text-muted);
  font-size: 12px;
}

.form-input {
  width: 100%;
  box-sizing: border-box;
  min-height: 38px;
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

.mcp-headers-hint {
  margin-top: 8px;
  color: var(--text-muted);
  font-size: 11.5px;
}

.mcp-advanced {
  border: 1px solid var(--border);
  border-radius: 10px;
  background: color-mix(in srgb, var(--surface-hover) 40%, transparent);
}

.mcp-advanced summary {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 12px;
  cursor: pointer;
  user-select: none;
  list-style: none;
}

.mcp-advanced summary::-webkit-details-marker {
  display: none;
}

.mcp-advanced summary::after {
  content: '▸';
  margin-left: auto;
  color: var(--text-muted);
  font-size: 12px;
  transition: transform 140ms ease;
}

.mcp-advanced[open] summary::after {
  transform: rotate(90deg);
}

.mcp-advanced summary strong {
  font-size: 12.5px;
  font-weight: 600;
  color: var(--text-secondary);
}

.mcp-advanced-grid {
  padding: 12px;
  border-top: 1px solid var(--border);
}

.mcp-enabled-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 18px;
  padding: 12px 14px;
  cursor: pointer;
  background: color-mix(in srgb, var(--surface-hover) 65%, transparent);
  border: 1px solid var(--border);
  border-radius: 9px;
  transition: background-color 140ms ease;
}

.mcp-enabled-row:hover {
  background: var(--surface-hover);
}

.mcp-enabled-row > span:first-child {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 3px;
}

.mcp-enabled-row strong {
  font-size: 13px;
  color: var(--text);
}

.mcp-enabled-row small {
  color: var(--text-muted);
  font-size: 11.5px;
}

.mcp-editor-error {
  margin: 0;
  padding: 9px 12px;
  color: var(--danger);
  font-size: 12px;
  background: color-mix(in srgb, var(--danger) 9%, transparent);
  border: 1px solid color-mix(in srgb, var(--danger) 25%, transparent);
  border-radius: 8px;
}

.mcp-token-hint {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  margin-top: 10px;
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

.mcp-editor-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 13px 24px;
  background: color-mix(in srgb, var(--sidebar) 72%, var(--surface-raised));
  border-top: 1px solid var(--border);
}

.mcp-editor-footer > span {
  color: var(--text-muted);
  font-size: 11.5px;
}

.mcp-editor-footer > div {
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

.toggle-switch {
  display: flex;
  width: 38px;
  height: 22px;
  flex: 0 0 auto;
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

.mcp-enabled-row input:checked + .toggle-switch {
  background: var(--accent);
}

.mcp-enabled-row input:checked + .toggle-switch > span {
  transform: translateX(16px);
}
</style>
