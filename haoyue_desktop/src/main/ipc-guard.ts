import type { BrowserWindow, IpcMainInvokeEvent } from 'electron'
import { isAbsolute } from 'node:path'

/**
 * 只接受主窗口顶层 frame 的 IPC。内嵌 <webview> 承载的是任意白名单站点：即使
 * 某次配置失误把 preload 挂到了 guest 上，也不该让它借通道读写本机文件。
 */
export function isTrustedIpcSender(event: IpcMainInvokeEvent, window: BrowserWindow | null): boolean {
  const sender = event.sender
  if (!window || sender !== window.webContents) return false
  return event.senderFrame === null || event.senderFrame === sender.mainFrame
}

/** 渲染层传进来的路径必须是绝对路径：相对路径会按主进程 cwd 解析，指向意料之外的位置。 */
export function requireAbsolutePath(value: unknown): string {
  if (typeof value !== 'string' || !value || value.length > 4096 || !isAbsolute(value))
    throw new Error('无效的路径参数')
  return value
}
