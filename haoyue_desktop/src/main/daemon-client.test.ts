import { once } from 'node:events'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { createServer, type Server, type Socket } from 'node:net'
import { afterEach, describe, expect, it } from 'vitest'
import type { DaemonMessage } from '../shared/ipc.js'
import { DaemonClient } from './daemon-client.js'

const servers: Server[] = []
const clients: DaemonClient[] = []

afterEach(async () => {
  for (const client of clients.splice(0)) client.disconnect()
  await Promise.all(servers.splice(0).map((server) => new Promise<void>((resolve) => server.close(() => resolve()))))
})

type ParsedRequest = { id: number; method: string; params: Record<string, unknown> }

/**
 * Wraps a request handler so the mandatory daemon handshake is answered first.
 * Lines are split defensively: the client may pipeline messages into one chunk.
 */
function withHandshake(
  handler: (socket: Socket, request: ParsedRequest) => void
): (socket: Socket) => void {
  return (socket) => {
    socket.setEncoding('utf8')
    socket.on('data', (chunk: string) => {
      for (const line of chunk.split('\n')) {
        if (!line.trim()) continue
        const request = JSON.parse(line.trim()) as ParsedRequest
        if (request.method === 'handshake') {
          socket.write(`${JSON.stringify({ id: request.id, event: 'result', data: '{}' })}\n`)
          continue
        }
        handler(socket, request)
      }
    })
  }
}

describe('DaemonClient', () => {
  it('streams JSONL events and resolves on the terminal event', async () => {
    const suffix = `${process.pid}-${Date.now()}`
    const endpoint = process.platform === 'win32'
      ? String.raw`\\.\pipe\haoyue-test-${suffix}`
      : join(tmpdir(), `haoyue-test-${suffix}.sock`)

    const server = createServer(
      withHandshake((socket, request) => {
        expect(request.method).toBe('chat')
        socket.write(`${JSON.stringify({ id: request.id, event: 'thinking', data: 'checking' })}\n`)
        socket.write(`${JSON.stringify({ id: request.id, event: 'delta', data: 'hello' })}\n`)
        socket.write(`${JSON.stringify({ id: request.id, event: 'done', data: 'hello' })}\n`)
      })
    )
    servers.push(server)
    server.listen(endpoint)
    await once(server, 'listening')

    const client = new DaemonClient(endpoint)
    clients.push(client)
    const events: DaemonMessage[] = []
    client.on('event', (event: DaemonMessage) => events.push(event))

    const response = await client.request('chat', { message: 'hello' })

    expect(response.event).toBe('done')
    expect(response.data).toBe('hello')
    expect(response.requestMethod).toBe('chat')
    expect(events.map((event) => event.event)).toEqual(['thinking', 'delta', 'done'])
  })

  it('rejects a request that never receives any event after timeoutMs', async () => {
    const suffix = `${process.pid}-${Date.now()}-idle`
    const endpoint = process.platform === 'win32'
      ? String.raw`\\.\pipe\haoyue-test-${suffix}`
      : join(tmpdir(), `haoyue-test-${suffix}.sock`)

    // A server that answers the handshake but never the request itself. Resume
    // the socket so the client disconnect is observed and teardown can close.
    const sockets: Socket[] = []
    const server = createServer((socket) => {
      sockets.push(socket)
      socket.setEncoding('utf8')
      socket.on('error', () => undefined)
      socket.on('data', (chunk: string) => {
        const request = JSON.parse(chunk.trim().split('\n')[0] ?? '') as ParsedRequest
        if (request.method === 'handshake') {
          socket.write(`${JSON.stringify({ id: request.id, event: 'result', data: '{}' })}\n`)
        }
        // Business requests are deliberately never answered.
      })
    })
    servers.push(server)
    server.listen(endpoint)
    await once(server, 'listening')

    const client = new DaemonClient(endpoint)
    clients.push(client)
    await expect(client.request('chat', { message: 'hello' }, { timeoutMs: 150 }))
      .rejects.toThrow('请求超时')
  })

  it('keeps waiting for a request that produced an event before timeoutMs', async () => {
    const suffix = `${process.pid}-${Date.now()}-activity`
    const endpoint = process.platform === 'win32'
      ? String.raw`\\.\pipe\haoyue-test-${suffix}`
      : join(tmpdir(), `haoyue-test-${suffix}.sock`)

    const server = createServer(
      withHandshake((socket, request) => {
        // Answer long after the timeout window, but first emit activity that
        // confirms the turn is running and should keep waiting.
        socket.write(`${JSON.stringify({ id: request.id, event: 'status', data: 'Thinking' })}\n`)
        setTimeout(() => {
          socket.write(`${JSON.stringify({ id: request.id, event: 'done', data: 'ok' })}\n`)
        }, 250)
      })
    )
    servers.push(server)
    server.listen(endpoint)
    await once(server, 'listening')

    const client = new DaemonClient(endpoint)
    clients.push(client)
    const response = await client.request('chat', { message: 'hello' }, { timeoutMs: 100 })
    expect(response.event).toBe('done')
    expect(response.data).toBe('ok')
  })

  it('treats a cancelled turn as a terminal response', async () => {
    const suffix = `${process.pid}-${Date.now()}-cancel`
    const endpoint = process.platform === 'win32'
      ? String.raw`\\.\pipe\haoyue-test-${suffix}`
      : join(tmpdir(), `haoyue-test-${suffix}.sock`)

    const server = createServer(
      withHandshake((socket, request) => {
        socket.write(`${JSON.stringify({ id: request.id, event: 'cancelled', data: 'partial' })}\n`)
      })
    )
    servers.push(server)
    server.listen(endpoint)
    await once(server, 'listening')

    const client = new DaemonClient(endpoint)
    clients.push(client)
    const response = await client.request('chat', { message: 'hello' })

    expect(response.event).toBe('cancelled')
    expect(response.data).toBe('partial')
  })

  it('correctly reassembles large messages delivered across multiple fragmented chunks', async () => {
    const suffix = `${process.pid}-${Date.now()}-fragments`
    const endpoint = process.platform === 'win32'
      ? String.raw`\\.\pipe\haoyue-test-${suffix}`
      : join(tmpdir(), `haoyue-test-${suffix}.sock`)

    const largePayload = 'A'.repeat(50_000)
    const server = createServer(
      withHandshake((socket, request) => {
        const line = `${JSON.stringify({ id: request.id, event: 'result', data: largePayload })}\n`
        // Fragment into 1KB slices
        const sliceSize = 1024
        for (let i = 0; i < line.length; i += sliceSize) {
          socket.write(line.slice(i, i + sliceSize))
        }
      })
    )
    servers.push(server)
    server.listen(endpoint)
    await once(server, 'listening')

    const client = new DaemonClient(endpoint)
    clients.push(client)
    const response = await client.request('session.get', { id: 'test' })

    expect(response.event).toBe('result')
    expect(response.data).toBe(largePayload)
  })

  it('fails to connect when the daemon rejects the handshake', async () => {
    const suffix = `${process.pid}-${Date.now()}-authfail`
    const endpoint = process.platform === 'win32'
      ? String.raw`\\.\pipe\haoyue-test-${suffix}`
      : join(tmpdir(), `haoyue-test-${suffix}.sock`)

    const server = createServer((socket) => {
      socket.setEncoding('utf8')
      socket.on('data', (chunk: string) => {
        const request = JSON.parse(chunk.trim().split('\n')[0] ?? '') as ParsedRequest
        if (request.method === 'handshake') {
          socket.write(
            `${JSON.stringify({ id: request.id, event: 'error', data: 'authentication failed: missing or invalid handshake token' })}\n`
          )
        }
      })
    })
    servers.push(server)
    server.listen(endpoint)
    await once(server, 'listening')

    const client = new DaemonClient(endpoint)
    clients.push(client)
    const state = await client.connect()

    expect(state.connected).toBe(false)
    expect(state.error).toContain('authentication failed')
  })
})
