/**
 * 配置字段补丁的前端通道。
 *
 * 背景：`config.save` 是 daemon 侧的「字段级深合并」写入口（多端单写者收敛），此前只有
 * CLI 在委派保存时用它；桌面端大量配置项因此没有界面入口。这里把它包装成两个原语：
 *
 * - `loadConfigDocument(workspace?)` → `config.get`，读全局或某个项目的配置文档；
 * - `saveConfigPatch(patch, workspace?)` → 只提交**变更的叶子节点**。
 *
 * 「只提交变更叶子」是安全前提：读侧不回传密钥（`ApiKey` 被移除、Authorization 类头被
 * 置空），如果把整份文档原样写回，就会把这些字段擦掉。所以写前必须 diff。
 */
import { requestJson } from './daemon-json'

export type ConfigObject = Record<string, unknown>

export interface ConfigDocument {
  scope: 'global' | 'workspace'
  path: string
  exists: boolean
  config: ConfigObject
}

export async function loadConfigDocument(workspace?: string): Promise<ConfigDocument> {
  return requestJson<ConfigDocument>('config.get', workspace ? { workspace } : {})
}

/**
 * 递归 diff：返回 `after` 相对 `before` 的变更叶子。
 * 数组按整体替换处理（配置里的数组都是有序列表，逐项 diff 只会制造歧义）。
 * 返回空对象表示没有变更——调用方应跳过写请求。
 */
export function diffPatch(before: unknown, after: unknown): ConfigObject {
  const patch: ConfigObject = {}
  if (!isPlainObject(after)) return patch
  const base = isPlainObject(before) ? before : {}
  for (const [key, next] of Object.entries(after)) {
    const previous = (base as ConfigObject)[key]
    if (isPlainObject(next) && isPlainObject(previous)) {
      const nested = diffPatch(previous, next)
      if (Object.keys(nested).length > 0) patch[key] = nested
      continue
    }
    if (deepEqual(previous, next)) continue
    patch[key] = next
  }
  return patch
}

function isPlainObject(value: unknown): value is ConfigObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function deepEqual(left: unknown, right: unknown): boolean {
  if (left === right) return true
  if (Array.isArray(left) && Array.isArray(right))
    return left.length === right.length && left.every((item, index) => deepEqual(item, right[index]))
  if (isPlainObject(left) && isPlainObject(right)) {
    const leftKeys = Object.keys(left)
    const rightKeys = Object.keys(right)
    return leftKeys.length === rightKeys.length
      && leftKeys.every((key) => key in right && deepEqual(left[key], right[key]))
  }
  return false
}

/**
 * 写补丁。`patch` 为空时直接返回 false（不产生无意义的写帧）。
 * 返回 true 表示 daemon 已合并落盘。
 */
export async function saveConfigPatch(patch: ConfigObject, workspace?: string): Promise<boolean> {
  if (Object.keys(patch).length === 0) return false
  await requestJson<string>('config.save', {
    fields: patch,
    ...(workspace ? { workspace } : {})
  })
  return true
}
