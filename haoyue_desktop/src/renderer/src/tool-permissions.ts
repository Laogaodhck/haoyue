/**
 * 项目级工具权限（`<workspace>/.haoyue/config.json` 的 `allowedTools` /
 * `allowToolFallbackToGlobal`）的读写辅助。
 *
 * 校验语义必须与 runtime 侧 `ToolPermissionPolicy.IsValidPattern` 保持一致：
 * 界面在保存前拦一次，服务端在写盘前再拦一次——否则用户以为已经收紧，
 * 实际写进去的却是被静默忽略的垃圾模式。
 */
import type { ConfigObject } from './config-patch'

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
  fallback: boolean
  /** 磁盘上的字段本身不合法时的说明（例如 allowedTools 不是字符串数组）。 */
  problem: string
}

/** 从工作区配置文档里读出工具权限字段；缺省/空都表示「不限制」。 */
export function readToolPermissions(config: ConfigObject | null | undefined): ToolPermissionView {
  const problem: string[] = []
  let patterns: string[] = []
  const raw = config?.allowedTools
  if (raw !== undefined && raw !== null) {
    if (!Array.isArray(raw)) {
      problem.push('allowedTools 不是字符串数组')
    } else if (!raw.every((item) => typeof item === 'string')) {
      problem.push('allowedTools 含有非字符串条目')
      patterns = raw.filter((item): item is string => typeof item === 'string')
    } else {
      patterns = [...raw]
    }
  }

  const fallbackRaw = config?.allowToolFallbackToGlobal
  let fallback = false
  if (typeof fallbackRaw === 'boolean') fallback = fallbackRaw
  else if (fallbackRaw !== undefined && fallbackRaw !== null) problem.push('allowToolFallbackToGlobal 不是布尔值')

  return { patterns, fallback, problem: problem.length > 0 ? `${problem.join('；')}，请先修正配置。` : '' }
}

/** 比较两份清单是否完全一致（顺序敏感），用于判断是否有未保存改动。 */
export function sameToolPatterns(left: string[], right: string[]): boolean {
  return left.length === right.length && left.every((item, index) => item === right[index])
}
