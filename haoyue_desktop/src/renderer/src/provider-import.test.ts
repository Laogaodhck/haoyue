import { describe, expect, it } from 'vitest'
import { parseProviderImport } from './provider-import'

describe('parseProviderImport', () => {
  it('parses a cURL snippet with a bearer header and JSON body', () => {
    const result = parseProviderImport(`curl -X POST 'https://tokenhub.example.com/v1/chat/completions' \\
  -H 'Authorization: Bearer sk-test-import-key' \\
  -H 'Content-Type: application/json' \\
  -d '{"model": "deepseek/deepseek-flash", "messages": [{"role": "user", "content": "你好"}]}'`)

    expect(result.baseUrl).toBe('https://tokenhub.example.com/v1')
    expect(result.apiKey).toBe('sk-test-import-key')
    expect(result.models).toEqual(['deepseek/deepseek-flash'])
    expect(result.kind).toBe('openai')
    expect(result.suggestedId).toBe('tokenhub-example')
    expect(result.warnings).toEqual([])
  })

  it('strips the chat completions path and keeps the version prefix', () => {
    expect(parseProviderImport('https://api.example.com/v1/chat/completions').baseUrl)
      .toBe('https://api.example.com/v1')
    expect(parseProviderImport('https://api.example.com/chat/completions').baseUrl)
      .toBe('https://api.example.com')
    expect(parseProviderImport('https://api.anthropic.com/v1/messages').baseUrl)
      .toBe('https://api.anthropic.com/v1')
  })

  it('parses an OpenAI python client snippet', () => {
    const result = parseProviderImport(`from openai import OpenAI
client = OpenAI(
    api_key="sk-test-python-key",
    base_url="https://api.example.com/v1",
)
client.chat.completions.create(model="deepseek/deepseek-flash", messages=[])`)

    expect(result.baseUrl).toBe('https://api.example.com/v1')
    expect(result.apiKey).toBe('sk-test-python-key')
    expect(result.models).toEqual(['deepseek/deepseek-flash'])
    expect(result.kind).toBe('openai')
  })

  it('parses a requests.post snippet with single quotes', () => {
    const result = parseProviderImport(`requests.post(
    'https://api.example.com/v1/chat/completions',
    headers={'Authorization': 'Bearer sk-test-requests-key'},
    json={'model': 'qwen-plus', 'messages': []},
)`)

    expect(result.baseUrl).toBe('https://api.example.com/v1')
    expect(result.apiKey).toBe('sk-test-requests-key')
    expect(result.models).toEqual(['qwen-plus'])
  })

  it('detects vendor presets and the anthropic protocol', () => {
    expect(parseProviderImport('https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions').kind)
      .toBe('aliyun')
    expect(parseProviderImport('https://ark.cn-beijing.volces.com/api/v3/chat/completions').kind)
      .toBe('volcengine')
    expect(parseProviderImport('https://api.anthropic.com/v1/messages').kind)
      .toBe('anthropic')
  })

  it('collects and de-duplicates models from keyword, JSON and array forms', () => {
    const result = parseProviderImport(`model="gpt-5"
{"model": "gpt-5", "models": ["gpt-5", "gpt-5-mini", "deepseek/deepseek-flash"]}`)

    expect(result.models).toEqual(['gpt-5', 'gpt-5-mini', 'deepseek/deepseek-flash'])
  })

  it('leaves environment-variable keys unset and reports the missing fields', () => {
    const result = parseProviderImport(`client = OpenAI(api_key=os.environ["OPENAI_API_KEY"], base_url="https://api.example.com/v1")
client.chat.completions.create(model=os.environ["MODEL"])`)

    expect(result.baseUrl).toBe('https://api.example.com/v1')
    expect(result.apiKey).toBeNull()
    expect(result.models).toEqual([])
    expect(result.warnings).toContain('未识别到 API Key（可能引用了环境变量），请手动填写。')
    expect(result.warnings).toContain('未识别到模型 ID，请手动添加。')
  })

  it('returns only warnings for empty or unrelated text', () => {
    const result = parseProviderImport('hello world')

    expect(result.baseUrl).toBeNull()
    expect(result.apiKey).toBeNull()
    expect(result.models).toEqual([])
    expect(result.kind).toBe('openai')
    expect(result.suggestedId).toBeNull()
    expect(result.warnings).toHaveLength(3)
  })
})
