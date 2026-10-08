#!/usr/bin/env node
// Contract First：从 C# 侧落盘的契约快照（contracts/daemon-contract.json）生成 TS 类型。
// 快照由 haoyue_tests 的 Contract_Export_MatchesSnapshot 测试以 HAOYUE_UPDATE_CONTRACT=1 生成。
// 用法：pnpm sync:contract（或 node scripts/generate-contract.mjs [快照路径]）
import { readFileSync, writeFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'

const desktopRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const snapshotPath =
  process.argv[2] ?? resolve(desktopRoot, '..', 'contracts', 'daemon-contract.json')
const outPath = resolve(desktopRoot, 'src', 'shared', 'daemon-contract.gen.ts')

const contract = JSON.parse(readFileSync(snapshotPath, 'utf8'))
if (contract.title !== 'Haoyue Daemon Contract') {
  throw new Error(`不是 Haoyue 契约快照: ${snapshotPath}`)
}

const allEvents = [...contract.events.stream, ...contract.events.terminal, ...contract.events.broadcast]

const lines = [
  '// 由 scripts/generate-contract.mjs 从 contracts/daemon-contract.json 生成，禁止手工编辑。',
  `// 契约版本 ${contract.contractVersion}（协议 ${contract.protocolVersion}）。重新生成：pnpm sync:contract`,
  '',
  `export const DAEMON_PROTOCOL_VERSION = '${contract.protocolVersion}' as const`,
  `export const DAEMON_CONTRACT_VERSION = '${contract.contractVersion}' as const`,
  '',
  'export const DAEMON_ERROR_CODES = [',
  ...contract.errorCodes.map((code) => `  '${code}',`),
  '] as const',
  '',
  '/** daemon error 事件 details.code 的契约取值；旧版 daemon 未携带时回退为 unknown。 */',
  "export type DaemonErrorCode = (typeof DAEMON_ERROR_CODES)[number] | 'unknown'",
  '',
  'export const DAEMON_STREAM_EVENTS = [',
  ...contract.events.stream.map((event) => `  '${event}',`),
  '] as const',
  '',
  'export const DAEMON_TERMINAL_EVENTS = [',
  ...contract.events.terminal.map((event) => `  '${event}',`),
  '] as const',
  '',
  'export const DAEMON_BROADCAST_EVENTS = [',
  ...contract.events.broadcast.map((event) => `  '${event}',`),
  '] as const',
  '',
  'export const DAEMON_EVENT_NAMES = [',
  ...allEvents.map((event) => `  '${event}',`),
  '] as const',
  '',
  'export type DaemonEventName = (typeof DAEMON_EVENT_NAMES)[number]',
  '',
  'export const DAEMON_METHODS = [',
  ...contract.methods.map((method) => `  '${method.name}',`),
  '] as const',
  '',
  'export type DaemonMethod = (typeof DAEMON_METHODS)[number]',
  '',
]

writeFileSync(outPath, lines.join('\n'))
console.log(
  `generated src/shared/daemon-contract.gen.ts (${contract.methods.length} methods, ${contract.errorCodes.length} error codes, ${allEvents.length} events)`
)
