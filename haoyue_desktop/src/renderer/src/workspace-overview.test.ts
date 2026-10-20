import { describe, expect, it } from 'vitest'
import { dayGreeting } from './greeting'
import { summarizeWorkspaceFiles } from './workspace-overview'

describe('dayGreeting', () => {
  it('按小时返回对应问候语', () => {
    expect(dayGreeting(3)).toBe('夜深了')
    expect(dayGreeting(6)).toBe('早上好')
    expect(dayGreeting(12)).toBe('中午好')
    expect(dayGreeting(15)).toBe('下午好')
    expect(dayGreeting(20)).toBe('晚上好')
    expect(dayGreeting(23)).toBe('夜深了')
  })

  it('非法输入回退为通用问候', () => {
    expect(dayGreeting(Number.NaN)).toBe('你好')
  })
})

describe('summarizeWorkspaceFiles', () => {
  it('按首段归纳目录并统计文件数，散落文件单独列出', () => {
    const summary = summarizeWorkspaceFiles([
      'haoyue_runtime/Agents/A.cs',
      'haoyue_runtime/Tools/T.cs',
      'haoyue_runtime/Daemon/D.cs',
      'README.md',
      'README.md' // 重复路径去重
    ])
    expect(summary[0]).toEqual({ name: 'haoyue_runtime', kind: 'dir', fileCount: 3 })
    expect(summary[1]).toEqual({ name: 'README.md', kind: 'file' })
    expect(summary).toHaveLength(2)
  })

  it('目录按文件数倒序，其次按名称；文件按名称排序', () => {
    const summary = summarizeWorkspaceFiles([
      'b/a.txt', 'b/c.txt',
      'a/x.txt',
      'zeta.md', 'alpha.md'
    ])
    const dirs = summary.filter((entry) => entry.kind === 'dir')
    const files = summary.filter((entry) => entry.kind === 'file')
    expect(dirs.map((entry) => entry.name)).toEqual(['b', 'a'])
    expect(files.map((entry) => entry.name)).toEqual(['alpha.md', 'zeta.md'])
  })

  it('截断到 maxEntries，非法输入被忽略', () => {
    const paths = ['d/1', 'd/2', 'd/3', 'd/4', 'd/5', 'root.txt', '', undefined as unknown as string]
    // 5 个文件同属目录 d + 1 个顶层文件：归纳后只有 2 项，截断上限不生效但非法输入已剔除。
    const summary = summarizeWorkspaceFiles(paths, 3)
    expect(summary).toHaveLength(2)
    expect(summary[0]).toEqual({ name: 'd', kind: 'dir', fileCount: 5 })
    expect(summary[1]).toEqual({ name: 'root.txt', kind: 'file' })
  })

  it('空列表返回空数组', () => {
    expect(summarizeWorkspaceFiles([])).toEqual([])
  })
})
