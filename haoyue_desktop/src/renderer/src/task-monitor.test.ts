import { describe, expect, it } from 'vitest'
import type { ThreadItem } from './types'
import type { ToolActivityEntry } from './tools-activity'
import {
  collectDemo,
  collectGoal,
  collectKnowledge,
  collectMemoryUpdates,
  collectOutputs,
  collectPlan,
  collectReview,
  collectSideChat,
  collectSources,
  collectSubAgents,
  collectWebVisits,
  collectSkillMcp,
  sortBackgroundTasks
} from './task-monitor'

function entry(partial: Partial<ToolActivityEntry> & { name: string }): ToolActivityEntry {
  return { id: `act-${partial.name}`, messageId: 'm1', at: 1000, state: 'done', ...partial }
}

function thread(partial: Partial<ThreadItem> = {}): ThreadItem {
  return {
    id: 't1',
    title: '任务',
    updatedAt: 2000,
    messages: [],
    ...partial
  }
}

describe('task-monitor 聚合层', () => {
  it('collectReview 汇总回合节奏与最近时间线', () => {
    const review = collectReview(thread({
      running: true,
      phase: 'tool',
      turnStepHighWater: 7,
      stats: { llmRounds: 3, inputTokens: 10, outputTokens: 20 },
      messages: [
        { id: 'a', role: 'user', content: '帮我调研', createdAt: 1000 },
        { id: 'b', role: 'assistant', content: '第一步完成了', createdAt: 1500 },
        { id: 'c', role: 'assistant', content: ' 第二步\n完成 ', createdAt: 2000 }
      ]
    }))
    expect(review).not.toBeNull()
    expect(review!.steps).toBe(7)
    expect(review!.llmRounds).toBe(3)
    expect(review!.running).toBe(true)
    expect(review!.timeline).toHaveLength(3)
    expect(review!.timeline[2]!.snippet).toBe('第二步 完成')
  })

  it('collectGoal 取标题与首条用户请求', () => {
    const goal = collectGoal(thread({
      title: '调研竞品',
      messages: [
        { id: 'u', role: 'user', content: '请对比 A 与 B 两款产品', createdAt: 1 },
        { id: 'a', role: 'assistant', content: '好的', createdAt: 2 }
      ]
    }))
    expect(goal).toEqual({ title: '调研竞品', request: '请对比 A 与 B 两款产品' })
    expect(collectGoal(undefined)).toBeNull()
  })

  it('collectPlan 直出 customPlan', () => {
    const plan = [{ id: 'p', step: 1, title: '读资料', state: 'running' as const }]
    expect(collectPlan(thread({ customPlan: plan }))).toEqual(plan)
    expect(collectPlan(thread())).toBeNull()
  })

  it('collectSubAgents 只收委派工具', () => {
    const activities = [
      entry({ name: 'delegate_task', detail: '子任务 A' }),
      entry({ name: 'bash', detail: 'ls' }),
      entry({ name: 'delegate_tasks', detail: '并行' })
    ]
    expect(collectSubAgents(activities).map((e) => e.name)).toEqual(['delegate_task', 'delegate_tasks'])
  })

  it('collectSkillMcp 只收未命中内置映射的工具名', () => {
    const activities = [
      entry({ name: 'bash' }),
      entry({ name: 'my_custom_skill' }),
      entry({ name: 'github-mcp.search_repos' })
    ]
    expect(collectSkillMcp(activities).map((e) => e.name)).toEqual(['my_custom_skill', 'github-mcp.search_repos'])
  })

  it('collectSideChat 排除当前线程并按更新时间排序', () => {
    const other = thread({ id: 't2', title: '另一个', updatedAt: 5000, messages: [{ id: 'x', role: 'user', content: 'hi', createdAt: 5000 }] })
    const idle = thread({ id: 't3', title: '空', updatedAt: 6000 })
    const active = thread({ id: 't1', title: '当前', updatedAt: 7000, running: true, messages: [{ id: 'y', role: 'user', content: 'yo', createdAt: 7000 }] })
    expect(collectSideChat([other, idle, active], 't1').map((t) => t.id)).toEqual(['t2'])
  })

  it('collectOutputs 聚合文件工具与 undoableFiles 并去重', () => {
    const activities = [
      entry({ name: 'write_file', filePath: 'a.txt', at: 100 }),
      entry({ name: 'edit_file', filePath: 'b.ts', addedLines: 2, removedLines: 1, at: 200 }),
      entry({ name: 'bash', detail: '无关', at: 300 })
    ]
    const outputs = collectOutputs(thread({ undoableFiles: ['b.ts', 'c.md'], updatedAt: 999 }), activities)
    // 按时间倒序：回合结束快照（undoableFiles）最新在前，工具调用次之。
    expect(outputs.map((o) => o.path)).toEqual(['c.md', 'b.ts', 'a.txt'])
    expect(outputs.find((o) => o.path === 'b.ts')).toMatchObject({ addedLines: 2, removedLines: 1 })
  })

  it('collectWebVisits 只收 web 工具', () => {
    const activities = [entry({ name: 'web_search', detail: 'x' }), entry({ name: 'bash' }), entry({ name: 'web_fetch', detail: 'y' })]
    expect(collectWebVisits(activities).map((e) => e.name)).toEqual(['web_search', 'web_fetch'])
  })

  it('collectSources 从 args/detail 提取 URL 并去重保序', () => {
    const activities = [
      entry({ name: 'web_fetch', args: 'https://example.com/a 与 https://example.com/a', detail: '来源 https://example.com/b。' }),
      entry({ name: 'bash', detail: 'https://ignored.com' })
    ]
    expect(collectSources(activities)).toEqual(['https://example.com/a', 'https://example.com/b'])
  })

  it('collectKnowledge 覆盖知识库与事实记忆工具', () => {
    const activities = [
      entry({ name: 'knowledge_search' }),
      entry({ name: 'fact_save' }),
      entry({ name: 'memory_save' }),
      entry({ name: 'bash' })
    ]
    expect(collectKnowledge(activities).map((e) => e.name)).toEqual(['knowledge_search', 'fact_save', 'memory_save'])
  })

  it('collectMemoryUpdates 合并广播与会话内固化并倒序', () => {
    const updates = collectMemoryUpdates(
      [{ at: 500, factsAdded: 3, memoryPromoted: 1 }],
      [entry({ name: 'fact_save', detail: '存了一条', at: 800 })]
    )
    expect(updates).toHaveLength(2)
    expect(updates[0]).toMatchObject({ at: 800, factsAdded: 1 })
  })

  it('collectDemo 只收图片与可视化文件', () => {
    const demo = collectDemo(thread({
      messages: [
        {
          id: 'm',
          role: 'assistant',
          content: '',
          createdAt: 3000,
          images: [{ id: 'i', name: '图.png', mediaType: 'image/png', sizeBytes: 10 }],
          files: [
            { id: 'f1', name: '报告.html', path: '/p/报告.html', sizeBytes: 1, extension: 'html' },
            { id: 'f2', name: 'data.json', path: '/p/data.json', sizeBytes: 1, extension: 'json' }
          ]
        }
      ]
    }))
    expect(demo).not.toBeNull()
    expect(demo!.images).toHaveLength(1)
    expect(demo!.files.map((f) => f.name)).toEqual(['报告.html'])
    expect(collectDemo(thread())).toBeNull()
  })

  it('sortBackgroundTasks 运行中优先，再按创建时间倒序', () => {
    const tasks = [
      { id: 'a', status: 'completed', createdAt: '2026-01-01T00:00:00Z' },
      { id: 'b', status: 'running', createdAt: '2026-01-02T00:00:00Z' },
      { id: 'c', status: 'completed', createdAt: '2026-01-03T00:00:00Z' }
    ]
    expect(sortBackgroundTasks(tasks).map((t) => t.id)).toEqual(['b', 'c', 'a'])
  })
})
