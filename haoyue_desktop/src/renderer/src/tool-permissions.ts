/**
 * 项目级工具权限（`<workspace>/.haoyue/config.json` 的 `allowedTools` /
 * `allowToolFallbackToGlobal`）的读写辅助。
 *
 * 校验语义必须与 runtime 侧 `ToolPermissionPolicy.IsValidPattern` 保持一致：
 * 界面在保存前拦一次，服务端在写盘前再拦一次——否则用户以为已经收紧，
 * 实际写进去的却是被静默忽略的垃圾模式。
 */
import type { ConfigObject, ConfigSaveReason } from './config-patch'

/** 与 `ToolPermissionPolicy.MaxPatternLength` 相同的上限。 */
export const TOOL_PATTERN_MAX_LENGTH = 128

/** 单条模式校验；返回错误文案，合法时返回 null。 */
export function validateToolPattern(pattern: string): string | null {
  const value = pattern ?? ''
  if (value.length === 0) return '模式不能为空'
  if (value.length > TOOL_PATTERN_MAX_LENGTH) return `模式长度不能超过 ${TOOL_PATTERN_MAX_LENGTH} 个字符`
  if (value !== value.trim()) return '模式首尾不能包含空白'
  for (const ch of value) {
    if (/^[A-Za-z0-9_\-.*/]$/.test(ch)) continue
    return `不允许字符 '${ch}'（只支持字母、数字、_ - . * /）`
  }
  if (value.startsWith('/') || value.endsWith('/') || value.includes('//')) return '斜杠两侧不能为空'
  if (value.includes('**')) return '只支持单个 `*`，不允许 `**`'
  return null
}

export interface ParsedToolPatterns {
  /** 合法的模式（按输入顺序，行内首尾空白已去掉）。 */
  patterns: string[]
  /** 逐行报错，已带行号，供界面直接展示。 */
  errors: string[]
}

/** 把「一行一条」的文本解析成模式清单：空行忽略，非法行收集为带行号的错误。 */
export function parseToolPatterns(text: string): ParsedToolPatterns {
  const patterns: string[] = []
  const errors: string[] = []
  const lines = (text ?? '').split(/\r?\n/)
  lines.forEach((raw, index) => {
    const line = raw.trim()
    if (!line) return
    const error = validateToolPattern(line)
    if (error) {
      errors.push(`第 ${index + 1} 行「${line}」${error}`)
      return
    }
    patterns.push(line)
  })
  return { patterns, errors }
}

export interface ToolPermissionView {
  patterns: string[]
  /** 硬禁用清单（deny-overrides 最高优先级，优先于 allowedTools）。 */
  disabledPatterns: string[]
  /** 磁盘上 `allowedTools` 里的历史非法条目：不进编辑框，也不阻止其它字段保存。 */
  invalidPatterns: string[]
  /** 磁盘上 `disabledTools` 里的历史非法条目（同上）。 */
  invalidDisabledPatterns: string[]
  fallback: boolean
  /** 字段本身不是数组 / 布尔值时的说明；条目级问题进 invalid*，不阻塞保存。 */
  problem: string
}

function describeValue(value: unknown): string {
  const text = JSON.stringify(value) ?? String(value)
  return text.length > 48 ? `${text.slice(0, 48)}…` : text
}

/**
 * 读一个模式列表，并把条目分成「可编辑的有效项」与「历史遗留的非法项」。
 *
 * 历史文件可能是用户手工改坏的：非法条目单独列出（界面可一键清理），合法条目照常
 * 编辑；这样一条坏数据不会把整块面板锁死。字段类型不对（不是数组）才记入 problem。
 */
function readPatternList(
  config: ConfigObject | null | undefined,
  key: string,
  problem: string[],
  invalid: string[]
): string[] {
  const raw = config?.[key]
  if (raw === undefined || raw === null) return []
  if (!Array.isArray(raw)) {
    problem.push(`${key} 不是字符串数组`)
    return []
  }
  const valid: string[] = []
  raw.forEach((item, index) => {
    if (typeof item !== 'string') {
      invalid.push(`${key} 第 ${index + 1} 项不是字符串（${describeValue(item)}）`)
      return
    }
    const error = validateToolPattern(item)
    if (error) {
      invalid.push(`${key} 第 ${index + 1} 项「${item}」${error}`)
      return
    }
    valid.push(item)
  })
  return valid
}

/** 从工作区配置文档里读出工具权限字段；缺省/空都表示不限制。 */
export function readToolPermissions(config: ConfigObject | null | undefined): ToolPermissionView {
  const problem: string[] = []
  const invalidPatterns: string[] = []
  const invalidDisabledPatterns: string[] = []
  const patterns = readPatternList(config, 'allowedTools', problem, invalidPatterns)
  const disabledPatterns = readPatternList(config, 'disabledTools', problem, invalidDisabledPatterns)

  const fallbackRaw = config?.allowToolFallbackToGlobal
  let fallback = false
  if (typeof fallbackRaw === 'boolean') fallback = fallbackRaw
  else if (fallbackRaw !== undefined && fallbackRaw !== null) problem.push('allowToolFallbackToGlobal 不是布尔值')

  return {
    patterns,
    disabledPatterns,
    invalidPatterns,
    invalidDisabledPatterns,
    fallback,
    problem: problem.length > 0 ? `${problem.join('；')}，请先修正配置。` : ''
  }
}

export interface ToolPermissionSaveInput {
  allowedCount: number
  disabledCount: number
  /** `config.save` 回执里的 appliedReason；决定「已热生效」还是两种只落盘的提示。 */
  reason: ConfigSaveReason
  path: string
}

/** 保存回执文案：区分「已热生效」「写了别的工作区」「当前工作区未受信」三种情况。 */
export function describeToolPermissionSave(input: ToolPermissionSaveInput): string {
  const changes = [
    input.allowedCount === 0
      ? '已清空白名单（= 不限制工具）'
      : `已保存 ${input.allowedCount} 条白名单模式`,
    input.disabledCount === 0
      ? '已清空硬禁用清单'
      : `已保存 ${input.disabledCount} 条硬禁用模式`
  ]
  const suffix = input.reason === 'applied'
    ? '项目配置已同步到内存，下一回合生效。'
    : input.reason === 'untrusted'
      ? `已写入配置文件（${input.path}），但当前工作区尚未受信任（内存中没有加载配置），`
        + '信任或重新打开该工作区后生效。'
      : `已写入配置文件（${input.path}），但当前引擎驻留的是另一个工作区，`
        + '切换到该工作区后生效。'
  return `${changes.join('，')}；${suffix}`
}

/** 比较两份清单是否完全一致（顺序敏感），用于判断是否有未保存改动。 */
export function sameToolPatterns(left: string[], right: string[]): boolean {
  return left.length === right.length && left.every((item, index) => item === right[index])
}
