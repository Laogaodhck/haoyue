// 图标加载探针：验证 Electron 能否从 asar 内部路径加载 .ico / .png
// 运行：pnpm exec electron scripts/test-icon-load.cjs
const { app, nativeImage } = require('electron')
const { join } = require('node:path')

app.disableHardwareAcceleration()

const ASAR = 'E:\\haoyue-release\\publish\\Haoyue-portable-1.3.2-win-x64\\Haoyue-win-x64\\resources\\app.asar'
const REAL_ICO = 'E:\\GitHub\\haoyue\\haoyue_desktop\\resources\\favicon.ico'

const cases = [
  ['真实文件 ico', REAL_ICO],
  ['asar 内 ico', join(ASAR, 'resources', 'favicon.ico')],
  ['asar 内 png', join(ASAR, 'resources', 'logo.png')],
]

app.whenReady().then(() => {
  for (const [name, p] of cases) {
    try {
      const img = nativeImage.createFromPath(p)
      const size = img.isEmpty() ? null : img.getSize()
      console.log(`[RESULT] ${name}: isEmpty=${img.isEmpty()} size=${size ? `${size.width}x${size.height}` : 'N/A'}`)
    } catch (e) {
      console.log(`[RESULT] ${name}: EXCEPTION ${e.message}`)
    }
  }
  app.quit()
})
