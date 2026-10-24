import { describe, expect, it } from 'vitest'
import {
  checkBrowserUrl,
  evaluateUrl,
  parseRule,
  withScheme,
  type AllowlistSource,
  type WebRule
} from './web-guard.js'

/** 假 daemon：只回答 web.allowedGet，用于验证白名单链路。 */
function sourceWith(allowedSites: string[]): AllowlistSource {
  return {
    request: (method) => {
      if (method !== 'web.allowedGet') throw new Error(`unexpected method: ${method}`)
      return Promise.resolve({ data: JSON.stringify({ allowedSites, mode: 'allowlist' }) })
    }
  }
}

function rules(entries: string[]): WebRule[] {
  return entries.map(parseRule).filter((rule): rule is WebRule => rule !== null)
}

describe('withScheme', () => {
  it('treats host:port input as a local http address instead of a scheme', () => {
    expect(withScheme('localhost:5173')).toBe('http://localhost:5173')
    expect(withScheme('127.0.0.1:8080/app')).toBe('http://127.0.0.1:8080/app')
  })

  it('defaults bare host names to https and keeps explicit schemes untouched', () => {
    expect(withScheme('example.com')).toBe('https://example.com')
    expect(withScheme('http://example.com')).toBe('http://example.com')
  })
})

describe('parseRule', () => {
  it('keeps scheme, wildcard host and port and drops malformed entries', () => {
    expect(parseRule('https://*.example.com')).toEqual({ scheme: 'https', host: '*.example.com', port: null })
    expect(parseRule('https://example.com:8443/x')).toEqual({ scheme: 'https', host: 'example.com', port: '8443' })
    expect(parseRule('example.com')).toBeNull()
    expect(parseRule('ftp://example.com')).toBeNull()
    expect(parseRule('https://example.com:*')).toBeNull()
  })
})

describe('evaluateUrl', () => {
  it('allows everything while the allowlist is empty', () => {
    expect(evaluateUrl('https://anywhere.example/x', []).allowed).toBe(true)
  })

  it('always allows localhost but denies unlisted external origins', () => {
    const list = rules(['https://example.com'])
    expect(evaluateUrl('http://localhost:5173/index.html', list).allowed).toBe(true)
    const denied = evaluateUrl('https://other.example/x', list)
    expect(denied.allowed).toBe(false)
    expect(denied.reason).toContain('白名单限制')
  })

  it('matches subdomains for "*.domain" rules without matching the apex', () => {
    const list = rules(['https://*.example.com'])
    expect(evaluateUrl('https://docs.example.com/guide', list).allowed).toBe(true)
    expect(evaluateUrl('https://example.com/', list).allowed).toBe(false)
  })
})

describe('checkBrowserUrl', () => {
  it('normalizes the address and reports the whitelist verdict', async () => {
    const source = sourceWith(['https://example.com'])
    await expect(checkBrowserUrl(source, 'example.com')).resolves.toEqual({
      ok: true,
      url: 'https://example.com/'
    })
    const denied = await checkBrowserUrl(source, 'https://blocked.example')
    expect(denied.ok).toBe(false)
    expect(denied.reason).toContain('白名单限制')
  })

  it('rejects non-http schemes and falls back to unrestricted when the daemon is unreachable', async () => {
    const source = sourceWith([])
    const raw = await checkBrowserUrl(source, 'file:///etc/passwd')
    expect(raw.ok).toBe(false)

    const offline: AllowlistSource = { request: () => Promise.reject(new Error('daemon down')) }
    await expect(checkBrowserUrl(offline, 'localhost:3000')).resolves.toEqual({
      ok: true,
      url: 'http://localhost:3000/'
    })
  })
})
