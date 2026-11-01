<script setup lang="ts">
import hljs from 'highlight.js/lib/core'
import bash from 'highlight.js/lib/languages/bash'
import csharp from 'highlight.js/lib/languages/csharp'
import css from 'highlight.js/lib/languages/css'
import diff from 'highlight.js/lib/languages/diff'
import javascript from 'highlight.js/lib/languages/javascript'
import json from 'highlight.js/lib/languages/json'
import markdown from 'highlight.js/lib/languages/markdown'
import python from 'highlight.js/lib/languages/python'
import typescript from 'highlight.js/lib/languages/typescript'
import xml from 'highlight.js/lib/languages/xml'
import katex from 'katex'
import 'katex/dist/katex.min.css'
import MarkdownIt from 'markdown-it'
import { computed, nextTick, ref, watch } from 'vue'
import { renderStreamingContent, restoreFences } from '../streaming-markdown'

const props = withDefaults(defineProps<{
  content: string
  /** True while this bubble is actively streaming: skips the expensive katex pass so
   * the reply renders as plain $…$ text until the turn finishes, then renders once. */
  streaming?: boolean
}>(), { streaming: false })

hljs.registerLanguage('bash', bash)
hljs.registerLanguage('csharp', csharp)
hljs.registerLanguage('cs', csharp)
hljs.registerLanguage('css', css)
hljs.registerLanguage('diff', diff)
hljs.registerLanguage('javascript', javascript)
hljs.registerLanguage('js', javascript)
hljs.registerLanguage('json', json)
hljs.registerLanguage('markdown', markdown)
hljs.registerLanguage('md', markdown)
hljs.registerLanguage('python', python)
hljs.registerLanguage('py', python)
hljs.registerLanguage('typescript', typescript)
hljs.registerLanguage('ts', typescript)
hljs.registerLanguage('vue', xml)
hljs.registerLanguage('html', xml)

const md = new MarkdownIt({
  html: false,
  linkify: true,
  typographer: true,
  breaks: true
})

md.renderer.rules.link_open = (tokens, index, options, _env, self) => {
  tokens[index]?.attrSet('target', '_blank')
  tokens[index]?.attrSet('rel', 'noreferrer')
  return self.renderToken(tokens, index, options)
}

md.renderer.rules.fence = (tokens, index, _options, env) => {
  const token = tokens[index]!
  const language = token.info.trim().split(/\s+/)[0] ?? ''

  if (language.toLocaleLowerCase() === 'mermaid') {
    const encoded = encodeURIComponent(token.content)
    return `<div class="mermaid-render" data-src="${encoded}"><span class="mermaid-placeholder">加载图表…</span></div>`
  }

  // While streaming, skip hljs: the whole accumulated message is re-parsed on every
  // flush window, and highlighting every fence each time is a main-thread sink for
  // long replies. The final pass (streaming=false) renders the colors once.
  const streaming = Boolean((env as { streaming?: boolean } | undefined)?.streaming)
  const highlighted = !streaming && language && hljs.getLanguage(language)
    ? hljs.highlight(token.content, { language, ignoreIllegals: true }).value
    : md.utils.escapeHtml(token.content)
  const encoded = encodeURIComponent(token.content)
  const label = language || 'text'
  return `<div class="code-block"><div class="code-header"><span>${md.utils.escapeHtml(label)}</span><button class="copy-code" data-code="${encoded}">复制</button></div><pre><code class="hljs language-${md.utils.escapeHtml(language)}">${highlighted}</code></pre></div>`
}

// ---------------------------------------------------------------- math

/** Extracts fenced blocks and inline code (math inside code must stay untouched). */
function protectCode(source: string): { text: string; fences: string[] } {
  const fences: string[] = []
  const stash = (code: string): string => {
    const token = `@@FENCE${fences.length}@@`
    fences.push(code)
    return token
  }
  // 围栏先整体取出：其中的反引号不能被后面的行内代码规则当成配对符。
  let text = source.replace(/```[\s\S]*?```|~~~[\s\S]*?~~~/g, stash)
  // 行内代码：`code` 或 ``code``。不取出的话，`$HOME` 与 `$PATH` 之间的文本
  // 会被 $…$ 规则当成一条公式。
  text = text.replace(/(`+)([^`][\s\S]*?)\1/g, stash)
  return { text, fences }
}

/** 公式渲染：返回占位后的文本与待回填的 katex HTML（局部数组，块级复用在并发下安全）。 */
function renderMath(source: string): { text: string; pending: string[] } {
  const pending: string[] = []
  let result = source
  result = result.replace(/\$\$([\s\S]+?)\$\$/g, (_all, tex: string) => {
    const token = `@@MATH${pending.length}@@`
    pending.push(katex.renderToString(tex, { displayMode: true, throwOnError: false }))
    return token
  })
  // 与 KaTeX auto-render 一致：定界符内侧不能是空白。"$5 and $10" 这类金额不该变成公式。
  result = result.replace(/(^|[^\\$])\$(?!\s)([^$\n]+?)(?<!\s)\$/g, (_all, prefix: string, tex: string) => {
    const token = `@@MATH${pending.length}@@`
    pending.push(katex.renderToString(tex, { displayMode: false, throwOnError: false }))
    return `${prefix}${token}`
  })
  return { text: result, pending }
}

function resolveMath(html: string, pending: string[]): string {
  if (pending.length === 0) return html
  return html.replace(/@@MATH(\d+)@@/g, (_all, index: string) => pending[Number(index)] ?? '')
}

/**
 * 渲染一个 markdown 单元（整篇或单个块）。finalized=false（流式尾部块）跳过
 * katex 与 hljs——两者只在块定稿时执行一次（1.1：替代旧的全篇流式降级）。
 */
function renderUnit(source: string, streaming: boolean): string {
  const { text, fences } = protectCode(source)
  const math = streaming ? { text, pending: [] as string[] } : renderMath(text)
  const html = md.render(restoreFences(math.text, fences), { streaming })
  return resolveMath(html, math.pending)
}

/** 每个气泡的定稿块 memo。条目上限兜底：长回合会积累成百上千块，不再无限增长。 */
const MaxCachedBlocks = 256
const blockCache = new Map<string, string>()

function cacheBlock(source: string, html: string): void {
  blockCache.set(source, html)
  while (blockCache.size > MaxCachedBlocks) {
    const oldest = blockCache.keys().next().value
    if (oldest === undefined) break
    blockCache.delete(oldest)
  }
}

const fallbackBlock = (source: string): string =>
  `<pre class="unclosed-fence">${md.utils.escapeHtml(source)}</pre>`

const rendered = computed(() => {
  // 定稿：全量渲染（与改造前完全一致，含公式/高亮/mermaid 布局）。
  if (!props.streaming) return renderUnit(props.content, false)
  // 流式：分块增量——定稿块命中缓存，每帧只解析尾部未定稿块。
  return renderStreamingContent(props.content, {
    renderBlock: (source, finalized) => renderUnit(source, !finalized),
    cache: blockCache,
    renderFallback: fallbackBlock,
    cacheBlock,
  }).html
})

// ---------------------------------------------------------------- mermaid

const body = ref<HTMLElement | null>(null)
let mermaidPromise: Promise<typeof import('mermaid')> | null = null
let mermaidSequence = 0

// Mermaid renders are expensive (full diagram layout per diagram). During streaming
// the markdown is re-parsed every flush window, so each window would re-run
// mermaid.render on the incomplete diagram — defer to the final full pass.
async function renderMermaid(): Promise<void> {
  if (props.streaming) return
  const container = body.value
  if (!container) return
  const nodes = Array.from(container.querySelectorAll<HTMLElement>('.mermaid-render[data-src]'))
  if (nodes.length === 0) return
  try {
    mermaidPromise ??= import('mermaid').then((mod) => {
      mod.default.initialize({ startOnLoad: false, securityLevel: 'strict', theme: 'default' })
      return mod
    })
    const mermaid = (await mermaidPromise).default
    for (const node of nodes) {
      const source = decodeURIComponent(node.dataset.src ?? '')
      if (!source) continue
      node.classList.add('is-loading')
      try {
        const id = `mermaid-${++mermaidSequence}`
        const { svg } = await mermaid.render(id, source)
        node.innerHTML = svg
      } catch {
        node.innerHTML = `<pre class="mermaid-error">${md.utils.escapeHtml(source)}</pre>`
      } finally {
        node.classList.remove('is-loading')
      }
    }
  } catch {
    // Rendering unavailable; leave placeholders as-is.
  }
}

watch(rendered, () => { void nextTick(() => void renderMermaid()) }, { immediate: true })

async function handleClick(event: MouseEvent): Promise<void> {
  const target = (event.target as HTMLElement).closest<HTMLButtonElement>('.copy-code')
  if (!target?.dataset.code) return
  await navigator.clipboard.writeText(decodeURIComponent(target.dataset.code))
  const previous = target.textContent
  target.textContent = '已复制'
  window.setTimeout(() => { target.textContent = previous }, 1500) // H20: unified feedback duration
}
</script>

<template>
  <div ref="body" class="markdown-body" @click="handleClick" v-html="rendered" />
</template>
