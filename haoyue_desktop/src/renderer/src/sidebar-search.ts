import type { SessionSearchHit, ThreadItem } from './types'

/// 侧栏跨会话检索的纯逻辑：本地列表只覆盖已加载项目/全局任务，键入查询词长度达到
/// 阈值才触发后端全文检索（session.search），避免每个 keystroke 都打 daemon。
export const REMOTE_SEARCH_MIN_CHARS = 2

/// 检索词是否值得发起一次跨会话后端检索（空白与单字不值得）。
export function shouldSearchRemotely(query: string): boolean {
  return query.trim().length >= REMOTE_SEARCH_MIN_CHARS
}

/// 后端命中里剔除已在本地列表出现过的会话（按 sessionId 去重），
/// 让「全部会话」区只展示本地看不到的结果。
export function filterUnloadedHits(
  hits: readonly SessionSearchHit[],
  knownSessionIds: ReadonlySet<string>
): SessionSearchHit[] {
  return hits.filter((hit) => !knownSessionIds.has(hit.id))
}

/// 侧栏一个列表最多渲染的行数（P1-2.3）：攒到几百个任务时全量 v-for 会把整棵树的重建
/// 成本压到每次输入上，所以默认只画最近的一批，其余靠「显示全部」按需展开。
export const TASK_ROW_LIMIT = 40

/// 按更新时间排序、剔除归档、按查询词过滤，再截取一屏。当前任务必须留在结果里，
/// 否则从「全部会话」搜开一个旧会话后侧栏会失去选中项。
export function capTaskList(
  source: readonly ThreadItem[],
  options: { query: string; expanded: boolean; activeThreadId?: string }
): { rows: ThreadItem[]; hidden: number } {
  const sorted = source
    .filter((thread) => !thread.archived)
    .filter((thread) => !options.query || thread.title.toLocaleLowerCase().includes(options.query))
    .sort((left, right) => right.updatedAt - left.updatedAt)
  if (options.expanded || sorted.length <= TASK_ROW_LIMIT) return { rows: sorted, hidden: 0 }
  const rows = sorted.slice(0, TASK_ROW_LIMIT)
  const active = options.activeThreadId && sorted.find((thread) => thread.id === options.activeThreadId)
  if (active && !rows.some((thread) => thread.id === active.id)) rows.push(active)
  return { rows, hidden: sorted.length - rows.length }
}
