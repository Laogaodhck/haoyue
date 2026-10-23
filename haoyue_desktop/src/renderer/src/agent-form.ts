/**
 * 智能体设置工作台的纯逻辑：把 runtime 侧 ConfigSchema / DaemonAdminApi 的区间
 * 规则（0.1–0.9，保留两位小数）镜像到前端，避免设置页各写一套钳制逻辑。
 */

export const COMPACTION_TAIL_RATIO_MIN = 0.1
export const COMPACTION_TAIL_RATIO_MAX = 0.9
export const COMPACTION_TAIL_RATIO_DEFAULT = 0.5

/** 非法输入（NaN / ±Infinity）回退默认值；越界钳制；四舍五入到两位小数。 */
export function clampCompactionTailRatio(value: number): number {
  if (!Number.isFinite(value)) return COMPACTION_TAIL_RATIO_DEFAULT
  const clamped = Math.min(COMPACTION_TAIL_RATIO_MAX, Math.max(COMPACTION_TAIL_RATIO_MIN, value))
  return Math.round(clamped * 100) / 100
}
