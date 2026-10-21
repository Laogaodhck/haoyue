import { describe, expect, it } from 'vitest'
import {
  applySlashCommand,
  filterSlashCommands,
  looksLikeUnknownCommand,
  parseSlashCommand,
  slashToken,
  SLASH_COMMANDS,
} from './composer-slash'

describe('slashToken (1.5 命令面板触发)', () => {
  it('triggers only at line start or after whitespace', () => {
    expect(slashToken('/', 1)).toEqual({ query: '', start: 0 })
    expect(slashToken('请 /模型', 5)).toEqual({ query: '模型', start: 2 })
    // 路径不误触发：最后一个 / 前是字母（/usr/local 的 local 段）→ 不当命令。
    expect(slashToken('路径 /usr/local', 13)).toBeNull()
    // 词中间的 / 不触发（路径、除号）。
    expect(slashToken('a/b', 3)).toBeNull()
    expect(slashToken('https://x.y', 11)).toBeNull()
  })

  it('closes once the command word is followed by a space (arguments phase)', () => {
    expect(slashToken('/模型 gpt', 8)).toBeNull()
  })
})

describe('filterSlashCommands', () => {
  it('returns all commands for an empty query', () => {
    expect(filterSlashCommands('')).toHaveLength(SLASH_COMMANDS.length)
    expect(SLASH_COMMANDS.length).toBeGreaterThanOrEqual(5)
  })

  it('matches by label and by id', () => {
    expect(filterSlashCommands('导').map((command) => command.id)).toEqual(['export'])
    expect(filterSlashCommands('depth').map((command) => command.id)).toEqual(['depth'])
    expect(filterSlashCommands('都不匹配')).toHaveLength(0)
  })
})

describe('applySlashCommand', () => {
  it('fills the command and adds a trailing space only for parameterized commands', () => {
    const model = SLASH_COMMANDS.find((command) => command.id === 'model')!
    const exported = applySlashCommand('/mo', 3, 0, model)
    expect(exported.text).toBe('/模型 ')
    expect(exported.caret).toBe(4)

    const exportCommand = SLASH_COMMANDS.find((command) => command.id === 'export')!
    const noArg = applySlashCommand('/导', 2, 0, exportCommand)
    expect(noArg.text).toBe('/导出会话')
  })

  it('preserves surrounding text', () => {
    const mode = SLASH_COMMANDS.find((command) => command.id === 'mode')!
    const result = applySlashCommand('前缀 /模 后缀', 5, 3, mode)
    expect(result.text).toBe('前缀 /模式  后缀')
  })
})

describe('parseSlashCommand (提交时拦截)', () => {
  it('parses a known command with and without arguments', () => {
    const withArg = parseSlashCommand('/模式 plan')
    expect(withArg?.command.id).toBe('mode')
    expect(withArg?.arg).toBe('plan')

    const noArg = parseSlashCommand('/导出会话')
    expect(noArg?.command.id).toBe('export')
    expect(noArg?.arg).toBe('')
  })

  it('is case-insensitive on the command label', () => {
    expect(parseSlashCommand('/MODE auto')?.command.id).toBe('mode')
  })

  it('returns null for plain messages and unknown commands', () => {
    expect(parseSlashCommand('普通消息')).toBeNull()
    expect(parseSlashCommand('/不存在')).toBeNull()
    // 未知命令由 looksLikeUnknownCommand 单独识别，给出提示而不是发出去。
    expect(looksLikeUnknownCommand('/不存在')).toBe(true)
    expect(looksLikeUnknownCommand('/usr/local/bin')).toBe(false) // 路径不是命令
    expect(looksLikeUnknownCommand('/模式 plan')).toBe(false) // 已知命令
    expect(looksLikeUnknownCommand('普通消息')).toBe(false)
  })
})
