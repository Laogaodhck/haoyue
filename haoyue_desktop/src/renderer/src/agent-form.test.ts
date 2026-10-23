import { describe, expect, it } from 'vitest'
import {
  COMPACTION_TAIL_RATIO_DEFAULT,
  clampCompactionTailRatio
} from './agent-form'

describe('clampCompactionTailRatio', () => {
  it('keeps a valid ratio and rounds to two decimals', () => {
    expect(clampCompactionTailRatio(0.62)).toBe(0.62)
    expect(clampCompactionTailRatio(0.625)).toBe(0.63)
    expect(clampCompactionTailRatio(0.1)).toBe(0.1)
    expect(clampCompactionTailRatio(0.9)).toBe(0.9)
  })

  it('clamps out-of-range values to the runtime bounds', () => {
    expect(clampCompactionTailRatio(9)).toBe(0.9)
    expect(clampCompactionTailRatio(1)).toBe(0.9)
    expect(clampCompactionTailRatio(0)).toBe(0.1)
    expect(clampCompactionTailRatio(-1)).toBe(0.1)
  })

  it('falls back to the default for non-finite input', () => {
    expect(clampCompactionTailRatio(Number.NaN)).toBe(COMPACTION_TAIL_RATIO_DEFAULT)
    expect(clampCompactionTailRatio(Number.POSITIVE_INFINITY)).toBe(COMPACTION_TAIL_RATIO_DEFAULT)
    expect(clampCompactionTailRatio(Number.NEGATIVE_INFINITY)).toBe(COMPACTION_TAIL_RATIO_DEFAULT)
  })
})
