import { describe, expect, it } from 'vitest'
import type { BrowserWindow, IpcMainInvokeEvent } from 'electron'
import { isTrustedIpcSender, requireAbsolutePath } from './ipc-guard.js'

// 只用到类型，运行时不需要 Electron；事件对象按守卫实际读取的字段伪造。

type FakeWebContents = { id: number; mainFrame: object }

function fakeEvent(sender: object, frame: object | null): IpcMainInvokeEvent {
  return { sender, senderFrame: frame } as unknown as IpcMainInvokeEvent
}

function fakeWindow(webContents: FakeWebContents): BrowserWindow {
  return { webContents } as unknown as BrowserWindow
}

describe('isTrustedIpcSender', () => {
  it('accepts the main window top-level frame only', () => {
    const mainFrame = { url: 'file:///app/index.html' }
    const webContents: FakeWebContents = { id: 1, mainFrame }
    const window = fakeWindow(webContents)

    expect(isTrustedIpcSender(fakeEvent(webContents, mainFrame), window)).toBe(true)
    expect(isTrustedIpcSender(fakeEvent(webContents, null), window)).toBe(true)
  })

  it('rejects frames from a webview guest or an iframe', () => {
    const mainFrame = { url: 'file:///app/index.html' }
    const webContents: FakeWebContents = { id: 1, mainFrame }
    const window = fakeWindow(webContents)

    expect(isTrustedIpcSender(fakeEvent({ id: 2 }, mainFrame), window)).toBe(false)
    expect(isTrustedIpcSender(fakeEvent(webContents, { url: 'https://example.com' }), window)).toBe(false)
  })

  it('rejects every sender once the window is gone', () => {
    expect(isTrustedIpcSender(fakeEvent({ id: 1 }, {}), null)).toBe(false)
  })
})

describe('requireAbsolutePath', () => {
  it('accepts absolute paths', () => {
    const absolute = process.platform === 'win32' ? 'C:\\work\\project' : '/work/project'
    expect(requireAbsolutePath(absolute)).toBe(absolute)
  })

  it.each([
    [''],
    ['relative/dir'],
    ['../escape'],
    ['C:'],
    [42],
    [null],
  ])('rejects %p', (value) => {
    expect(() => requireAbsolutePath(value)).toThrow('无效的路径参数')
  })
})
