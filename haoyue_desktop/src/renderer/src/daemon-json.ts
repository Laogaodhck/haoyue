import { daemonRequest } from './daemon-rpc'
/// daemon JSON-RPC 的便捷封装：发出请求并把 `result` 的 data 字符串反序列化为对象。
/// 从 SettingsDialog 下沉共享（ToolPolicyPanel 等新面板复用，避免每个面板复制实现）。
export async function requestJson<T>(
  method: string,
  params: Record<string, unknown> = {},
  timeoutMs?: number
): Promise<T> {
  const response = await daemonRequest(method, params, timeoutMs ? { timeoutMs } : undefined)
  return JSON.parse(response.data) as T
}
