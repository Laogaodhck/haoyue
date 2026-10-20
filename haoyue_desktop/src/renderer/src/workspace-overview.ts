/// 「工作区速览」卡片的纯逻辑：workspace.files 只回相对路径（已滤凭证与构建产物），
/// 这里把路径列表归纳成「顶层目录（含样例内文件数）+ 顶层散落文件」两级结构，
/// 让用户在欢迎页一眼看到项目目录结构与项目加载结果，而不必打开文件管理器。

export interface WorkspaceDirEntry {
  /** 目录名或顶层文件名。 */
  name: string
  kind: 'dir' | 'file'
  /** 目录：样例里统计到的文件数（并非目录内文件总数，仅作规模感提示）。 */
  fileCount?: number
}

export function summarizeWorkspaceFiles(
  paths: readonly string[],
  maxEntries = 8
): WorkspaceDirEntry[] {
  const dirs = new Map<string, number>()
  const files: string[] = []
  for (const raw of paths) {
    if (typeof raw !== 'string' || !raw) continue
    const normalized = raw.replace(/\\/g, '/')
    const slash = normalized.indexOf('/')
    if (slash <= 0) {
      files.push(normalized)
      continue
    }
    const dir = normalized.slice(0, slash)
    dirs.set(dir, (dirs.get(dir) ?? 0) + 1)
  }
  const dirEntries = [...dirs.entries()]
    .sort((left, right) => right[1] - left[1] || left[0].localeCompare(right[0]))
    .map(([name, fileCount]): WorkspaceDirEntry => ({ name, kind: 'dir', fileCount }))
  const fileEntries = [...new Set(files)]
    .sort((left, right) => left.localeCompare(right))
    .map((name): WorkspaceDirEntry => ({ name, kind: 'file' }))
  return [...dirEntries, ...fileEntries].slice(0, maxEntries)
}
