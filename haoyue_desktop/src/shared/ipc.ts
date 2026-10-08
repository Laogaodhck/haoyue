// 事件名、错误码与方法清单的唯一事实源是 contracts/daemon-contract.json
// （由 haoyue_runtime 的 DaemonContract 导出，scripts/generate-contract.mjs 生成下方引用）。
import type { DaemonErrorCode, DaemonEventName } from './daemon-contract.gen.js'
import { DAEMON_ERROR_CODES } from './daemon-contract.gen.js'
export type { DaemonEventName } from './daemon-contract.gen.js'
export type { DaemonMethod } from './daemon-contract.gen.js'
export {
  DAEMON_BROADCAST_EVENTS,
  DAEMON_ERROR_CODES,
  DAEMON_EVENT_NAMES,
  DAEMON_METHODS,
  DAEMON_STREAM_EVENTS,
  DAEMON_TERMINAL_EVENTS,
} from './daemon-contract.gen.js'

export interface DaemonMessage {
  id: number
  event: DaemonEventName
  data: string
  /** Structured metadata for tool and file-diff events. */
  details?: Record<string, unknown>
  /** Session that owns a streamed agent event. Present for chat turn events. */
  sessionId?: string
  requestMethod?: string
}

/**
 * daemon error 事件的契约化异常：跨语言行为分支以 code 为准（见契约 errorCodes），
 * message 仅是人类可读展示文本，禁止对其做字符串匹配。
 */
export class DaemonError extends Error {
  readonly code: DaemonErrorCode
  /** 发起请求的方法名，便于上层按方法分类处理。 */
  readonly method?: string

  constructor(code: DaemonErrorCode, message: string, method?: string) {
    super(message)
    this.name = 'DaemonError'
    this.code = code
    this.method = method
  }
}

/** 从 error 事件信封提取契约错误码；旧版 daemon 未携带 details.code 时回退为 unknown。 */
export function errorCodeOf(message: DaemonMessage): DaemonErrorCode {
  const code = message.details?.['code']
  return typeof code === 'string' && (DAEMON_ERROR_CODES as readonly string[]).includes(code)
    ? (code as DaemonErrorCode)
    : 'unknown'
}

export interface DaemonState {
  connected: boolean
  endpoint: string
  error?: string
}

/** Optional per-request controls passed from the renderer to the daemon client. */
export interface DaemonRequestOptions {
  /** Rejects the request when no event or terminal response arrives within this window. */
  timeoutMs?: number
}

export interface AppInfo {
  version: string
  platform: 'aix' | 'darwin' | 'freebsd' | 'linux' | 'openbsd' | 'sunos' | 'win32' | 'android'
  supportsMica: boolean
  defaultWorkspace: string
  /** Used only to migrate the legacy project that Desktop implicitly created here. */
  documentsPath: string
  /** Home profile used to reject non-project paths (user profile / Haoyue state dir). */
  userProfilePath: string
}

export type AppearanceTheme = 'system' | 'light' | 'dark'

export interface GitOverview {
  isRepository: boolean
  root: string
  branch: string
  status: string[]
  diff: string
  error?: string
}

export interface GitCommit {
  hash: string
  shortHash: string
  author: string
  authoredAt: string
  subject: string
}

export interface GitHistory {
  commits: GitCommit[]
  error?: string
}

export interface DesktopSkillFileSelection {
  paths: string[]
  warning?: string
}

export interface RevertDiffItem {
  filePath: string
  diff: string
}

export interface RevertDiffsResult {
  reverted: string[]
  failed: Array<{ filePath: string; reason: string }>
}

export interface DesktopApi {
  getAppInfo(): Promise<AppInfo>
  selectWorkspace(): Promise<string | null>
  selectFiles(): Promise<string[]>
  readFileBase64(path: string): Promise<{ data: string; mediaType: string; sizeBytes: number } | null>
  getPathForFile(file: File): string
  selectSkillFiles(): Promise<DesktopSkillFileSelection>
  selectModelDirectory(): Promise<string | null>
  selectGgufFile(): Promise<string | null>
  showItemInFolder(path: string): Promise<void>
  saveTextFile(defaultName: string, content: string): Promise<string | null>
  closeApp(): Promise<void>
  openDevTools(): Promise<void>
  setTheme(theme: AppearanceTheme): Promise<void>
  notify(title: string, body: string): Promise<void>
  project: {
    openTerminal(path: string): Promise<void>
    gitOverview(path: string): Promise<GitOverview>
    gitHistory(path: string): Promise<GitHistory>
    revertFileDiffs(workspace: string, patches: RevertDiffItem[]): Promise<RevertDiffsResult>
  }
  daemon: {
    connect(): Promise<DaemonState>
    request(
      method: string,
      params?: Record<string, unknown>,
      options?: DaemonRequestOptions
    ): Promise<DaemonMessage>
    onEvent(listener: (message: DaemonMessage) => void): () => void
    onState(listener: (state: DaemonState) => void): () => void
  }
}

/**
 * Normalizes a renderer payload into cloneable JSON before it crosses the
 * Electron IPC boundary. Vue `reactive()` values are Proxy objects, and the
 * structured clone algorithm rejects them with
 * "An object could not be cloned." — daemon requests are JSON on the wire, so
 * round-tripping through JSON both fixes that and drops `undefined` fields.
 */
export function toIpcPayload<T>(value: T): T {
  if (value === null || typeof value !== 'object') return value
  return JSON.parse(JSON.stringify(value)) as T
}
