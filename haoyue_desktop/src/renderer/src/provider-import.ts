import { PROVIDER_PROTOCOLS, type ProviderKind } from './provider-presets'

/**
 * 从 cURL / Python / JSON 片段中提取新增模型提供商所需的字段。
 *
 * 只做本地文本解析、不发网络请求、不写入任何地方；API Key 仅回填到表单，
 * 保存时仍走 provider.upsert 的加密引用链路。
 */
export interface ProviderImportResult {
  baseUrl: string | null
  apiKey: string | null
  models: string[]
  kind: ProviderKind
  suggestedId: string | null
  suggestedName: string | null
  warnings: string[]
}

const URL_RE = /https?:\/\/[^\s'"`<>\\]+/gi
const MODEL_JSON_RE = /"model"\s*:\s*"([^"]+)"/g
const MODEL_DICT_RE = /['"]model['"]\s*:\s*['"]([^'"]+)['"]/g
const MODEL_LIST_RE = /['"]models['"]\s*:\s*\[([^\]]*)\]/g
const MODEL_KEYWORD_RE = /\bmodel\s*=\s*['"]([^'"]+)['"]/g
const KEY_ASSIGN_RE = /\b(?:api[_-]?key|apiKey)\s*[:=]\s*['"]([^'"]+)['"]/gi
const HEADER_KEY_RE = /['"]?(?:x-api-key|api-key)['"]?\s*[:=]\s*['"]?([A-Za-z0-9._~+/=-]+)/gi
const BEARER_RE = /\bBearer\s+([A-Za-z0-9._~+/=-]+)/gi

export function parseProviderImport(input: string): ProviderImportResult {
  const text = (input ?? '').replace(/\\\//g, '/')
  const endpoint = pickEndpoint(text)
  const baseUrl = endpoint ? normalizeBaseUrl(endpoint) : null
  const host = baseUrl ? safeHost(baseUrl) : null
  const kind = detectKind(text, host)
  const apiKey = pickApiKey(text)
  const models = pickModels(text)

  const warnings: string[] = []
  if (!baseUrl) warnings.push('未识别到 API 地址，请手动填写。')
  if (!apiKey) warnings.push('未识别到 API Key（可能引用了环境变量），请手动填写。')
  if (models.length === 0) warnings.push('未识别到模型 ID，请手动添加。')

  return {
    baseUrl,
    apiKey,
    models,
    kind,
    suggestedId: host ? slugFromHost(host) : null,
    suggestedName: host,
    warnings
  }
}

/** 多个 URL 时优先带 chat/completions / messages 的，其次 curl、post、base_url 附近的。 */
function pickEndpoint(text: string): string | null {
  const candidates: Array<{ url: string; score: number }> = []
  for (const match of text.matchAll(URL_RE)) {
    const url = match[0].replace(/[),;\]]+$/, '')
    if (!url) continue
    const before = text.slice(Math.max(0, match.index - 48), match.index).toLowerCase()
    let score = 0
    if (/\/(?:chat\/)?completions\b/.test(url)) score += 4
    if (/\/messages\b/.test(url)) score += 3
    if (/base[_-]?url|curl|post\(|url\s*=/.test(before)) score += 2
    candidates.push({ url, score })
  }
  candidates.sort((a, b) => b.score - a.score)
  return candidates[0]?.url ?? null
}

function normalizeBaseUrl(raw: string): string | null {
  try {
    const url = new URL(raw)
    url.search = ''
    url.hash = ''
    const path = url.pathname
      .replace(/\/+$/, '')
      .replace(/\/(?:chat\/)?completions$/i, '')
      .replace(/\/messages$/i, '')
      .replace(/\/+$/, '')
    return `${url.origin}${path}`
  } catch {
    return null
  }
}

function safeHost(url: string): string | null {
  try {
    return new URL(url).hostname.toLowerCase()
  } catch {
    return null
  }
}

function detectKind(text: string, host: string | null): ProviderKind {
  if (host?.endsWith('anthropic.com')) return 'anthropic'
  if (host) {
    for (const option of PROVIDER_PROTOCOLS) {
      if (!option.baseUrl) continue
      const presetHost = safeHost(option.baseUrl)
      if (presetHost && presetHost === host) return option.value
    }
  }
  return /\/messages\b/.test(text) ? 'anthropic' : 'openai'
}

function pickApiKey(text: string): string | null {
  const candidates = [
    ...text.matchAll(KEY_ASSIGN_RE),
    ...text.matchAll(HEADER_KEY_RE),
    ...text.matchAll(BEARER_RE)
  ]
  for (const match of candidates) {
    const value = match[1]?.trim()
    if (value && value.length >= 4 && !/[\s${}<>]/.test(value)) return value
  }
  return null
}

function pickModels(text: string): string[] {
  const found: string[] = []
  const push = (value: string | undefined): void => {
    const model = value?.trim()
    if (!model || /[\s${}]/.test(model)) return
    if (!found.some((existing) => existing.toLowerCase() === model.toLowerCase())) found.push(model)
  }

  for (const match of text.matchAll(MODEL_JSON_RE)) push(match[1])
  for (const match of text.matchAll(MODEL_DICT_RE)) push(match[1])
  for (const match of text.matchAll(MODEL_KEYWORD_RE)) push(match[1])
  for (const match of text.matchAll(MODEL_LIST_RE)) {
    for (const value of match[1]?.matchAll(/['"]([^'"]+)['"]/g) ?? []) push(value[1])
  }
  return found
}

function slugFromHost(host: string): string {
  const labels = host.split('.').filter(Boolean)
  const kept = labels.slice(0, -1).filter((label) => label !== 'www' && label !== 'api')
  const slug = (kept.length > 0 ? kept : labels)
    .join('-')
    .replace(/[^a-z0-9-]+/gi, '-')
    .replace(/^-+|-+$/g, '')
    .toLowerCase()
  return slug || 'custom'
}
