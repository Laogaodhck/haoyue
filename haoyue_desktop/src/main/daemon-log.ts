import { closeSync, createWriteStream, existsSync, fstatSync, mkdirSync, openSync, readSync, renameSync, statSync, type WriteStream } from 'node:fs'
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

/** 一次最多读入的尾部字节窗口。诊断页只要最近几百行，整文件读进渲染进程会白白搬 8MB。 */
const tailWindowBytes = 512 * 1024
const maxTailLines = 2000

/**
 * 服务商报错原文有时会带上密钥片段（401 响应体、curl 调试行）。日志本身只落在本机
 * 用户目录，但 UI 展示与「导出诊断包」是外发链路，出口处统一抹一遍。
 */
export function redactLogSecrets(text: string): string {
  return text
    // JSON 里键与冒号之间还有一层引号（"api_key":"...")，两类写法都要覆盖。
    .replace(/(api[_-]?key|authorization|proxy[_-]?authorization|access[_-]?token|refresh[_-]?token|client[_-]?secret|password|secret)\s*"?\s*[=:]\s*"?\s*[^\s"',;}\]]+/gi, (_m, key: string) => `${key}=<已脱敏>`)
    .replace(/\b(Bearer|Basic)\s+[A-Za-z0-9+/=._-]{8,}/gi, '$1 <已脱敏>')
    .replace(/\bsk-[A-Za-z0-9_-]{12,}/g, 'sk-<已脱敏>')
}

export interface DaemonLogTail {
  /** 日志文件绝对路径；文件不存在时为期望路径，`text` 为空。 */
  path: string
  text: string
  /** 文件总字节数（用于说明窗口外的内容被跳过了）。 */
  totalBytes: number
  /** 本次窗口起点之前被跳过的字节数。 */
  skippedBytes: number
  error?: string
}

/** 读取 daemon.log 末尾若干行（脱敏后）。纯同步：调用方是 IPC handler，文件上限 8MB。 */
export function readDaemonLogTail(logDir: string, lines = 400): DaemonLogTail {
  const path = join(logDir, 'daemon.log')
  const wanted = Math.max(1, Math.min(Math.trunc(lines) || 1, maxTailLines))
  if (!existsSync(path)) return { path, text: '', totalBytes: 0, skippedBytes: 0 }

  let totalBytes = 0
  let body = ''
  let skippedBytes = 0
  let descriptor: number | null = null
  try {
    totalBytes = statSync(path).size
    descriptor = openSync(path, 'r')
    const size = fstatSync(descriptor).size
    const start = Math.max(0, size - tailWindowBytes)
    if (start > 0) {
      // 窗口边界多半落在半行上，丢掉那半行以免拼出错误文本。
      const probe = Buffer.alloc(1)
      let offset = start
      while (offset > 0) {
        readSync(descriptor, probe, 0, 1, offset - 1)
        if (probe[0] === 0x0a) break
        offset -= 1
      }
      skippedBytes = offset
      body = readRange(descriptor, offset, size - offset)
    } else {
      skippedBytes = 0
      body = readRange(descriptor, 0, size)
    }
  } catch (error) {
    return {
      path,
      text: '',
      totalBytes,
      skippedBytes: 0,
      error: error instanceof Error ? error.message : String(error)
    }
  } finally {
    if (descriptor !== null) closeSync(descriptor)
  }

  const rows = body.split(/\r?\n/)
  // 文件通常以换行收尾，切分会多出一个空行；不丢掉它就会少显示一行。
  if (rows.length > 1 && rows[rows.length - 1] === '') rows.pop()
  const kept = rows.slice(-wanted)
  return {
    path,
    text: redactLogSecrets(kept.join('\n')).trim(),
    totalBytes,
    skippedBytes
  }
}

function readRange(descriptor: number, position: number, length: number): string {
  const buffer = Buffer.alloc(length)
  let read = 0
  while (read < length) {
    const chunk = readSync(descriptor, buffer, read, length - read, position + read)
    if (chunk <= 0) break
    read += chunk
  }
  return buffer.subarray(0, read).toString('utf-8')
}
