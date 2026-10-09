/**
 * 进化引擎（Evolution）工作台的纯逻辑：缺陷信号、健康概览、反思历史、
 * 草稿生命周期与效果统计的展示映射。所有函数无副作用，方便 vitest 覆盖。
 */

export interface EvolutionDefectReport {
  fingerprint: string
  kind: string
  severity?: string | null
  firstSeen: string
  lastSeen: string
  occurrences: number
  toolName?: string | null
  errorSummary?: string | null
  sessionId?: string | null
}

export interface EvolutionHealth {
  score: number
  grade: string
}

export interface EvolutionTrendPoint {
  date: string
  toolFailures: number
  gaps: number
  verificationFailures: number
  feedback: number
  corrections: number
  cancels: number
  retries: number
}

export interface EvolutionDistribution {
  byKind: Record<string, number>
  topTools: Array<{ tool: string; count: number }>
}

/** `evolution.pending-list` 中捆绑的草稿文件（超长内容已被 runtime 截断）。 */
export interface EvolutionCandidateFile {
  name: string
  content: string
}

export interface EvolutionCandidate {
  fingerprint: string
  kind: string
  skillName: string
  candidateDir: string
  summary?: string | null
  createdAt: string
  status: string
  files?: EvolutionCandidateFile[] | null
}

export interface EvolutionRun {
  id: number
  sessionId?: string | null
  trigger: string
  processed: number
  candidates: number
  noAction: number
  skipped: number
  failed: number
  error?: string | null
  createdAt: string
}

export interface EvolutionSkillStat {
  fingerprint: string
  skillName: string
  adoptedAt: string
  kind: string
  usageCount: number
  resolved: boolean
}

export interface EvolutionStats {
  runs: number
  candidatesProduced: number
  adopted: number
  rejected: number
  deferred: number
  noAction: number
  failed: number
  adoptionRate: number
  skills: EvolutionSkillStat[]
}

export interface EvolutionConfigValue {
  autoReflect: boolean
  intervalMinutes: number
  thresholdEnabled?: boolean
  thresholdSignals?: number
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
export const EVOLUTION_MIN_THRESHOLD_SIGNALS = 1
export const EVOLUTION_MAX_THRESHOLD_SIGNALS = 50

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
  UserNegativeFeedback: '用户负反馈',
  ProviderInstability: '提供商不稳',
  UserCorrection: '用户纠偏',
  TurnCancelled: '回合中断'
}

export function defectKindLabel(kind: string): string {
  return DEFECT_KIND_LABELS[kind] ?? kind
}

/** 缺陷严重度（runtime EvolutionAnalytics.SeverityOf）的中文展示名。 */
export const SEVERITY_LABELS: Record<string, string> = { high: '高', medium: '中' }

export function severityLabel(severity?: string | null): string {
  return SEVERITY_LABELS[severity ?? ''] ?? '中'
}

export function severityClass(severity?: string | null): string {
  return severity === 'high' ? 'severity-high' : ''
}

/** 反思触发方式（manual / auto / threshold）的中文展示名。 */
export const TRIGGER_LABELS: Record<string, string> = {
  manual: '手动',
  auto: '定时',
  threshold: '阈值'
}

export function triggerLabel(trigger: string): string {
  return TRIGGER_LABELS[trigger] ?? trigger
}

export function clampIntervalMinutes(minutes: number): number {
  if (!Number.isFinite(minutes)) return EVOLUTION_MIN_INTERVAL_MINUTES
  return Math.min(
    EVOLUTION_MAX_INTERVAL_MINUTES,
    Math.max(EVOLUTION_MIN_INTERVAL_MINUTES, Math.round(minutes))
  )
}

export function clampThresholdSignals(signals: number): number {
  if (!Number.isFinite(signals)) return EVOLUTION_MIN_THRESHOLD_SIGNALS
  return Math.min(
    EVOLUTION_MAX_THRESHOLD_SIGNALS,
    Math.max(EVOLUTION_MIN_THRESHOLD_SIGNALS, Math.round(signals))
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
  return formatDateOnly(iso)
}

/** ISO 时间戳 → 「YYYY-MM-DD」。非法输入原样返回。 */
export function formatDateOnly(iso: string): string {
  const time = Date.parse(iso)
  if (!Number.isFinite(time)) return iso
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

/** 近 7 天趋势图的信号类别（顺序即堆叠展示顺序）。 */
export const TREND_KEYS = [
  'toolFailures',
  'gaps',
  'verificationFailures',
  'feedback',
  'corrections',
  'cancels',
  'retries'
] as const

export type TrendKey = (typeof TREND_KEYS)[number]

export const TREND_KEY_LABELS: Record<TrendKey, string> = {
  toolFailures: '工具失败',
  gaps: '能力缺口',
  verificationFailures: '验证受困',
  feedback: '负反馈',
  corrections: '纠偏',
  cancels: '中断',
  retries: '重试'
}

export function trendTotal(point: EvolutionTrendPoint): number {
  return TREND_KEYS.reduce((sum, key) => sum + point[key], 0)
}

/** 趋势条形图的峰值；全为 0 时返回 1，避免除零。 */
export function trendMax(points: EvolutionTrendPoint[]): number {
  return Math.max(1, ...points.map(trendTotal))
}

/** 趋势条悬停提示：「2026-10-09：工具失败 2 · 纠偏 1 …」。 */
export function trendTitle(point: EvolutionTrendPoint): string {
  const parts = TREND_KEYS.map((key) => `${TREND_KEY_LABELS[key]} ${point[key]}`)
  return `${point.date}：${parts.join(' · ')}`
}
