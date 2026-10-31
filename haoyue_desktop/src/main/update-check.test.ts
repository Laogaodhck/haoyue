import { describe, expect, it, vi } from 'vitest'
import {
  RELEASES_PAGE,
  checkForUpdate,
  compareVersions,
  isNewerVersion,
  normalizeVersion
} from './update-check.js'

const jsonResponse = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' }
  })

describe('compareVersions', () => {
  it('orders patch, minor and major bumps', () => {
    expect(compareVersions('1.3.30', '1.3.29')).toBeGreaterThan(0)
    expect(compareVersions('1.4.0', '1.3.29')).toBeGreaterThan(0)
    expect(compareVersions('2.0.0', '1.99.99')).toBeGreaterThan(0)
    expect(compareVersions('1.3.29', '1.3.29')).toBe(0)
    expect(compareVersions('v1.3.29', '1.3.29')).toBe(0)
    expect(compareVersions('1.3.28', '1.3.29')).toBeLessThan(0)
  })

  it('treats prereleases as older than the matching stable version', () => {
    expect(compareVersions('1.4.0-beta.1', '1.4.0')).toBeLessThan(0)
    expect(compareVersions('1.4.0', '1.4.0-beta.1')).toBeGreaterThan(0)
  })
})

describe('isNewerVersion / normalizeVersion', () => {
  it('ignores the v prefix', () => {
    expect(normalizeVersion(' v1.3.29 ')).toBe('1.3.29')
    expect(isNewerVersion('v1.3.30', '1.3.29')).toBe(true)
    expect(isNewerVersion('v1.3.29', '1.3.29')).toBe(false)
  })
})

describe('checkForUpdate', () => {
  it('reports an update with the release page url', async () => {
    const fetchImpl = vi.fn(async () =>
      jsonResponse({ tag_name: 'v1.4.0', html_url: 'https://github.com/Laogaodhck/haoyue/releases/tag/v1.4.0' })
    ) as unknown as typeof fetch

    const result = await checkForUpdate({ currentVersion: '1.3.29', fetchImpl })

    expect(result).toEqual({
      status: 'update',
      version: '1.4.0',
      url: 'https://github.com/Laogaodhck/haoyue/releases/tag/v1.4.0'
    })
    expect(fetchImpl).toHaveBeenCalledWith(
      'https://api.github.com/repos/Laogaodhck/haoyue/releases/latest',
      expect.objectContaining({ headers: expect.objectContaining({ 'User-Agent': 'Haoyue-Desktop' }) })
    )
  })

  it('reports latest when the release is not newer', async () => {
    const fetchImpl = vi.fn(async () => jsonResponse({ tag_name: 'v1.3.29' })) as unknown as typeof fetch

    await expect(checkForUpdate({ currentVersion: '1.3.29', fetchImpl })).resolves.toEqual({
      status: 'latest',
      version: '1.3.29',
      url: RELEASES_PAGE
    })
  })

  it('rejects non-numeric tags instead of guessing a version', async () => {
    const fetchImpl = vi.fn(async () =>
      jsonResponse({ tag_name: 'nightly', html_url: 'https://evil.example/download' })
    ) as unknown as typeof fetch

    await expect(checkForUpdate({ currentVersion: '1.3.29', fetchImpl })).resolves.toEqual({
      status: 'unavailable',
      error: '版本号无法解析'
    })
  })

  it('degrades silently on HTTP errors and network failures', async () => {
    const notFound = vi.fn(async () => jsonResponse({ message: 'Not Found' }, 404)) as unknown as typeof fetch
    await expect(checkForUpdate({ currentVersion: '1.3.29', fetchImpl: notFound })).resolves.toEqual({
      status: 'unavailable',
      error: 'HTTP 404'
    })

    const offline = vi.fn(async () => {
      throw new Error('offline')
    }) as unknown as typeof fetch
    await expect(checkForUpdate({ currentVersion: '1.3.29', fetchImpl: offline })).resolves.toEqual({
      status: 'unavailable',
      error: 'offline'
    })
  })
})
