import { describe, expect, it } from 'vitest'
import {
  GITHUB_TOKEN_ENV_KEY,
  MCP_PRESETS,
  MCP_TRANSPORT_OPTIONS,
  buildMcpServerPayload,
  buildMcpTogglePayload,
  createMcpFormValue,
  envNeedsGithubToken,
  githubTokenCreateUrl,
  mcpFormError,
  mcpFormFromServer,
  mcpStatusText,
  normalizeTransport,
  parseEnvText,
  transportLabel,
  type McpFormValue,
  type McpServerSummary
} from './mcp-form'

function makeForm(patch: Partial<McpFormValue> = {}): McpFormValue {
  return { ...createMcpFormValue(), ...patch }
}

const remoteServer: McpServerSummary = {
  name: 'StarLife',
  scope: 'global',
  transport: 'sse',
  args: [],
  url: 'http://localhost:5070/mcp',
  envKeys: [],
  enabled: true,
  connected: false,
  toolCount: 0,
  error: 'connection refused'
}

describe('transport naming', () => {
  it('names the remote transports after the MCP specification', () => {
    expect(MCP_TRANSPORT_OPTIONS.map((option) => option.label))
      .toEqual(['stdio', 'HTTP SSE', 'Streamable HTTP'])
  })

  it('maps runtime transport ids onto readable labels', () => {
    expect(transportLabel('sse')).toBe('HTTP SSE')
    expect(transportLabel('http')).toBe('Streamable HTTP')
    expect(transportLabel('streamable-http')).toBe('Streamable HTTP')
    expect(transportLabel('STDIO')).toBe('stdio')
    expect(transportLabel('')).toBe('')
  })

  it('normalizes alias ids onto the three editor values', () => {
    expect(normalizeTransport('streamable_http')).toBe('http')
    expect(normalizeTransport('SSE')).toBe('sse')
    expect(normalizeTransport('mystery')).toBe('stdio')
  })
})

describe('mcpFormError', () => {
  it('requires a server name', () => {
    expect(mcpFormError(makeForm())).toBe('请填写 MCP 服务器名称')
  })

  it('requires a command for stdio servers', () => {
    expect(mcpFormError(makeForm({ name: 'fs' }))).toBe('stdio 连接需要填写启动命令，例如 npx')
  })

  it('requires an absolute URL for remote servers', () => {
    expect(mcpFormError(makeForm({ name: 'StarLife', transport: 'sse' })))
      .toBe('HTTP SSE 连接需要填写 URL')
    expect(mcpFormError(makeForm({ name: 'StarLife', transport: 'http', url: 'localhost:5070/mcp' })))
      .toBe('URL 需要以 http:// 或 https:// 开头')
  })

  it('accepts complete forms', () => {
    expect(mcpFormError(makeForm({ name: 'fs', command: 'npx' }))).toBeNull()
    expect(mcpFormError(makeForm({ name: 'web', transport: 'http', url: 'https://example.com/mcp' }))).toBeNull()
  })
})

describe('buildMcpServerPayload', () => {
  it('sends commands and parsed env for stdio servers', () => {
    const payload = buildMcpServerPayload(makeForm({
      name: 'fs',
      command: 'npx',
      args: '-y\n@modelcontextprotocol/server-filesystem\n',
      env: 'TOKEN=abc\nEMPTY='
    }))

    expect(payload).toEqual({
      transport: 'stdio',
      command: 'npx',
      args: ['-y', '@modelcontextprotocol/server-filesystem'],
      url: '',
      enabled: true,
      env: { TOKEN: 'abc', EMPTY: '' }
    })
  })

  it('drops stdio-only fields for remote servers', () => {
    const payload = buildMcpServerPayload(makeForm({
      name: 'StarLife',
      transport: 'sse',
      command: 'npx',
      args: '-y\nfoo',
      url: ' http://localhost:5070/mcp ',
      env: 'TOKEN=abc'
    }))

    expect(payload).toEqual({
      transport: 'sse',
      command: '',
      args: [],
      url: 'http://localhost:5070/mcp',
      enabled: true
    })
  })
})

describe('buildMcpTogglePayload', () => {
  it('flips enabled while keeping the stored configuration', () => {
    expect(buildMcpTogglePayload(remoteServer)).toEqual({
      transport: 'sse',
      command: '',
      args: [],
      url: 'http://localhost:5070/mcp',
      enabled: false
    })
  })

  it('keeps stdio arguments when toggling a local server', () => {
    const stdioServer: McpServerSummary = {
      ...remoteServer, transport: 'stdio', command: 'npx', args: ['-y', 'server'], url: undefined
    }

    expect(buildMcpTogglePayload(stdioServer)).toEqual({
      transport: 'stdio', command: 'npx', args: ['-y', 'server'], url: '', enabled: false
    })
  })

  it('returns a payload that survives structured cloning when the source is reactive', () => {
    // Vue reactive arrays are Proxies; sending them straight to ipcRenderer.invoke
    // fails with "An object could not be cloned."
    const reactiveArgs = new Proxy(['-y', 'server'], {})
    expect(() => structuredClone(reactiveArgs)).toThrow()

    const payload = buildMcpTogglePayload({ ...remoteServer, transport: 'stdio', command: 'npx', args: reactiveArgs })
    expect(() => structuredClone(payload)).not.toThrow()
    expect(payload.args).toEqual(['-y', 'server'])
  })
})

describe('mcpFormFromServer', () => {
  it('restores editor state and never leaks stored env values', () => {
    const form = mcpFormFromServer({ ...remoteServer, transport: 'streamable-http', envKeys: ['TOKEN'] })

    expect(form).toEqual({
      name: 'StarLife',
      scope: 'global',
      transport: 'http',
      command: '',
      args: '',
      url: 'http://localhost:5070/mcp',
      env: '',
      enabled: true
    })
  })
})

describe('mcpStatusText', () => {
  it('prefers connection, tool count, disabled state, then the error', () => {
    expect(mcpStatusText({ connected: true, enabled: true, toolCount: 3 })).toBe('3 个工具')
    expect(mcpStatusText({ connected: false, connecting: true, enabled: true, toolCount: 0 })).toBe('连接中…')
    expect(mcpStatusText({ connected: false, enabled: false, toolCount: 0, error: 'disabled' })).toBe('已禁用')
    expect(mcpStatusText({ connected: false, enabled: true, toolCount: 0, error: 'connection refused' }))
      .toBe('connection refused')
    expect(mcpStatusText({ connected: false, enabled: true, toolCount: 0 })).toBe('未连接')
  })
})

describe('parseEnvText', () => {
  it('reads KEY=value lines and treats a bare key as an empty value', () => {
    // The key is trimmed; the value is kept verbatim because leading or trailing
    // spaces can be meaningful in tokens.
    expect(parseEnvText('A=1\nB\n C = 2 ')).toEqual({ A: '1', B: '', C: ' 2' })
    expect(parseEnvText('   ')).toBeUndefined()
  })
})

describe('GitHub token guidance', () => {
  it('links to the GitHub token form with the scopes the MCP server needs', () => {
    const url = new URL(githubTokenCreateUrl())
    expect(url.origin + url.pathname).toBe('https://github.com/settings/tokens/new')
    expect(url.searchParams.get('description')).toBe('Haoyue MCP GitHub Server')
    expect(url.searchParams.get('scopes')).toBe('repo,read:org,read:repo_hook,read:user,user:email')
  })

  it('detects a GitHub token key that still needs a value', () => {
    expect(envNeedsGithubToken(`${GITHUB_TOKEN_ENV_KEY}=`)).toBe(true)
    expect(envNeedsGithubToken(`github_personal_access_token = `)).toBe(true)
    expect(envNeedsGithubToken(GITHUB_TOKEN_ENV_KEY)).toBe(true)
    expect(envNeedsGithubToken(`TOKEN=\n${GITHUB_TOKEN_ENV_KEY} = `)).toBe(true)
    expect(envNeedsGithubToken(`${GITHUB_TOKEN_ENV_KEY}=ghp_abc`)).toBe(false)
    expect(envNeedsGithubToken('TOKEN=\nEMPTY=')).toBe(false)
    expect(envNeedsGithubToken('')).toBe(false)
  })
})

describe('MCP_PRESETS', () => {
  it('covers the commonly used reference servers', () => {
    expect(MCP_PRESETS.map((item) => item.id)).toEqual([
      'github', 'filesystem', 'fetch', 'memory', 'sequential-thinking', 'git'
    ])
  })

  it('ships a GitHub preset that compiles into a valid stdio payload', () => {
    const preset = MCP_PRESETS.find((item) => item.id === 'github')
    expect(preset).toBeDefined()

    const form = preset!.createForm()
    expect(mcpFormError(form)).toBeNull()
    expect(buildMcpServerPayload(form)).toEqual({
      transport: 'stdio',
      command: 'npx',
      args: ['-y', '@modelcontextprotocol/server-github'],
      url: '',
      enabled: true,
      env: { GITHUB_PERSONAL_ACCESS_TOKEN: '' }
    })
  })

  it('prefills filesystem with the workspace directory when provided', () => {
    const preset = MCP_PRESETS.find((item) => item.id === 'filesystem')!
    const form = preset.createForm({ workspacePath: ' E:\\notes ' })

    expect(mcpFormError(form)).toBeNull()
    expect(parseEnvText(form.env)).toBeUndefined()
    expect(form.args.split(/\r?\n/)).toEqual(['-y', '@modelcontextprotocol/server-filesystem', 'E:\\notes'])
    expect(buildMcpServerPayload(form).args)
      .toEqual(['-y', '@modelcontextprotocol/server-filesystem', 'E:\\notes'])
  })

  it('falls back to the current directory when no workspace path is known', () => {
    const preset = MCP_PRESETS.find((item) => item.id === 'filesystem')!
    expect(preset.createForm().args.endsWith('\n.')).toBe(true)
  })

  it('ships zero-configuration presets for fetch, memory, sequential-thinking and git', () => {
    const shapes: Record<string, { command: string; args: string[] }> = {
      fetch: { command: 'uvx', args: ['mcp-server-fetch'] },
      memory: { command: 'npx', args: ['-y', '@modelcontextprotocol/server-memory'] },
      'sequential-thinking': { command: 'npx', args: ['-y', '@modelcontextprotocol/server-sequential-thinking'] },
      git: { command: 'uvx', args: ['mcp-server-git'] }
    }

    for (const [id, shape] of Object.entries(shapes)) {
      const preset = MCP_PRESETS.find((item) => item.id === id)
      expect(preset, id).toBeDefined()

      const form = preset!.createForm()
      expect(mcpFormError(form)).toBeNull()
      expect(form.command).toBe(shape.command)
      expect(buildMcpServerPayload(form)).toEqual({
        transport: 'stdio',
        command: shape.command,
        args: shape.args,
        url: '',
        enabled: true
      })
    }
  })

  it('never prefills credentials and returns independent form values', () => {
    for (const preset of MCP_PRESETS) {
      const first = preset.createForm({ workspacePath: 'E:\\w' })
      const second = preset.createForm({ workspacePath: 'E:\\w' })

      const env = parseEnvText(first.env)
      if (env) {
        for (const value of Object.values(env)) expect(value).toBe('')
      }

      first.name = 'mutated'
      first.args = ''
      expect(second.name).toBe(preset.id)
      expect(second.args).not.toBe('')
    }
  })
})
