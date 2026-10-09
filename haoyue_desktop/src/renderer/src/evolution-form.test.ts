import { describe, expect, it } from 'vitest'
import {
  DEFECT_KIND_LABELS,
  EVOLUTION_INTERVAL_OPTIONS,
  EVOLUTION_MAX_INTERVAL_MINUTES,
  EVOLUTION_MIN_INTERVAL_MINUTES,
  TREND_KEYS,
  clampIntervalMinutes,
  clampThresholdSignals,
  defectKindLabel,
  formatDateOnly,
  formatInterval,
  reflectionOutcomeText,
  relativeTime,
  severityClass,
  severityLabel,
  triggerLabel,
  trendMax,
  trendTitle,
  trendTotal
} from './evolution-form'
import type { EvolutionTrendPoint } from './evolution-form'

describe('defectKindLabel', () => {
  it('maps every runtime DefectKind to a Chinese label', () => {
    expect(defectKindLabel('ToolFailureCluster')).toBe('工具反复失败')
    expect(defectKindLabel('VerificationStruggle')).toBe('验证链受困')
    expect(defectKindLabel('CapabilityGap')).toBe('能力缺口')
    expect(defectKindLabel('UserNegativeFeedback')).toBe('用户负反馈')
    expect(defectKindLabel('ProviderInstability')).toBe('提供商不稳')
    expect(defectKindLabel('UserCorrection')).toBe('用户纠偏')
    expect(defectKindLabel('TurnCancelled')).toBe('回合中断')
    expect(Object.keys(DEFECT_KIND_LABELS)).toHaveLength(7)
  })

  it('falls back to the raw kind for unknown values', () => {
    expect(defectKindLabel('SomethingNew')).toBe('SomethingNew')
  })
})

describe('severityLabel', () => {
  it('maps severity values and treats missing as medium', () => {
    expect(severityLabel('high')).toBe('高')
    expect(severityLabel('medium')).toBe('中')
    expect(severityLabel(null)).toBe('中')
    expect(severityLabel(undefined)).toBe('中')
    expect(severityClass('high')).toBe('severity-high')
    expect(severityClass('medium')).toBe('')
  })
})

describe('triggerLabel', () => {
  it('maps every reflection trigger', () => {
    expect(triggerLabel('manual')).toBe('手动')
    expect(triggerLabel('auto')).toBe('定时')
    expect(triggerLabel('threshold')).toBe('阈值')
    expect(triggerLabel('unknown')).toBe('unknown')
  })
})

describe('clampIntervalMinutes', () => {
  it('keeps in-range values', () => {
    expect(clampIntervalMinutes(360)).toBe(360)
  })

  it('clamps to the runtime bounds', () => {
    expect(clampIntervalMinutes(1)).toBe(EVOLUTION_MIN_INTERVAL_MINUTES)
    expect(clampIntervalMinutes(99999)).toBe(EVOLUTION_MAX_INTERVAL_MINUTES)
  })

  it('rounds fractional minutes and repairs non-finite input', () => {
    expect(clampIntervalMinutes(45.6)).toBe(46)
    expect(clampIntervalMinutes(Number.NaN)).toBe(EVOLUTION_MIN_INTERVAL_MINUTES)
  })
})

describe('clampThresholdSignals', () => {
  it('clamps to the runtime bounds (1..50) and repairs non-finite input', () => {
    expect(clampThresholdSignals(5)).toBe(5)
    expect(clampThresholdSignals(0)).toBe(1)
    expect(clampThresholdSignals(99)).toBe(50)
    expect(clampThresholdSignals(Number.NaN)).toBe(1)
  })
})

describe('formatInterval', () => {
  it('formats minutes, hours and days', () => {
    expect(formatInterval(30)).toBe('30 分钟')
    expect(formatInterval(60)).toBe('1 小时')
    expect(formatInterval(360)).toBe('6 小时')
    expect(formatInterval(1440)).toBe('1 天')
    expect(formatInterval(10080)).toBe('7 天')
  })

  it('keeps fractional hours and days readable', () => {
    expect(formatInterval(90)).toBe('1.5 小时')
    expect(formatInterval(2160)).toBe('1.5 天')
  })
})

describe('relativeTime', () => {
  const now = Date.parse('2026-10-08T12:00:00Z')

  it('buckets fresh and aging timestamps', () => {
    expect(relativeTime('2026-10-08T11:59:40Z', now)).toBe('刚刚')
    expect(relativeTime('2026-10-08T11:40:00Z', now)).toBe('20 分钟前')
    expect(relativeTime('2026-10-08T07:00:00Z', now)).toBe('5 小时前')
    expect(relativeTime('2026-10-05T12:00:00Z', now)).toBe('3 天前')
  })

  it('switches to a date beyond a week', () => {
    expect(relativeTime('2026-09-01T12:00:00Z', now)).toBe('2026-09-01')
  })

  it('returns empty for invalid input', () => {
    expect(relativeTime('', now)).toBe('')
    expect(relativeTime('not-a-date', now)).toBe('')
  })
})

describe('formatDateOnly', () => {
  it('renders the date part and keeps invalid input as-is', () => {
    expect(formatDateOnly('2026-10-08T07:00:00Z')).toBe('2026-10-08')
    expect(formatDateOnly('not-a-date')).toBe('not-a-date')
  })
})

describe('reflectionOutcomeText', () => {
  it('reports failures first', () => {
    expect(reflectionOutcomeText({ error: 'provider offline' })).toBe('反思失败：provider offline')
  })

  it('distinguishes drafts, processed defects and no-ops', () => {
    expect(reflectionOutcomeText({ candidates: 2, processed: 3 }))
      .toBe('反思完成：产出 2 个候选技能，待人工审阅后生效')
    expect(reflectionOutcomeText({ processed: 3, noAction: 1 }))
      .toBe('反思完成：已复盘 3 个缺陷信号，本轮无需新增技能')
    expect(reflectionOutcomeText({ skipped: 4, noAction: 0 }))
      .toBe('反思完成：缺陷信号此前均已处理，本轮无新增动作')
    expect(reflectionOutcomeText({})).toBe('反思完成：暂无可处理的缺陷信号')
  })

  it('ships interval presets within the runtime clamp range', () => {
    for (const option of EVOLUTION_INTERVAL_OPTIONS) {
      expect(option.value).toBe(clampIntervalMinutes(option.value))
    }
  })
})

describe('trend helpers', () => {
  const points: EvolutionTrendPoint[] = [
    {
      date: '2026-10-01', toolFailures: 0, gaps: 0, verificationFailures: 0,
      feedback: 0, corrections: 0, cancels: 0, retries: 0
    },
    {
      date: '2026-10-02', toolFailures: 3, gaps: 1, verificationFailures: 0,
      feedback: 1, corrections: 0, cancels: 2, retries: 4
    }
  ]
  const [empty, busy] = points

  it('sums all seven signal kinds per day', () => {
    expect(TREND_KEYS).toHaveLength(7)
    expect(trendTotal(empty!)).toBe(0)
    expect(trendTotal(busy!)).toBe(11)
  })

  it('takes the peak across days and never returns zero', () => {
    expect(trendMax(points)).toBe(11)
    expect(trendMax([empty!])).toBe(1)
  })

  it('builds a human-readable hover title', () => {
    expect(trendTitle(busy!)).toBe(
      '2026-10-02：工具失败 3 · 能力缺口 1 · 验证受困 0 · 负反馈 1 · 纠偏 0 · 中断 2 · 重试 4'
    )
  })
})
