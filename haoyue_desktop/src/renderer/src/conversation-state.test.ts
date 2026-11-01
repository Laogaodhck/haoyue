import { describe, expect, it } from 'vitest'
import { markThreadInterrupted } from './conversation-state'
import type { ThreadItem } from './types'

function thread(): ThreadItem {
  return {
    id: 't1',
    title: '任务',
    updatedAt: 0,
    running: true,
    queueDraining: true,
    assistantId: 'm2',
    sessionLoaded: true,
    pendingApproval: {
      callId: 'call-1',
      tool: 'terminal',
      summary: 'rm -rf build',
      reason: '高危命令',
      requestedAt: 0
    },
    messages: [
      { id: 'm1', role: 'user', content: 'hi', state: 'done', createdAt: 0 },
      { id: 'm2', role: 'assistant', content: 'working', state: 'streaming', createdAt: 0 }
    ]
  }
}

describe('markThreadInterrupted', () => {
  it('clears the pending approval so the modal cannot outlive the turn', () => {
    const item = thread()

    markThreadInterrupted(item)

    expect(item.pendingApproval).toBeUndefined()
    expect(item.running).toBe(false)
    expect(item.messages[1]).toMatchObject({ state: 'done', interrupted: true })
  })
})
