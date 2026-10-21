import MarkdownIt from 'markdown-it'
import { describe, expect, it } from 'vitest'
import { renderStreamingContent, restoreFences, splitFinalizedBlocks } from './streaming-markdown'

const md = new MarkdownIt({ html: false, linkify: true, typographer: true, breaks: true })
const renderBlock = (source: string): string => md.render(source)

function incremental(markdown: string, cache = new Map<string, string>()) {
  return renderStreamingContent(markdown, {
    renderBlock,
    cache,
    renderFallback: (source) => `<pre class="unclosed-fence">${md.utils.escapeHtml(source)}</pre>`,
  })
}

describe('splitFinalizedBlocks (1.1 块边界切分)', () => {
  it('splits on blank lines and keeps the last block as tail', () => {
    const { finalized, tail, tailInFence } = splitFinalizedBlocks('第一段\n\n第二段\n\n尾部正在增长')
    expect(finalized).toEqual(['第一段', '第二段'])
    expect(tail).toBe('尾部正在增长')
    expect(tailInFence).toBe(false)
  })

  it('never splits inside a code fence', () => {
    const source = '说明\n\n```ts\nconst a = 1\n\nconst b = 2\n```\n\n后续'
    const { finalized, tail } = splitFinalizedBlocks(source)
    expect(finalized).toEqual(['说明', '```ts\nconst a = 1\n\nconst b = 2\n```'])
    expect(tail).toBe('后续')
  })

  it('keeps loose-list items in one block (blank line between items is not a boundary)', () => {
    const { finalized, tail } = splitFinalizedBlocks('- 第一项\n\n- 第二项\n\n- 第三项')
    // 列表项之间不切：整组成一块（tail），与整篇渲染的 loose list 保持一致。
    expect(finalized).toEqual([])
    expect(tail).toBe('- 第一项\n\n- 第二项\n\n- 第三项')
  })

  it('marks an unclosed fence tail so callers can degrade to plain text', () => {
    const { tail, tailInFence, finalized } = splitFinalizedBlocks('前言\n\n```ts\nconst x = 1')
    expect(finalized).toEqual(['前言'])
    expect(tailInFence).toBe(true)
    expect(tail).toContain('const x = 1')
  })

  it('handles empty input and whitespace-only input', () => {
    expect(splitFinalizedBlocks('')).toEqual({ finalized: [], tail: '', tailInFence: false })
    expect(splitFinalizedBlocks('  \n\n \n').finalized).toEqual([])
  })

  it('treats a trailing blank line as end-of-last-block (tail becomes the real last block)', () => {
    const { finalized, tail } = splitFinalizedBlocks('第一段\n\n第二段\n\n')
    expect(finalized).toEqual(['第一段'])
    expect(tail).toBe('第二段')
  })
})

describe('renderStreamingContent (1.1 增量渲染)', () => {
  it('output equals per-block full render (no content lost, no reordering)', () => {
    const source = '# 标题\n\n第一段 **加粗**\n\n- 甲\n- 乙\n\n```js\nconsole.log(1)\n```\n\n最后一段'
    const result = incremental(source)
    const blocks = splitFinalizedBlocks(source)
    const expected = [...blocks.finalized, blocks.tail].filter((b) => b.length > 0).map(renderBlock).join('')
    expect(result.html).toBe(expected)
    expect(result.parsedBlocks).toBe(expected.length > 0 ? [blocks.finalized.length, 1].reduce((a, b) => a + b, 0) : 0)
  })

  it('reuses cached finalized blocks across frames (O(增量) 单帧解析)', () => {
    const cache = new Map<string, string>()
    const base = '第一段内容\n\n第二段内容\n\n第三段内容\n\n'
    const first = incremental(base, cache)
    expect(first.cacheHits).toBe(0)
    expect(first.parsedBlocks).toBe(3) // 前两段定稿 + 尾块

    // 追加 delta：前两段命中缓存；第三段从尾块转为定稿（首次解析，此前不缓存），
    // 新的尾块再解析一次——单帧最多 2 次块解析，与全篇规模无关。
    const second = incremental(base + '不断增长的第四段', cache)
    expect(second.cacheHits).toBe(2)
    expect(second.parsedBlocks).toBe(2)
    expect(second.html).toContain('不断增长的第四段')
  })

  it('degrades an unclosed fence tail to escaped plain text', () => {
    const result = incremental('前言\n\n```ts\nconst danger = "<script>"')
    expect(result.html).toContain('<pre class="unclosed-fence">')
    expect(result.html).toContain('&lt;script&gt;')
    // 定稿块照常渲染。
    expect(result.html).toContain('<p>前言</p>')
  })

  it('keeps 30k-char stream frames within budget: one tail block parsed per frame', () => {
    // 性能打点（宽松阈值，防 O(n²) 回归）：30k 字首帧全解析，随后每帧只解析尾部块。
    const paragraphs = Array.from({ length: 300 }, (_, i) => `第 ${i} 段 ` + '内容'.repeat(60))
    const source = paragraphs.join('\n\n')
    expect(source.length).toBeGreaterThan(25_000)

    const cache = new Map<string, string>()
    const coldStart = performance.now()
    const first = incremental(source, cache)
    const coldMs = performance.now() - coldStart
    expect(first.parsedBlocks).toBeGreaterThan(100)

    const warmStart = performance.now()
    const second = incremental(source + '\n\n增量尾巴', cache)
    const warmMs = performance.now() - warmStart
    // 热帧最多解析 2 块（原尾块转为定稿 + 新尾块），与全篇规模无关。
    expect(second.parsedBlocks).toBeLessThanOrEqual(2)
    expect(second.cacheHits).toBeGreaterThan(290)
    // 热帧应该显著快于冷启动整篇解析（宽松 3 倍关系，避免 CI 抖动误报）。
    expect(warmMs).toBeLessThan(Math.max(8, coldMs))
    expect(warmMs).toBeLessThan(50)
  })
})

describe('restoreFences (单遍还原)', () => {
  it('restores every placeholder in one pass, including duplicates of the same index', () => {
    expect(restoreFences('a @@FENCE0@@ b @@FENCE1@@ c @@FENCE0@@', ['```x```', '```y```']))
      .toBe('a ```x``` b ```y``` c ```x```')
  })

  it('is a no-op when there are no fences', () => {
    expect(restoreFences('plain text', [])).toBe('plain text')
  })
})
