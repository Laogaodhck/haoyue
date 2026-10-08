// 跨语言契约一致性测试：TS 侧生成类型必须与 C# 导出的契约快照逐项一致。
// 快照由 haoyue_tests（HAOYUE_UPDATE_CONTRACT=1）生成并提交到 contracts/daemon-contract.json。
import { readFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import {
  DAEMON_BROADCAST_EVENTS,
  DAEMON_ERROR_CODES,
  DAEMON_EVENT_NAMES,
  DAEMON_METHODS,
  DAEMON_STREAM_EVENTS,
  DAEMON_TERMINAL_EVENTS,
} from '../shared/daemon-contract.gen.js'
import { DaemonError, errorCodeOf, type DaemonMessage } from '../shared/ipc.js'
import { TERMINAL_EVENTS } from './daemon-client.js'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..', '..')
const contract = JSON.parse(
  readFileSync(resolve(repoRoot, 'contracts', 'daemon-contract.json'), 'utf8')
) as {
  errorCodes: string[]
  events: { stream: string[]; terminal: string[]; broadcast: string[] }
  methods: Array<{ name: string; terminalEvents: string[] }>
}

const asSet = (values: readonly string[]): Set<string> => new Set(values)

describe('daemon contract 一致性', () => {
  it('错误码与契约快照一致', () => {
    expect([...DAEMON_ERROR_CODES]).toEqual(contract.errorCodes)
  })

  it('事件名与契约快照一致', () => {
    expect([...DAEMON_STREAM_EVENTS]).toEqual(contract.events.stream)
    expect([...DAEMON_TERMINAL_EVENTS]).toEqual(contract.events.terminal)
    expect([...DAEMON_BROADCAST_EVENTS]).toEqual(contract.events.broadcast)
    expect([...DAEMON_EVENT_NAMES]).toEqual([
      ...contract.events.stream,
      ...contract.events.terminal,
      ...contract.events.broadcast,
    ])
  })

  it('方法清单与契约快照一致', () => {
    expect([...DAEMON_METHODS]).toEqual(contract.methods.map((method) => method.name))
    // 终态事件必须落在 terminal 事件集合内。
    for (const method of contract.methods) {
      for (const event of method.terminalEvents) {
        expect(asSet(DAEMON_TERMINAL_EVENTS).has(event), `${method.name} -> ${event}`).toBe(true)
      }
    }
  })

  it('daemon-client 终态集合与契约 terminal 事件一致', () => {
    expect([...TERMINAL_EVENTS].sort()).toEqual([...asSet(DAEMON_TERMINAL_EVENTS)].sort())
  })
})

describe('errorCodeOf / DaemonError', () => {
  const message = (details?: Record<string, unknown>): DaemonMessage =>
    ({ id: 1, event: 'error', data: 'boom', details }) as DaemonMessage

  it('识别契约错误码', () => {
    expect(errorCodeOf(message({ code: 'unknownMethod' }))).toBe('unknownMethod')
    expect(errorCodeOf(message({ code: 'invalidParams' }))).toBe('invalidParams')
  })

  it('缺失或未知 code 回退为 unknown（兼容旧版 daemon）', () => {
    expect(errorCodeOf(message())).toBe('unknown')
    expect(errorCodeOf(message({ code: 'no-such-code' }))).toBe('unknown')
    expect(errorCodeOf(message({ code: 42 }))).toBe('unknown')
  })

  it('DaemonError 保留 code 与 method，message 仅用于展示', () => {
    const error = new DaemonError('conflict', 'A turn is already active', 'chat')
    expect(error).toBeInstanceOf(Error)
    expect(error.name).toBe('DaemonError')
    expect(error.code).toBe('conflict')
    expect(error.method).toBe('chat')
    expect(error.message).toBe('A turn is already active')
  })
})
