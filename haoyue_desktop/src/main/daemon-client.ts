import { EventEmitter } from 'node:events'
import { readFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'
import { createConnection, type Socket } from 'node:net'
import type { DaemonMessage, DaemonRequestOptions, DaemonState } from '../shared/ipc.js'
import { DaemonError, errorCodeOf } from '../shared/ipc.js'
import { DAEMON_PROTOCOL_VERSION } from '../shared/daemon-contract.gen.js'

interface PendingRequest {
  method: string
  resolve: (message: DaemonMessage) => void
  reject: (error: Error) => void
  /** Safety net that rejects the request if the daemon never answers at all. */
  idleTimer?: NodeJS.Timeout
}

// 与契约的 terminal 事件保持一致（daemon-contract.test.ts 双向校验）。
export const TERMINAL_EVENTS = new Set(['pong', 'result', 'done', 'cancelled', 'error', 'bye'])

/** How long the daemon gets to answer the mandatory handshake. */
const HANDSHAKE_TIMEOUT_MS = 5000

/**
 * The daemon requires a handshake token as the first message of every
 * connection. It lives next to the socket in ~/.haoyue/daemon.token and is
 * readable only by the current user; a missing file simply means the daemon
 * has not been started with the current version yet.
 */
export function readDaemonToken(): string | null {
  try {
    return readFileSync(join(homedir(), '.haoyue', 'daemon.token'), 'utf8').trim() || null
  } catch {
    return null
  }
}

export class DaemonClient extends EventEmitter {
  private socket: Socket | null = null
  private bufferChunks: string[] = []
  private nextId = 1
  private connecting: Promise<DaemonState> | null = null
  private readonly pending = new Map<number, PendingRequest>()
  // Generation counter: disconnect() bumps it so an in-flight connect() can no
  // longer adopt its socket after the handshake — previously disconnect() during
  // the connect window was silently overridden by a "connected" state.
  private generation = 0

  constructor(readonly endpoint = process.platform === 'win32'
    ? String.raw`\\.\pipe\haoyue`
    : join(homedir(), '.haoyue', 'daemon.sock')) {
    super()
  }

  get state(): DaemonState {
    return { connected: this.socket?.readyState === 'open', endpoint: this.endpoint }
  }

  async connect(): Promise<DaemonState> {
    if (this.socket?.readyState === 'open') return this.state
    if (this.connecting) return this.connecting

    this.connecting = new Promise<DaemonState>((resolve) => {
      const epoch = this.generation
      const socket = createConnection(this.endpoint)
      let settled = false

      let timer: NodeJS.Timeout | null = setTimeout(() => {
        timer = null
        try {
          socket.destroy()
        } catch {}
        finish({ connected: false, endpoint: this.endpoint, error: 'Connection timed out' })
      }, 2500)

      const finish = (state: DaemonState): void => {
        if (settled) return
        settled = true
        if (timer) {
          clearTimeout(timer)
          timer = null
        }
        this.connecting = null
        this.emit('state', state)
        resolve(state)
      }

      socket.setEncoding('utf8')
      socket.once('connect', async () => {
        if (epoch !== this.generation) {
          socket.destroy()
          finish({ connected: false, endpoint: this.endpoint, error: 'Connection cancelled' })
          return
        }
        try {
          await this.authenticate(socket)
          if (epoch !== this.generation) {
            socket.destroy()
            finish({ connected: false, endpoint: this.endpoint, error: 'Connection cancelled' })
            return
          }
          this.socket = socket
          this.bindSocket(socket)
          finish({ connected: true, endpoint: this.endpoint })
        } catch (error) {
          socket.destroy()
          finish({
            connected: false,
            endpoint: this.endpoint,
            error: (error as Error).message
          })
        }
      })
      socket.once('error', (error) => {
        finish({ connected: false, endpoint: this.endpoint, error: error.message })
      })
    })

    return this.connecting
  }

  /**
   * Sends {"method":"handshake"} carrying the shared token and waits for the
   * daemon's verdict. Runs before the regular message pump is bound; any bytes
   * arriving after the handshake reply are pushed back onto the socket so the
   * normal pipeline sees them in order.
   */
  private authenticate(socket: Socket): Promise<void> {
    return new Promise((resolve, reject) => {
      const token = readDaemonToken()
      let buffer = ''
      let timer: NodeJS.Timeout | null = setTimeout(() => {
        timer = null
        socket.removeListener('data', onData)
        reject(new Error('Daemon handshake timed out'))
      }, HANDSHAKE_TIMEOUT_MS)

      const onData = (chunk: string): void => {
        buffer += chunk
        const newline = buffer.indexOf('\n')
        if (newline < 0) return
        if (timer) {
          clearTimeout(timer)
          timer = null
        }
        socket.removeListener('data', onData)
        const rest = buffer.slice(newline + 1)
        if (rest) socket.unshift(rest)
        try {
          const response = JSON.parse(buffer.slice(0, newline).trim()) as {
            id: number
            event: string
            data: string
          }
          if (response.id === 0 && response.event === 'result') {
            // 版本契约：daemon 回报其协议版本，主版本不一致视为 Breaking Change，
            // 客户端拒绝继续（daemon 侧也会对客户端声明做同样校验，此处是双保险）。
            let info: { version?: string; versionWarning?: string } = {}
            try {
              info = JSON.parse(response.data) as typeof info
            } catch { /* 版本信息缺失：旧 daemon，跳过校验 */ }
            if (info.version) {
              const daemonMajor = info.version.split('.')[0]
              const clientMajor = DAEMON_PROTOCOL_VERSION.split('.')[0]
              if (daemonMajor !== clientMajor) {
                reject(
                  new DaemonError(
                    'versionMismatch',
                    `协议版本不兼容（Breaking Change）：daemon=${info.version}，desktop=${DAEMON_PROTOCOL_VERSION}。请升级 haoyue desktop 或 daemon。`,
                  ),
                )
                return
              }
              if (info.versionWarning) console.warn(`[daemon] ${info.versionWarning}`)
            }
            resolve()
          } else {
            reject(new DaemonError('unknown', `Daemon authentication failed: ${response.data || 'invalid token'}`))
          }
        } catch {
          reject(new Error('Invalid daemon handshake response'))
        }
      }

      socket.on('data', onData)
      socket.write(`${JSON.stringify({ id: 0, method: 'handshake', params: { token, protocolVersion: DAEMON_PROTOCOL_VERSION } })}\n`)
    })
  }

  disconnect(): void {
    // Invalidate any in-flight connect() attempt before tearing down the socket.
    this.generation++
    this.socket?.destroy()
    this.socket = null
    this.bufferChunks = []
    this.rejectPending(new Error('Daemon disconnected'))
    this.emit('state', { connected: false, endpoint: this.endpoint } satisfies DaemonState)
  }

  async request(
    method: string,
    params: Record<string, unknown> = {},
    options: DaemonRequestOptions = {}
  ): Promise<DaemonMessage> {
    const state = await this.connect()
    if (!state.connected || !this.socket)
      throw new Error(state.error ?? `Unable to connect to ${this.endpoint}`)

    const id = this.nextId++
    // Guard against 0/negative timeouts that would fail the request instantly.
    const timeoutMs = Math.max(1_000, options.timeoutMs ?? 60_000)
    return new Promise<DaemonMessage>((resolve, reject) => {
      const pending: PendingRequest = { method, resolve, reject }
      this.pending.set(id, pending)
      // A request that never produces any event (for example a chat turn the
      // daemon never started) must not leave the UI waiting forever. Any first
      // event or terminal response clears the timer; the turn is then confirmed
      // to be running and may legitimately take minutes.
      pending.idleTimer = setTimeout(() => {
        if (this.pending.delete(id) === false) return
        reject(new Error(
          `请求超时：Runtime 在 ${Math.round(timeoutMs / 1000)} 秒内未响应 (${method})`))
      }, timeoutMs)

      this.socket!.write(`${JSON.stringify({ id, method, params })}\n`, (error) => {
        if (!error) return
        if (pending.idleTimer) clearTimeout(pending.idleTimer)
        this.pending.delete(id)
        reject(error)
      })
    })
  }

  private bindSocket(socket: Socket): void {
    socket.on('data', (chunk: string) => this.consume(chunk))
    socket.on('close', () => {
      if (this.socket !== socket) return
      this.socket = null
      this.bufferChunks = []
      this.rejectPending(new Error('Daemon connection closed'))
      this.emit('state', { connected: false, endpoint: this.endpoint } satisfies DaemonState)
    })
    socket.on('error', (error) => {
      if (this.socket !== socket) return
      this.socket = null
      this.bufferChunks = []
      socket.destroy()
      this.rejectPending(new Error(`Daemon connection failed: ${error.message}`))
      this.emit('state', {
        connected: false,
        endpoint: this.endpoint,
        error: error.message
      } satisfies DaemonState)
    })
  }

  private consume(chunk: string): void {
    let start = 0
    let newline = chunk.indexOf('\n')
    while (newline >= 0) {
      const part = chunk.slice(start, newline)
      let line: string
      if (this.bufferChunks.length === 0) {
        line = part.trim()
      } else {
        this.bufferChunks.push(part)
        line = this.bufferChunks.join('').trim()
        this.bufferChunks = []
      }
      if (line) this.consumeLine(line)
      start = newline + 1
      newline = chunk.indexOf('\n', start)
    }
    if (start < chunk.length) {
      this.bufferChunks.push(chunk.slice(start))
    }
  }

  private consumeLine(line: string): void {
    let message: DaemonMessage
    try {
      message = JSON.parse(line) as DaemonMessage
    } catch {
      return
    }

    const request = this.pending.get(message.id)
    if (request?.idleTimer) {
      clearTimeout(request.idleTimer)
      request.idleTimer = undefined
    }
    const event = request ? { ...message, requestMethod: request.method } : message
    this.emit('event', event)
    if (!TERMINAL_EVENTS.has(message.event)) return

    if (!request) return
    this.pending.delete(message.id)
    if (message.event === 'error') request.reject(new DaemonError(errorCodeOf(message), message.data, request.method))
    else request.resolve(event)
  }

  private rejectPending(error: Error): void {
    for (const request of this.pending.values()) {
      if (request.idleTimer) clearTimeout(request.idleTimer)
      request.reject(error)
    }
    this.pending.clear()
  }
}
