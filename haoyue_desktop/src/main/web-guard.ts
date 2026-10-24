import type { BrowserWindow } from 'electron'
import type { WebNavigationCheck } from '../shared/ipc.js'

/**
 * 可视化浏览器（右侧工作区「浏览」页签）的访问边界。
 *
 * 白名单语义与 haoyue_runtime/Tools/WebSiteAllowlist.cs 完全一致（同一份
 * config.web.allowedSites）：空列表 = 不限制；localhost / 127.x / ::1 始终放行；
 * 规则形如 scheme://host[:port]，host 支持前导 "*." 通配（根域需单独添加）；
 * 仅 http/https 参与匹配的协议。
 *
 * 本模块不含任何 electron 运行时依赖（只 import type），因此可以被 vitest 直接覆盖。
 */

/** 白名单查询所需的最小依赖面；main 进程的 DaemonClient 结构化满足此接口。 */
export interface AllowlistSource {
  request(
    method: string,
    params?: Record<string, unknown>,
    options?: { timeoutMs?: number }
  ): Promise<{ data: string }>
}

/** 解析后的白名单条目：scheme + host（可含 "*." 前缀）+ 可选端口。 */
export interface WebRule {
  scheme: string
  host: string
  port: string | null
}

/** 输入已带显式 scheme（http://… / https://…）。 */
const HAS_SCHEME = /^[a-z][a-z0-9+.-]*:\/\//i
/**
 * host:port 形式（localhost:5173 / 127.0.0.1:8080 / example.com:8443）。
 * 这类输入不能直接交给 URL 解析——URL 会把 "localhost" 当成 scheme 解析成
 * 绝对 URL "localhost:5173"，于是把最常见的本地地址挡在门外。
 */
const HOST_PORT = /^[a-z0-9.-]+:\d+(?:[/?#]|$)/i

/** 补齐 scheme：host:port 视为本地 http 服务，裸主机名默认 https。 */
export function withScheme(input: string): string {
  const trimmed = input.trim()
  if (HAS_SCHEME.test(trimmed)) return trimmed
  if (HOST_PORT.test(trimmed)) return `http://${trimmed}`
  return `https://${trimmed}`
}

/** 本地主机判定：localhost / ::1 / 127.x.x.x（IPv6 字面量带方括号，需剥离后再比）。 */
export function isLocalHost(host: string): boolean {
  const normalized = host.replace(/^\[|\]$/g, '').toLowerCase()
  return normalized === 'localhost' || normalized === '::1' || normalized.startsWith('127.')
}

/** 解析单条规则；形态不合法一律返回 null（与 C# 侧严格解析保持一致）。 */
export function parseRule(entry: string): WebRule | null {
  const trimmed = entry.trim()
  // scheme 必填：裸域名会被拒绝，避免 "http://localhost:3000" 的意图被静默降级成任意 scheme。
  const schemeEnd = trimmed.indexOf('://')
  if (schemeEnd <= 0) return null
  const scheme = trimmed.slice(0, schemeEnd).toLowerCase()
  if (scheme !== 'http' && scheme !== 'https') return null

  let hostPart = trimmed.slice(schemeEnd + 3)
  const slash = hostPart.indexOf('/')
  if (slash >= 0) hostPart = hostPart.slice(0, slash) // 容忍误粘的路径
  if (!hostPart) return null

  let port: string | null = null
  if (hostPart.startsWith('[')) {
    // [::1]:8080 —— IPv6 字面量按原样精确匹配，端口在 ']' 之后可选。
    const close = hostPart.indexOf(']')
    if (close > -1 && hostPart[close + 1] === ':') {
      port = hostPart.slice(close + 2)
      hostPart = hostPart.slice(0, close + 1)
    }
  } else if ((hostPart.match(/:/g) ?? []).length === 1) {
    const colon = hostPart.indexOf(':')
    port = hostPart.slice(colon + 1)
    hostPart = hostPart.slice(0, colon)
    if (!port || !/^\d+$/.test(port) || Number(port) > 65535) return null
  }
  // 多个 ':' 且无方括号 = 裸 IPv6 字面量：无端口，精确匹配。

  const host = hostPart.toLowerCase()
  if (!host || (host.includes('*') && !host.startsWith('*.')) || host === '*') return null
  return { scheme, host, port }
}

/** 单条 URL 的白名单裁决；rules 为空表示白名单未启用（全部放行）。 */
export function evaluateUrl(rawUrl: string, rules: WebRule[]): { allowed: boolean; reason?: string } {
  if (rules.length === 0) return { allowed: true }

  let uri: URL
  try {
    uri = new URL(rawUrl)
  } catch {
    return { allowed: false, reason: `URL 无法解析：${rawUrl}` }
  }

  const scheme = uri.protocol === 'https:' ? 'https' : uri.protocol === 'http:' ? 'http' : ''
  if (!scheme)
    return { allowed: false, reason: `仅允许 http/https 访问（收到 ${uri.protocol.replace(':', '')}）。` }

  if (isLocalHost(uri.hostname)) return { allowed: true }

  const host = uri.hostname.toLowerCase()
  const port = uri.port || null
  for (const rule of rules) {
    if (rule.scheme !== scheme) continue
    if (rule.port !== null && rule.port !== port) continue
    if (rule.host.startsWith('*.')) {
      // ".example.com"：只放行子域，根域需单独添加（与桌面端设置卡片提示一致）。
      if (host.endsWith(rule.host.slice(1))) return { allowed: true }
    } else if (rule.host === host) {
      return { allowed: true }
    }
  }

  return {
    allowed: false,
    reason: `外部网站访问受白名单限制：${uri.origin} 不在允许列表中。` +
      '可在「设置 → 允许的网站」添加该站点后重试。'
  }
}

/** 从 daemon 现读白名单（web.allowedGet），每次导航前调用以保证增删立即生效。 */
export async function fetchRules(source: AllowlistSource): Promise<WebRule[]> {
  const response = await source.request('web.allowedGet', {})
  const payload = JSON.parse(response.data) as { allowedSites?: string[] }
  return (payload.allowedSites ?? [])
    .map(parseRule)
    .filter((rule): rule is WebRule => rule !== null)
}

/**
 * 地址栏提交前的校验：补 scheme → 校验协议 → 对照白名单，回填规范化地址。
 * 白名单读取失败（daemon 尚未就绪）时按「未配置」放行：这与 web.allowedSites
 * 空列表的既有语义一致，也避免启动瞬间把本地预览整体锁死。
 */
export async function checkBrowserUrl(
  source: AllowlistSource,
  raw: string
): Promise<WebNavigationCheck> {
  const trimmed = raw.trim()
  if (!trimmed) return { ok: false, reason: '请输入网址' }

  let uri: URL
  try {
    uri = new URL(withScheme(trimmed))
  } catch {
    return { ok: false, reason: `无法识别的网址：${raw}` }
  }
  if (uri.protocol !== 'http:' && uri.protocol !== 'https:')
    return { ok: false, reason: `仅支持 http/https 网址（收到 ${uri.protocol.replace(':', '')}）` }

  let rules: WebRule[] = []
  try {
    rules = await fetchRules(source)
  } catch {
    rules = []
  }
  cachedRules = rules

  const verdict = evaluateUrl(uri.href, rules)
  return verdict.allowed ? { ok: true, url: uri.href } : { ok: false, reason: verdict.reason }
}

/** 最近一次读到的白名单快照，供 will-navigate / window.open 同步裁决。 */
let cachedRules: WebRule[] = []

/**
 * 把白名单约束挂到 <webview> 上。
 *
 * 渲染层在 loadURL 之前会自己调 `web:check-url`，但页面内的链接点击
 * （will-navigate）与 target=_blank（window.open）不经过渲染层，必须在主进程拦，
 * 否则白名单只挡得住地址栏。快照由每次 checkBrowserUrl 刷新。
 */
export function attachWebviewGuard(win: BrowserWindow, source: AllowlistSource): void {
  void fetchRules(source)
    .then((rules) => { cachedRules = rules })
    .catch(() => undefined)

  win.webContents.on('will-attach-webview', (event, webPreferences, params) => {
    // 渲染层提供的 webPreferences 一律不可信：preload 只能由宿主指定，node 能力全关。
    delete webPreferences.preload
    webPreferences.nodeIntegration = false
    webPreferences.contextIsolation = true
    webPreferences.sandbox = true
    webPreferences.webSecurity = true
    const src = params.src ?? ''
    // 组件挂载时尚未指定 src，空串不做校验（后续导航在下面两条路径上把关）。
    if (src && !evaluateUrl(src, cachedRules).allowed) event.preventDefault()
  })

  win.webContents.on('did-attach-webview', (_event, contents) => {
    // allowpopups 只是为了不把 target=_blank 静默丢弃；这里一律 deny，
    // 放行的目标改为在同一视图内加载（仍受白名单与后续 will-navigate 约束）。
    contents.setWindowOpenHandler(({ url }) => {
      if (evaluateUrl(url, cachedRules).allowed) void contents.loadURL(url)
      return { action: 'deny' }
    })
    contents.on('will-navigate', (event, url) => {
      const verdict = evaluateUrl(url, cachedRules)
      if (verdict.allowed) return
      event.preventDefault()
      win.webContents.send('web:blocked', { url, reason: verdict.reason })
    })
  })
}
