/// 输入框 `@` 文件引用的纯文本逻辑（P1-2.3）。候选列表由 workspace.files 给出，
/// 这里只负责判断「caret 前是不是正在输入一个引用」以及选中后怎么替换那一段。
/// 插入的是工作区相对路径：runtime 的读工具按工作区根解析，模型可以直接 read_file。
export const MENTION_LIMIT = 30

/** caret 之前正在输入的 `@` 片段。 */
export interface MentionToken {
  query: string
  start: number
}

/** caret 之前正在输入的 `@` 片段；不是引用时返回 null。 */
export function mentionToken(text: string, caret: number): MentionToken | null {
  const before = text.slice(0, Math.max(0, Math.min(caret, text.length)))
  const at = before.lastIndexOf('@')
  if (at < 0) return null
  // @ 必须落在行首或分隔符之后：邮箱、URL、git 引用里的 @ 不该弹文件选择。
  const head = at === 0 ? ' ' : before[at - 1] ?? ''
  if (!/[\s([【:：，,、]/.test(head)) return null
  const query = before.slice(at + 1)
  // 引用里出现空白说明那半句已经写完，不是在挑文件。
  return /\s/.test(query) ? null : { query: query.toLocaleLowerCase(), start: at }
}

/** 用 `@path` 替换正在输入的 `@` 片段，光标停在替换结果之后。 */
export function applyMention(
  text: string,
  caret: number,
  start: number,
  path: string
): { text: string; caret: number } {
  const end = Math.max(0, Math.min(caret, text.length))
  // 补一个空格让下一次输入重新回到「非引用」状态；后面已经有空白就别重复加。
  const inserted = /^\s/.test(text.slice(end)) ? `@${path}` : `@${path} `
  return {
    text: `${text.slice(0, start)}${inserted}${text.slice(end)}`,
    caret: start + inserted.length
  }
}
