import { mkdtempSync, readFileSync, rmSync, statSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'
import {
  DEFAULT_CONNECTION_SETTINGS,
  connectionSettingsProblem,
  normalizeConnectionSettings,
  readConnectionSettings,
  writeConnectionSettings
} from './connection-settings.js'

const dirs: string[] = []

function tempFile(): string {
  const dir = mkdtempSync(join(tmpdir(), 'haoyue-connection-'))
  dirs.push(dir)
  return join(dir, 'connection.json')
}

afterEach(() => {
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true })
})

describe('connection settings', () => {
  it('falls back to local defaults when the file is missing or corrupt', () => {
    const file = tempFile()
    expect(readConnectionSettings(file)).toEqual(DEFAULT_CONNECTION_SETTINGS)
  })

  it('normalizes untrusted values instead of trusting the file', () => {
    expect(normalizeConnectionSettings({
      mode: 'lan',
      host: '  192.168.1.20 ',
      port: 70_000,
      token: '  token-value '
    })).toEqual({ mode: 'lan', host: '192.168.1.20', port: 47_615, token: 'token-value' })
    expect(normalizeConnectionSettings({ mode: 'nonsense', host: 5 })).toEqual(DEFAULT_CONNECTION_SETTINGS)
  })

  it('round-trips settings and restricts file permissions on POSIX', () => {
    const file = tempFile()
    const settings = { mode: 'lan' as const, host: '10.0.0.8', port: 47_615, token: 'secret' }
    writeConnectionSettings(file, settings)

    expect(readConnectionSettings(file)).toEqual(settings)
    expect(JSON.parse(readFileSync(file, 'utf8'))).toMatchObject({ mode: 'lan', host: '10.0.0.8' })
    if (process.platform !== 'win32') {
      expect(statSync(file).mode & 0o777).toBe(0o600)
    }
  })

  it('requires a host and a valid port in LAN mode', () => {
    const base = { mode: 'lan' as const, host: '', port: 47_615, token: null }
    expect(connectionSettingsProblem({ ...base, host: '' })).toContain('地址')
    expect(connectionSettingsProblem({ ...base, host: 'a b' })).toContain('地址')
    expect(connectionSettingsProblem({ ...base, host: '10.0.0.8', port: 80 })).toContain('端口')
    expect(connectionSettingsProblem({ ...base, host: '10.0.0.8' })).toBeNull()
    expect(connectionSettingsProblem({ mode: 'local', host: '', port: 0, token: null })).toBeNull()
  })
})
