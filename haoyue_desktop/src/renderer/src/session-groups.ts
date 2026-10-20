import type { ThreadItem } from './types'

/// 豆包式历史会话管理的纯逻辑：把会话按「更新时间」归入时间分组
/// （今天 / 昨天 / 近 7 天 / 近 30 天 / 更早），供侧栏「按时间」视图渲染。
/// 与按项目视图并存——项目仍是工作区配置的组织单位，时间分组只是会话检索的另一条轴。

export type SessionGroupKey = 'today' | 'yesterday' | 'week' | 'month' | 'earlier'

export interface SessionGroup {
  key: SessionGroupKey
  label: string
  rows: ThreadItem[]
}

export const SESSION_GROUP_LABELS: Record<SessionGroupKey, string> = {
  today: '今天',
  yesterday: '昨天',
  week: '近 7 天',
  month: '近 30 天',
  earlier: '更早'
}

const GROUP_ORDER: SessionGroupKey[] = ['today', 'yesterday', 'week', 'month', 'earlier']

const DAY_MS = 86_400_000

function startOfDay(now: Date): number {
  return new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime()
}

/// 单条会话归属哪个时间分组。边界口径：今天=本地日 0 点起；昨天=前一个自然日；
/// 近 7 天=昨天往前再数 5 个自然日（含今天共 7 天）；近 30 天=今天起往前 30 个自然日。
export function sessionGroupKeyFor(updatedAt: number, now: Date = new Date()): SessionGroupKey {
  if (!Number.isFinite(updatedAt)) return 'earlier'
  const day = startOfDay(now)
  if (updatedAt >= day) return 'today'
  if (updatedAt >= day - DAY_MS) return 'yesterday'
  if (updatedAt >= day - 6 * DAY_MS) return 'week'
  if (updatedAt >= day - 29 * DAY_MS) return 'month'
  return 'earlier'
}

/// 全部未归档会话按时间分组：先按更新时间倒序，再依次落入有序分组；
/// 空分组不输出，保证侧栏不出现「今天（0）」这类空标题。
export function groupThreadsByTime(
  source: readonly ThreadItem[],
  now: Date = new Date()
): SessionGroup[] {
  const sorted = source
    .filter((thread) => !thread.archived)
    .sort((left, right) => right.updatedAt - left.updatedAt)
  const buckets = new Map<SessionGroupKey, ThreadItem[]>()
  for (const thread of sorted) {
    const key = sessionGroupKeyFor(thread.updatedAt, now)
    const rows = buckets.get(key)
    if (rows) rows.push(thread)
    else buckets.set(key, [thread])
  }
  return GROUP_ORDER.filter((key) => buckets.has(key)).map((key) => ({
    key,
    label: SESSION_GROUP_LABELS[key],
    rows: buckets.get(key)!
  }))
}
