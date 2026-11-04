import { ref, type Ref } from 'vue'
import { daemonRequest } from './daemon-rpc'
import { defaultTaskMonitorConfig, type TaskMonitorConfig, type WorkDockTab } from './types'

/**
 * 任务监控偏好（固定/浮动、固定模式默认展示）的桌面侧状态：
 * 启动时读一次 advanced.get；header 面板按钮按形式分流（浮动 → 悬浮窗，固定 → 右栏）；
 * 「固定模式默认展示 = 展示」在打开右栏时把默认页签切到任务监控。
 * 面板组件自身（TaskMonitorPanel / SettingsTaskMonitorPanel）各自再读配置，
 * 本模块只负责外壳层的分流与默认页签。
 */
export function createMonitorPreferences() {
  const config = ref<TaskMonitorConfig>(defaultTaskMonitorConfig())
  /** 浮动模式悬浮窗的显隐（独立于右栏）。 */
  const floatingOpen = ref(false)

  async function load(): Promise<void> {
    try {
      const advanced = await daemonRequest('advanced.get', {})
      const payload = JSON.parse(advanced.data) as { taskMonitor?: TaskMonitorConfig }
      config.value = { ...defaultTaskMonitorConfig(), ...payload.taskMonitor }
    } catch {
      config.value = defaultTaskMonitorConfig()
    }
  }

  /** header「面板」按钮：浮动模式切换悬浮窗；固定模式走右栏开关。 */
  function togglePanel(toggleWorkDock: () => void): void {
    if (config.value.mode === 'floating') {
      floatingOpen.value = !floatingOpen.value
      return
    }
    toggleWorkDock()
  }

  /** 固定模式默认展示 = 展示：打开右栏时默认选中任务监控页签。 */
  function applyDefaultTab(open: boolean, workDockTab: Ref<WorkDockTab>): void {
    if (!open) return
    if (config.value.mode === 'fixed' && config.value.fixedDefaultShow === 'show') {
      workDockTab.value = 'monitor'
    }
  }

  return { config, floatingOpen, load, togglePanel, applyDefaultTab }
}
