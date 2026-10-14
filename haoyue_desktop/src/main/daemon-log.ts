import { createWriteStream, existsSync, mkdirSync, renameSync, statSync, type WriteStream } from 'node:fs'
import { join } from 'node:path'

const maxLogBytes = 8 * 1024 * 1024

export interface DaemonLogSink {
  append(chunk: string): void
  writeMarker(line: string): void
  close(): void
}

const silentSink: DaemonLogSink = {
  append: () => {},
  writeMarker: () => {},
  close: () => {}
}

/**
 * 把 daemon 的原生输出落盘。此前 stdout 被直接丢弃、stderr 只在 Electron 主进程内存里
 * 留 4000 字符尾巴，进程一旦被 llama.cpp 的显存断言 abort，就再没有证据可查——「发消息
 * 没有回复」这类问题只能靠猜。
 */
export function openDaemonLogSink(logDir: string): DaemonLogSink {
  let stream: WriteStream
  try {
    mkdirSync(logDir, { recursive: true })
    const path = join(logDir, 'daemon.log')
    if (existsSync(path) && statSync(path).size > maxLogBytes)
      renameSync(path, join(logDir, 'daemon.prev.log'))
    stream = createWriteStream(path, { flags: 'a' })
  } catch {
    return silentSink
  }
  stream.write(`\n===== daemon spawn ${new Date().toISOString()} =====\n`)
  return {
    append: (chunk) => {
      if (stream.writable) stream.write(chunk)
    },
    writeMarker: (line) => {
      if (stream.writable) stream.write(`\n----- ${line} -----\n`)
    },
    close: () => stream.end()
  }
}
