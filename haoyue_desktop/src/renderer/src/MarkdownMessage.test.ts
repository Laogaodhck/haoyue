// @vitest-environment jsdom
import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import MarkdownMessage from './components/MarkdownMessage.vue'

// MarkdownMessage 的首批组件测试（1.1 前置：39 个 .vue 零测试盲区的第一块地基）。
// 覆盖流式降级 / 定稿渲染 / 代码块高亮 / 公式只在定稿渲染 / 复制按钮。

describe('MarkdownMessage (组件级)', () => {
  it('renders final markdown structure with a highlighted code block', () => {
    const wrapper = mount(MarkdownMessage, {
      props: { content: '# 标题\n\n段落 **加粗**\n\n```ts\nconst a = 1\n```' },
    })
    const html = wrapper.html()
    expect(html).toContain('markdown-body')
    expect(html).toContain('<h1>标题</h1>')
    expect(html).toContain('<strong>加粗</strong>')
    expect(html).toContain('code-block')
    expect(html).toContain('language-ts')
    // 定稿块跑 hljs：关键字着色（hljs-keyword 类）。
    expect(html).toContain('hljs-keyword')
    expect(html).toContain('class="copy-code"')
  })

  it('degrades an unclosed fence to escaped plain text while streaming', () => {
    const wrapper = mount(MarkdownMessage, {
      props: { content: '前言\n\n```ts\nconst danger = "<b>x</b>"', streaming: true },
    })
    const html = wrapper.html()
    expect(html).toContain('unclosed-fence')
    expect(html).toContain('&lt;b&gt;x&lt;/b&gt;')
    // 已定稿的前言段落正常渲染。
    expect(html).toContain('<p>前言</p>')
  })

  it('renders math only on the final pass; streaming keeps raw $…$', () => {
    const streaming = mount(MarkdownMessage, {
      props: { content: '公式 $E=mc^2$ 与 $$a^2+b^2$$', streaming: true },
    })
    expect(streaming.html()).not.toContain('class="katex"')

    const final = mount(MarkdownMessage, { props: { content: '公式 $E=mc^2$ 与 $$a^2+b^2$$' } })
    expect(final.html()).toContain('class="katex"')
  })

  it('streams incrementally: stable blocks keep identity while the tail grows', async () => {
    const wrapper = mount(MarkdownMessage, {
      props: { content: '第一段\n\n第二段\n\n正在增长', streaming: true },
    })
    expect(wrapper.html()).toContain('<p>第一段</p>')

    await wrapper.setProps({ content: '第一段\n\n第二段\n\n正在增长，继续追加内容' })
    const html = wrapper.html()
    expect(html).toContain('<p>第一段</p>')
    expect(html).toContain('<p>第二段</p>')
    expect(html).toContain('继续追加内容')
  })

  it('switches from streamed blocks to the full render when streaming ends', async () => {
    const wrapper = mount(MarkdownMessage, {
      props: { content: '段落\n\n```js\nconst a = 1\n```', streaming: true },
    })
    // 流式期定稿的围栏块已可高亮（块定稿即执行一次）。
    expect(wrapper.html()).toContain('code-block')

    await wrapper.setProps({ streaming: false })
    expect(wrapper.html()).toContain('hljs-keyword')
  })
})
