import { describe, expect, it } from 'vitest'
import {
  GITHUB_TOKEN_ENV_KEY,
  MCP_PRESETS,
  MCP_TRANSPORT_OPTIONS,
  buildMcpServerPayload,
  buildMcpTogglePayload,
  classifyTransport,
  createMcpFormValue,
  credentialRowsToText,
  envNeedsGithubToken,
  githubTokenCreateUrl,
  inferMcpName,
  mcpFormError,
  mcpFormFromServer,
  mcpStatusText,
  normalizeConnection,
  normalizeTransport,
  parseCredentialRows,
  parseEnvText,
  parseMcpJsonConfig,
  parseToolNameList,
  tokenizeCommand,
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

describe('tokenizeCommand', () => {
  it('splits on whitespace and respects single/double quotes', () => {
    expect(tokenizeCommand('npx -y @modelcontextprotocol/server-github'))
      .toEqual(['npx', '-y', '@modelcontextprotocol/server-github'])
    expect(tokenizeCommand('node "E:\\My Server\\mcp.js" --port 5'))
      .toEqual(['node', 'E:\\My Server\\mcp.js', '--port', '5'])
    expect(tokenizeCommand("uvx 'mcp server'")).toEqual(['uvx', 'mcp server'])
    expect(tokenizeCommand('   ')).toEqual([])
  })
})

describe('normalizeConnection', () => {
  it('expands bare npm package names with npx -y', () => {
    expect(normalizeConnection('@modelcontextprotocol/server-github'))
      .toBe('npx -y @modelcontextprotocol/server-github')
    expect(normalizeConnection('some/mcp-pkg')).toBe('npx -y some/mcp-pkg')
  })

  it('expands mcp-server-* uv packages with uvx', () => {
    expect(normalizeConnection('mcp-server-fetch')).toBe('uvx mcp-server-fetch')
  })

  it('leaves full commands, interpreters and URLs unchanged', () => {
    expect(normalizeConnection('npx -y foo')).toBe('npx -y foo')
    expect(normalizeConnection('node server.js --flag')).toBe('node server.js --flag')
    expect(normalizeConnection('https://example.com/mcp')).toBe('https://example.com/mcp')
    expect(normalizeConnection('C:\\tools\\mcp.exe')).toBe('C:\\tools\\mcp.exe')
  })
})

describe('classifyTransport and inferMcpName', () => {
  it('routes URLs to the http transport and everything else to stdio', () => {
    expect(classifyTransport('https://example.com/mcp')).toBe('http')
    expect(classifyTransport('http://127.0.0.1:5070/mcp')).toBe('http')
    expect(classifyTransport('npx -y foo')).toBe('stdio')
  })

  it('derives names from package tails', () => {
    expect(inferMcpName('npx -y @modelcontextprotocol/server-github')).toBe('github')
    expect(inferMcpName('uvx mcp-server-fetch')).toBe('fetch')
    expect(inferMcpName('node E:\\tools\\memory.js')).toBe('memory')
  })

  it('derives names from URL hostnames', () => {
    expect(inferMcpName('https://mcp.example.com/mcp')).toBe('example')
    expect(inferMcpName('https://api.github.com/mcp')).toBe('github')
  })

  it('returns an empty name for unrecognized input', () => {
    expect(inferMcpName('')).toBe('')
    expect(inferMcpName('???')).toBe('')
  })
})

describe('parseMcpJsonConfig', () => {
  it('reads the mcpServers wrapper (Claude/Cursor style)', () => {
    const parsed = parseMcpJsonConfig(JSON.stringify({
      mcpServers: {
        github: {
          command: 'npx',
          args: ['-y', '@modelcontextprotocol/server-github'],
          env: { GITHUB_PERSONAL_ACCESS_TOKEN: 'ghp_x' }
        }
      }
    }))

    expect(parsed?.name).toBe('github')
    expect(parsed?.server.command).toBe('npx')
    expect(parsed?.server.args).toEqual(['-y', '@modelcontextprotocol/server-github'])
  })

  it('reads a single server object and rejects unrelated JSON', () => {
    expect(parseMcpJsonConfig('{"command":"npx","args":["-y","pkg"]}')).not.toBeNull()
    expect(parseMcpJsonConfig('{"hello":"world"}')).toBeNull()
    expect(parseMcpJsonConfig('not json at all')).toBeNull()
    expect(parseMcpJsonConfig('{"mcpServers":{}}')).toBeNull()
  })
})

describe('mcpFormError', () => {
  it('requires a server name', () => {
    expect(mcpFormError(makeForm({ connection: 'npx -y pkg' }))).toBe('请填写 MCP 服务器名称')
  })

  it('requires a connection string', () => {
    expect(mcpFormError(makeForm({ name: 'fs' })))
      .toBe('请填写连接内容：包名（如 @modelcontextprotocol/server-github）、完整命令或 npx/uvx 启动命令')
  })

  it('requires an absolute URL for remote servers', () => {
    expect(mcpFormError(makeForm({ name: 'StarLife', transport: 'sse' })))
      .toBe('HTTP SSE 连接需要填写 URL')
    expect(mcpFormError(makeForm({ name: 'StarLife', transport: 'http', connection: 'localhost:5070/mcp' })))
      .toBe('URL 需要以 http:// 或 https:// 开头')
  })

  it('rejects unrecognizable stdio commands', () => {
    expect(mcpFormError(makeForm({ name: 'x', connection: 'not-a-command!' })))
      .toBe('无法识别的命令；请输入 npx/uvx 包名、完整命令或 http(s):// URL')
  })

  it('requires the expert timeout to be a positive integer', () => {
    expect(mcpFormError(makeForm({ name: 'web', transport: 'http', connection: 'https://example.com/mcp', connectTimeoutSeconds: '30' })))
      .toBeNull()
    expect(mcpFormError(makeForm({ name: 'web', transport: 'http', connection: 'https://example.com/mcp', connectTimeoutSeconds: 'abc' })))
      .toBe('连接超时需要是 1~120 之间的整数（秒），留空使用默认 10 秒')
    // stdio servers have no connect timeout to validate.
    expect(mcpFormError(makeForm({ name: 'fs', connection: 'npx -y pkg', connectTimeoutSeconds: 'abc' })))
      .toBeNull()
  })

  it('accepts complete forms', () => {
    expect(mcpFormError(makeForm({ name: 'fs', connection: 'npx -y @modelcontextprotocol/server-filesystem' }))).toBeNull()
    expect(mcpFormError(makeForm({ name: 'fs', connection: 'C:\\tools\\mcp.exe --port 5' }))).toBeNull()
    expect(mcpFormError(makeForm({ name: 'web', transport: 'http', connection: 'https://example.com/mcp' }))).toBeNull()
  })
})

describe('buildMcpServerPayload', () => {
  it('tokenizes the connection line and sends parsed env for stdio servers', () => {
    const payload = buildMcpServerPayload(makeForm({
      name: 'fs',
      connection: 'npx -y @modelcontextprotocol/server-filesystem',
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

  it('handles quoted arguments with whitespace', () => {
    const payload = buildMcpServerPayload(makeForm({
      name: 'fs',
      connection: 'node "E:\\My Server\\mcp.js" --port 5'
    }))
    expect(payload.command).toBe('node')
    expect(payload.args).toEqual(['E:\\My Server\\mcp.js', '--port', '5'])
  })

  it('sends headers for remote servers and drops stdio fields', () => {
    const payload = buildMcpServerPayload(makeForm({
      name: 'StarLife',
      transport: 'sse',
      connection: ' http://localhost:5070/mcp ',
      env: 'TOKEN=abc',
      headers: 'Authorization=Bearer tok'
    }))

    expect(payload).toEqual({
      transport: 'sse',
      command: '',
      args: [],
      url: 'http://localhost:5070/mcp',
      enabled: true,
      headers: { Authorization: 'Bearer tok' }
    })
  })

  it('omits headers when none are configured', () => {
    const payload = buildMcpServerPayload(makeForm({
      name: 'web', transport: 'http', connection: 'https://example.com/mcp'
    }))
    expect(payload).not.toHaveProperty('headers')
    expect(payload).not.toHaveProperty('env')
  })

  it('sends expert-mode overrides for remote servers only when non-default', () => {
    const payload = buildMcpServerPayload(makeForm({
      name: 'web',
      transport: 'http',
      connection: 'https://example.com/mcp',
      connectTimeoutSeconds: ' 45 ',
      mutatingTools: 'send_email\n\ndeploy\n',
      readOnlyTools: 'search',
      trustReadOnly: true
    }))

    expect(payload.connectTimeoutSeconds).toBe(45)
    expect(payload.mutatingTools).toEqual(['send_email', 'deploy'])
    expect(payload.readOnlyTools).toEqual(['search'])
    expect(payload.trustReadOnly).toBe(true)
    expect(payload).not.toHaveProperty('oauthDisabled')
  })

  it('clamps the expert timeout into the 1-120 range', () => {
    const payload = buildMcpServerPayload(makeForm({
      name: 'web', transport: 'http', connection: 'https://example.com/mcp', connectTimeoutSeconds: '999'
    }))
    expect(payload.connectTimeoutSeconds).toBe(120)
  })

  it('never sends expert-mode overrides for stdio servers', () => {
    const payload = buildMcpServerPayload(makeForm({
      name: 'fs',
      connection: 'npx -y pkg',
      connectTimeoutSeconds: '30',
      mutatingTools: 'deploy',
      readOnlyTools: 'search',
      trustReadOnly: true,
      oauthDisabled: true
    }))
    expect(payload).not.toHaveProperty('connectTimeoutSeconds')
    expect(payload).not.toHaveProperty('mutatingTools')
    expect(payload).not.toHaveProperty('readOnlyTools')
    expect(payload).not.toHaveProperty('trustReadOnly')
    expect(payload).not.toHaveProperty('oauthDisabled')
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
    const reactiveArgs = new Proxy(['-y', 'server'], {})
    expect(() => structuredClone(reactiveArgs)).toThrow()

    const payload = buildMcpTogglePayload({ ...remoteServer, transport: 'stdio', command: 'npx', args: reactiveArgs })
    expect(() => structuredClone(payload)).not.toThrow()
    expect(payload.args).toEqual(['-y', 'server'])
  })
})

describe('mcpFormFromServer', () => {
  it('rebuilds the single connection line and never leaks credential values', () => {
    const form = mcpFormFromServer({
      ...remoteServer, transport: 'streamable-http', envKeys: ['TOKEN'], headerKeys: ['Authorization']
    })

    expect(form).toEqual({
      name: 'StarLife',
      scope: 'global',
      transport: 'http',
      connection: 'http://localhost:5070/mcp',
      env: '',
      headers: '',
      enabled: true,
      connectTimeoutSeconds: '',
      mutatingTools: '',
      readOnlyTools: '',
      trustReadOnly: false,
      oauthDisabled: false
    })
  })

  it('quotes stdio arguments containing whitespace when rebuilding the line', () => {
    const form = mcpFormFromServer({
      ...remoteServer, transport: 'stdio', command: 'node', args: ['E:\\My Server\\mcp.js'], url: undefined
    })
    expect(form.connection).toBe('node "E:\\My Server\\mcp.js"')
  })

  it('restores expert-mode fields from the runtime summary', () => {
    const form = mcpFormFromServer({
      ...remoteServer,
      connectTimeoutSeconds: 30,
      mutatingTools: ['send_email', 'deploy'],
      readOnlyTools: ['search'],
      trustReadOnly: true,
      oauthConfigured: true
    })

    expect(form.connectTimeoutSeconds).toBe('30')
    expect(form.mutatingTools).toBe('send_email\ndeploy')
    expect(form.readOnlyTools).toBe('search')
    expect(form.trustReadOnly).toBe(true)
    // oauthConfigured is a presence flag with no editable counterpart in the form.
    expect(form.oauthDisabled).toBe(false)
  })
})

describe('credential rows', () => {
  it('round-trips KEY=value lines through structured rows', () => {
    const rows = parseCredentialRows('A=1\nB\n C = 2 ')
    expect(rows).toEqual([
      { key: 'A', value: '1' },
      { key: 'B', value: '' },
      { key: 'C', value: '2' }
    ])
    expect(credentialRowsToText(rows)).toBe('A=1\nB=\nC=2')
  })

  it('keeps empty values as the keep-stored-value marker', () => {
    // The daemon interprets an empty value as "unchanged"; deleting a row drops the key.
    expect(credentialRowsToText([{ key: 'TOKEN', value: '' }])).toBe('TOKEN=')
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

describe('parseToolNameList', () => {
  it('reads one tool name per line and drops blanks', () => {
    expect(parseToolNameList('send_email\n\ndeploy \n')).toEqual(['send_email', 'deploy'])
    expect(parseToolNameList('')).toEqual([])
  })
})

describe('MCP_PRESETS', () => {
  it('covers the commonly used reference servers', () => {
    expect(MCP_PRESETS.map((item) => item.id)).toEqual([
      'github', 'filesystem', 'fetch', 'memory', 'sequential-thinking', 'git',
      'playwright', 'brave-search', 'context7', 'postgres', 'slack', 'time'
    ])
  })

  it('ships a GitHub preset that compiles into a valid stdio payload', () => {
    const preset = MCP_PRESETS.find((item) => item.id === 'github')
    expect(preset).toBeDefined()

    const form = preset!.createForm()
    expect(mcpFormError(form)).toBeNull()
    expect(form.name).toBe('github')
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
    const form = preset.createForm({ workspacePath: 'E:\\notes' })

    expect(mcpFormError(form)).toBeNull()
    expect(parseEnvText(form.env)).toBeUndefined()
    expect(buildMcpServerPayload(form).args)
      .toEqual(['-y', '@modelcontextprotocol/server-filesystem', 'E:\\notes'])
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
      expect(buildMcpServerPayload(form)).toEqual({
        transport: 'stdio',
        command: shape.command,
        args: shape.args,
        url: '',
        enabled: true
      })
    }
  })

  it('ships the extended preset set with explicit names and credential keys', () => {
    const shapes: Record<string, { name: string; command: string; args: string[]; env?: Record<string, string> }> = {
      playwright: { name: 'playwright', command: 'npx', args: ['-y', '@playwright/mcp'] },
      'brave-search': { name: 'brave-search', command: 'npx', args: ['-y', '@modelcontextprotocol/server-brave-search'], env: { BRAVE_API_KEY: '' } },
      context7: { name: 'context7', command: 'npx', args: ['-y', '@upstash/context7-mcp'] },
      postgres: {
        name: 'postgres',
        command: 'npx',
        args: ['-y', '@modelcontextprotocol/server-postgres', 'postgresql://user:password@localhost:5432/postgres']
      },
      slack: {
        name: 'slack',
        command: 'npx',
        args: ['-y', '@modelcontextprotocol/server-slack'],
        env: { SLACK_BOT_TOKEN: '', SLACK_TEAM_ID: '' }
      },
      time: { name: 'time', command: 'uvx', args: ['mcp-server-time'] }
    }

    for (const [id, shape] of Object.entries(shapes)) {
      const preset = MCP_PRESETS.find((item) => item.id === id)
      expect(preset, id).toBeDefined()

      const form = preset!.createForm()
      expect(mcpFormError(form)).toBeNull()
      expect(form.name).toBe(shape.name)

      const payload = buildMcpServerPayload(form)
      expect(payload.command).toBe(shape.command)
      expect(payload.args).toEqual(shape.args)
      if (shape.env) expect(payload.env).toEqual(shape.env)
      else expect(payload).not.toHaveProperty('env')
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
      first.connection = ''
      expect(second.name).not.toBe('mutated')
      expect(second.connection).not.toBe('')
    }
  })
})
