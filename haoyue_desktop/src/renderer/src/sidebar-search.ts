import type { SessionSearchHit } from './types'

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
