import { execFile, spawn } from 'node:child_process'
import { existsSync, readdirSync, readFileSync, statSync, unlinkSync } from 'node:fs'
import type { Dirent } from 'node:fs'
import { stat } from 'node:fs/promises'
import { extname, join, resolve, sep } from 'node:path'
import { promisify } from 'node:util'
import type {
  GitBranchCreateResult,
  GitCommit,
  GitHistory,
  GitOverview,
  GitPushResult,
  GitWorktreeEntry,
  GitWorktreesResult,
  RevertDiffItem,
  RevertDiffsResult,
  SecurityFinding,
  SecurityScanResult
} from '../shared/ipc.js'

const execFileAsync = promisify(execFile)

async function workspaceDirectory(path: string): Promise<string> {
  const directory = resolve(path)
  const info = await stat(directory)
  if (!info.isDirectory()) throw new Error(`Workspace is not a directory: ${directory}`)
  return directory
}

async function git(directory: string, args: string[]): Promise<string> {
  // core.quotepath=off：Windows 默认把非 ASCII 文件名转成八进制转义，中文路径会乱码。
  const result = await execFileAsync('git', ['-c', 'core.quotepath=off', ...args], {
    cwd: directory,
    encoding: 'utf8',
    maxBuffer: 8 * 1024 * 1024,
    windowsHide: true
  })
  return result.stdout.trimEnd()
}

// 补丁来自 agent 生成的内容（file_diff 事件），必须假定可能被注入越界路径。
// 统一在此校验：目标路径解析后必须落在工作区内，../ 与任意绝对路径逃逸一律拒绝
//（file_diff 事件本身携带工作区内文件的绝对路径，属于合法输入）。
function resolvePatchTarget(directory: string, filePath: string): string {
  const target = resolve(directory, filePath)
  const workspaceWithSep = directory.endsWith(sep) ? directory : directory + sep
  const samePath = (left: string, right: string): boolean =>
    process.platform === 'win32'
      ? left.toLowerCase() === right.toLowerCase()
      : left === right
  if (samePath(target, directory) || target.toLowerCase().startsWith(workspaceWithSep.toLowerCase())) {
    return target
  }
  throw new Error(`补丁路径越出工作区，已拒绝：${filePath}`)
}

function detail(error: unknown): string {
  if (!(error instanceof Error)) return String(error)
  const candidate = error as Error & { stderr?: string }
  return candidate.stderr?.trim() || error.message
}

function friendlyGitError(error: unknown): string {
  const message = detail(error)
  return /not a git repository/i.test(message) ? '当前目录没有 Git 仓库信息' : message
}

export function parseGitLog(output: string): GitCommit[] {
  return output
    .split('\x1e')
    .map((entry) => entry.trim())
    .filter(Boolean)
    .map((entry) => {
      const [hash = '', shortHash = '', author = '', authoredAt = '', ...subject] = entry.split('\x1f')
      return { hash, shortHash, author, authoredAt, subject: subject.join('\x1f') }
    })
    .filter((commit) => commit.hash.length > 0)
}

export async function getGitOverview(path: string): Promise<GitOverview> {
  const directory = await workspaceDirectory(path)
  try {
    const [root, branch, status, unstaged, staged] = await Promise.all([
      git(directory, ['rev-parse', '--show-toplevel']),
      git(directory, ['branch', '--show-current']),
      git(directory, ['status', '--short']),
      git(directory, ['diff', '--no-ext-diff', '--no-color', '--unified=3']),
      git(directory, ['diff', '--cached', '--no-ext-diff', '--no-color', '--unified=3'])
    ])
    return {
      isRepository: true,
      root,
      branch: branch || 'detached HEAD',
      status: status ? status.split(/\r?\n/) : [],
      diff: [staged && '# Staged changes\n' + staged, unstaged && '# Working tree changes\n' + unstaged]
        .filter(Boolean).join('\n\n')
    }
  } catch (error) {
    return { isRepository: false, root: directory, branch: '', status: [], diff: '', error: friendlyGitError(error) }
  }
}

export async function getGitHistory(path: string): Promise<GitHistory> {
  const directory = await workspaceDirectory(path)
  try {
    const output = await git(directory, [
      'log', '-n', '100', '--date=iso-strict', '--pretty=format:%H%x1f%h%x1f%an%x1f%aI%x1f%s%x1e'
    ])
    return { commits: parseGitLog(output) }
  } catch (error) {
    return { commits: [], error: friendlyGitError(error) }
  }
}

/**
 * 新建分支（git checkout -b）。分支名由调用方按「编码 → Git → 分支前缀」预填
 * 后传入；此处只做基本合法性校验，具体前缀逻辑在渲染层（留空则不添加前缀）。
 */
export async function createGitBranch(path: string, name: string): Promise<GitBranchCreateResult> {
  const directory = await workspaceDirectory(path)
  const branch = name.trim()
  if (!branch) {
    return { ok: false, branch: '', error: '分支名不能为空' }
  }
  if (branch.includes('..') || /[\s~^:?*[\]\\]/.test(branch) || branch.startsWith('-')) {
    return { ok: false, branch: '', error: '分支名包含非法字符' }
  }
  try {
    await git(directory, ['checkout', '-b', branch])
    return { ok: true, branch }
  } catch (error) {
    return { ok: false, branch, error: friendlyGitError(error) }
  }
}

/**
 * 推送当前分支到 origin。forceWithLease=true 时追加 --force-with-lease：
 * 会改写远端分支历史，但远端已变化时拒绝覆盖（安全推送开关的落地行为）。
 */
export async function pushGitBranch(path: string, forceWithLease: boolean): Promise<GitPushResult> {
  const directory = await workspaceDirectory(path)
  try {
    const current = (await git(directory, ['branch', '--show-current'])).trim()
    if (!current) {
      return { ok: false, error: '当前处于 detached HEAD，无法推送' }
    }
    const args = ['push', '-u', 'origin', current]
    if (forceWithLease) args.push('--force-with-lease')
    await git(directory, args)
    return { ok: true }
  } catch (error) {
    return { ok: false, error: friendlyGitError(error) }
  }
}

/** 解析 `git worktree list --porcelain`：按空行分块，逐键取值。 */
export function parseGitWorktrees(output: string, repositoryRoot: string): GitWorktreeEntry[] {
  const entries: GitWorktreeEntry[] = []
  for (const block of output.split(/\n{2,}/)) {
    const lines = block.split(/\r?\n/)
    const worktreeLine = lines.find((line) => line.startsWith('worktree '))
    if (!worktreeLine) continue
    let path = worktreeLine.slice('worktree '.length)
    if (!path.startsWith('/') && !/^[A-Za-z]:[\\/]/.test(path)) {
      // porcelain 模式相对路径以仓库根为基准
      path = resolve(repositoryRoot, path)
    }
    const branchLine = lines.find((line) => line.startsWith('branch '))
    const headLine = lines.find((line) => line.startsWith('HEAD '))
    entries.push({
      path,
      branch: branchLine ? branchLine.slice('branch refs/heads/'.length) : '',
      head: headLine ? headLine.slice('HEAD '.length).slice(0, 7) : undefined,
      bare: lines.includes('bare')
    })
  }
  return entries
}

/** 列出仓库全部工作树（含主工作树），供「编码 → Worktrees 工作区索引」展示与刷新。 */
export async function getGitWorktrees(path: string): Promise<GitWorktreesResult> {
  const directory = await workspaceDirectory(path)
  try {
    const output = await git(directory, ['worktree', 'list', '--porcelain'])
    return { worktrees: parseGitWorktrees(output, directory) }
  } catch (error) {
    return { worktrees: [], error: friendlyGitError(error) }
  }
}

const SCAN_SKIP_DIRS = new Set(['.git', 'node_modules', 'dist', 'build', 'out', '.venv', 'venv', '.idea', '.vscode'])
const SCAN_SKIP_EXT = new Set(['.png', '.jpg', '.jpeg', '.gif', '.ico', '.pdf', '.zip', '.exe', '.dll', '.bin', '.woff', '.woff2', '.ttf', '.icns'])
const SCAN_MAX_FILES = 2000
const SCAN_MAX_FILE_BYTES = 512 * 1024

/** 密钥/口令/私钥等敏感信息模式（三层渐进式扫描的第一层：密钥与敏感信息快扫）。 */
const SECRET_PATTERNS: Array<{ kind: string; severity: 'high' | 'medium'; re: RegExp }> = [
  { kind: 'AWS 访问密钥', severity: 'high', re: /\bAKIA[0-9A-Z]{16}\b/g },
  { kind: '私钥内容', severity: 'high', re: /-----BEGIN (?:RSA|EC|OPENSSH|DSA|PGP) PRIVATE KEY-----/g },
  { kind: '口令赋值', severity: 'medium', re: /\b(?:password|passwd|pwd)\s*[=:]\s*[^\s"'`]+/gi },
  { kind: 'API 密钥/令牌', severity: 'medium', re: /\b(?:api[_-]?key|apikey|secret|token|access[_-]?token)\s*[=:]\s*['"][0-9A-Za-z_-]{16,}['"]/gi },
]

function collectScanFiles(directory: string, files: string[]): void {
  if (files.length >= SCAN_MAX_FILES) return
  let entries: Dirent[]
  try {
    entries = readdirSync(directory, { withFileTypes: true })
  } catch {
    return
  }
  for (const entry of entries) {
    if (files.length >= SCAN_MAX_FILES) return
    if (entry.name.startsWith('.') && entry.name !== '.env') continue
    const full = join(directory, entry.name)
    if (entry.isDirectory()) {
      if (SCAN_SKIP_DIRS.has(entry.name)) continue
      collectScanFiles(full, files)
      continue
    }
    if (SCAN_SKIP_EXT.has(extname(entry.name).toLowerCase())) continue
    try {
      const stats = statSync(full)
      if (stats.size === 0 || stats.size > SCAN_MAX_FILE_BYTES) continue
    } catch {
      continue
    }
    files.push(full)
  }
}

/**
 * 安全快速扫描（「编码 → 安全」三层渐进式扫描的第一层）：在工作区文本文件中
 * 匹配常见敏感信息模式（AWS 密钥 / 私钥 / 口令 / API 密钥令牌），
 * 返回逐条发现（文件 + 行号 + 类型 + 严重级），供推送前推荐与确认。
 */
export async function scanForSecrets(path: string): Promise<SecurityScanResult> {
  const directory = await workspaceDirectory(path)
  const files: string[] = []
  collectScanFiles(directory, files)
  const findings: SecurityFinding[] = []
  for (const file of files) {
    let text: string
    try {
      text = readFileSync(file, 'utf8')
    } catch {
      continue // 二进制读取失败按文本兼容处理
    }
    for (const { kind, severity, re } of SECRET_PATTERNS) {
      re.lastIndex = 0
      let match: RegExpExecArray | null
      while ((match = re.exec(text)) !== null) {
        const line = text.slice(0, match.index).split(/\r?\n/).length
        findings.push({ file, line, kind, severity })
        if (findings.length >= 500) break
      }
      if (findings.length >= 500) break
    }
    if (findings.length >= 500) break
  }
  return { findings, scannedFiles: files.length }
}

async function spawnDetached(command: string, args: string[], cwd: string): Promise<void> {
  await new Promise<void>((resolveSpawn, reject) => {
    const child = spawn(command, args, { cwd, detached: true, stdio: 'ignore', windowsHide: false })
    child.once('spawn', () => {
      child.unref()
      resolveSpawn()
    })
    child.once('error', reject)
  })
}

export async function openProjectTerminal(path: string): Promise<void> {
  const directory = await workspaceDirectory(path)
  if (process.platform === 'win32') {
    try {
      await spawnDetached('wt.exe', ['-d', directory], directory)
    } catch {
      const escaped = directory.replaceAll("'", "''")
      await spawnDetached('powershell.exe', ['-NoExit', '-Command', `Set-Location -LiteralPath '${escaped}'`], directory)
    }
    return
  }
  if (process.platform === 'darwin') {
    await spawnDetached('open', ['-a', 'Terminal', directory], directory)
    return
  }

  const terminals: Array<[string, string[]]> = [
    ['x-terminal-emulator', ['--working-directory', directory]],
    ['gnome-terminal', ['--working-directory', directory]],
    ['konsole', ['--workdir', directory]]
  ]
  let lastError: unknown
  for (const [command, args] of terminals) {
    try {
      await spawnDetached(command, args, directory)
      return
    } catch (error) {
      lastError = error
    }
  }
  throw lastError instanceof Error ? lastError : new Error(`No supported terminal found on ${process.platform}`)
}

export function normalizeDiffForGit(diff: string): string {
  let normalized = diff.replace(/\r\n/g, '\n')
  normalized = normalized.replace(/^([+-]{3} [ab])[/\\](.*)$/gm, (_match, prefix, filePath) => {
    return prefix + '/' + filePath.replace(/\\/g, '/')
  })
  if (!normalized.endsWith('\n')) {
    normalized += '\n'
  }
  return normalized
}

function cleanupNewFileIfEmpty(directory: string, patch: RevertDiffItem): void {
  if (/@@ -[01],0 \+/.test(patch.diff)) {
    // 与补丁应用一致：越出工作区的路径不允许触碰（包括按绝对路径 unlink）。
    const target = resolvePatchTarget(directory, patch.filePath)
    if (existsSync(target)) {
      const stats = statSync(target)
      if (stats.size === 0) {
        unlinkSync(target)
      }
    }
  }
}

async function applyPatchReverse(directory: string, patch: RevertDiffItem): Promise<void> {
  const normalizedDiff = normalizeDiffForGit(patch.diff)
  // 预先校验补丁声明的目标路径；真正的越界防护仍由 git 自身边界检查兜底
  //（--unsafe-paths 会关闭该防护，因此必须移除）。
  resolvePatchTarget(directory, patch.filePath)
  await new Promise<void>((resolvePromise, rejectPromise) => {
    const child = spawn('git', ['apply', '--reverse', '--whitespace=nowarn'], {
      cwd: directory,
      windowsHide: true,
      stdio: ['pipe', 'pipe', 'pipe']
    })

    let stderr = ''
    child.stderr.setEncoding('utf8')
    child.stderr.on('data', (chunk) => {
      stderr += chunk
    })

    child.once('error', (err) => {
      rejectPromise(err)
    })

    child.once('close', (code) => {
      if (code === 0) {
        try {
          cleanupNewFileIfEmpty(directory, patch)
        } catch {
          // ignore cleanup error
        }
        resolvePromise()
      } else {
        const message = stderr.trim() || `git apply failed with exit code ${code}`
        rejectPromise(new Error(message))
      }
    })

    child.stdin.end(normalizedDiff, 'utf8')
  })
}

export async function revertFileDiffs(
  workspacePath: string,
  patches: RevertDiffItem[]
): Promise<RevertDiffsResult> {
  let directory: string
  try {
    directory = await workspaceDirectory(workspacePath)
  } catch (error) {
    return {
      reverted: [],
      failed: [{ filePath: workspacePath, reason: detail(error) }]
    }
  }

  // Reverse chronological order: newest edits reverted first
  const reversed = [...patches].reverse()
  const failedMap = new Map<string, string>()
  const revertedSet = new Set<string>()

  for (const patch of reversed) {
    if (!patch.diff || !patch.diff.trim()) continue
    try {
      await applyPatchReverse(directory, patch)
      revertedSet.add(patch.filePath)
    } catch (error) {
      const raw = detail(error)
      const reason = /ENOENT/i.test(raw) ? '系统未安装或未配置 Git' : raw
      failedMap.set(patch.filePath, reason)
    }
  }

  const reverted = Array.from(revertedSet).filter((filePath) => !failedMap.has(filePath))
  const failed = Array.from(failedMap.entries()).map(([filePath, reason]) => ({ filePath, reason }))

  return { reverted, failed }
}

