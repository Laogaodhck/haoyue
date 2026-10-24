import { computed, ref, watch } from 'vue'
import type { ComputedRef, Ref } from 'vue'
import type { ThreadItem } from './types'
import { messageMatches } from './app-helpers'

/**
 * 会话内搜索（标题栏的搜索框 + 上下跳转）。
 *
 * 状态与行为都不依赖组件上下文，只是一组 ref/computed —— 从 App.vue 抽出是为了守住
 * 该文件的行数预算（ADR-20261015-giant-class-budget.md），顺带让「匹配游标越界」
 * 这类逻辑可以被单独测试。
 */
export interface ConversationSearch {
  query: Ref<string>
  matches: ComputedRef<ThreadItem['messages']>
  cursor: Ref<number>
  /** 在匹配之间循环移动并滚动到目标消息。 */
  jump: (delta: number) => void
}

export function createConversationSearch(getThread: () => ThreadItem | undefined): ConversationSearch {
  const query = ref('')
  const cursor = ref(0)

  const matches = computed(() => {
    const needle = query.value.trim()
    const thread = getThread()
    if (!needle || !thread) return []
    return thread.messages.filter((message) => messageMatches(message, needle))
  })

  watch(query, () => { cursor.value = 0 })

  function jump(delta: number): void {
    const found = matches.value
    if (found.length === 0) return
    cursor.value = (cursor.value + delta + found.length) % found.length
    const target = found[cursor.value]
    if (!target) return
    document.querySelector(`[data-message-id="${CSS.escape(target.id)}"]`)
      ?.scrollIntoView({ block: 'center', behavior: 'smooth' })
  }

  return { query, matches, cursor, jump }
}
