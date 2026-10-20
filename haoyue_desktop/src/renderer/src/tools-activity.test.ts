import { describe, expect, it } from 'vitest'
import type { ChatMessage, ToolActivity } from './types'
import { collectActivities, formatDuration, toolKind, toolLabel, uniqueToolNames } from './tools-activity'

function message(id: string, createdAt: number, tools: ToolActivity[]): ChatMessage {
  return { id, role: 'assistant', content: '', createdAt, tools }
}

function tool(name: string, state: ToolActivity['state'] = 'done', extra: Partial<ToolActivity> = {}): ToolActivity {
  return { id: `${name}-${Math.random().toString(36).slice(2)}`, name, state, ...extra }
}

describe('collectActivities', () => {
  it('跨消息聚合并倒序（最新在前）', () => {
    const entries = collectActivities([
      message('m1', 100, [tool('bash'), tool('edit_file')]),
      message('m2', 200, [tool('terminal')])
    ])
    expect(entries.map((entry) => entry.name)).toEqual(['terminal', 'bash', 'edit_file'])
    expect(entries[0]?.messageId).toBe('m2')
    expect(entries[2]?.messageId).toBe('m1')
  })

  it('无工具与缺字段时返回空数组', () => {
    expect(collectActivities([])).toEqual([])
    expect(collectActivities([message('m1', 1, [])])).toEqual([])
    expect(collectActivities([{ id: 'm1', role: 'user', content: 'hi', createdAt: 1 }])).toEqual([])
  })
})

describe('toolLabel / toolKind', () => {
  it('内置名映射中文与类别', () => {
    expect(toolLabel('terminal')).toBe('终端')
    expect(toolLabel('browser')).toBe('浏览器')
    expect(toolLabel('web_fetch')).toBe('网页')
    expect(toolKind('terminal')).toBe('shell')
    expect(toolKind('browser')).toBe('browser')
    expect(toolKind('web_search')).toBe('web')
    expect(toolKind('edit_file')).toBe('file')
    expect(toolKind('grep')).toBe('search')
    expect(toolKind('update_plan')).toBe('plan')
  })

  it('未知工具（技能/MCP）原样返回并归为 other', () => {
    expect(toolLabel('my_skill_tool')).toBe('my_skill_tool')
    expect(toolKind('my_skill_tool')).toBe('other')
  })
})

describe('uniqueToolNames', () => {
  it('去重且保持最新出现顺序', () => {
    const entries = collectActivities([
      message('m1', 100, [tool('bash'), tool('browser'), tool('bash')]),
      message('m2', 200, [tool('terminal')])
    ])
    expect(uniqueToolNames(entries)).toEqual(['terminal', 'bash', 'browser'])
  })
})

describe('formatDuration', () => {
  it('毫秒 / 秒 / 分钟三档', () => {
    expect(formatDuration(420)).toBe('420ms')
    expect(formatDuration(7400)).toBe('7.4s')
    expect(formatDuration(65_000)).toBe('1m05s')
    expect(formatDuration(0)).toBe('0ms')
  })

  it('缺失或非法耗时返回空串', () => {
    expect(formatDuration(undefined)).toBe('')
    expect(formatDuration(-5)).toBe('')
    expect(formatDuration(Number.NaN)).toBe('')
  })
})
