import { spawn, type ChildProcess } from 'node:child_process'
import { chmodSync, existsSync, statSync } from 'node:fs'
import { readFile, stat } from 'node:fs/promises'
import { basename, extname, join, resolve } from 'node:path'
import { release } from 'node:os'
import { app, BrowserWindow, dialog, ipcMain, Menu, nativeTheme, Notification, shell, Tray } from 'electron'
import { electronApp, is, optimizer } from '@electron-toolkit/utils'
import icon from '../../resources/logo.png?asset'
import type { DaemonMessage, DaemonRequestOptions, RevertDiffItem } from '../shared/ipc.js'
import { DaemonClient } from './daemon-client.js'
import { getGitHistory, getGitOverview, openProjectTerminal, revertFileDiffs } from './project-tools.js'
import {
  destroyComputerOverlay,
  hideComputerOverlay,
  isComputerOverlayActive,
  scheduleHideComputerOverlay,
  showComputerOverlay
} from './computer-overlay.js'

const daemon = new DaemonClient()
let mainWindow: BrowserWindow | null = null
let tray: Tray | null = null
let isQuitting = false
let managedDaemon: ChildProcess | null = null
let runtimeShutdownStarted = false
// Supervised restart state: a crash of the bundled runtime must not brick the app
// (previously the daemon was spawned exactly once and never re-spawned).
let daemonRestartTimer: NodeJS.Timeout | null = null
let daemonRestartAttempts = 0
let daemonStartedAt = 0
const maxDaemonRestartAttempts = 5
const activeNotifications = new Set<Notification>()
const supportsMica = process.platform === 'win32' && Number(release().split('.')[2] ?? 0) >= 22000
const maxImageCount = 10
const maxImageBytes = 10 * 1024 * 1024
const maxTotalImageBytes = 40 * 1024 * 1024
const imageMediaTypes: Record<string, string> = {
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.webp': 'image/webp',
  '.gif': 'image/gif'
}

const delay = (milliseconds: number): Promise<void> =>
  new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds))

function resolveRuntimeExecutable(): string | null {
  const executable = process.platform === 'win32' ? 'haoyue.exe' : 'haoyue'
  const configured = process.env.HAOYUE_RUNTIME_EXECUTABLE?.trim()
  const platformRid = process.platform === 'win32' ? 'win-x64' : 'linux-x64'
  const candidates = [
    configured ? resolve(configured) : '',
    app.isPackaged ? join(process.resourcesPath, 'runtime', executable) : '',
    !app.isPackaged ? resolve(app.getAppPath(), 'runtime', platformRid, executable) : '',
    !app.isPackaged ? resolve(app.getAppPath(), 'runtime', 'win-x64', executable) : '',
    !app.isPackaged
      ? resolve(app.getAppPath(), '..', 'haoyue_cli', 'bin', 'Debug', 'net10.0', executable)
      : '',
    !app.isPackaged
      ? resolve(app.getAppPath(), '..', 'haoyue_cli', 'bin', 'Release', 'net10.0', platformRid, 'publish', executable)
      : '',
    !app.isPackaged
      ? resolve(app.getAppPath(), '..', 'haoyue_cli', 'bin', 'Release', 'net10.0', 'win-x64', 'publish', executable)
      : ''
  ].filter(Boolean)
  const found = candidates.find((candidate) => existsSync(candidate)) ?? null
  if (found && process.platform !== 'win32') {
    try {
      const stat = statSync(found)
      if ((stat.mode & 0o111) === 0) {
        chmodSync(found, 0o755)
      }
    } catch {}
  }
  return found
}

async function ensureDaemonRunning(): Promise<void> {
  const existing = await daemon.connect()
  if (existing.connected) return

  const executable = resolveRuntimeExecutable()
  if (!executable) {
    throw new Error('Bundled Haoyue Runtime was not found. Rebuild the Desktop release package.')
  }

  const child = spawn(executable, ['daemon'], {
    cwd: is.dev ? resolve(app.getAppPath(), '..') : app.getPath('userData'),
    env: {
      ...process.env,
      HAOYUE_MANAGED_BY_DESKTOP: '1',
      DOTNET_SYSTEM_GLOBALIZATION_INVARIANT: '1',
      DOTNET_BUNDLE_EXTRACT_BASE_DIR: join(app.getPath('userData'), 'dotnet_bundle')
    },
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true
  })
  managedDaemon = child
  // stdout 是 pipe 但没有任何消费者：一旦 runtime 开始向 stdout 输出（日志/异常），
  // 管道缓冲写满即阻塞 daemon 进程。保持排水，避免潜在的整进程假死。
  child.stdout?.resume()

  let startupError: Error | null = null
  let childStderr = ''
  child.stderr?.on('data', (chunk) => {
    childStderr += chunk.toString()
    if (childStderr.length > 4000) childStderr = childStderr.slice(-4000)
  })
  child.once('error', (error) => {
    startupError = error
  })
  child.once('exit', (code) => {
    if (managedDaemon === child) managedDaemon = null
    if (code !== 0 && code !== null) {
      console.error(`Haoyue Runtime exited with code ${code}. Stderr: ${childStderr}`)
    }
    if (runtimeShutdownStarted || isQuitting) return
    // Crash supervision: restart with capped exponential backoff. A run that lasted
    // over 30s counts as healthy and resets the attempt budget.
    if (Date.now() - daemonStartedAt > 30_000) daemonRestartAttempts = 0
    if (daemonRestartAttempts >= maxDaemonRestartAttempts) {
      showNativeNotification('Haoyue Runtime 已退出', '自动重启多次失败，请重启 Haoyue 应用。')
      return
    }
    daemonRestartAttempts++
    const backoff = Math.min(30_000, 1_000 * 2 ** (daemonRestartAttempts - 1))
    if (daemonRestartTimer) clearTimeout(daemonRestartTimer)
    daemonRestartTimer = setTimeout(() => {
      daemonRestartTimer = null
      ensureDaemonRunning().catch((error) => {
        console.error('Haoyue Runtime restart failed:', error)
        showNativeNotification('Haoyue Runtime 重启失败', error instanceof Error ? error.message : String(error))
      })
    }, backoff)
  })

  for (let attempt = 0; attempt < 28; attempt++) {
    await delay(attempt === 0 ? 150 : 250)
    if (startupError) throw startupError
    if (child.exitCode !== null) {
      throw new Error(`Haoyue Runtime exited with code ${child.exitCode}: ${childStderr.trim() || 'unknown error'}`)
    }
    const state = await daemon.connect()
    if (state.connected) {
      daemonStartedAt = Date.now()
      daemonRestartAttempts = 0
      return
    }
  }

  if (child.exitCode === null) child.kill()
  throw new Error(`Haoyue Runtime did not become ready in time. ${childStderr.trim() ? 'Stderr: ' + childStderr.trim() : ''}`)
}

async function stopManagedDaemon(): Promise<void> {
  const child = managedDaemon
  if (!child) {
    daemon.disconnect()
    return
  }

  await Promise.race([
    daemon.request('shutdown').catch(() => undefined),
    delay(1_000)
  ])
  daemon.disconnect()

  if (child.exitCode === null) {
    await Promise.race([
      new Promise<void>((resolveExit) => child.once('exit', () => resolveExit())),
      delay(1_500)
    ])
  }
  if (child.exitCode === null) child.kill()
  if (managedDaemon === child) managedDaemon = null
}

function nativeWindowColors(): { background: string; titlebar: string; symbols: string } {
  const dark = nativeTheme.shouldUseDarkColors
  return {
    background: dark ? '#181818' : '#f7f7f7',
    titlebar: dark ? '#202020' : '#f3f3f3',
    symbols: dark ? '#f2f2f2' : '#343434'
  }
}

function syncNativeWindowTheme(): void {
  if (!mainWindow) return
  const colors = nativeWindowColors()
  if (!supportsMica) mainWindow.setBackgroundColor(colors.background)
  if (process.platform !== 'darwin') {
    mainWindow.setTitleBarOverlay({
      color: supportsMica ? '#00000000' : colors.titlebar,
      symbolColor: colors.symbols,
      height: 42
    })
  }
}

function notificationText(message: DaemonMessage, key: string): string {
  const value = message.details?.[key]
  return typeof value === 'string' ? value : ''
}

function showMainWindow(): void {
  if (!mainWindow) {
    createWindow()
    return
  }
  if (mainWindow.isMinimized()) mainWindow.restore()
  if (!mainWindow.isVisible()) mainWindow.show()
  mainWindow.focus()
}

function createTray(): void {
  if (tray) return
  try {
    tray = new Tray(icon)
    tray.setToolTip('Haoyue（浩玥）')

    const contextMenu = Menu.buildFromTemplate([
      {
        label: '显示',
        click: () => showMainWindow()
      },
      { type: 'separator' },
      {
        label: '退出',
        click: () => {
          isQuitting = true
          app.quit()
        }
      }
    ])

    tray.setContextMenu(contextMenu)

    tray.on('click', () => {
      showMainWindow()
    })

    tray.on('double-click', () => {
      showMainWindow()
    })
  } catch (error) {
    console.warn('System tray initialization skipped (not supported or failed):', error)
    tray = null
  }
}

function showNativeNotification(title: string, body: string): void {
  if (!Notification.isSupported()) return
  const notification = new Notification({ title, body })
  activeNotifications.add(notification)
  notification.once('click', () => {
    showMainWindow()
  })
  notification.once('close', () => activeNotifications.delete(notification))
  notification.show()
}

function showScheduleNativeNotification(message: DaemonMessage): void {
  if (message.event === 'schedule.upcoming') {
    const name = notificationText(message, 'name') || '计划任务'
    showNativeNotification('计划任务提醒', `一分钟后「${name}」将自动执行`)
    return
  }

  if (message.event !== 'schedule.updated') return
  const name =
    notificationText(message, 'name')
    || notificationText(message, 'taskId')
    || '计划任务'
  const status = notificationText(message, 'status')
  const error = notificationText(message, 'error')

  if (status === 'cancelled') {
    showNativeNotification('计划任务已取消', `「${name}」已取消`)
    return
  }
  if (status === 'error') {
    const suffix = error ? `：${error}` : ''
    showNativeNotification('计划任务执行失败', `「${name}」执行失败${suffix}`)
    return
  }
  showNativeNotification('计划任务完成', `「${name}」已完成`)
}

function createWindow(): void {
  const colors = nativeWindowColors()
  mainWindow = new BrowserWindow({
    width: 1280,
    height: 820,
    minWidth: 760,
    minHeight: 620,
    show: false,
    backgroundColor: supportsMica ? '#00000000' : colors.background,
    ...(supportsMica ? { backgroundMaterial: 'mica' as const } : {}),
    title: 'Haoyue（浩玥）',
    icon,
    titleBarStyle: 'hidden',
    titleBarOverlay: process.platform === 'darwin' ? false : {
      color: supportsMica ? '#00000000' : colors.titlebar,
      symbolColor: colors.symbols,
      height: 42
    },
    webPreferences: {
      preload: join(__dirname, '../preload/index.cjs'),
      sandbox: true,
      contextIsolation: true,
      nodeIntegration: false
    }
  })

  mainWindow.on('ready-to-show', () => mainWindow?.show())
  mainWindow.on('close', (event) => {
    if (!isQuitting) {
      event.preventDefault()
      mainWindow?.hide()
    }
  })
  mainWindow.on('closed', () => { mainWindow = null })
  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('https://') || url.startsWith('http://')) void shell.openExternal(url)
    return { action: 'deny' }
  })

  if (is.dev && process.env.ELECTRON_RENDERER_URL)
    void mainWindow.loadURL(process.env.ELECTRON_RENDERER_URL)
  else
    void mainWindow.loadFile(join(__dirname, '../renderer/index.html'))
}

function registerIpc(): void {
  ipcMain.handle('app:info', () => ({
    version: app.getVersion(),
    platform: process.platform,
    supportsMica,
    defaultWorkspace: is.dev ? resolve(app.getAppPath(), '..') : app.getPath('userData'),
    documentsPath: app.getPath('documents'),
    userProfilePath: app.getPath('home')
  }))

  ipcMain.handle('app:select-workspace', async () => {
    const options: Electron.OpenDialogOptions = {
      title: 'Open workspace',
      properties: ['openDirectory', 'createDirectory']
    }
    const result = mainWindow
      ? await dialog.showOpenDialog(mainWindow, options)
      : await dialog.showOpenDialog(options)
    return result.canceled ? null : result.filePaths[0] ?? null
  })

  ipcMain.handle('app:select-images', async () => {
    const options: Electron.OpenDialogOptions = {
      title: '选择图片',
      filters: [{ name: '图片', extensions: ['png', 'jpg', 'jpeg', 'webp', 'gif'] }],
      properties: ['openFile', 'multiSelections']
    }
    const result = mainWindow
      ? await dialog.showOpenDialog(mainWindow, options)
      : await dialog.showOpenDialog(options)
    if (result.canceled) return { images: [] }

    const images: Array<{ name: string; mediaType: string; data: string; sizeBytes: number }> = []
    const warnings: string[] = []
    let totalBytes = 0
    if (result.filePaths.length > maxImageCount)
      warnings.push(`一次最多添加 ${maxImageCount} 张图片，已保留前 ${maxImageCount} 张。`)
    for (const path of result.filePaths.slice(0, maxImageCount)) {
      const name = basename(path)
      const mediaType = imageMediaTypes[extname(path).toLocaleLowerCase()]
      if (!mediaType) {
        warnings.push(`${name} 的格式暂不支持。`)
        continue
      }
      let data: Buffer
      try { data = await readFile(path) }
      catch {
        warnings.push(`${name} 无法读取，未添加。`)
        continue
      }
      if (data.byteLength > maxImageBytes) {
        warnings.push(`${name} 超过 10 MB，未添加。`)
        continue
      }
      if (totalBytes + data.byteLength > maxTotalImageBytes) {
        warnings.push(`图片合计不能超过 40 MB，${name} 未添加。`)
        continue
      }
      totalBytes += data.byteLength
      images.push({ name, mediaType, data: data.toString('base64'), sizeBytes: data.byteLength })
    }
    return { images, warning: warnings.join(' ') || undefined }
  })

  ipcMain.handle('app:select-skill-files', async () => {
    const options: Electron.OpenDialogOptions = {
      title: '导入全局技能',
      filters: [{ name: 'Skill 文件', extensions: ['md', 'zip'] }],
      properties: ['openFile', 'multiSelections']
    }
    const result = mainWindow
      ? await dialog.showOpenDialog(mainWindow, options)
      : await dialog.showOpenDialog(options)
    if (result.canceled) return { paths: [] }
    return { paths: result.filePaths }
  })

  ipcMain.handle('app:select-model-directory', async () => {
    const options: Electron.OpenDialogOptions = {
      title: '选择模型目录',
      properties: ['openDirectory', 'createDirectory']
    }
    const result = mainWindow
      ? await dialog.showOpenDialog(mainWindow, options)
      : await dialog.showOpenDialog(options)
    return result.canceled ? null : result.filePaths[0] ?? null
  })

  ipcMain.handle('app:select-gguf-file', async () => {
    const options: Electron.OpenDialogOptions = {
      title: '选择 GGUF 模型文件',
      filters: [{ name: 'GGUF 模型', extensions: ['gguf'] }],
      properties: ['openFile']
    }
    const result = mainWindow
      ? await dialog.showOpenDialog(mainWindow, options)
      : await dialog.showOpenDialog(options)
    return result.canceled ? null : result.filePaths[0] ?? null
  })

  ipcMain.handle('app:select-files', async () => {
    const options: Electron.OpenDialogOptions = {
      title: '选择文件',
      properties: ['openFile', 'multiSelections']
    }
    const result = mainWindow
      ? await dialog.showOpenDialog(mainWindow, options)
      : await dialog.showOpenDialog(options)
    if (result.canceled) return []
    return result.filePaths
  })

  ipcMain.handle('app:read-file-base64', async (_event, path: string) => {
    try {
      // 安全边界：该通道面向图片附件，拒绝任意扩展名与超大文件，
      // 防止渲染层被攻破后获得任意文件读取原语。
      const ext = extname(String(path)).toLocaleLowerCase()
      if (!imageMediaTypes[ext]) return null
      const info = await stat(String(path))
      if (!info.isFile() || info.size > maxTotalImageBytes) return null
      const data = await readFile(path)
      return {
        data: data.toString('base64'),
        mediaType: imageMediaTypes[ext] || 'image/png',
        sizeBytes: data.byteLength
      }
    } catch {
      return null
    }
  })

  ipcMain.handle('app:show-item', async (_event, path: string) => {
    shell.showItemInFolder(path)
  })

  ipcMain.handle('app:close', () => mainWindow?.close())

  ipcMain.handle('app:open-devtools', () => {
    mainWindow?.webContents.openDevTools({ mode: 'detach' })
  })

  ipcMain.handle('app:notify', (_event, title: string, body: string) => {
    if (typeof title === 'string' && typeof body === 'string') showNativeNotification(title, body)
  })

  ipcMain.handle('app:set-theme', (_event, theme: 'system' | 'light' | 'dark') => {
    if (theme === 'system' || theme === 'light' || theme === 'dark') {
      nativeTheme.themeSource = theme
      syncNativeWindowTheme()
    }
  })

  ipcMain.handle('project:open-terminal', (_event, path: string) => openProjectTerminal(path))
  ipcMain.handle('project:git-overview', (_event, path: string) => getGitOverview(path))
  ipcMain.handle('project:git-history', (_event, path: string) => getGitHistory(path))
  ipcMain.handle('project:revert-file-diffs', (_event, workspace: string, patches: RevertDiffItem[]) =>
    revertFileDiffs(workspace, patches)
  )

  ipcMain.handle('daemon:connect', () => daemon.connect())
  ipcMain.handle('daemon:disconnect', () => daemon.disconnect())
  ipcMain.handle(
    'daemon:request',
    (_event, method: string, params?: Record<string, unknown>, options?: DaemonRequestOptions) =>
      daemon.request(method, params, options))

  daemon.on('event', (message) => {
    if (message.event === 'tool_start' && (message.data === 'computer' || message.data === 'computer_inspect')) {
      showComputerOverlay(() => {
        void daemon.request('agent.cancel').catch(() => undefined)
      })
    } else if (message.event === 'tool_done' && (message.data?.includes('computer') || isComputerOverlayActive())) {
      scheduleHideComputerOverlay(900)
    } else if (message.event === 'done' || message.event === 'cancelled' || message.event === 'error') {
      hideComputerOverlay(true)
    }

    showScheduleNativeNotification(message)
    mainWindow?.webContents.send('daemon:event', message)
  })
  daemon.on('state', (state) => mainWindow?.webContents.send('daemon:state', state))
}

const hasSingleInstanceLock = app.requestSingleInstanceLock()
if (!hasSingleInstanceLock) {
  app.quit()
} else {
  app.on('second-instance', () => {
    showMainWindow()
  })

  app.whenReady().then(async () => {
    registerIpc()
    electronApp.setAppUserModelId('com.hoilai.haoyue')
    app.on('browser-window-created', (_, window) => optimizer.watchWindowShortcuts(window))
    createTray()
    await ensureDaemonRunning().catch((error) => {
      console.error('Unable to start Haoyue Runtime:', error)
      // 启动失败必须让用户看到，而不是永远停留在笼统的「连接失败」。
      dialog.showErrorBox(
        'Haoyue Runtime 启动失败',
        error instanceof Error ? error.message : String(error)
      )
    })
    createWindow()
    nativeTheme.on('updated', syncNativeWindowTheme)

    app.on('activate', () => {
      showMainWindow()
    })
  })
}

app.on('before-quit', (event) => {
  isQuitting = true
  if (daemonRestartTimer) {
    clearTimeout(daemonRestartTimer)
    daemonRestartTimer = null
  }
  destroyComputerOverlay()
  if (tray) {
    tray.destroy()
    tray = null
  }
  if (!managedDaemon || runtimeShutdownStarted) {
    daemon.disconnect()
    return
  }
  event.preventDefault()
  runtimeShutdownStarted = true
  void stopManagedDaemon().finally(() => app.quit())
})

app.on('window-all-closed', () => {
  if (isQuitting) app.quit()
})
