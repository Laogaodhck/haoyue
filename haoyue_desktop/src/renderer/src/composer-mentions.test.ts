import { describe, expect, it } from 'vitest'
import { applyMention, mentionToken } from './composer-mentions'

describe('mentionToken', () => {
  it('recognises an @ token at the caret', () => {
    const text = '看看 @src/App'
    expect(mentionToken(text, text.length)).toEqual({ query: 'src/app', start: 3 })
  })

  it('accepts a token opened after a CJK separator or line start', () => {
    expect(mentionToken('@Com', 4)).toEqual({ query: 'com', start: 0 })
    const text = '改一下，@readme'
    expect(mentionToken(text, text.length)).toEqual({ query: 'readme', start: 4 })
  })

  it('ignores @ inside emails, URLs and git refs', () => {
    for (const text of ['mail me@example.com', 'see https://github.com/haoyue/haoyue', 'cherry-pick abc@HEAD']) {
      expect(mentionToken(text, text.length)).toBeNull()
    }
  })

  it('closes the token once whitespace follows the @', () => {
    expect(mentionToken('@src/App.cs 里的问题', 15)).toBeNull()
  })

  it('uses only the text before the caret', () => {
    const text = '@a then @b'
    expect(mentionToken(text, 2)).toEqual({ query: 'a', start: 0 })
  })
})

describe('applyMention', () => {
  it('replaces the half-typed token and keeps the existing space', () => {
    const next = applyMention('看看 @src/App 的实现', 11, 3, 'src/App.cs')
    expect(next.text).toBe('看看 @src/App.cs 的实现')
    expect(next.caret).toBe(14)
  })

  it('appends a separator space when the token ends the text', () => {
    const next = applyMention('@read', 5, 0, 'README.md')
    expect(next.text).toBe('@README.md ')
    expect(next.caret).toBe(11)
  })

  it('replaces only the token span, leaving the rest untouched', () => {
    const next = applyMention('fix @ap please', 7, 4, 'App.vue')
    expect(next.text).toBe('fix @App.vue please')
    expect(next.caret).toBe(12)
  })
})
