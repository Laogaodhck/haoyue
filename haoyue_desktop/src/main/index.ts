import { spawn, type ChildProcess } from 'node:child_process'
import { chmodSync, existsSync, statSync, writeFileSync } from 'node:fs'
import { readFile, stat } from 'node:fs/promises'
import { extname, join, resolve } from 'node:path'
import { release } from 'node:os'
import { app, BrowserWindow, dialog, ipcMain, Menu, nativeTheme, Notification, shell, Tray } from 'electron'
import { electronApp, is, optimizer } from '@electron-toolkit/utils'
import iconPng from '../../resources/logo.png?asset'
import iconIco from '../../resources/favicon.ico?asset'
// Windows 任务栏/托盘必须用多分辨率 .ico（16-256 全套）：256px 单尺寸 PNG 在部分 DPI
// 缩放配置下会被系统解析失败，表现为任务栏图标空白或回退默认图标。
const appIcon = process.platform === 'win32' ? iconIco : iconPng
import type {
  ConnectionSettingsView,
  ConnectionTestResult,
  ConnectionUpdate,
  DaemonMessage,
  DaemonRequestOptions,
  DaemonRpcResult,
  RevertDiffItem,
  UpdateCheckResult
} from '../shared/ipc.js'
import { toDaemonRpcError } from '../shared/ipc.js'
import {
  DaemonClient,
  defaultLocalTarget,
  readDaemonToken,
  targetLabel,
  type DaemonAuth,
  type DaemonTarget
} from './daemon-client.js'
import {
  DEFAULT_CONNECTION_SETTINGS,
  connectionSettingsProblem,
  normalizeConnectionSettings,
  readConnectionSettings,
  writeConnectionSettings,
  type ConnectionSettings
} from './connection-settings.js'
import { openDaemonLogSink, readDaemonLogTail } from './daemon-log.js'
import { getGitHistory, getGitOverview, openProjectTerminal, revertFileDiffs } from './project-tools.js'
import { isTrustedIpcSender, requireAbsolutePath } from './ipc-guard.js'
import { checkForUpdate } from './update-check.js'
import { attachWebviewGuard, checkBrowserUrl } from './web-guard.js'
import {
  destroyComputerOverlay,
  getComputerOverlaySessionId,
  hideComputerOverlay,
  isComputerOverlayActive,
  scheduleHideComputerOverlay,
  showComputerOverlay
} from './computer-overlay.js'

const daemon = new DaemonClient()
// 连接偏好（本机 / 局域网）持久化在 userData/connection.json，与 daemon 自身配置无关。
let connectionSettings: ConnectionSettings = { ...DEFAULT_CONNECTION_SETTINGS }

const connectionFile = (): string => join(app.getPath('userData'), 'connection.json')

function connectionTarget(settings: ConnectionSettings): { target: DaemonTarget; auth: DaemonAuth } {
  return settings.mode === 'lan'
    ? {
        target: { host: settings.host, port: settings.port },
        auth: { kind: 'proof', token: settings.token ?? '' }
      }
    : { target: defaultLocalTarget(), auth: { kind: 'local' } }
}

/** 应用连接偏好到唯一的 DaemonClient（有变化时由 configure 断开旧连接）。 */
function applyConnectionSettings(settings: ConnectionSettings): void {
  connectionSettings = settings
  const { target, auth } = connectionTarget(settings)
  daemon.configure(target, auth)
}

function mergedConnectionUpdate(update: ConnectionUpdate): ConnectionSettings {
  return normalizeConnectionSettings({
    ...connectionSettings,
    mode: update.mode,
    host: update.host,
    port: update.port,
    token: update.token === undefined ? connectionSettings.token : update.token
  })
}

// H26: one-time "minimize to tray or quit" explanation, remembered via a flag file.
const trayFlagPath = (): string => join(app.getPath('userData'), 'tray-close-explained')
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
const maxTotalImageBytes = 40 * 1024 * 1024
const imageMediaTypes: Record<string, string> = {
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.webp': 'image/webp',
  '.gif': 'image/gif'
}
// 语音/视频产物展示通道：只放行工具落盘的媒体扩展名，base64 传输受体积上限约束。
const maxAudioBytes = 30 * 1024 * 1024
const maxVideoBytes = 150 * 1024 * 1024
const audioMediaTypes: Record<string, string> = {
  '.mp3': 'audio/mpeg',
  '.wav': 'audio/wav',
  '.ogg': 'audio/ogg',
  '.opus': 'audio/ogg',
  '.m4a': 'audio/mp4',
  '.flac': 'audio/flac'
}
const videoMediaTypes: Record<string, string> = {
  '.mp4': 'video/mp4',
  '.webm': 'video/webm',
  '.mov': 'video/quicktime',
  '.m4v': 'video/mp4'
}

const delay = (milliseconds: number): Promise<void> =>
  new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds))

// 新版本检查：同一进程内 10 分钟内复用结果，失败不缓存（手动重试应真正重试）。
let updateCache: { at: number; result: UpdateCheckResult } | null = null

async function queryLatestVersion(): Promise<UpdateCheckResult> {
  if (updateCache && Date.now() - updateCache.at < 10 * 60_000) return updateCache.result
  const result = await checkForUpdate({ currentVersion: app.getVersion() })
  if (result.status !== 'unavailable') updateCache = { at: Date.now(), result }
  return result
}

/** 打包版启动时静默检查一次：有新版本才弹原生对话框，其余情况不打扰。 */
async function promptForUpdates(): Promise<void> {
  if (!app.isPackaged) return
  const result = await queryLatestVersion()
  if (result.status !== 'update' || !result.url) return
  const options: Electron.MessageBoxOptions = {
    type: 'info',
    title: '发现新版本',
    message: `Haoyue v${result.version} 已发布`,
    detail: `当前版本 v${app.getVersion()}。可在发布页查看更新说明并下载安装包。`,
    buttons: ['前往下载', '稍后'],
    defaultId: 0,
    cancelId: 1,
    noLink: true
  }
  const { response } = mainWindow
    ? await dialog.showMessageBox(mainWindow, options)
    : await dialog.showMessageBox(options)
  if (response === 0) await shell.openExternal(result.url)
}

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
    } catch { /* read-only filesystems keep their mode */ }
  }
  return found
}

/** Electron 主进程持有的后台日志目录；诊断页与日志落盘共用同一条路径。 */
function daemonLogDir(): string {
  return join(app.getPath('userData'), 'logs')
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
  const daemonLog = openDaemonLogSink(daemonLogDir())
  // stdout 是 pipe 且必须有消费者：以前只排水不落地，但原生层 abort 前的最后一句
  // 断言（显存不足等）往往就在这两条流上，丢掉它「发消息没回复」就只能靠猜。
  child.stdout?.on('data', (chunk) => daemonLog.append(chunk.toString()))

  let startupError: Error | null = null
  let childStderr = ''
  child.stderr?.on('data', (chunk) => {
    const text = chunk.toString()
    daemonLog.append(text)
    childStderr += text
    if (childStderr.length > 4000) childStderr = childStderr.slice(-4000)
  })
  child.once('error', (error) => {
    startupError = error
  })
  child.once('exit', (code, signal) => {
    daemonLog.writeMarker(`daemon exit code=${code ?? 'null'} signal=${signal ?? 'none'}`)
    daemonLog.close()
    if (managedDaemon === child) managedDaemon = null
    if (code !== 0 && code !== null) {
      console.error(`Haoyue Runtime exited with code ${code}. Stderr: ${childStderr}`)
    }
    if (runtimeShutdownStarted || isQuitting) return
    // 桌面已切到局域网模式：本机 Runtime 不再是被使用的一方，崩溃后不自动拉起。
    if (connectionSettings.mode !== 'local') return
    // Crash supervision: restart with capped exponential backoff. A run that lasted
    // over 30s counts as healthy and resets the attempt budget.
    if (Date.now() - daemonStartedAt > 30_000) daemonRestartAttempts = 0
    if (daemonRestartAttempts >= maxDaemonRestartAttempts) {
      const reason = childStderr.split('\n').map((line) => line.trim()).filter(Boolean).pop() ?? ''
      showNativeNotification(
        'Haoyue Runtime 已退出',
        `${reason || `退出码 ${code ?? '未知'}`}（自动重启多次失败，完整记录见 logs/daemon.log）`)
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
    tray = new Tray(appIcon)
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
    icon: appIcon,
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
      nodeIntegration: false,
      // 右侧工作区「浏览」页签用 <webview> 承载页面；附件的安全约束与白名单
      // 边界由 attachWebviewGuard 的 will-attach-webview / will-navigate 收口。
      webviewTag: true
    }
  })

  mainWindow.on('ready-to-show', () => mainWindow?.show())
  mainWindow.on('close', (event) => {
    if (!isQuitting) {
      event.preventDefault()
      // H26: first close asks explicitly whether to keep the app in the tray — without
      // it users see the window vanish and assume the app "ghost"ed (kept running).
      if (!existsSync(trayFlagPath())) {
        const choice = dialog.showMessageBoxSync(mainWindow!, {
          type: 'question',
          buttons: ['最小化到托盘', '退出应用'],
          defaultId: 0,
          cancelId: 0,
          message: '关闭窗口后要继续运行吗？',
          detail: '最小化到托盘会保留 Haoyue Runtime 与正在运行的任务，可从系统托盘重新打开窗口。'
        })
        if (choice === 1) {
          isQuitting = true
          app.quit()
          return
        }
        try { writeFileSync(trayFlagPath(), '1') } catch { /* non-fatal */ }
      }
      mainWindow?.hide()
    }
  })
  mainWindow.on('closed', () => { mainWindow = null })
  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('https://') || url.startsWith('http://')) void shell.openExternal(url)
    return { action: 'deny' }
  })
  // 渲染层是单页应用，任何整页跳转都意味着一次链接误点把应用导航走（返回键都回不来，
  // 且跳转后的页面不再是本应用）。这里拦下并交给系统浏览器，只放行开发服务器的
  // HMR 整页刷新（同一 origin）。
  mainWindow.webContents.on('will-navigate', (event, url) => {
    const current = mainWindow?.webContents.getURL() ?? ''
    const sameOrigin = (() => {
      try { return new URL(url).origin === new URL(current).origin } catch { return false }
    })()
    if (sameOrigin && (url.startsWith('http://localhost') || url.startsWith('http://127.0.0.1'))) return
    event.preventDefault()
    if (url.startsWith('https://') || url.startsWith('http://')) void shell.openExternal(url)
  })
  attachWebviewGuard(mainWindow, daemon)

  if (is.dev && process.env.ELECTRON_RENDERER_URL)
    void mainWindow.loadURL(process.env.ELECTRON_RENDERER_URL)
  else
    void mainWindow.loadFile(join(__dirname, '../renderer/index.html'))
}

/**
 * 只接受主窗口顶层 frame 的 IPC。内嵌 <webview> 承载的是任意白名单站点：即使
 * 某次配置失误把 preload 挂到了 guest 上，也不该让它借通道读写本机文件。
 */
function registerIpc(): void {
  const logDir = daemonLogDir()
  const handle = <TArgs extends unknown[], TResult>(
    channel: string,
    handler: (event: Electron.IpcMainInvokeEvent, ...args: TArgs) => TResult
  ): void => {
    ipcMain.handle(channel, (event: Electron.IpcMainInvokeEvent, ...args: TArgs) => {
      if (!isTrustedIpcSender(event, mainWindow)) throw new Error(`拒绝来自不可信发送方的 IPC 调用：${channel}`)
      return handler(event, ...args)
    })
  }
  handle('app:info', () => ({
    version: app.getVersion(),
    platform: process.platform,
    supportsMica,
    defaultWorkspace: is.dev ? resolve(app.getAppPath(), '..') : app.getPath('userData'),
    documentsPath: app.getPath('documents'),
    userProfilePath: app.getPath('home'),
    logDir,
    daemonLogFile: join(logDir, 'daemon.log')
  }))

  handle('app:daemon-log-tail', (_event, lines: number) => readDaemonLogTail(logDir, Number(lines)))

  handle('app:select-workspace', async () => {
    const options: Electron.OpenDialogOptions = {
      title: '选择项目文件夹',
      properties: ['openDirectory', 'createDirectory']
    }
    const result = mainWindow
      ? await dialog.showOpenDialog(mainWindow, options)
      : await dialog.showOpenDialog(options)
    return result.canceled ? null : result.filePaths[0] ?? null
  })

  handle('app:select-skill-files', async () => {
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

  handle('app:select-model-directory', async () => {
    const options: Electron.OpenDialogOptions = {
      title: '选择模型目录',
      properties: ['openDirectory', 'createDirectory']
    }
    const result = mainWindow
      ? await dialog.showOpenDialog(mainWindow, options)
      : await dialog.showOpenDialog(options)
    return result.canceled ? null : result.filePaths[0] ?? null
  })

  handle('app:select-gguf-file', async () => {
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

  handle('app:select-files', async () => {
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

  handle('app:read-file-base64', async (_event, path: string) => {
    try {
      // 安全边界：该通道面向图片附件，拒绝任意扩展名与超大文件，
      // 防止渲染层被攻破后获得任意文件读取原语。
      const filePath = requireAbsolutePath(path)
      const ext = extname(filePath).toLocaleLowerCase()
      if (!imageMediaTypes[ext]) return null
      const info = await stat(filePath)
      if (!info.isFile() || info.size > maxTotalImageBytes) return null
      const data = await readFile(filePath)
      return {
        data: data.toString('base64'),
        mediaType: imageMediaTypes[ext] || 'image/png',
        sizeBytes: data.byteLength
      }
    } catch {
      return null
    }
  })

  handle('app:read-media-base64', async (_event, path: string) => {
    try {
      // 安全边界：仅放行语音/视频产物扩展名，限制体积，防止渲染层获得任意文件读取原语。
      const filePath = requireAbsolutePath(path)
      const ext = extname(filePath).toLocaleLowerCase()
      const mediaType = audioMediaTypes[ext] || videoMediaTypes[ext]
      if (!mediaType) return null
      const info = await stat(filePath)
      if (!info.isFile()) return null
      const cap = audioMediaTypes[ext] ? maxAudioBytes : maxVideoBytes
      if (info.size > cap) return null
      const data = await readFile(filePath)
      return {
        data: data.toString('base64'),
        mediaType,
        sizeBytes: data.byteLength
      }
    } catch {
      return null
    }
  })

  handle('app:show-item', async (_event, path: string) => {
    shell.showItemInFolder(requireAbsolutePath(path))
  })

  handle('app:save-text-file', async (_event, defaultName: string, content: string) => {
    const options: Electron.SaveDialogOptions = {
      title: '保存文件',
      defaultPath: String(defaultName || 'export.txt')
    }
    const result = mainWindow
      ? await dialog.showSaveDialog(mainWindow, options)
      : await dialog.showSaveDialog(options)
    if (result.canceled || !result.filePath) return null
    writeFileSync(result.filePath, String(content), 'utf-8')
    return result.filePath
  })

  handle('app:close', () => mainWindow?.close())

  handle('app:open-devtools', () => {
    mainWindow?.webContents.openDevTools({ mode: 'detach' })
  })

  handle('app:notify', (_event, title: string, body: string) => {
    if (typeof title === 'string' && typeof body === 'string') showNativeNotification(title, body)
  })

  handle('app:set-theme', (_event, theme: 'system' | 'light' | 'dark') => {
    if (theme === 'system' || theme === 'light' || theme === 'dark') {
      nativeTheme.themeSource = theme
      syncNativeWindowTheme()
    }
  })

  handle('app:check-update', () => queryLatestVersion())

  handle('project:open-terminal', (_event, path: string) => openProjectTerminal(requireAbsolutePath(path)))
  handle('project:git-overview', (_event, path: string) => getGitOverview(requireAbsolutePath(path)))
  handle('project:git-history', (_event, path: string) => getGitHistory(requireAbsolutePath(path)))
  handle('project:revert-file-diffs', (_event, workspace: string, patches: RevertDiffItem[]) =>
    revertFileDiffs(requireAbsolutePath(workspace), patches)
  )

  // 可视化浏览器：导航前校验白名单（与无头 browser 工具同一份 config.web.allowedSites）。
  handle('web:check-url', (_event, url: string) => checkBrowserUrl(daemon, url))
  handle('web:open-external', (_event, url: string) => {
    if (!url.startsWith('https://') && !url.startsWith('http://')) return undefined
    return shell.openExternal(url)
  })

  handle('daemon:connect', () => daemon.connect())

  handle('connection:get', (): ConnectionSettingsView => ({
    mode: connectionSettings.mode,
    host: connectionSettings.host,
    port: connectionSettings.port,
    hasToken: Boolean(connectionSettings.token)
  }))

  handle('connection:set', async (_event, update: ConnectionUpdate): Promise<{ ok: boolean; error?: string }> => {
    const candidate = mergedConnectionUpdate(update)
    const problem = connectionSettingsProblem(candidate)
    if (problem) return { ok: false, error: problem }
    if (candidate.mode === 'lan' && !candidate.token) return { ok: false, error: '请填写局域网 Runtime 的握手令牌' }

    writeConnectionSettings(connectionFile(), candidate)
    applyConnectionSettings(candidate)
    if (candidate.mode === 'lan') {
      void daemon.connect().catch(() => undefined)
      return { ok: true }
    }
    try {
      await ensureDaemonRunning()
      return { ok: true }
    } catch (error) {
      return { ok: false, error: error instanceof Error ? error.message : String(error) }
    }
  })

  // 试连使用独立的客户端实例：不能打断当前连接，也不改变已保存的设置。
  handle('connection:test', async (_event, update: ConnectionUpdate): Promise<ConnectionTestResult> => {
    const candidate = mergedConnectionUpdate(update)
    const problem = connectionSettingsProblem(candidate)
    if (problem) return { ok: false, endpoint: '', error: problem }
    if (candidate.mode === 'lan' && !candidate.token) {
      return { ok: false, endpoint: '', error: '请填写局域网 Runtime 的握手令牌' }
    }
    const { target, auth } = connectionTarget(candidate)
    const endpoint = targetLabel(target)
    const probe = new DaemonClient({ target, auth })
    try {
      const state = await probe.connect()
      return state.connected
        ? { ok: true, endpoint, protocolVersion: state.protocolVersion }
        : { ok: false, endpoint, error: state.error ?? '无法连接' }
    } catch (error) {
      return { ok: false, endpoint, error: error instanceof Error ? error.message : String(error) }
    } finally {
      probe.disconnect()
    }
  })

  handle('connection:reveal-token', () => connectionSettings.token)
  handle('connection:local-token', () => readDaemonToken())
  // 结构化回包：ipcMain.handle 的 reject 会把 Error 压成字符串，code/method 到不了渲染进程。
  handle(
    'daemon:request',
    async (
      _event,
      method: string,
      params?: Record<string, unknown>,
      options?: DaemonRequestOptions
    ): Promise<DaemonRpcResult> => {
      try {
        return { ok: true, message: await daemon.request(method, params, options) }
      } catch (error) {
        return { ok: false, error: toDaemonRpcError(error, method) }
      }
    })

  daemon.on('event', (message) => {
    // 浮层绑定发起电脑操作的会话：别的会话跑完工具时不能把它收走。
    const overlaySessionId = getComputerOverlaySessionId()
    const fromOverlaySession = overlaySessionId === undefined || message.sessionId === overlaySessionId
    if (message.event === 'tool_start' && (message.data === 'computer' || message.data === 'computer_inspect')) {
      // 必须带上 sessionId：daemon 的 agent.cancel 在 params 为空时会取消「本连接上
      // 所有活跃回合」（见 DaemonServer 该分支的兜底语义），浮层「停止」会顺手杀掉
      // 另一个会话正在跑的任务。
      const overlaySession = message.sessionId
      showComputerOverlay(() => {
        void daemon
          .request('agent.cancel', overlaySession ? { sessionId: overlaySession } : undefined)
          .catch(() => undefined)
      }, overlaySession)
    } else if (message.event === 'tool_done' && fromOverlaySession && (message.data?.includes('computer') || isComputerOverlayActive())) {
      scheduleHideComputerOverlay(900)
    } else if ((message.event === 'done' || message.event === 'cancelled' || message.event === 'error') && fromOverlaySession) {
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
    // AUMID 必须与已注册的快捷方式一致，否则 Windows 任务栏图标会解析为空白：
    // - 安装版（NSIS 装到 %LOCALAPPDATA%\Programs\Haoyue）：快捷方式由 electron-builder 注册为 appId
    // - 便携版/开发版：AUMID 从未注册，回退到 exe 路径（Electron 默认可解析出 exe 内嵌图标）
    const installedExe = join(process.env.LOCALAPPDATA ?? '', 'Programs', 'Haoyue', 'Haoyue.exe')
    const isInstalledCopy =
      process.platform === 'win32' &&
      existsSync(installedExe) &&
      installedExe.toLowerCase() === process.execPath.toLowerCase()
    electronApp.setAppUserModelId(isInstalledCopy ? 'com.hoilai.haoyue' : process.execPath)
    app.on('browser-window-created', (_, window) => optimizer.watchWindowShortcuts(window))
    createTray()
    // 连接偏好必须在任何 daemon 访问之前应用：它决定 DaemonClient 连本机还是局域网。
    applyConnectionSettings(readConnectionSettings(connectionFile()))
    if (connectionSettings.mode === 'lan') {
      // 远程模式：不拉起本机 Runtime，也不弹启动失败框；连不上由设置页与重连提示引导。
      if (connectionSettings.host) void daemon.connect().catch(() => undefined)
    } else {
      await ensureDaemonRunning().catch((error) => {
        console.error('Unable to start Haoyue Runtime:', error)
        // 启动失败必须让用户看到，而不是永远停留在笼统的「连接失败」。
        dialog.showErrorBox(
          'Haoyue Runtime 启动失败',
          error instanceof Error ? error.message : String(error)
        )
      })
    }
    createWindow()
    nativeTheme.on('updated', syncNativeWindowTheme)
    // 不阻塞启动：网络慢/离线时检查自然降级为静默。
    void promptForUpdates().catch(() => undefined)

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
