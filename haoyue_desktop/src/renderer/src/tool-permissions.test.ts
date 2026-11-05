import { describe, expect, it } from 'vitest'
import {
  TOOL_PATTERN_MAX_LENGTH,
  describeToolPermissionSave,
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
  it('treats a missing or empty list as unrestricted', () => {
    const empty = {
      patterns: [], disabledPatterns: [], invalidPatterns: [], invalidDisabledPatterns: [],
      fallback: false, problem: ''
    }
    expect(readToolPermissions({})).toEqual(empty)
    expect(readToolPermissions({ allowedTools: [], disabledTools: [] })).toEqual(empty)
    expect(readToolPermissions(null)).toEqual(empty)
  })

  it('reads both lists and the fallback switch', () => {
    expect(readToolPermissions({
      allowedTools: ['github/*'],
      disabledTools: ['bash', 'mcp__dangerous__*'],
      allowToolFallbackToGlobal: true
    })).toEqual({
      patterns: ['github/*'],
      disabledPatterns: ['bash', 'mcp__dangerous__*'],
      invalidPatterns: [],
      invalidDisabledPatterns: [],
      fallback: true,
      problem: ''
    })
  })

  it('surfaces malformed on-disk field shapes instead of silently swallowing them', () => {
    expect(readToolPermissions({ allowedTools: 'github/*' }).problem)
      .toBe('allowedTools 不是字符串数组，请先修正配置。')
    expect(readToolPermissions({ disabledTools: 'bash' }).problem)
      .toBe('disabledTools 不是字符串数组，请先修正配置。')
    expect(readToolPermissions({ allowToolFallbackToGlobal: 'yes' }).problem)
      .toBe('allowToolFallbackToGlobal 不是布尔值，请先修正配置。')
  })

  it('keeps illegal on-disk entries in a separate bucket without blocking valid ones', () => {
    const view = readToolPermissions({
      allowedTools: ['read_file', 42],
      disabledTools: ['bash', 'bad pattern', 'github/**']
    })
    // 合法条目照常进入编辑框；非法条目单独列出，problem 不被条目级问题污染。
    expect(view.patterns).toEqual(['read_file'])
    expect(view.disabledPatterns).toEqual(['bash'])
    expect(view.invalidPatterns).toEqual(['allowedTools 第 2 项不是字符串（42）'])
    expect(view.invalidDisabledPatterns[0]).toContain('disabledTools 第 2 项「bad pattern」')
    expect(view.invalidDisabledPatterns[0]).toContain('不允许字符')
    expect(view.invalidDisabledPatterns[1]).toBe('disabledTools 第 3 项「github/**」只支持单个 `*`，不允许 `**`')
    expect(view.problem).toBe('')
  })
})

describe('describeToolPermissionSave', () => {
  it('says the next turn takes effect when the write was applied to memory', () => {
    expect(describeToolPermissionSave({
      allowedCount: 2,
      disabledCount: 1,
      reason: 'applied',
      path: 'C:/ws/.haoyue/config.json'
    })).toBe('已保存 2 条白名单模式，已保存 1 条硬禁用模式；项目配置已同步到内存，下一回合生效。')
  })

  it('distinguishes a different workspace from an untrusted current workspace', () => {
    const base = {
      allowedCount: 0,
      disabledCount: 0,
      path: 'C:/ws/.haoyue/config.json'
    }
    expect(describeToolPermissionSave({ ...base, reason: 'other-workspace' })).toBe(
      '已清空白名单（= 不限制工具），已清空硬禁用清单；'
      + '已写入配置文件（C:/ws/.haoyue/config.json），但当前引擎驻留的是另一个工作区，切换到该工作区后生效。'
    )
    expect(describeToolPermissionSave({ ...base, reason: 'untrusted' })).toBe(
      '已清空白名单（= 不限制工具），已清空硬禁用清单；'
      + '已写入配置文件（C:/ws/.haoyue/config.json），但当前工作区尚未受信任（内存中没有加载配置），'
      + '信任或重新打开该工作区后生效。'
    )
  })
})

describe('sameToolPatterns', () => {
  it('compares order-sensitively', () => {
    expect(sameToolPatterns(['read_file', 'github/*'], ['read_file', 'github/*'])).toBe(true)
    expect(sameToolPatterns(['read_file', 'github/*'], ['github/*', 'read_file'])).toBe(false)
    expect(sameToolPatterns([], [])).toBe(true)
  })
})
