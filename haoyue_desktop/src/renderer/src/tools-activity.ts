import type { ChatMessage, ToolActivity } from './types'

/** 工具面板的一条聚合条目：工具活动 + 归属消息（用于「定位消息」跳转）。 */
export interface ToolActivityEntry extends ToolActivity {
  messageId: string
  at: number
}

/**
 * 按时间倒序（最新在前）聚合会话内全部工具调用。工具面板的数据源：
 * 消息气泡里的折叠行只服务单条消息的上下文，面板需要跨消息的全局视角。
 * 消息间倒序（新消息在前），消息内保持调用时间正序——同一回合的
 * 工具链（读文件 → 编辑 → 测试）不应被反转成难以阅读的倒放。
 */
export function collectActivities(messages: ChatMessage[]): ToolActivityEntry[] {
  const entries: ToolActivityEntry[] = []
  for (const message of [...messages].reverse()) {
    for (const tool of message.tools ?? []) {
      entries.push({ ...tool, messageId: message.id, at: message.createdAt })
    }
  }
  return entries
}

const TOOL_LABELS: Record<string, string> = {
  bash: 'Shell',
  terminal: '终端',
  browser: '浏览器',
  read_file: '读取',
  write_file: '写入',
  edit_file: '编辑',
  grep: '搜索',
  glob: '搜索',
  list_dir: '目录',
  web_search: '网页',
  web_fetch: '网页',
  update_plan: '计划',
  capture_screen: '截屏',
  image_generate: '图像',
  doc_render: '文档',
  fact_search: '记忆',
  fact_save: '记忆',
  fact_forget: '记忆',
  knowledge_search: '知识库',
  knowledge_save: '知识库',
  knowledge_forget: '知识库',
  memory_save: '记忆'
}

/** 工具展示名：内置名映射中文，未知名（技能/MCP）原样显示。 */
export function toolLabel(name: string): string {
  return TOOL_LABELS[name] ?? name
}

export type ToolKind = 'shell' | 'browser' | 'file' | 'search' | 'web' | 'plan' | 'other'

/** 按工具类别归组，驱动面板行的图标与配色。 */
export function toolKind(name: string): ToolKind {
  if (name === 'bash' || name === 'terminal' || name === 'computer_exec') return 'shell'
  if (name === 'browser') return 'browser'
  if (name.startsWith('web_')) return 'web'
  if (name === 'grep' || name === 'glob' || name === 'list_dir') return 'search'
  if (name === 'read_file' || name === 'write_file' || name === 'edit_file') return 'file'
  if (name === 'update_plan') return 'plan'
  return 'other'
}

/** 去重保序的工具名列表（驱动筛选 chips，出现的顺序 = 最新调用的顺序）。 */
export function uniqueToolNames(entries: ToolActivityEntry[]): string[] {
  const seen: string[] = []
  for (const entry of entries) {
    if (!seen.includes(entry.name)) seen.push(entry.name)
  }
  return seen
}

/** 耗时显示：<1s 用毫秒，<60s 一位小数秒，更长 1m05s 形态。 */
export function formatDuration(ms?: number): string {
  if (ms == null || !Number.isFinite(ms) || ms < 0) return ''
  if (ms < 1000) return `${Math.round(ms)}ms`
  const total = ms / 1000
  if (total < 60) return `${total.toFixed(1)}s`
  const minutes = Math.floor(total / 60)
  const seconds = Math.round(total % 60)
  return `${minutes}m${String(seconds).padStart(2, '0')}s`
}
