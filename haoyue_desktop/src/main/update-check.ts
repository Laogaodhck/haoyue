import type { UpdateCheckResult } from '../shared/ipc.js'

/** 桌面端新版本检查：只读 GitHub Releases 最新正式版，不下载、不执行任何远端代码。 */
export const UPDATE_REPO = 'Laogaodhck/haoyue'
export const RELEASES_PAGE = `https://github.com/${UPDATE_REPO}/releases`

export type { UpdateCheckResult } from '../shared/ipc.js'

export interface CheckUpdateOptions {
  currentVersion: string
  fetchImpl?: typeof fetch
  timeoutMs?: number
}

export function normalizeVersion(raw: string): string {
  return raw.trim().replace(/^v/i, '')
}

interface ParsedVersion {
  core: number[]
  prerelease: string
}

function parseVersion(raw: string): ParsedVersion {
  const [core = '', ...rest] = normalizeVersion(raw).split('-')
  return {
    core: core.split('.').map((part) => Number.parseInt(part, 10) || 0),
    prerelease: rest.join('-')
  }
}

/** 语义化版本比较：返回 >0 表示 left 更新，<0 表示更旧，0 表示相同（预发布版低于正式版）。 */
export function compareVersions(left: string, right: string): number {
  const a = parseVersion(left)
  const b = parseVersion(right)
  for (let index = 0; index < Math.max(a.core.length, b.core.length); index++) {
    const diff = (a.core[index] ?? 0) - (b.core[index] ?? 0)
    if (diff !== 0) return diff
  }
  if (a.prerelease === b.prerelease) return 0
  if (!a.prerelease) return 1
  if (!b.prerelease) return -1
  return a.prerelease < b.prerelease ? -1 : 1
}

export function isNewerVersion(latest: string, current: string): boolean {
  return compareVersions(latest, current) > 0
}

function releaseUrlOf(value: unknown): string {
  return typeof value === 'string' && value.startsWith('https://github.com/') ? value : RELEASES_PAGE
}

export async function checkForUpdate(options: CheckUpdateOptions): Promise<UpdateCheckResult> {
  const doFetch = options.fetchImpl ?? fetch
  try {
    const response = await doFetch(`https://api.github.com/repos/${UPDATE_REPO}/releases/latest`, {
      headers: {
        Accept: 'application/vnd.github+json',
        'User-Agent': 'Haoyue-Desktop'
      },
      signal: AbortSignal.timeout(options.timeoutMs ?? 8000)
    })
    if (!response.ok) return { status: 'unavailable', error: `HTTP ${response.status}` }

    const release = (await response.json()) as { tag_name?: unknown; html_url?: unknown }
    const version = normalizeVersion(typeof release.tag_name === 'string' ? release.tag_name : '')
    if (!/^\d+\.\d+\.\d+/.test(version)) return { status: 'unavailable', error: '版本号无法解析' }

    const url = releaseUrlOf(release.html_url)
    return isNewerVersion(version, options.currentVersion)
      ? { status: 'update', version, url }
      : { status: 'latest', version, url }
  } catch (error) {
    // 离线/超时/限流一律静默降级：升级提示不能变成启动噪音。
    return { status: 'unavailable', error: error instanceof Error ? error.message : String(error) }
  }
}
