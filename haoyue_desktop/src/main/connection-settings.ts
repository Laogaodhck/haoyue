import { chmodSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname } from 'node:path'

/**
 * 桌面端连接目标：本机 Runtime（命名管道 / unix socket）或局域网 Runtime（TCP + 挑战应答）。
 * 独立于 daemon 的 ~/.haoyue/config.json —— 这是「本机桌面连哪台 daemon」的偏好，
 * 换一台电脑登录同一局域网时互不影响。
 */
export type ConnectionMode = 'local' | 'lan'

export interface ConnectionSettings {
  mode: ConnectionMode
  host: string
  port: number
  /**
   * 局域网 daemon 的握手令牌。落盘权限与 ~/.haoyue/daemon.token 同级
   * （POSIX 0600；Windows 位于当前用户的 userData 目录）。
   */
  token: string | null
}

export const DEFAULT_LAN_PORT = 47_615

export const DEFAULT_CONNECTION_SETTINGS: ConnectionSettings = {
  mode: 'local',
  host: '',
  port: DEFAULT_LAN_PORT,
  token: null
}

/** 校验用户提交的连接参数；返回可直接展示的中文错误，合法时返回 null。 */
export function connectionSettingsProblem(value: ConnectionSettings): string | null {
  if (value.mode !== 'local' && value.mode !== 'lan') return '连接方式无效'
  if (value.mode === 'local') return null
  if (!value.host) return '请填写局域网 Runtime 的地址'
  if (value.host.length > 253 || /[\s/\\]/.test(value.host)) return '地址格式无效'
  if (!Number.isInteger(value.port) || value.port < 1024 || value.port > 65535)
    return '端口需在 1024–65535 之间'
  return null
}

/** 读盘结果一律归一化：损坏 / 手改的文件退回默认值，不让桌面启动失败。 */
export function normalizeConnectionSettings(raw: unknown): ConnectionSettings {
  if (typeof raw !== 'object' || raw === null) return { ...DEFAULT_CONNECTION_SETTINGS }
  const value = raw as Record<string, unknown>
  const port = typeof value.port === 'number' && Number.isInteger(value.port)
    && value.port >= 1024 && value.port <= 65535
    ? value.port
    : DEFAULT_LAN_PORT
  return {
    mode: value.mode === 'lan' ? 'lan' : 'local',
    host: typeof value.host === 'string' ? value.host.trim() : '',
    port,
    token: typeof value.token === 'string' && value.token.trim() ? value.token.trim() : null
  }
}

export function readConnectionSettings(file: string): ConnectionSettings {
  try {
    return normalizeConnectionSettings(JSON.parse(readFileSync(file, 'utf8')))
  } catch {
    return { ...DEFAULT_CONNECTION_SETTINGS }
  }
}

export function writeConnectionSettings(file: string, settings: ConnectionSettings): void {
  mkdirSync(dirname(file), { recursive: true })
  writeFileSync(file, `${JSON.stringify(settings, null, 2)}\n`, { encoding: 'utf8', mode: 0o600 })
  if (process.platform !== 'win32') {
    try {
      chmodSync(file, 0o600) // 已存在的文件不受 open 模式约束，显式收紧
    } catch { /* 只读文件系统 / 无权限：保持现状 */ }
  }
}
