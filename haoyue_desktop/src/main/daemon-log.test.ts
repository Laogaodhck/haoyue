import { mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { openDaemonLogSink } from './daemon-log.js'

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
