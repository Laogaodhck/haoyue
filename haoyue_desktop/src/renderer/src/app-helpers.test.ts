import { describe, expect, it } from 'vitest'
import { classifyModelError, extractModelError, formatTokenCount, messageMatches, normalizePath, pathName, samePath, stripModelErrorBlock } from './app-helpers'

describe('formatTokenCount', () => {
  it('formats millions with M suffix correctly without unnecessary trailing zeros', () => {
    expect(formatTokenCount(1_280_000)).toBe('1.28M')
    expect(formatTokenCount(1_000_000)).toBe('1M')
    expect(formatTokenCount(2_000_000)).toBe('2M')
    expect(formatTokenCount(1_500_000)).toBe('1.5M')
    expect(formatTokenCount(10_000_000)).toBe('10M')
  })

  it('formats thousands with k suffix correctly', () => {
    expect(formatTokenCount(128_000)).toBe('128k')
    expect(formatTokenCount(64_000)).toBe('64k')
    expect(formatTokenCount(32_000)).toBe('32k')
    expect(formatTokenCount(8_192)).toBe('8k')
    expect(formatTokenCount(1_000)).toBe('1k')
  })

  it('falls back to 1M default when undefined', () => {
    expect(formatTokenCount(undefined)).toBe('1M')
    expect(formatTokenCount(0)).toBe('1M')
  })

  it('formats values under 1000 as plain numbers', () => {
    expect(formatTokenCount(512)).toBe('512')
  })
})

describe('path helpers', () => {
  it('normalizes and compares paths correctly', () => {
    expect(normalizePath('C:\\Project\\Haoyue\\')).toBe('c:/project/haoyue')
    expect(samePath('C:\\Project\\Haoyue', 'c:/project/haoyue/')).toBe(true)
    expect(pathName('C:\\Project\\Haoyue')).toBe('Haoyue')
  })
})

describe('attachment helpers', () => {
  it('formats prompt with attached files including filenames and absolute paths', async () => {
    const { formatPromptWithFiles, parseAttachmentsFromText } = await import('./app-helpers')
    const files = [
      { id: '1', name: '简历.pdf', path: 'C:\\Users\\zhang\\Documents\\简历.pdf', sizeBytes: 1024, extension: 'pdf' },
      { id: '2', name: 'config.ts', path: 'E:\\Project\\Haoyue\\config.ts', sizeBytes: 2048, extension: 'ts' }
    ]
    const prompt = formatPromptWithFiles('帮我分析简历', files)
    expect(prompt).toContain('简历.pdf')
    expect(prompt).toContain('C:\\Users\\zhang\\Documents\\简历.pdf')
    expect(prompt).toContain('config.ts')
    expect(prompt).toContain('帮我分析简历')

    const parsed = parseAttachmentsFromText(prompt)
    expect(parsed.content).toBe('帮我分析简历')
    expect(parsed.files).toHaveLength(2)
    expect(parsed.files[0]!.name).toBe('简历.pdf')
    expect(parsed.files[0]!.path).toBe('C:\\Users\\zhang\\Documents\\简历.pdf')
    expect(parsed.files[0]!.extension).toBe('pdf')
    expect(parsed.files[1]!.name).toBe('config.ts')
    expect(parsed.files[1]!.extension).toBe('ts')
  })

  it('handles empty content with default instruction', async () => {
    const { formatPromptWithFiles, parseAttachmentsFromText } = await import('./app-helpers')
    const files = [
      { id: '1', name: 'doc.txt', path: 'C:\\doc.txt', sizeBytes: 100, extension: 'txt' }
    ]
    const prompt = formatPromptWithFiles('', files)
    expect(prompt).toContain('请直接查看、分析并处理以上附加的文件。')
    const parsed = parseAttachmentsFromText(prompt)
    expect(parsed.content).toBe('')
    expect(parsed.files).toHaveLength(1)
  })
})

describe('formatMessageTime', () => {
  it('formats timestamp into M月d日 H:mm format', async () => {
    const { formatMessageTime } = await import('./app-helpers')
    // 2026-09-15 02:11:00 UTC -> Local time
    const date = new Date(2026, 8, 15, 2, 11) // September is month index 8
    expect(formatMessageTime(date.getTime())).toBe('9月15日 2:11')

    const date2 = new Date(2026, 8, 26, 14, 5)
    expect(formatMessageTime(date2.getTime())).toBe('9月26日 14:05')
  })

  it('returns empty string for invalid or missing timestamp', async () => {
    const { formatMessageTime } = await import('./app-helpers')
    expect(formatMessageTime(undefined)).toBe('')
    expect(formatMessageTime(0)).toBe('')
    expect(formatMessageTime(NaN)).toBe('')
  })
})

describe('hydrateMessages', () => {
  it('preserves message timestamp from backend', async () => {
    const { hydrateMessages } = await import('./app-helpers')
    const ts1 = 1757890000000
    const ts2 = 1757890060000
    const hydrated = hydrateMessages({
      id: 'session-1',
      createdAt: '2026-09-15T02:11:00Z',
      updatedAt: '2026-09-15T02:12:00Z',
      messages: [
        { role: 'user', text: 'hello', timestamp: ts1 },
        { role: 'assistant', text: 'hi', timestamp: ts2 }
      ]
    })
    expect(hydrated).toHaveLength(2)
    expect(hydrated[0]!.createdAt).toBe(ts1)
    expect(hydrated[1]!.createdAt).toBe(ts2)
  })
})

describe('classifyModelError', () => {
  it.each([
    ['circuit open (cooling down)', '模型服务暂时不可用'],
    ['HTTP 429: rate limit exceeded', '请求被限流或配额不足'],
    ['quota exceeded for project', '请求被限流或配额不足'],
    ['HTTP 401 Unauthorized: invalid api key', '认证失败'],
    ['Request timed out after 120s', '请求超时'],
    ['Connection refused (ECONNRESET)', '网络连接失败'],
    ['no provider configured', '模型未配置'],
    ['something totally unexpected', '模型调用失败']
  ])('maps "%s" to a human summary', (detail, title) => {
    expect(classifyModelError(detail).title).toBe(title)
  })

  it('always provides an actionable suggestion', () => {
    const meta = classifyModelError('HTTP 500 internal error')
    expect(meta.suggestion.length).toBeGreaterThan(5)
    expect(meta.suggestion).toMatch(/重试|设置|检查|网络/)
  })
})

describe('extractModelError / stripModelErrorBlock', () => {
  const block = '半截回答\n\n模型调用失败：\n```text\n    HTTP 429: rate limit exceeded\n```'

  it('recovers the raw detail from persisted content', () => {
    expect(extractModelError(block)).toBe('HTTP 429: rate limit exceeded')
  })

  it('returns empty for content without an error block', () => {
    expect(extractModelError('正常回答')).toBe('')
  })

  it('strips the error block while keeping the real answer', () => {
    expect(stripModelErrorBlock(block)).toBe('半截回答')
    expect(stripModelErrorBlock('正常回答')).toBe('正常回答')
  })

  it('keeps earlier error blocks when a newer turn appended one', () => {
    const doubled = block + '\n\n后续正文\n\n模型调用失败：\n```text\n    boom\n```'
    expect(stripModelErrorBlock(doubled)).toBe('半截回答\n\n后续正文')
  })
})

describe('messageMatches', () => {
  const base = { id: 'm1', role: 'assistant' as const, content: '', createdAt: 1 }

  it('searches the answer text', () => {
    expect(messageMatches({ ...base, content: '读取了 src/main.ts' }, 'main.ts')).toBe(true)
  })

  it('also searches thinking content (H10)', () => {
    expect(messageMatches({ ...base, thinking: '先查 ContextPlanner 的预算逻辑' }, '预算')).toBe(true)
  })

  it('also searches tool names and details (H10)', () => {
    expect(messageMatches({
      ...base,
      tools: [{ id: 't1', name: 'read_file', detail: 'haoyue_runtime/Agents/Agent.cs', state: 'done' }]
    }, 'agent.cs')).toBe(true)
    expect(messageMatches({
      ...base,
      tools: [{ id: 't1', name: 'bash', detail: 'dotnet test', state: 'done' }]
    }, 'read_file')).toBe(false)
  })
})
