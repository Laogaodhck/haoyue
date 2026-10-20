import { ReasoningLevel } from './types'
import type {
  ChatMessage,
  FileAttachment,
  ImageAttachment,
  ProjectItem,
  QueuedMessage,
  ThreadItem,
  ThreadStats,
  ToolActivity
} from './types'

// Vue reactive proxies cannot be structured-cloned by Electron IPC; copy the
// attachments into plain objects at the IPC boundary so requests reach the
// daemon instead of failing with a DataCloneError.
export function plainImages(images?: ImageAttachment[]): ImageAttachment[] {
  return (images ?? []).map((image) => ({
    id: image.id,
    name: image.name,
    mediaType: image.mediaType,
    data: image.data,
    sizeBytes: image.sizeBytes,
    path: image.path
  }))
}

export const makeId = (): string => crypto.randomUUID()
export const pathName = (path: string): string => path.replace(/[\\/]+$/, '').split(/[\\/]/).pop() || path
export const normalizePath = (path: string): string => path.replace(/\\/g, '/').replace(/\/$/, '').toLocaleLowerCase()
export const samePath = (left?: string, right?: string): boolean => Boolean(left && right && normalizePath(left) === normalizePath(right))

export interface RuntimeSessionHeader {
  id: string
  title?: string
  workspace?: string
  archived?: boolean
  createdAt: string
  updatedAt: string
  reasoningLevel?: string
  networkEnabled?: boolean
  expertId?: string
  llmRounds?: number
  executionSteps?: number
  inputTokens?: number
  totalInputTokens?: number
  cachedInputTokens?: number
  outputTokens?: number
  outputElapsedMs?: number
}

export interface RuntimeSession extends RuntimeSessionHeader {
  messages: Array<{
    role: 'user' | 'assistant' | 'tool'
    text: string
    images?: ImageAttachment[]
    thinking?: string
    modelRef?: string
    viewedImages?: Array<{ id: string; name: string }>
    finishReason?: string
    toolCalls?: Array<{ id: string; name: string }>
    toolCallId?: string
    toolName?: string
    toolSuccess?: boolean
    toolDiff?: string
    toolFilePath?: string
    timestamp?: number
  }>
  /** session.get 分页游标（P1-2.3）：总条数、本页首条序号、是否还有更早。 */
  total?: number
  firstIndex?: number
  hasMore?: boolean
}

export interface RuntimeWorkspace {
  path: string
  name: string
  /** 服务端 workspace.get 返回的项目类型标签（如 dotnet/node/python）。 */
  projectKinds?: string[]
  mode: string
}

export interface RuntimeProject {
  id: string
  path: string
  name: string
  createdAt: string
  updatedAt: string
}

export interface RuntimeModelCatalogItem {
  ref: string
  active: boolean
  /** 模型声明的上下文窗口（tokens）；model.list 线上一直携带，此前渲染层未使用。 */
  contextWindow?: number
  capabilities?: { vision?: boolean }
}

export function normalizeReasoningLevel(value?: string): ReasoningLevel {
  const normalized = value?.toLocaleLowerCase()
  return Object.values(ReasoningLevel).includes(normalized as ReasoningLevel)
    ? normalized as ReasoningLevel
    : ReasoningLevel.High
}

export function formatPromptWithFiles(content: string, files?: FileAttachment[]): string {
  if (!files || files.length === 0) return content

  const fileLines = files
    .map((file) => `- 文件名: "${file.name}" | 绝对路径: "${file.path}"`)
    .join('\n')

  const header = `[用户已附加以下本地文件。请直接根据任务需求调用相应的工具（如 read_file、bash 等）读取和处理这些文件，无需让用户手动提供文件路径]：\n${fileLines}`

  if (!content.trim()) {
    return `${header}\n\n请直接查看、分析并处理以上附加的文件。`
  }

  return `${header}\n\n用户指示：\n${content.trim()}`
}

export function parseAttachmentsFromText(rawText: string): { content: string; files: FileAttachment[] } {
  if (!rawText || !rawText.startsWith('[用户已附加以下本地文件')) {
    return { content: rawText, files: [] }
  }

  const files: FileAttachment[] = []
  const fileRegex = /- 文件名: "([^"]+)" \| 绝对路径: "([^"]+)"/g
  let match: RegExpExecArray | null
  while ((match = fileRegex.exec(rawText)) !== null) {
    const name = match[1] ?? ''
    const path = match[2] ?? ''
    if (!name || !path) continue
    const dot = name.lastIndexOf('.')
    const extension = dot >= 0 ? name.slice(dot + 1).toLowerCase() : ''
    files.push({
      id: `${name}:${path}`,
      name,
      path,
      sizeBytes: 0,
      extension
    })
  }

  const marker = '\n\n用户指示：\n'
  const defaultMarker = '\n\n请直接查看、分析并处理以上附加的文件。'
  let content = ''
  if (rawText.includes(marker)) {
    content = rawText.slice(rawText.indexOf(marker) + marker.length).trim()
  } else if (!rawText.includes(defaultMarker)) {
    content = ''
  }

  return { content, files }
}

export function getFileExtension(name: string): string {
  const dot = name.lastIndexOf('.')
  if (dot < 0 || dot === name.length - 1) return ''
  return name.slice(dot + 1).toLowerCase()
}

export function fileBadgeText(ext?: string): string {
  if (!ext) return 'FILE'
  const upper = ext.toUpperCase()
  if (upper.length <= 4) return upper
  return upper.slice(0, 3)
}

export function fileExtClass(ext?: string): string {
  const e = (ext || '').toLowerCase()
  if (e === 'pdf') return 'badge-pdf'
  if (['ts', 'js', 'py', 'cs', 'cpp', 'c', 'go', 'rs', 'java', 'vue', 'json', 'html', 'css', 'sql', 'sh', 'bat', 'ps1', 'dart'].includes(e)) return 'badge-code'
  if (['doc', 'docx', 'txt', 'md', 'rtf', 'odt'].includes(e)) return 'badge-doc'
  if (['xls', 'xlsx', 'csv', 'tsv'].includes(e)) return 'badge-sheet'
  if (['ppt', 'pptx'].includes(e)) return 'badge-slide'
  if (['zip', 'rar', '7z', 'tar', 'gz'].includes(e)) return 'badge-archive'
  if (['png', 'jpg', 'jpeg', 'webp', 'gif', 'svg', 'bmp', 'ico'].includes(e)) return 'badge-image'
  return 'badge-default'
}

/**
 * 把 session.get 的消息行转成界面气泡。`offset` 是本页首条在整个会话里的序号：
 * 用它拼出绝对序号 id，向前翻页时既有气泡的 key 不会漂移，虚拟滚动不整屏重建。
 */
export function hydrateMessages(saved: RuntimeSession, offset = 0): ChatMessage[] {
  const messages: ChatMessage[] = []
  saved.messages.forEach((item, index) => {
    const ordinal = offset + index
    if (item.role === 'tool') {
      const assistant = messages.findLast((message) => message.role === 'assistant')
      if (assistant && item.toolName) {
        assistant.tools ??= []
        const tool = assistant.tools.find((activity) =>
          Boolean(item.toolCallId) && (activity.callId === item.toolCallId || activity.id === item.toolCallId))
        const hydrated: ToolActivity = tool ?? {
          id: item.toolCallId ?? `${saved.id}:tool:${ordinal}`,
          callId: item.toolCallId,
          name: item.toolName,
          state: 'done'
        }
        hydrated.state = item.toolSuccess === false ? 'error' : 'done'
        hydrated.detail = item.text
        hydrated.filePath = item.toolFilePath
        hydrated.diff = item.toolDiff
        if (!tool) assistant.tools.push(hydrated)
      }
      return
    }

    let messageContent = item.text
    let messageFiles: FileAttachment[] | undefined
    if (item.role === 'user' && item.text) {
      const parsed = parseAttachmentsFromText(item.text)
      messageContent = parsed.content
      if (parsed.files.length > 0) messageFiles = parsed.files
    }

    messages.push({
      id: `${saved.id}:${ordinal}`,
      role: item.role,
      content: messageContent,
      images: item.images,
      files: messageFiles,
      thinking: item.thinking,
      modelRef: item.modelRef,
      viewedImages: item.viewedImages,
      finishReason: item.finishReason,
      tools: item.toolCalls?.map((call) => ({
        id: call.id,
        callId: call.id,
        name: call.name,
        state: 'done'
      })),
      state: item.role === 'assistant' ? 'done' : undefined,
      createdAt: typeof item.timestamp === 'number' && item.timestamp > 0
        ? item.timestamp
        : new Date(saved.createdAt).getTime() + index
    })
  })
  return messages
}

/**
 * 回合终态文案。'stop'/'tool_calls' 表示模型确实答完了（或只是发起工具调用），
 * 返回 undefined 让气泡保持正常样式；其余取值都是「没答完」，必须显式呈现，
 * 否则半截回答看起来和完整回答一模一样。
 */
export function finishReasonLabel(reason?: string): string | undefined {
  if (!reason || reason === 'stop' || reason === 'tool_calls') return undefined
  switch (reason) {
    case 'length':
    case 'max_tokens':
      return '输出达到长度上限，回答可能被截断'
    case 'interrupted':
      return '模型连接中断，以上为中断前生成的部分输出'
    case 'incomplete':
      return '未取得模型终态，回答可能不完整'
    case 'refusal':
    case 'content_filter':
    case 'safety':
      return '模型拒绝回答该请求'
    case 'empty':
      return '模型连续两次返回空回答，请重试'
    case 'max_steps':
      return '已达到本回合步数上限，任务可能未完成'
    case 'budget':
      return '已达到本回合预算上限，任务可能未完成'
    case 'cancelled':
      return '回合被中断，回答可能不完整'
    case 'error':
      return '回合因错误终止'
    default:
      return `模型未完成回答（finish_reason=${reason}）`
  }
}

export function sessionStats(saved: RuntimeSession): ThreadStats {
  const assistantCount = saved.messages.filter((item) => item.role === 'assistant').length
  // 持久化只存累计口径；上下文占比需要「最近一轮单次 prompt」，恢复时用会话均值代理，
  // 运行中的 usage 事件会用实测值覆盖（见 daemon-event-handler usage 分支）。
  const rounds = Math.max(1, saved.llmRounds || assistantCount)
  const totalInput = saved.totalInputTokens ?? 0
  return {
    llmRounds: saved.llmRounds || assistantCount,
    executionSteps: saved.executionSteps || assistantCount,
    inputTokens: saved.inputTokens ?? 0,
    totalInputTokens: saved.totalInputTokens ?? 0,
    lastRoundInputTokens: totalInput > 0 ? Math.round(totalInput / rounds) : undefined,
    cachedInputTokens: saved.cachedInputTokens ?? 0,
    outputTokens: saved.outputTokens ?? 0,
    outputElapsedMs: saved.outputElapsedMs ?? 0
  }
}

/**
 * 上下文占用比例读数（修正累计口径虚涨）。
 * usage 事件的 totalInputTokens 是「本次调用的完整 prompt 规模」，跨轮累计后再除以
 * 模型窗口会随轮数线性高估（10 轮 20k prompt 会显示 200k/128k≈156%）。占比只按
 * 最近一轮单次规模计算，与「下一轮至少要装下这么多」的真实约束对齐。
 */
export function formatContextRatio(lastRoundInputTokens?: number, contextWindow?: number): string {
  if (!lastRoundInputTokens || !contextWindow || lastRoundInputTokens <= 0 || contextWindow <= 0) return ''
  return ` | 上下文 ${Math.min(999, Math.round(lastRoundInputTokens / contextWindow * 100))}%`
}

/**
 * 启动预热提示文案（daemon 后台装载本地权重期间常驻于输入区）。Failed 时说清楚
 * 「不影响使用」——预热是纯优化，失败只意味着第一条消息要当场等加载，不是客户端坏了。
 */
export function formatPreloadLabel(preload?: { phase: string; percent: number } | null): string {
  if (!preload) return ''
  return preload.phase === 'Failed'
    ? '本地模型预热失败，发送消息时将自动重试'
    : `本地模型预热中 ${Math.round(preload.percent * 100)}%`
}

export function sessionScope(thread: ThreadItem, project?: ProjectItem): Record<string, unknown> {
  return thread.projectId && project ? { workspace: project.path } : { global: true }
}

/**
 * agent.cancel 的定位参数（P1-2.3）：优先按请求 id 精确取消，首个事件还没到达、
 * 请求 id 尚未就绪时按会话取消。返回 null 表示两者都没有——绝不能发空参取消，
 * 那会取消该连接上的全部活动回合，误伤其他后台任务。
 */
export function cancelParams(thread: ThreadItem): { requestId: number } | { sessionId: string } | null {
  if (thread.requestId !== undefined) return { requestId: thread.requestId }
  if (thread.sessionId) return { sessionId: thread.sessionId }
  return null
}

export function messageMatches(message: ChatMessage, query: string): boolean {
  const normalized = query.trim().toLocaleLowerCase()
  if (!normalized) return true
  // H10: the search covers the full turn — thinking and tool results carry most of
  // the facts in a coding session; searching the answer text only hides them.
  if (message.content.toLocaleLowerCase().includes(normalized)) return true
  if (message.thinking?.toLocaleLowerCase().includes(normalized)) return true
  return (message.tools ?? []).some((tool) =>
    tool.name.toLocaleLowerCase().includes(normalized)
    || (tool.detail ?? '').toLocaleLowerCase().includes(normalized)
    || (tool.args ?? '').toLocaleLowerCase().includes(normalized))
}

export function phaseLabel(status: string): string {
  const s = status.toLocaleLowerCase()
  if (s.includes('preparing')) return '正在准备'
  if (s.includes('compacting')) return '压缩记忆'
  if (s.includes('verifying')) return '构建验证'
  if (s.includes('truncated')) return '自动续写'
  if (s.includes('thinking')) return '思考中'
  return status
}

export function queuedMessagePreview(message: QueuedMessage): string {
  const text = message.content.trim().replace(/\s+/g, ' ')
  if (text) return text.length > 120 ? `${text.slice(0, 120)}…` : text
  if (message.files?.length) {
    return `发送 ${message.files.length} 个附件文件`
  }
  return message.images.length > 1 ? `发送 ${message.images.length} 张图片` : '发送图片'
}

export function queuedImageUrl(image?: ImageAttachment): string {
  return image ? `data:${image.mediaType};base64,${image.data}` : ''
}

export function updateThreadTitle(thread: ThreadItem, prompt: string): boolean {
  if (thread.title !== '新任务') return false
  thread.title = prompt.length > 42 ? `${prompt.slice(0, 42)}…` : prompt
  return true
}

export function formatTokenCount(tokens?: number): string {
  const count = tokens || 1000000
  if (count >= 1_000_000) {
    const inMillions = count / 1_000_000
    const formatted = inMillions % 1 === 0 ? inMillions.toString() : inMillions.toFixed(2).replace(/\.?0+$/, '')
    return `${formatted}M`
  }
  if (count >= 1000) {
    const inThousands = count / 1000
    const formatted = inThousands % 1 === 0 ? inThousands.toString() : Math.round(inThousands).toString()
    return `${formatted}k`
  }
  return count.toString()
}

export function formatMessageTime(timestamp?: number): string {
  if (!timestamp) return ''
  const date = new Date(timestamp)
  if (isNaN(date.getTime())) return ''
  const month = date.getMonth() + 1
  const day = date.getDate()
  const hours = date.getHours()
  const minutes = date.getMinutes().toString().padStart(2, '0')
  return `${month}月${day}日 ${hours}:${minutes}`
}

// ------------------------------------------------------------------ error card
/** Human-facing summary + actionable suggestion for a failed model turn (H18). */
export interface ErrorCardMeta {
  title: string
  suggestion: string
}

export function classifyModelError(detail: string): ErrorCardMeta {
  const d = detail.toLowerCase()
  if (d.includes('circuit open') || d.includes('cooling down')) {
    return {
      title: '模型服务暂时不可用',
      suggestion: '所有候选均处于熔断冷却中，通常稍等片刻即可重试；也可到设置中检查各服务商状态。'
    }
  }
  if (/\b429\b|rate.?limit|quota|配额/.test(d)) {
    return {
      title: '请求被限流或配额不足',
      suggestion: '请求过于频繁或额度已用尽：稍后重试，或到设置中更换模型候选。'
    }
  }
  if (/\b401\b|\b403\b|unauthorized|forbidden|invalid.{0,16}key|api.?key|authentication/.test(d)) {
    return {
      title: '认证失败',
      suggestion: 'API 密钥无效或已过期：到设置中检查对应服务商的密钥配置后重试。'
    }
  }
  if (d.includes('timeout') || d.includes('timed out') || d.includes('deadline') || d.includes('超时')) {
    return {
      title: '请求超时',
      suggestion: '模型响应过慢或网络不畅：可直接重试；若持续超时请检查网络或代理设置。'
    }
  }
  if (d.includes('network') || d.includes('socket') || d.includes('econn')
    || d.includes('connection') || d.includes('unreachable') || d.includes('dns')
    || d.includes('getaddrinfo')) {
    return {
      title: '网络连接失败',
      suggestion: '无法连接到模型服务：检查网络或代理设置后重试。'
    }
  }
  if (d.includes('no provider') || d.includes('not configured') || d.includes('未配置')
    || d.includes('no candidates') || d.includes('no enabled')) {
    return {
      title: '模型未配置',
      suggestion: '当前没有可用的模型候选：到设置中添加并启用至少一个服务商。'
    }
  }
  return {
    title: '模型调用失败',
    suggestion: '服务返回了异常：可点击重试；若持续失败请展开技术详情定位原因。'
  }
}

const MODEL_ERROR_MARKER = '模型调用失败：'
/**
 * 历史/重载后的消息只剩 content，技术详情必须能从文本里复原，否则「查看技术详情」按钮
 * 在重新打开会话后就不渲染了。凡是「lead 句子 + 这一段围栏块」的形态都算详情块。
 */
export const ERROR_DETAIL_MARKER = '技术详情：'
const ERROR_BLOCK_HEADLINES = ['模型调用失败', '技术详情']
const MODEL_ERROR_BLOCK_RE = new RegExp(
  `(?:\\r?\\n)+(?:${ERROR_BLOCK_HEADLINES.join('|')})：\\r?\\n\`\`\`text\\n[\\s\\S]*?\`\`\``,
  'g'
)

/** Recover the raw error detail from persisted content (historical/reloaded messages).
 * A candidate chain may fail several times, so the last block wins. */
export function extractModelError(content: string): string {
  let detail = ''
  const global = new RegExp(MODEL_ERROR_BLOCK_RE.source, 'g')
  for (const match of content.matchAll(global)) {
    const fenced = match[0].match(/```text\n([\s\S]*?)(?:```|$)/)
    detail = fenced?.[1] ?? match[0]
    detail = detail
      .split(/\r?\n/)
      .map((line) => line.replace(/^ {4}/, ''))
      .join('\n')
      .trim()
  }
  if (detail) return detail
  for (const marker of [MODEL_ERROR_MARKER, ERROR_DETAIL_MARKER]) {
    const at = content.lastIndexOf(marker)
    if (at >= 0) return content.slice(at + marker.length).trim()
  }
  return ''
}

/** 详情块（模型失败或后台失败追加的围栏段）在展示时剥掉，只留给人看的正文。 */
export function stripModelErrorBlock(content: string): string {
  return content.replace(MODEL_ERROR_BLOCK_RE, '')
}

/**
 * 回合失败的来源分类。此前所有 catch 都写成「无法连接 Haoyue Daemon」，
 * 30 秒首事件超时（后台活着、正在加载权重）也被说成连不上，用户按提示去重启后台
 * 反而把事情弄坏；文案必须区分「连不上」与「等太久」。
 */
export type TurnFailureKind = 'daemon_unreachable' | 'first_event_timeout' | 'request_failed'

/** 客户端/主进程抛出的错误文本 → 失败来源。判定放在纯函数里，便于单测钉住。 */
export function classifyTurnFailure(error: unknown): TurnFailureKind {
  const detail = error instanceof Error ? error.message : String(error ?? '')
  const text = detail.toLowerCase()
  // 请求超时由 daemon-client 明确写成「请求超时：Runtime 在 N 秒内未响应」，
  // 它要的是「再等等或看日志」，不是「重连后台」。
  if (detail.includes('请求超时') || text.includes('no response within')) return 'first_event_timeout'
  if (text.includes('unable to connect') || text.includes('connection failed')
    || text.includes('connection closed') || text.includes('disconnected')
    || text.includes('handshake') || text.includes('authentication')
    || text.includes('econnrefused') || text.includes('enoent') || text.includes('versionmismatch')
    || text.includes('协议版本不兼容'))
    return 'daemon_unreachable'
  return 'request_failed'
}

const TURN_FAILURE_LEADS: Record<TurnFailureKind, string> = {
  daemon_unreachable: '浩玥后台（daemon）未运行或连接已断开，消息没有送出。可点击重连；反复失败请到 设置 → 诊断 打开后台日志查看原因。',
  first_event_timeout: '后台已收到请求，但在期限内没有回第一个字。本地大模型首次加载权重、或显卡不足时这会持续几分钟——可以先看 设置 → 诊断 里的后台日志确认进展。',
  request_failed: '这一回合失败了。可点击重试；若持续失败请展开技术详情定位原因。'
}

/**
 * 统一的回合失败文案：lead 说清来源，围栏块带可复原的技术详情
 * （重载后「查看技术详情」仍要能点开，详情此前只在内存里的 message.errorDetail 上）。
 */
export function formatTurnFailure(error: unknown): { content: string; detail: string; kind: TurnFailureKind } {
  const kind = classifyTurnFailure(error)
  const detail = error instanceof Error ? error.message : String(error ?? '')
  return { kind, detail, content: `${TURN_FAILURE_LEADS[kind]}\n\n${ERROR_DETAIL_MARKER}\n\`\`\`text\n${detail}\n\`\`\`` }
}
