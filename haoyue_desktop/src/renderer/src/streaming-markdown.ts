/**
 * 1.1 流式渲染增量管线：把累积 markdown 切成「已定稿块」+「尾部未定稿块」，
 * 定稿块按内容作键 memo 缓存 HTML，每帧只解析尾部块——单帧解析量从 O(全篇)
 * 降为 O(增量)。定稿（streaming=false）走全量渲染，最终画面与旧实现 100% 等价。
 */

export interface SplitResult {
  /** 已完整结束、内容不会再变的块（可直接复用缓存）。 */
  finalized: string[]
  /** 最后一块：流式期仍在增长，每帧重解析。可能为空串。 */
  tail: string
  /** tail 是否停在未闭合的代码围栏内（此时降级为纯文本，避免半个代码块被当格式解析）。 */
  tailInFence: boolean
}

const FENCE_RE = /^(```|~~~)/

/** 列表项行（- * + 或「数字.」）：loose list 的空行分隔不能当块边界。 */
function isListItem(line: string): boolean {
  const trimmed = line.trimStart()
  if (trimmed.length >= 2 && (trimmed[0] === '-' || trimmed[0] === '*' || trimmed[0] === '+') && trimmed[1] === ' ') {
    return true
  }
  const digits = /^\d{1,4}[.)]\s/.exec(trimmed)
  return digits !== null
}

/**
 * 切分块：以「空行」为边界，但遇到以下情况不切——
 * - 代码围栏内（```/~~~ 配对之间）
 * - 空行两侧都是列表项（保持 loose list 与整篇渲染一致）
 * 围栏未闭合时该块归入 tail 并标记 tailInFence（调用方降级纯文本）。
 */
export function splitFinalizedBlocks(markdown: string): SplitResult {
  const normalized = markdown.replace(/\r\n/g, '\n')
  if (normalized.trim().length === 0) return { finalized: [], tail: '', tailInFence: false }

  const lines = normalized.split('\n')
  const blocks: string[] = []
  let current: string[] = []
  let inFence = false
  let fenceChar = ''

  const flush = (): void => {
    // 去掉块尾的空行，但保留块内结构；空块直接丢弃。
    while (current.length > 0 && current[current.length - 1]!.trim() === '') current.pop()
    if (current.length > 0) blocks.push(current.join('\n'))
    current = []
  }

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]!
    const trimmed = line.trimStart()

    if (inFence) {
      current.push(line)
      if (trimmed.startsWith(fenceChar) && trimmed.trimEnd().length >= 3) inFence = false
      continue
    }
    if (FENCE_RE.test(trimmed)) {
      inFence = true
      fenceChar = trimmed[0] === '`' ? '```' : '~~~'
      current.push(line)
      continue
    }

    if (line.trim() === '') {
      // 边界候选：列表项之间不切（loose list 的项间空行属于同一列表）。
      const previous = current.length > 0 ? current[current.length - 1]! : undefined
      const next = findNextNonEmpty(lines, i + 1)
      if (previous !== undefined && isListItem(previous) && next !== undefined && isListItem(next)) {
        current.push(line)
        continue
      }
      flush()
      continue
    }

    current.push(line)
  }
  flush()

  if (blocks.length === 0) return { finalized: [], tail: '', tailInFence: false }
  if (inFence) {
    // 围栏未闭合：最后一块是正在流入的代码，降级纯文本。
    const tail = blocks.pop()!
    return { finalized: blocks, tail, tailInFence: true }
  }
  // 末尾若以空行收尾，最后一块其实也已定稿（尾部无增长内容）。
  const tail = blocks.pop()!
  return { finalized: blocks, tail, tailInFence: false }
}

function findNextNonEmpty(lines: string[], start: number): string | undefined {
  for (let i = start; i < lines.length; i++) {
    if (lines[i]!.trim() !== '') return lines[i]!
  }
  return undefined
}

export interface IncrementalOptions {
  /** 渲染单个块为 HTML。finalized=true 表示内容已定稿（可跑高亮/公式并缓存）。 */
  renderBlock: (source: string, finalized: boolean) => string
  /** 块内容 → HTML 的 memo（同一块跨帧复用）。 */
  cache: Map<string, string>
  /** 未闭合围栏块的降级渲染（纯文本转义）。 */
  renderFallback: (source: string) => string
}

export interface IncrementalResult {
  html: string
  /** 本帧实际调用 renderBlock 的次数（缓存命中不计）。 */
  parsedBlocks: number
  cacheHits: number
}

/**
 * 增量渲染：定稿块走缓存（命中即跳过解析），尾部块每帧重解析。
 * 返回的 html 与「逐块全量渲染」结果完全一致。
 */
export function renderStreamingContent(markdown: string, options: IncrementalOptions): IncrementalResult {
  const { finalized, tail, tailInFence } = splitFinalizedBlocks(markdown)
  const parts: string[] = []
  let parsedBlocks = 0
  let cacheHits = 0

  for (const block of finalized) {
    const cached = options.cache.get(block)
    if (cached !== undefined) {
      cacheHits++
      parts.push(cached)
      continue
    }
    const html = options.renderBlock(block, true)
    options.cache.set(block, html)
    parsedBlocks++
    parts.push(html)
  }

  if (tail.length > 0) {
    parts.push(tailInFence ? options.renderFallback(tail) : options.renderBlock(tail, false))
    parsedBlocks++
  }
  return { html: parts.join(''), parsedBlocks, cacheHits }
}

/** 单遍围栏还原（旧实现 N 次全串 replace，N = 围栏数）。 */
export function restoreFences(text: string, fences: string[]): string {
  if (fences.length === 0) return text
  return text.replace(/@@FENCE(\d+)@@/g, (_all, index: string) => fences[Number(index)] ?? '')
}
