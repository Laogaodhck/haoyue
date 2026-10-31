import type { DaemonMessage, DaemonRequestOptions } from '../../shared/ipc'
import { unwrapDaemonResult } from '../../shared/ipc'

/**
 * 渲染进程里的 daemon 请求入口：`daemon:request` 回的是结构化结果，
 * 这里还原为消息或抛出带 code/method 的 DaemonRequestError。
 * 组件不要直接调用 `window.haoyue.daemon.request`，否则拿到的是未拆的信封。
 */
export async function daemonRequest(
  method: string,
  params?: Record<string, unknown>,
  options?: DaemonRequestOptions
): Promise<DaemonMessage> {
  const result = options === undefined
    ? await window.haoyue.daemon.request(method, params)
    : await window.haoyue.daemon.request(method, params, options)
  return unwrapDaemonResult(result)
}
