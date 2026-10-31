import { ref } from 'vue'
import { daemonRequest } from './daemon-rpc'
import type { EvolutionCandidate } from './types'

/**
 * 进化引擎的客户端状态：候选技能审批横幅 + 回合负反馈。
 * 候选产生于后台反思 turn，通过 evolution.reflected 广播或启动时 pending-list 恢复。
 */
export function createEvolutionController(getActiveSessionId: () => string | undefined) {
  const candidates = ref<EvolutionCandidate[]>([])
  const inFlight = ref(false)

  async function refresh(): Promise<void> {
    try {
      const list = await daemonRequest('evolution.pending-list')
      candidates.value = Array.isArray(list)
        ? list.filter((item): item is EvolutionCandidate =>
          Boolean(item && typeof item.fingerprint === 'string' && typeof item.skillName === 'string'))
        : []
    } catch {
      // daemon offline / old runtime without evolution support — banner stays hidden
    }
  }

  async function onReflected(): Promise<void> {
    await refresh()
    if (candidates.value.length > 0) {
      void window.haoyue.notify(
        '进化引擎',
        `反思完成：${candidates.value.length} 个技能候选待审批`)
    }
  }

  async function decide(candidate: EvolutionCandidate, adopt: boolean): Promise<void> {
    if (inFlight.value) return
    inFlight.value = true
    try {
      await daemonRequest('evolution.decide', {
        fingerprint: candidate.fingerprint,
        decision: adopt ? 'adopt' : 'reject'
      })
      candidates.value = candidates.value.filter(
        (item) => item.fingerprint !== candidate.fingerprint)
    } catch (reason) {
      await window.haoyue.notify(
        '候选审批失败',
        reason instanceof Error ? reason.message : String(reason))
      await refresh()
    } finally {
      inFlight.value = false
    }
  }

  /** P4: explicit negative feedback on a finished turn; journaled as a defect signal. */
  async function feedbackNegative(): Promise<void> {
    const sessionId = getActiveSessionId()
    if (!sessionId) return
    try {
      await daemonRequest('feedback.turn', { sessionId, kind: 'negative' })
    } catch {
      // feedback is best-effort; never disturb the user over a failed journal write
    }
  }

  return { candidates, inFlight, refresh, onReflected, decide, feedbackNegative }
}
