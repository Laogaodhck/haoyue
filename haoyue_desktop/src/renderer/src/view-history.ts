import { computed, ref } from 'vue'
import type { ComputedRef } from 'vue'

/**
 * 会话浏览历史（标题栏「后退 / 前进」）。
 *
 * 这两个按钮此前是永久 disabled 的装饰件，占着最宝贵的左上一格。会话浏览历史对这类应用
 * 价值很高：从搜索结果跳进来、点开一个归档会话、在侧栏里穿几个任务之后，想退回原来的
 * 上下文是高频动作，而「重新在侧栏里找一遍」是纯损耗。
 *
 * 语义按浏览器来：记录访问栈；后退/前进在栈内移动；在非端点位置产生新的访问会截断前进
 * 分支；同一会话连续点击不重复入栈。
 */
export interface ViewHistory {
  record: (threadId: string) => void
  /** 返回上一个会话 id；已在栈头则返回 null。 */
  back: () => string | null
  /** 返回下一个会话 id；已在栈尾则返回 null。 */
  forward: () => string | null
  canBack: ComputedRef<boolean>
  canForward: ComputedRef<boolean>
  reset: () => void
}

export function createViewHistory(limit = 60): ViewHistory {
  const entries = ref<string[]>([])
  const cursor = ref(-1)
  /** 历史跳转期间抑制入栈，否则后退会立刻把前进分支截断。 */
  let suppressed = false

  const canBack = computed(() => cursor.value > 0)
  const canForward = computed(() => cursor.value >= 0 && cursor.value < entries.value.length - 1)

  function record(threadId: string): void {
    if (suppressed || !threadId) return
    if (entries.value[cursor.value] === threadId) return
    // 在中间位置产生新访问：截断前进分支（与浏览器一致）。
    const next = entries.value.slice(0, cursor.value + 1)
    next.push(threadId)
    while (next.length > limit) next.shift()
    entries.value = next
    cursor.value = next.length - 1
  }

  function move(delta: number): string | null {
    const target = cursor.value + delta
    if (target < 0 || target >= entries.value.length) return null
    cursor.value = target
    return entries.value[target] ?? null
  }

  function applyNavigation(delta: number): string | null {
    suppressed = true
    try {
      return move(delta)
    } finally {
      // 交给调用方的 await 链结束后再解除抑制；同步批处理足够，因为 selectThread 的
      // 首次入栈发生在它自己的 await 之前。
      queueMicrotask(() => { suppressed = false })
    }
  }

  return {
    record,
    back: () => applyNavigation(-1),
    forward: () => applyNavigation(1),
    canBack,
    canForward,
    reset: () => {
      entries.value = []
      cursor.value = -1
    }
  }
}
