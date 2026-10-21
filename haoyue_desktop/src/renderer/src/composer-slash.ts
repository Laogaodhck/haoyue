/// 输入框 slash 命令的纯文本逻辑（1.5）：`/` 开头触发命令面板、整条 `/命令 参数`
/// 提交时拦截为命令执行而不发消息。与 `composer-mentions.ts` 的 `@` 引用同构，
/// 两者互斥（`/` 只在行首、`@` 在分隔符后）。
export interface SlashToken {
  query: string
  start: number
}

export interface SlashCommand {
  /** 命令 id（执行分支用），与 label 的语义一一对应。 */
  id: string
  /** 输入法与面板展示的文本（含 /）。 */
  label: string
  /** 用法提示：带参数的命令把参数形态写清楚。 */
  usage: string
  description: string
}

/** 首批命令：切换类走参数（复用工具栏的 change-* 通道），动作类无参数。 */
export const SLASH_COMMANDS: SlashCommand[] = [
  { id: 'model', label: '/模型', usage: '/模型 <名称或关键字>', description: '切换当前模型' },
  { id: 'mode', label: '/模式', usage: '/模式 <edit|auto|plan|readonly>', description: '切换 Agent 模式' },
  { id: 'depth', label: '/深度', usage: '/深度 <fast|balanced|deep>', description: '切换推理深度' },
  { id: 'export', label: '/导出会话', usage: '/导出会话', description: '导出当前会话为 Markdown' },
  { id: 'clear', label: '/清空草稿', usage: '/清空草稿', description: '清空输入框内容' },
]

/**
 * caret 之前正在输入的 `/` 片段；不是命令时返回 null。
 * 与 `@` 不同，`/` 只在**行首**（或空白之后）生效——路径、除号、URL 里的 `/` 不弹面板。
 */
export function slashToken(text: string, caret: number): SlashToken | null {
  const before = text.slice(0, Math.max(0, Math.min(caret, text.length)))
  const slash = before.lastIndexOf('/')
  if (slash < 0) return null
  const head = slash === 0 ? '\n' : before[slash - 1] ?? ''
  if (!/[\s\n]/.test(head)) return null
  const query = before.slice(slash + 1)
  // 出现空白说明命令词已输完（接下来是参数），面板该收起。
  return /\s/.test(query) ? null : { query: query.toLocaleLowerCase(), start: slash }
}

/** 按 query 过滤命令（label 或 id 包含即命中）；空 query 返回全部。 */
export function filterSlashCommands(query: string): SlashCommand[] {
  const normalized = query.trim().toLocaleLowerCase()
  if (!normalized) return SLASH_COMMANDS
  return SLASH_COMMANDS.filter((command) =>
    command.label.toLocaleLowerCase().includes(normalized)
    || command.id.includes(normalized))
}

/** 选中命令后的文本替换：填入 `/命令 `（带尾随空格便于继续输入参数）。 */
export function applySlashCommand(
  text: string,
  caret: number,
  start: number,
  command: SlashCommand
): { text: string; caret: number } {
  const end = Math.max(0, Math.min(caret, text.length))
  const needsSpace = command.usage.includes('<') // 带参数的命令补空格
  const inserted = needsSpace ? `${command.label} ` : command.label
  return {
    text: `${text.slice(0, start)}${inserted}${text.slice(end)}`,
    caret: start + inserted.length,
  }
}

export interface ParsedSlashCommand {
  command: SlashCommand
  arg: string
}

/**
 * 提交时的整条解析：文本以 `/命令` 开头时返回命令与参数；不是命令/未知命令返回 null
 * （调用方把未知命令当普通消息？不——未知命令要显式提示，见 `unknownSlashCommand`）。
 */
export function parseSlashCommand(text: string): ParsedSlashCommand | null {
  const trimmed = text.trim()
  if (!trimmed.startsWith('/')) return null
  const space = trimmed.search(/\s/)
  const label = (space < 0 ? trimmed : trimmed.slice(0, space)).toLocaleLowerCase()
  // 中文 label 与英文 id 都可作命令词：/模式 与 /mode 等价（大小写不敏感）。
  const command = SLASH_COMMANDS.find((candidate) =>
    candidate.label.toLocaleLowerCase() === label || `/${candidate.id}` === label)
  if (!command) return null
  const arg = space < 0 ? '' : trimmed.slice(space + 1).trim()
  return { command, arg }
}

/** 文本看起来像命令但命令不存在（用于「误发送可归零」的显式提示）。 */
export function looksLikeUnknownCommand(text: string): boolean {
  const trimmed = text.trim()
  if (!trimmed.startsWith('/') || parseSlashCommand(trimmed)) return false
  // 只有「整条就是 /词」或「/词 参数」才算命令形态；`/path/to/file` 这类路径不当命令。
  const space = trimmed.search(/\s/)
  const head = space < 0 ? trimmed : trimmed.slice(0, space)
  return head.length > 1 && !head.slice(1).includes('/')
}
