/// 1.6 会话滚动位置记忆：切走再回来恢复阅读位（偏差 <1px = 直接存 scrollTop 原值）。
/// 模块级 Map（按 thread id 归属），任务关闭/切换时按需清理，防止无界增长。
const positions = new Map<string, number>()

export function saveScrollPosition(threadId: string, scrollTop: number): void {
  if (!threadId) return
  if (!Number.isFinite(scrollTop) || scrollTop <= 0) {
    positions.delete(threadId)
    return
  }
  positions.set(threadId, scrollTop)
}

/** 已保存的位置；没有记录返回 0（调用方回退到「滚到底」）。 */
export function readScrollPosition(threadId: string): number {
  return positions.get(threadId) ?? 0
}

/** 清理全部（或除某个 id 之外）的滚动记忆：任务被删除/归档时调用，避免 Map 单调增长。 */
export function clearScrollPositions(keepThreadId?: string): void {
  if (!keepThreadId) {
    positions.clear()
    return
  }
  for (const key of [...positions.keys()]) {
    if (key !== keepThreadId) positions.delete(key)
  }
}

/** 测试与诊断用：当前记忆的会话数量。 */
export function scrollPositionCount(): number {
  return positions.size
}
