import type { FileAttachment, ImageAttachment } from './types'

/**
 * 按任务隔离的输入框草稿（文字 + 附件），并落盘到 localStorage。
 *
 * 抽成模块的原因：这段逻辑与组件状态无关（纯 Map + 序列化），留在 App.vue 里只贡献行数；
 * 而 App.vue 有 2400 行硬预算（ADR-20261015-giant-class-budget.md），继续膨胀必须先拆分。
 */
export interface ComposerDraft {
  text: string
  images: ImageAttachment[]
  files: FileAttachment[]
}

const DRAFT_STORAGE_KEY = 'haoyue-composer-drafts'

/** 启动时恢复草稿。损坏的存储按「无草稿」处理，不阻塞启动。 */
export function loadDrafts(): Map<string, ComposerDraft> {
  const drafts = new Map<string, ComposerDraft>()
  try {
    const raw = localStorage.getItem(DRAFT_STORAGE_KEY)
    if (!raw) return drafts
    const parsed = JSON.parse(raw) as Record<string, ComposerDraft>
    for (const [threadId, draft] of Object.entries(parsed))
      if (draft && typeof draft.text === 'string') drafts.set(threadId, draft)
  } catch {
    // 损坏的草稿存储不影响启动：按无草稿处理。
  }
  return drafts
}

/** 落盘。只写有内容的草稿：空文本不占存储，也不该在恢复时抢焦点。 */
export function persistDrafts(drafts: Map<string, ComposerDraft>): void {
  try {
    const entries: Record<string, ComposerDraft> = {}
    for (const [threadId, draft] of drafts) {
      if (draft.text.trim() || draft.images.length > 0 || draft.files.length > 0)
        entries[threadId] = draft
    }
    localStorage.setItem(DRAFT_STORAGE_KEY, JSON.stringify(entries))
  } catch {
    // 存储配额满等故障静默降级：内存 Map 仍是权威来源。
  }
}
