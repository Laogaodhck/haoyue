import { mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { openDaemonLogSink, readDaemonLogTail, redactLogSecrets } from './daemon-log.js'

describe('daemon log sink', () => {
  let dir: string

  beforeEach(() => {
    dir = mkdtempSync(join(tmpdir(), 'haoyue-daemon-log-'))
  })

  afterEach(() => {
    rmSync(dir, { recursive: true, force: true })
  })

  it('persists daemon output and exit markers across restarts', async () => {
    const first = openDaemonLogSink(dir)
    first.append('llama_kv_cache: out of memory\n')
    first.writeMarker('daemon exit code=null signal=SIGABRT')
    first.close()
    await new Promise((resolve) => setTimeout(resolve, 20))

    const second = openDaemonLogSink(dir)
    second.append('second run output\n')
    second.close()
    await new Promise((resolve) => setTimeout(resolve, 20))

    const text = readFileSync(join(dir, 'daemon.log'), 'utf-8')
    expect(text).toContain('llama_kv_cache: out of memory')
    expect(text).toContain('daemon exit code=null signal=SIGABRT')
    expect(text).toContain('second run output')
  })

  it('rotates an oversized log instead of growing without bound', async () => {
    writeFileSync(join(dir, 'daemon.log'), 'x'.repeat(9 * 1024 * 1024))
    const sink = openDaemonLogSink(dir)
    sink.append('fresh\n')
    sink.close()
    await new Promise((resolve) => setTimeout(resolve, 20))

    expect(statSync(join(dir, 'daemon.prev.log')).size).toBeGreaterThan(8 * 1024 * 1024)
    expect(readFileSync(join(dir, 'daemon.log'), 'utf-8')).toContain('fresh')
  })
})

describe('readDaemonLogTail', () => {
  let dir: string

  beforeEach(() => {
    dir = mkdtempSync(join(tmpdir(), 'haoyue-daemon-tail-'))
  })

  afterEach(() => {
    rmSync(dir, { recursive: true, force: true })
  })

  it('reports the missing file instead of throwing', () => {
    const tail = readDaemonLogTail(dir, 50)
    expect(tail.path).toBe(join(dir, 'daemon.log'))
    expect(tail.text).toBe('')
    expect(tail.totalBytes).toBe(0)
  })

  it('keeps only the requested trailing lines', () => {
    writeFileSync(join(dir, 'daemon.log'), 'a\nb\nc\nd\ne\n', 'utf-8')
    const tail = readDaemonLogTail(dir, 2)
    expect(tail.text).toBe('d\ne')
    expect(tail.skippedBytes).toBe(0)
  })

  it('drops the half line at the window boundary', () => {
    // 6000 行 × 101 字节 ≈ 590KB > 512KB 读取窗口，窗口起点必然落在行中间。
    const body = Array.from({ length: 6000 }, (_, i) => `${String(i).padStart(6, '0')}${'.'.repeat(94)}`).join('\n')
    writeFileSync(join(dir, 'daemon.log'), `${body}\n`, 'utf-8')
    const tail = readDaemonLogTail(dir, 5)
    const lines = tail.text.split('\n')
    expect(lines).toHaveLength(5)
    expect(lines.at(-1)).toMatch(/^005999\.{94}$/)
    for (const line of lines) expect(line).toMatch(/^\d{6}\.{94}$/)
    expect(tail.skippedBytes).toBeGreaterThan(0)
  })

  it('clamps an out-of-range line count rather than reading the whole file', () => {
    writeFileSync(join(dir, 'daemon.log'), 'x\n'.repeat(3000), 'utf-8')
    expect(readDaemonLogTail(dir, 100000).text.split('\n')).toHaveLength(2000)
    expect(readDaemonLogTail(dir, 0).text).toBe('x')
  })

  it('masks credential-shaped fragments on the way out', () => {
    const raw = [
      'GET https://api.openai.com/v1/models 401',
      'Authorization: Bearer sk-abcdef0123456789',
      'request body {"api_key":"supersecretvalue"} failed',
      'llama_kv_cache: out of memory'
    ].join('\n')
    writeFileSync(join(dir, 'daemon.log'), `${raw}\n`, 'utf-8')
    const tail = readDaemonLogTail(dir, 10)
    expect(tail.text).not.toContain('sk-abcdef0123456789')
    expect(tail.text).not.toContain('supersecretvalue')
    expect(tail.text).toContain('llama_kv_cache: out of memory')
    expect(redactLogSecrets('sk-abcdef0123456789')).toBe('sk-<已脱敏>')
  })
})
