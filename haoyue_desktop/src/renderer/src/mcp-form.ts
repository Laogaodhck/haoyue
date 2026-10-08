export type McpTransport = 'stdio' | 'sse' | 'http'
export type McpScope = 'workspace' | 'global'

export interface McpFormValue {
  name: string
  scope: McpScope
  transport: McpTransport
  /**
   * Single-line connection input — the one field the user always fills. It may be
   * a package name (`@modelcontextprotocol/server-github`, auto-expanded to
   * `npx -y …`), a full command line, or a remote URL. The content itself decides
   * the transport; there is no separate transport selector to reason about.
   */
  connection: string
  /** `KEY=value` per line; values are credentials and stored encrypted. */
  env: string
  /** `KEY=value` per line for remote transports (e.g. `Authorization=Bearer …`). */
  headers: string
  enabled: boolean
}

export interface McpServerSummary {
  name: string
  scope: McpScope
  transport: string
  command?: string
  args: string[]
  url?: string
  envKeys: string[]
  /** Remote header key names only — values never leave the runtime. */
  headerKeys?: string[]
  enabled: boolean
  connected: boolean
  /** True while the runtime is still connecting this server in the background. */
  connecting?: boolean
  toolCount: number
  error?: string
  /** B3: prompts skipped by the per-server/global registration caps. */
  warnings?: string[]
}

export interface McpOption {
  value: string
  label: string
  description: string
}

export const MCP_SCOPE_OPTIONS: McpOption[] = [
  { value: 'global', label: '全局', description: '在所有工作区中生效（推荐）' },
  { value: 'workspace', label: '当前工作区', description: '仅在此工作区生效' }
]

/**
 * `stdio` runs a local subprocess; the two remote transports differ in how the
 * JSON-RPC stream travels: `sse` is the legacy HTTP+SSE transport, `http` is the
 * Streamable HTTP transport from the current MCP specification.
 */
export const MCP_TRANSPORT_OPTIONS: McpOption[] = [
  { value: 'stdio', label: 'stdio', description: '通过本地子进程的标准输入输出通信' },
  { value: 'sse', label: 'HTTP SSE', description: '通过 SSE 长连接与远程服务器通信' },
  { value: 'http', label: 'Streamable HTTP', description: '通过流式 HTTP 请求与远程服务器通信' }
]

const TRANSPORT_LABELS: Record<string, string> = {
  stdio: 'stdio',
  sse: 'HTTP SSE',
  http: 'Streamable HTTP',
  'streamable-http': 'Streamable HTTP',
  streamable_http: 'Streamable HTTP',
  websocket: 'WebSocket'
}

/** Human label for a transport id coming from the runtime or from config. */
export function transportLabel(transport?: string): string {
  if (!transport) return ''
  return TRANSPORT_LABELS[transport.toLowerCase()] ?? transport
}

/** Normalizes any accepted transport id onto the three values the editor offers. */
export function normalizeTransport(transport?: string): McpTransport {
  const value = (transport ?? '').toLowerCase()
  if (value === 'sse') return 'sse'
  if (value === 'http' || value === 'streamable-http' || value === 'streamable_http') return 'http'
  return 'stdio'
}

export function isRemoteTransport(transport: string): boolean {
  return transport !== 'stdio'
}

export function createMcpFormValue(): McpFormValue {
  return {
    name: '',
    scope: 'global',
    transport: 'stdio',
    connection: '',
    env: '',
    headers: '',
    enabled: true
  }
}

// ---------------------------------------------------------------------------
// Connection input recognition — all inference lives here and in the dialog's
// live preview. The stored config is always the explicit expansion produced by
// buildMcpServerPayload, so recognition mistakes surface in the connection test
// instead of failing silently.
// ---------------------------------------------------------------------------

/** Interpreters that are already runnable commands, not bare package names. */
const KNOWN_INTERPRETERS = new Set([
  'npx', 'uvx', 'pnpm', 'bunx', 'deno', 'node', 'python', 'python3', 'pipx', 'docker', 'cargo'
])

/** Quote-aware tokenizer for a single command line (double or single quotes). */
export function tokenizeCommand(line: string): string[] {
  const tokens: string[] = []
  let current = ''
  let quote: '"' | "'" | null = null
  let started = false
  for (const ch of line) {
    if (quote) {
      if (ch === quote) quote = null
      else current += ch
      continue
    }
    if (ch === '"' || ch === "'") {
      quote = ch
      started = true
      continue
    }
    if (ch === ' ' || ch === '\t') {
      if (current.length > 0 || started) tokens.push(current)
      current = ''
      started = false
      continue
    }
    current += ch
  }
  if (current.length > 0 || started) tokens.push(current)
  return tokens
}

/**
 * Expands a bare package name into the command that runs it. Idempotent: full
 * commands, URLs and already-expanded forms pass through unchanged.
 */
export function normalizeConnection(raw: string): string {
  const trimmed = raw.trim()
  if (!trimmed || /^https?:\/\//i.test(trimmed)) return trimmed
  const tokens = tokenizeCommand(trimmed)
  const first = tokens[0]
  if (tokens.length === 0 || !first) return trimmed
  if (KNOWN_INTERPRETERS.has(first.toLowerCase())) return trimmed

  const target = tokens[tokens.length - 1]
  if (!target) return trimmed
  const isUv = /^mcp-server-|^mcp_server-|^mcp-server$/.test(target)
  const isNpm = target.startsWith('@') || (target.includes('/') && !target.startsWith('-'))
  const expanded = isUv ? `uvx ${target}` : isNpm ? `npx -y ${target}` : null
  // Extra tokens before a bare package are unusual; only expand the clean case.
  if (expanded && tokens.length === 1) return expanded
  return trimmed
}

/** True when the connection string is a remote URL (decides the transport). */
export function classifyTransport(connection: string): McpTransport {
  return /^https?:\/\//i.test(connection.trim()) ? 'http' : 'stdio'
}

/**
 * Derives a short server name from the connection input: package tail for
 * commands (`server-github` → `github`), hostname body for URLs
 * (`mcp.example.com` → `example`). Returns '' when nothing confident is found.
 */
export function inferMcpName(connection: string): string {
  const trimmed = connection.trim()
  if (!trimmed) return ''

  if (/^https?:\/\//i.test(trimmed)) {
    try {
      const host = new URL(trimmed).hostname
      const label = host
        .replace(/^(mcp|api|server)\./i, '')
        .split('.')
        .find((part) => part.length > 0) ?? ''
      return sanitizeName(label)
    } catch {
      return ''
    }
  }

  const tokens = tokenizeCommand(normalizeConnection(trimmed))
  // The package is the first token that looks like one (skips npx/-y flags).
  const pkg = tokens.find((token, index) => index >= 1 && !token.startsWith('-')) ?? tokens[0]
  if (!pkg) return ''
  const tail = pkg.split(/[\\/]/).pop() ?? pkg
  const stripped = tail.replace(/^(mcp-)?server-/i, '').replace(/\.(exe|cmd|js|mjs|py)$/i, '')
  return sanitizeName(stripped || tail)
}

function sanitizeName(value: string): string {
  return value.toLowerCase().replace(/[^a-z0-9_-]/g, '').slice(0, 40)
}

/**
 * Parses a pasted JSON config (Claude/Cursor style) into form fields. Accepts
 * both the `{"mcpServers": {…}}` wrapper and a single server object. Returns
 * null when the text is not a recognizable MCP config.
 */
export function parseMcpJsonConfig(
  text: string
): { name: string; server: Record<string, unknown> } | null {
  let parsed: unknown
  try {
    parsed = JSON.parse(text)
  } catch {
    return null
  }
  if (parsed === null || typeof parsed !== 'object') return null

  const record = parsed as Record<string, unknown>
  const servers = record.mcpServers ?? record.servers
  if (servers && typeof servers === 'object' && !Array.isArray(servers)) {
    const entry = Object.entries(servers as Record<string, unknown>)[0]
    if (!entry) return null
    const [name, value] = entry
    if (value && typeof value === 'object') return { name, server: value as Record<string, unknown> }
    return null
  }

  if (record.command || record.url) return { name: '', server: record }
  return null
}

/** Environment key the GitHub MCP server reads its personal access token from. */
export const GITHUB_TOKEN_ENV_KEY = 'GITHUB_PERSONAL_ACCESS_TOKEN'

/**
 * GitHub's classic-token form with the scopes the GitHub MCP server needs
 * prefilled, so users can generate and copy a token in one pass.
 */
export function githubTokenCreateUrl(): string {
  const params = new URLSearchParams({
    description: 'Haoyue MCP GitHub Server',
    scopes: 'repo,read:org,read:repo_hook,read:user,user:email'
  })
  return `https://github.com/settings/tokens/new?${params.toString()}`
}

/**
 * One-click templates for well-known MCP servers. Presets fill in the
 * connection line and credential key names; credential values stay empty and
 * user-provided.
 */
export interface McpPresetContext {
  /** Active workspace directory, used by presets that serve a folder on disk. */
  workspacePath?: string
}

export interface McpPreset {
  id: string
  label: string
  description: string
  /** Returns a fresh form value on every call so entry points never share mutable state. */
  createForm: (context?: McpPresetContext) => McpFormValue
}

function presetForm(id: string, connection: string, env = '', description = ''): McpFormValue {
  return {
    ...createMcpFormValue(),
    name: inferMcpName(connection) || id,
    connection,
    env
  }
}

export const MCP_PRESETS: McpPreset[] = [
  {
    id: 'github',
    label: 'GitHub',
    description: '接入 GitHub MCP Server：浏览仓库、Issue、PR 与代码搜索；需自行填写个人访问令牌',
    createForm: () => presetForm('github', 'npx -y @modelcontextprotocol/server-github', `${GITHUB_TOKEN_ENV_KEY}=`)
  },
  {
    id: 'filesystem',
    label: '文件系统',
    description: '让 Agent 读写工作区目录中的文件；如需其他目录，保存前修改最后一个参数',
    createForm: (context) =>
      presetForm('filesystem', `npx -y @modelcontextprotocol/server-filesystem "${context?.workspacePath?.trim() || '.'}"`)
  },
  {
    id: 'fetch',
    label: '网页抓取',
    description: '抓取网页并转为 Markdown 供 Agent 阅读；需要本机安装 uv（Python 工具链）',
    createForm: () => presetForm('fetch', 'uvx mcp-server-fetch')
  },
  {
    id: 'memory',
    label: '记忆图谱',
    description: '基于知识图谱的长期记忆，跨会话记住实体与关系；无需任何配置',
    createForm: () => presetForm('memory', 'npx -y @modelcontextprotocol/server-memory')
  },
  {
    id: 'sequential-thinking',
    label: '顺序思考',
    description: '提供逐步推理与思路修订工具，适合把复杂问题拆解后再行动；无需任何配置',
    createForm: () => presetForm('sequential-thinking', 'npx -y @modelcontextprotocol/server-sequential-thinking')
  },
  {
    id: 'git',
    label: 'Git',
    description: '对 Git 仓库做只读分析（历史、分支、状态、差异）；需要本机安装 uv 与 Git',
    createForm: () => presetForm('git', 'uvx mcp-server-git')
  }
]

/** Builds editor state from a runtime server entry. Credential values are never sent to the UI. */
export function mcpFormFromServer(server: McpServerSummary): McpFormValue {
  const remote = isRemoteTransport(normalizeTransport(server.transport))
  // Quote args containing whitespace so the single line round-trips through
  // tokenizeCommand without splitting paths like "E:\My Files".
  const quote = (token: string) => (/\s/.test(token) ? `"${token.replace(/"/g, '\\"')}"` : token)
  const connection = remote
    ? server.url ?? ''
    : [server.command ?? '', ...(server.args ?? []).map(quote)].join(' ').trim()
  return {
    name: server.name,
    scope: server.scope,
    transport: normalizeTransport(server.transport),
    connection,
    env: '',
    headers: '',
    enabled: server.enabled
  }
}

/** Parses the `KEY=value` textarea into a plain environment map. */
export function parseEnvText(value: string): Record<string, string> | undefined {
  const entries = value.split(/\r?\n/).map((line) => line.trim()).filter(Boolean)
  if (entries.length === 0) return undefined
  return Object.fromEntries(entries.map((line) => {
    const index = line.indexOf('=')
    return index < 0 ? [line, ''] : [line.slice(0, index).trim(), line.slice(index + 1)]
  }))
}

export interface CredentialRow {
  key: string
  /** Empty means "unchanged" — the runtime keeps the stored value. */
  value: string
}

/** Reads credential rows out of a KEY=value textarea. Values are trimmed —
 *  credential UI edits each value in its own field, verbatim surrounding
 *  whitespace is never meaningful. */
export function parseCredentialRows(text: string): CredentialRow[] {
  return text.split(/\r?\n/).map((line) => {
    const trimmed = line.trim()
    if (!trimmed) return null
    const index = trimmed.indexOf('=')
    return index < 0
      ? { key: trimmed, value: '' }
      : { key: trimmed.slice(0, index).trim(), value: trimmed.slice(index + 1).trim() }
  }).filter((row): row is CredentialRow => row !== null && row.key.length > 0)
}

/** Serializes credential rows back into the KEY=value textarea format. */
export function credentialRowsToText(rows: CredentialRow[]): string {
  return rows.filter((row) => row.key.trim()).map((row) => `${row.key}=${row.value}`).join('\n')
}

/** True when the env rows declare the GitHub token key but still have no value. */
export function envNeedsGithubToken(value: string): boolean {
  const target = GITHUB_TOKEN_ENV_KEY.toUpperCase()
  return value.split(/\r?\n/).some((rawLine) => {
    const line = rawLine.trim()
    if (!line) return false
    const index = line.indexOf('=')
    const name = (index < 0 ? line : line.slice(0, index)).trim().toUpperCase()
    const filled = index >= 0 && line.slice(index + 1).trim() !== ''
    return name === target && !filled
  })
}

/** Returns a user-facing validation message, or null when the form can be saved. */
export function mcpFormError(form: McpFormValue): string | null {
  if (!form.name.trim()) return '请填写 MCP 服务器名称'
  const connection = form.connection.trim()
  if (isRemoteTransport(form.transport)) {
    if (!connection) return `${transportLabel(form.transport)} 连接需要填写 URL`
    if (!/^https?:\/\//i.test(connection)) return 'URL 需要以 http:// 或 https:// 开头'
    return null
  }
  if (!connection) return '请填写连接内容：包名（如 @modelcontextprotocol/server-github）、完整命令或 npx/uvx 启动命令'
  const command = tokenizeCommand(connection)[0]
  if (command && (KNOWN_INTERPRETERS.has(command.toLowerCase()) || command.includes('/') || command.includes('\\') || command.endsWith('.exe') || command.endsWith('.cmd'))) {
    return null
  }
  return '无法识别的命令；请输入 npx/uvx 包名、完整命令或 http(s):// URL'
}

/**
 * Payload for `mcp.upsert`. Always returns plain JSON values so it can cross the
 * Electron IPC boundary (Vue reactive proxies cannot be structured-cloned).
 */
export function buildMcpServerPayload(form: McpFormValue): Record<string, unknown> {
  const remote = isRemoteTransport(form.transport)
  const server: Record<string, unknown> = {
    transport: form.transport,
    command: '',
    args: [] as string[],
    url: '',
    enabled: form.enabled
  }
  if (remote) {
    server.url = form.connection.trim()
    // Only remote servers consume headers today.
    const headers = parseEnvText(form.headers)
    if (headers) server.headers = headers
  } else {
    const tokens = tokenizeCommand(form.connection)
    server.command = tokens[0] ?? ''
    server.args = tokens.slice(1)
    // Only stdio servers consume process environment variables today.
    const env = parseEnvText(form.env)
    if (env) server.env = env
  }
  return server
}

/**
 * Payload for toggling an existing server. `mcp.upsert` merges the fields it
 * receives, so every editable field is resent from the plain snapshot to keep
 * the stored configuration intact.
 */
export function buildMcpTogglePayload(server: McpServerSummary): Record<string, unknown> {
  const remote = isRemoteTransport(server.transport)
  return {
    transport: server.transport,
    command: remote ? '' : server.command ?? '',
    args: remote ? [] : [...(server.args ?? [])],
    url: remote ? server.url ?? '' : '',
    enabled: !server.enabled
  }
}

/** Status line for one server row. */
export function mcpStatusText(
  server: Pick<McpServerSummary, 'connected' | 'connecting' | 'enabled' | 'toolCount' | 'error'>
): string {
  if (server.connected) return `${server.toolCount} 个工具`
  if (server.connecting) return '连接中…'
  if (!server.enabled) return '已禁用'
  return server.error?.trim() || '未连接'
}
