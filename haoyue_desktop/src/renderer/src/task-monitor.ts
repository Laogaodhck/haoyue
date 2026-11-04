import type { ThreadItem } from './types'
import type { ToolActivityEntry } from './tools-activity'
import { toolKind, toolLabel } from './tools-activity'

/**
 * 任务监控面板的纯聚合层（无 UI 依赖，可单测）。
 * 面板按设置开关展示「进度与上下文 / 执行活动 / 结果与来源 / 辅助入口」四组区块，
 * 每个聚合函数只负责从 thread / 工具活动 / 侧栏线程中提取自己那一块的展示数据。
 */

// ---- 进度与上下文 ----

export interface MonitorReview {
  /** 最近一次工具活动/消息的时间戳（毫秒）。 */
  lastAt: number
  /** 当前回合已见执行步数（turnStepHighWater）。 */
  steps: number
  /** LLM 调用轮数。 */
  llmRounds: number
  /** 输入/输出 token 累计。 */
  inputTokens?: number
  outputTokens?: number
  /** 回合阶段（thinking/tool/verify…）。 */
  phase?: string
  running: boolean
  /** 最近三条消息的「角色 + 时间 + 截断正文」时间线。 */
  timeline: Array<{ role: 'user' | 'assistant'; at: number; snippet: string }>
}

/** 任务回顾：回合节奏 + 最近消息时间线。 */
export function collectReview(thread?: ThreadItem): MonitorReview | null {
  if (!thread) return null
  const messages = thread.messages
  const recent = messages.slice(-3)
  return {
    lastAt: thread.updatedAt,
    steps: thread.turnStepHighWater ?? 0,
    llmRounds: thread.stats?.llmRounds ?? 0,
    inputTokens: thread.stats?.inputTokens,
    outputTokens: thread.stats?.outputTokens,
    phase: thread.phase,
    running: Boolean(thread.running),
    timeline: recent.map((message) => ({
      role: message.role,
      at: message.createdAt,
      snippet: snippetOf(message.content)
    }))
  }
}

export interface MonitorGoal {
  title: string
  /** 会话的首条用户请求（任务的原始目标）。 */
  request?: string
}

/** 任务目标：会话标题 + 首条用户消息。 */
export function collectGoal(thread?: ThreadItem): MonitorGoal | null {
  if (!thread) return null
  const firstUser = thread.messages.find((message) => message.role === 'user')
  return { title: thread.title, request: firstUser ? snippetOf(firstUser.content, 240) : undefined }
}

/** 计划：update_plan 推来的自定义步骤（state: pending/running/done/error）。 */
export function collectPlan(thread?: ThreadItem): ThreadItem['customPlan'] | null {
  return thread?.customPlan ?? null
}

// ---- 执行活动 ----

/** 子智能体：委派类工具（delegate_task / delegate_tasks）的调用记录。 */
export function collectSubAgents(activities: ToolActivityEntry[]): ToolActivityEntry[] {
  return activities.filter((entry) => entry.name === 'delegate_task' || entry.name === 'delegate_tasks')
}

/** Skill 与 MCP：工具名不在内置映射表（toolLabel 未命中 → 原样显示）即技能/MCP 工具。 */
export function collectSkillMcp(activities: ToolActivityEntry[]): ToolActivityEntry[] {
  return activities.filter((entry) => toolLabel(entry.name) === entry.name)
}

/** 侧边聊天：其它正在运行/有消息的会话（点击可切换）。 */
export function collectSideChat(threads: ThreadItem[], activeId?: string): ThreadItem[] {
  return threads
    .filter((thread) => thread.id !== activeId)
    .filter((thread) => thread.messages.length > 0 || thread.running)
    .sort((a, b) => b.updatedAt - a.updatedAt)
    .slice(0, 8)
}

// ---- 结果与来源 ----

export interface MonitorOutput {
  path: string
  addedLines?: number
  removedLines?: number
  /** 文件类工具的调用摘要（如 write_file 的写入路径说明）。 */
  detail?: string
  at: number
}

/** 产出：回合改写的文件（undoableFiles）+ 文件类工具的写入路径，按时间倒序去重。 */
export function collectOutputs(thread: ThreadItem | undefined, activities: ToolActivityEntry[]): MonitorOutput[] {
  const outputs: MonitorOutput[] = []
  const seen = new Set<string>()
  const push = (output: MonitorOutput): void => {
    if (!output.path || seen.has(output.path)) return
    seen.add(output.path)
    outputs.push(output)
  }
  for (const entry of activities) {
    if (entry.name === 'write_file' || entry.name === 'edit_file') {
      push({ path: entry.filePath ?? entry.detail ?? '', addedLines: entry.addedLines, removedLines: entry.removedLines, detail: entry.detail, at: entry.at })
    }
  }
  for (const path of thread?.undoableFiles ?? []) push({ path, at: thread?.updatedAt ?? 0 })
  return outputs.sort((a, b) => b.at - a.at)
}

/** 网页查阅：web_search / web_fetch 调用记录。 */
export function collectWebVisits(activities: ToolActivityEntry[]): ToolActivityEntry[] {
  return activities.filter((entry) => entry.name === 'web_search' || entry.name === 'web_fetch')
}

/** 来源：从网页查阅的入参/摘要与文件路径中提取的 URL 列表（保序去重）。 */
export function collectSources(activities: ToolActivityEntry[]): string[] {
  const urls: string[] = []
  const seen = new Set<string>()
  const push = (url: string): void => {
    if (!url || seen.has(url)) return
    seen.add(url)
    urls.push(url)
  }
  for (const entry of activities) {
    if (entry.name !== 'web_search' && entry.name !== 'web_fetch') continue
    for (const text of [entry.args, entry.detail]) {
      if (!text) continue
      for (const match of text.matchAll(/https?:\/\/[^\s"'<>，。]+/g)) push(match[0])
    }
  }
  return urls.slice(0, 12)
}

/** 知识：知识库 / 事实记忆类工具调用。 */
export function collectKnowledge(activities: ToolActivityEntry[]): ToolActivityEntry[] {
  return activities.filter((entry) => {
    const name = entry.name
    return name.startsWith('knowledge_') || name.startsWith('fact_') || name === 'memory_save'
  })
}

// ---- 辅助入口 ----

export interface MemoryUpdate {
  at: number
  factsAdded: number
  memoryPromoted: number
  note?: string
  error?: string
}

/** 记忆更新：本面板订阅期间收到的 memory.consolidated 广播 + 会话内事实类工具。 */
export function collectMemoryUpdates(
  broadcasts: MemoryUpdate[],
  activities: ToolActivityEntry[]
): MemoryUpdate[] {
  const updates = [...broadcasts]
  for (const entry of activities) {
    if (entry.name === 'fact_save' || entry.name === 'memory_save') {
      updates.push({ at: entry.at, factsAdded: 1, memoryPromoted: 0, note: entry.detail })
    }
  }
  return updates.sort((a, b) => b.at - a.at).slice(0, 6)
}

export interface DemoScreen {
  /** 会话内图片附件（含引用视图）的最近展示。 */
  images: Array<{ name: string; mediaType: string; sizeBytes: number; at: number }>
  /** 可视化产物（图片/文档/网页等可展示文件）。 */
  files: Array<{ name: string; extension: string; path: string; at: number }>
}

const DEMO_EXTENSIONS = new Set(['png', 'jpg', 'jpeg', 'gif', 'webp', 'svg', 'pdf', 'html', 'md'])

/** 演示画面：最近的图片附件与可视化文件（用于「回看刚才做了什么」）。 */
export function collectDemo(thread?: ThreadItem): DemoScreen | null {
  if (!thread) return null
  const images: DemoScreen['images'] = []
  const files: DemoScreen['files'] = []
  for (const message of thread.messages) {
    for (const image of message.images ?? []) {
      images.push({ name: image.name, mediaType: image.mediaType, sizeBytes: image.sizeBytes, at: message.createdAt })
    }
    for (const file of message.files ?? []) {
      if (DEMO_EXTENSIONS.has(file.extension.toLowerCase())) {
        files.push({ name: file.name, extension: file.extension, path: file.path, at: message.createdAt })
      }
    }
  }
  if (images.length === 0 && files.length === 0) return null
  return {
    images: images.slice(-6).reverse(),
    files: files.slice(-6).reverse()
  }
}

/** 后台任务：运行时侧独立执行的回合（task.list 直出，按状态排序）。 */
export function sortBackgroundTasks<T extends { status: string; createdAt?: string; endedAt?: string }>(
  tasks: T[]
): T[] {
  const rank: Record<string, number> = { running: 0, completed: 1, failed: 2, cancelled: 3 }
  return [...tasks].sort((a, b) => {
    const ra = rank[a.status] ?? 4
    const rb = rank[b.status] ?? 4
    if (ra !== rb) return ra - rb
    return (b.createdAt ?? '').localeCompare(a.createdAt ?? '')
  })
}

// ---- 内部工具 ----

function snippetOf(text: string, limit = 80): string {
  const normalized = (text ?? '').replace(/\s+/g, ' ').trim()
  return normalized.length > limit ? `${normalized.slice(0, limit)}…` : normalized
}

// 供面板直接复用：判断文件类工具（产出区块的图标/配色）。
export function isFileTool(name: string): boolean {
  return toolKind(name) === 'file'
}
