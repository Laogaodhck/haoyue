/**
 * 进化引擎（Evolution）设置页的纯逻辑：缺陷报告与技能草稿的展示映射、
 * 自动反思间隔的夹紧与格式化。所有函数无副作用，方便 vitest 覆盖。
 */

export interface EvolutionDefectReport {
  fingerprint: string
  kind: string
  firstSeen: string
  lastSeen: string
  occurrences: number
  toolName?: string | null
  errorSummary?: string | null
  sessionId?: string | null
}

export interface EvolutionCandidate {
  fingerprint: string
  kind: string
  skillName: string
  candidateDir: string
  summary?: string | null
  createdAt: string
}

export interface EvolutionConfigValue {
  autoReflect: boolean
  intervalMinutes: number
}

/** `evolution.reflected` 广播事件中与本页相关的字段。 */
export interface ReflectionOutcome {
  processed?: number
  candidates?: number
  noAction?: number
  skipped?: number
  error?: string
}

export const EVOLUTION_MIN_INTERVAL_MINUTES = 30
export const EVOLUTION_MAX_INTERVAL_MINUTES = 10080

/** 自动反思间隔的快捷档位；与 runtime 侧 30 分钟..7 天的夹紧范围对齐。 */
export const EVOLUTION_INTERVAL_OPTIONS = [
  { value: 60, label: '1 小时' },
  { value: 360, label: '6 小时' },
  { value: 1440, label: '1 天' },
  { value: 10080, label: '7 天' }
] as const

/** DefectKind 枚举（C# DefectAggregator）的中文展示名。 */
export const DEFECT_KIND_LABELS: Record<string, string> = {
  ToolFailureCluster: '工具反复失败',
  VerificationStruggle: '验证链受困',
  CapabilityGap: '能力缺口',
  UserNegativeFeedback: '用户负反馈'
}

export function defectKindLabel(kind: string): string {
  return DEFECT_KIND_LABELS[kind] ?? kind
}

export function clampIntervalMinutes(minutes: number): number {
  if (!Number.isFinite(minutes)) return EVOLUTION_MIN_INTERVAL_MINUTES
  return Math.min(
    EVOLUTION_MAX_INTERVAL_MINUTES,
    Math.max(EVOLUTION_MIN_INTERVAL_MINUTES, Math.round(minutes))
  )
}

/** 30 → 「30 分钟」，60 → 「1 小时」，1440 → 「1 天」。 */
export function formatInterval(minutes: number): string {
  const value = clampIntervalMinutes(minutes)
  if (value < 60) return `${value} 分钟`
  if (value < 1440) {
    const hours = value / 60
    return Number.isInteger(hours) ? `${hours} 小时` : `${hours.toFixed(1)} 小时`
  }
  const days = value / 1440
  return Number.isInteger(days) ? `${days} 天` : `${days.toFixed(1)} 天`
}

/** ISO 时间戳 → 「刚刚 / N 分钟前 / N 小时前 / N 天前 / YYYY-MM-DD」。非法输入返回空串。 */
export function relativeTime(iso: string, now: number = Date.now()): string {
  const time = Date.parse(iso)
  if (!Number.isFinite(time)) return ''
  const diffMs = Math.max(0, now - time)
  const minutes = Math.floor(diffMs / 60_000)
  if (minutes < 1) return '刚刚'
  if (minutes < 60) return `${minutes} 分钟前`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours} 小时前`
  const days = Math.floor(hours / 24)
  if (days < 7) return `${days} 天前`
  const date = new Date(time)
  const month = `${date.getMonth() + 1}`.padStart(2, '0')
  const day = `${date.getDate()}`.padStart(2, '0')
  return `${date.getFullYear()}-${month}-${day}`
}

/** 反思完成通知文案：区分失败 / 产出草稿 / 已复盘 / 无新动作四种结果。 */
export function reflectionOutcomeText(outcome: ReflectionOutcome): string {
  if (outcome.error) return `反思失败：${outcome.error}`
  if ((outcome.candidates ?? 0) > 0)
    return `反思完成：产出 ${outcome.candidates} 个候选技能，待人工审阅后生效`
  if ((outcome.processed ?? 0) > 0)
    return `反思完成：已复盘 ${outcome.processed} 个缺陷信号，本轮无需新增技能`
  if ((outcome.skipped ?? 0) > 0 || (outcome.noAction ?? 0) > 0)
    return '反思完成：缺陷信号此前均已处理，本轮无新增动作'
  return '反思完成：暂无可处理的缺陷信号'
}
