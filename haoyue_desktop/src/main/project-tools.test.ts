import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { createGitBranch, normalizeDiffForGit, parseGitLog, parseGitWorktrees, revertFileDiffs, scanForSecrets } from './project-tools.js'

describe('parseGitLog', () => {
  it('parses record and unit separated git log output', () => {
    const commits = parseGitLog(
      'abcdef\x1fabcdef\x1fAlice\x1f2026-07-30T10:00:00+08:00\x1fAdd diff panel\x1e' +
      '123456\x1f123456\x1fBob\x1f2026-07-29T09:00:00+08:00\x1fInitial commit\x1e')

    expect(commits).toHaveLength(2)
    expect(commits[0]).toMatchObject({ shortHash: 'abcdef', author: 'Alice', subject: 'Add diff panel' })
    expect(commits[1]?.subject).toBe('Initial commit')
  })
})

describe('normalizeDiffForGit', () => {
  it('converts backslashes in headers to forward slashes and normalizes line endings', () => {
    const raw = '--- a\\src\\foo.ts\r\n+++ b\\src\\foo.ts\r\n@@ -1,2 +1,3 @@\r\n'
    const normalized = normalizeDiffForGit(raw)
    expect(normalized).toContain('--- a/src/foo.ts\n+++ b/src/foo.ts\n')
  })
})

describe('parseGitWorktrees', () => {
  it('parses porcelain worktree list including the main worktree and resolves relative paths', () => {
    const porcelain =
      'worktree E:/proj/main\n' +
      'HEAD abcdef123456\n' +
      'branch refs/heads/main\n\n' +
      'worktree E:/proj/main/.git/worktrees/feature\n' +
      'HEAD 123456abcdef\n' +
      'branch refs/heads/feature\n'

    const entries = parseGitWorktrees(porcelain, 'E:/proj/main')
    expect(entries).toHaveLength(2)
    expect(entries[0]).toMatchObject({ path: 'E:/proj/main', branch: 'main', head: 'abcdef1' })
    expect(entries[1]?.branch).toBe('feature')
  })

  it('marks detached HEAD worktrees with no branch', () => {
    const porcelain =
      'worktree E:/proj/main\n' +
      'HEAD 111122223333\n' +
      'branch refs/heads/main\n\n' +
      'worktree E:/proj/other\n' +
      'HEAD 444455556666\n'

    const entries = parseGitWorktrees(porcelain, 'E:/proj/main')
    expect(entries[1]).toMatchObject({ path: 'E:/proj/other', branch: '', head: '4444555' })
  })
})

describe('createGitBranch', () => {
  it('rejects empty or whitespace-only branch names', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'haoyue-test-branch-empty-'))
    try {
      const result = await createGitBranch(dir, '  ')
      expect(result.ok).toBe(false)
      expect(result.error).toContain('不能为空')
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  })

  it('rejects names with invalid characters or leading dash', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'haoyue-test-branch-bad-'))
    try {
      const bad = ['a..b', 'a b', 'a~b', 'a^b', 'a:b', 'a?b', 'a*b', 'a[b', '-lead']
      for (const name of bad) {
        const result = await createGitBranch(dir, name)
        expect(result.ok).toBe(false)
        expect(result.error).toContain('非法字符')
      }
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  })
})

describe('scanForSecrets', () => {
  it('detects AWS keys, private keys, passwords and api tokens with file and line', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'haoyue-test-secrets-'))
    try {
      writeFileSync(join(dir, 'conf.txt'),
        'ok line\npassword = hunter2\napi_key = "abcdef1234567890"\n', 'utf8')
      writeFileSync(join(dir, 'keys.pem'),
        '-----BEGIN RSA PRIVATE KEY-----\nMIIEow\n', 'utf8')
      writeFileSync(join(dir, 'aws.env'), 'AWS_ACCESS_KEY=AKIAIOSFODNN7EXAMPLE\n', 'utf8')

      const result = await scanForSecrets(dir)
      expect(result.findings.length).toBeGreaterThanOrEqual(4)
      expect(result.findings.some((f) => f.kind === 'AWS 访问密钥' && f.line === 1)).toBe(true)
      expect(result.findings.some((f) => f.kind === '私钥内容')).toBe(true)
      expect(result.findings.some((f) => f.kind === '口令赋值')).toBe(true)
      expect(result.findings.some((f) => f.kind === 'API 密钥/令牌' && f.line === 3)).toBe(true)
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  })

  it('skips node_modules and binary extensions while scanning', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'haoyue-test-scan-skip-'))
    try {
      writeFileSync(join(dir, 'a.ts'), 'const token = "abcdef1234567890"\n', 'utf8')
      const nodeModules = join(dir, 'node_modules')
      mkdirSync(nodeModules, { recursive: true })
      writeFileSync(join(nodeModules, 'dep.js'), 'password = xyz\n', 'utf8')
      writeFileSync(join(dir, 'img.png'), Buffer.from([0x89, 0x50, 0x4e, 0x47]))

      const result = await scanForSecrets(dir)
      expect(result.scannedFiles).toBe(1)
      expect(result.findings.some((f) => f.file.includes('node_modules'))).toBe(false)
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  })
})

describe('revertFileDiffs', () => {
  it('reverts a modified file back to original content in reverse order', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'haoyue-test-revert-'))
    try {
      const file = join(dir, 'sample.txt')
      // Current content after an edit
      writeFileSync(file, 'line 1\ninserted line\nline 2\n', 'utf8')

      const diff = '--- a/sample.txt\n+++ b/sample.txt\n@@ -1,2 +1,3 @@\n line 1\n+inserted line\n line 2\n'
      const result = await revertFileDiffs(dir, [{ filePath: file, diff }])

      expect(result.failed).toHaveLength(0)
      expect(result.reverted).toContain(file)
      expect(readFileSync(file, 'utf8').replace(/\r\n/g, '\n')).toBe('line 1\nline 2\n')
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  })

  it('reports failed files when diff does not apply cleanly', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'haoyue-test-conflict-'))
    try {
      const file = join(dir, 'sample.txt')
      writeFileSync(file, 'completely unmatching content\n', 'utf8')

      const diff = '--- a/sample.txt\n+++ b/sample.txt\n@@ -1,2 +1,3 @@\n line 1\n+inserted line\n line 2\n'
      const result = await revertFileDiffs(dir, [{ filePath: file, diff }])

      expect(result.failed).toHaveLength(1)
      expect(result.failed[0]?.filePath).toBe(file)
      expect(result.reverted).toHaveLength(0)
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  })

  it('rejects patches whose target path escapes the workspace', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'haoyue-test-escape-'))
    try {
      // 目标路径越出工作区（父目录）：必须在触碰文件系统之前拒绝。
      const result = await revertFileDiffs(dir, [{
        filePath: join(dir, '..', 'haoyue-escaped-target.txt'),
        diff: '--- a/escaped.txt\n+++ b/escaped.txt\n@@ -0,0 +1 @@\n+evil\n'
      }])

      expect(result.reverted).toHaveLength(0)
      expect(result.failed).toHaveLength(1)
      expect(result.failed[0]?.reason).toContain('越出工作区')
      expect(existsSync(join(dir, '..', 'haoyue-escaped-target.txt'))).toBe(false)
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  })
})
