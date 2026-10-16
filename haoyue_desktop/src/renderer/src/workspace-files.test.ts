import { describe, expect, it, vi } from 'vitest'
import type { DaemonMessage } from '../../shared/ipc'
import { createWorkspaceFileSuggester } from './workspace-files'

function message(data: string): DaemonMessage {
  return { id: 1, event: 'result', data }
}

describe('workspace file suggester', () => {
  it('skips the RPC when the task has no project root', async () => {
    const request = vi.fn()
    const suggest = createWorkspaceFileSuggester({
      daemon: () => ({ request }),
      root: () => null
    })
    expect(await suggest('app')).toEqual([])
    expect(request).not.toHaveBeenCalled()
  })

  it('skips the RPC while the runtime is offline', async () => {
    const suggest = createWorkspaceFileSuggester({
      daemon: () => null,
      root: () => 'E:/repo'
    })
    expect(await suggest('app')).toEqual([])
  })

  it('passes the workspace selector, query and limit', async () => {
    const request = vi.fn().mockResolvedValue(message('["src/App.cs"]'))
    const suggest = createWorkspaceFileSuggester({
      daemon: () => ({ request }),
      root: () => 'E:/repo'
    })
    expect(await suggest('app')).toEqual(['src/App.cs'])
    expect(request).toHaveBeenCalledWith('workspace.files', { workspace: 'E:/repo', query: 'app', limit: 30 })
  })

  it('degrades to no candidates on transport or payload failures', async () => {
    const failing = createWorkspaceFileSuggester({
      daemon: () => ({ request: vi.fn().mockRejectedValue(new Error('daemon offline')) }),
      root: () => 'E:/repo'
    })
    expect(await failing('app')).toEqual([])

    const garbage = createWorkspaceFileSuggester({
      daemon: () => ({ request: vi.fn().mockResolvedValue(message('{"path":"E:/repo/.ssh"}')) }),
      root: () => 'E:/repo'
    })
    expect(await garbage('app')).toEqual([])

    const mixed = createWorkspaceFileSuggester({
      daemon: () => ({ request: vi.fn().mockResolvedValue(message('["src/A.cs",42,null,"src/B.cs"]')) }),
      root: () => 'E:/repo'
    })
    expect(await mixed('src')).toEqual(['src/A.cs', 'src/B.cs'])
  })
})
