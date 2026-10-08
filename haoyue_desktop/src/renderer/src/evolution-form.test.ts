import { describe, expect, it } from 'vitest'
import {
  EVOLUTION_INTERVAL_OPTIONS,
  EVOLUTION_MAX_INTERVAL_MINUTES,
  EVOLUTION_MIN_INTERVAL_MINUTES,
  clampIntervalMinutes,
  defectKindLabel,
  formatInterval,
  reflectionOutcomeText,
  relativeTime
} from './evolution-form'

describe('defectKindLabel', () => {
  it('maps every runtime DefectKind to a Chinese label', () => {
    expect(defectKindLabel('ToolFailureCluster')).toBe('工具反复失败')
    expect(defectKindLabel('VerificationStruggle')).toBe('验证链受困')
    expect(defectKindLabel('CapabilityGap')).toBe('能力缺口')
    expect(defectKindLabel('UserNegativeFeedback')).toBe('用户负反馈')
  })

  it('falls back to the raw kind for unknown values', () => {
    expect(defectKindLabel('SomethingNew')).toBe('SomethingNew')
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
