import { describe, expect, it } from 'vitest'
import {
  TOOL_PATTERN_MAX_LENGTH,
  parseToolPatterns,
  readToolPermissions,
  sameToolPatterns,
  validateToolPattern
} from './tool-permissions'

describe('validateToolPattern', () => {
  it('accepts plain names, MCP names and single-star patterns', () => {
    for (const pattern of [
      'read_file',
      'github/create_issue',
      'github/*',
      '*/create_issue',
      '*',
      'mcp__server__tool',
      'mcp__github__*'
    ]) expect(validateToolPattern(pattern)).toBeNull()
  })

  it('rejects empty, whitespace, illegal characters and empty slash segments', () => {
    expect(validateToolPattern('')).toBe('模式不能为空')
    expect(validateToolPattern(' github/*')).toBe('模式首尾不能包含空白')
    expect(validateToolPattern('github/* ')).toBe('模式首尾不能包含空白')
    expect(validateToolPattern('read file')?.startsWith('不允许字符')).toBe(true)
    expect(validateToolPattern('github?/issue')?.startsWith('不允许字符')).toBe(true)
    expect(validateToolPattern('/issue')).toBe('斜杠两侧不能为空')
    expect(validateToolPattern('github/')).toBe('斜杠两侧不能为空')
    expect(validateToolPattern('github//issue')).toBe('斜杠两侧不能为空')
  })

  it('rejects double-star globs and oversized entries', () => {
    expect(validateToolPattern('github/**')).toBe('只支持单个 `*`，不允许 `**`')
    expect(validateToolPattern('a'.repeat(TOOL_PATTERN_MAX_LENGTH + 1)))
      .toBe(`模式长度不能超过 ${TOOL_PATTERN_MAX_LENGTH} 个字符`)
  })
})

describe('parseToolPatterns', () => {
  it('trims lines, skips blanks and keeps input order', () => {
    const parsed = parseToolPatterns('\n  read_file  \r\n\r\ngithub/*\n*/create_issue\n')
    expect(parsed.patterns).toEqual(['read_file', 'github/*', '*/create_issue'])
    expect(parsed.errors).toEqual([])
  })

  it('reports invalid entries with their line number instead of dropping them', () => {
    const parsed = parseToolPatterns('read_file\ngithub/**\n\n*/issue/')
    expect(parsed.patterns).toEqual(['read_file'])
    expect(parsed.errors).toEqual([
      '第 2 行「github/**」只支持单个 `*`，不允许 `**`',
      '第 4 行「*/issue/」斜杠两侧不能为空'
    ])
  })
})

describe('readToolPermissions', () => {
  it('treats a missing or empty allow list as unrestricted', () => {
    expect(readToolPermissions({})).toEqual({ patterns: [], fallback: false, problem: '' })
    expect(readToolPermissions({ allowedTools: [] }))
      .toEqual({ patterns: [], fallback: false, problem: '' })
    expect(readToolPermissions(null)).toEqual({ patterns: [], fallback: false, problem: '' })
  })

  it('reads patterns and the fallback switch', () => {
    expect(readToolPermissions({ allowedTools: ['github/*'], allowToolFallbackToGlobal: true }))
      .toEqual({ patterns: ['github/*'], fallback: true, problem: '' })
  })

  it('surfaces malformed on-disk fields instead of silently swallowing them', () => {
    expect(readToolPermissions({ allowedTools: 'github/*' }).problem)
      .toBe('allowedTools 不是字符串数组，请先修正配置。')
    expect(readToolPermissions({ allowedTools: ['read_file', 42] }))
      .toMatchObject({ patterns: ['read_file'], problem: 'allowedTools 含有非字符串条目，请先修正配置。' })
    expect(readToolPermissions({ allowToolFallbackToGlobal: 'yes' }).problem)
      .toBe('allowToolFallbackToGlobal 不是布尔值，请先修正配置。')
  })
})

describe('sameToolPatterns', () => {
  it('compares order-sensitively', () => {
    expect(sameToolPatterns(['read_file', 'github/*'], ['read_file', 'github/*'])).toBe(true)
    expect(sameToolPatterns(['read_file', 'github/*'], ['github/*', 'read_file'])).toBe(false)
    expect(sameToolPatterns([], [])).toBe(true)
  })
})
