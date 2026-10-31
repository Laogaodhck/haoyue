import { describe, expect, it } from 'vitest'
import {
  DaemonError,
  DaemonRequestError,
  toDaemonRpcError,
  toIpcPayload,
  unwrapDaemonResult
} from './ipc.js'
import type { DaemonRpcResult } from './ipc.js'

describe('toIpcPayload', () => {
  it('converts reactive-style proxies into structured-cloneable payloads', () => {
    const reactive = new Proxy({ args: new Proxy(['-y'], {}), nested: { enabled: true } }, {})
    // This is the failure the renderer used to hit: "An object could not be cloned."
    expect(() => structuredClone(reactive)).toThrow()

    const payload = toIpcPayload(reactive)
    expect(() => structuredClone(payload)).not.toThrow()
    expect(payload).toEqual({ args: ['-y'], nested: { enabled: true } })
  })

  it('passes primitives and nullish values through untouched', () => {
    expect(toIpcPayload('create')).toBe('create')
    expect(toIpcPayload(42)).toBe(42)
    expect(toIpcPayload(null)).toBeNull()
    expect(toIpcPayload(undefined)).toBeUndefined()
  })

  it('drops undefined fields exactly like the JSON daemon protocol does', () => {
    expect(toIpcPayload({ name: 'StarLife', url: undefined })).toEqual({ name: 'StarLife' })
  })
})

describe('daemon IPC error envelope', () => {
  it('unwraps a successful result into the daemon message', () => {
    const result: DaemonRpcResult = {
      ok: true,
      message: { id: 7, event: 'result', data: '{"ok":true}' }
    }
    expect(unwrapDaemonResult(result)).toBe(result.message)
  })

  it('reconstructs a DaemonRequestError with code and method on failure', () => {
    const result: DaemonRpcResult = {
      ok: false,
      error: { code: 'notFound', message: 'session not found', method: 'session.get' }
    }
    try {
      unwrapDaemonResult(result)
      expect.unreachable('should have thrown')
    } catch (error) {
      expect(error).toBeInstanceOf(DaemonRequestError)
      expect(error).toMatchObject({
        name: 'DaemonRequestError',
        code: 'notFound',
        message: 'session not found',
        method: 'session.get'
      })
    }
  })

  it('keeps the contract code of client errors and falls back to unknown otherwise', () => {
    expect(toDaemonRpcError(new DaemonError('invalidParams', 'bad params', 'config.save'), 'config.save'))
      .toEqual({ code: 'invalidParams', message: 'bad params', method: 'config.save' })
    expect(toDaemonRpcError(new Error('socket closed'), 'chat'))
      .toEqual({ code: 'unknown', message: 'socket closed', method: 'chat' })
  })
})
