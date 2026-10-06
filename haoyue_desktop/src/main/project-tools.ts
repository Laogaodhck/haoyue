import { execFile, spawn } from 'node:child_process'
import { existsSync, statSync, unlinkSync } from 'node:fs'
import { stat } from 'node:fs/promises'
import { resolve, sep } from 'node:path'
import { promisify } from 'node:util'
import type { GitCommit, GitHistory, GitOverview, RevertDiffItem, RevertDiffsResult } from '../shared/ipc.js'

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

