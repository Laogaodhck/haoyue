<script setup lang="ts">
import { ArrowLeft, ArrowRight, ExternalLink, Globe2, RotateCw, X } from '@lucide/vue'
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'

/**
 * 右侧工作区「浏览」页签：内嵌 Electron <webview> 的可视化网页浏览。
 *
 * 访问边界与无头 browser 工具共用同一份白名单（config.web.allowedSites）：
 * 地址栏提交前经主进程 web:check-url 校验；页面内的链接点击与 target=_blank
 * 由主进程 will-navigate / webContents.setWindowOpenHandler 兜底，命中白名单外
 * 站点时通过 web:blocked 事件回到这里展示原因。
 */
interface WebviewElement extends HTMLElement {
  loadURL(url: string): Promise<void>
  reload(): void
  goBack(): void
  goForward(): void
  canGoBack(): boolean
  canGoForward(): boolean
  getURL(): string
  stop(): void
}

const props = withDefaults(defineProps<{ open?: boolean }>(), { open: false })

// WorkDock 关闭会连同本组件一起卸载，重开时用模块级变量恢复上次地址，避免「关掉就忘」。
let lastUrl = ''

const view = ref<WebviewElement | null>(null)
const address = ref('')
const currentUrl = ref('')
const loading = ref(false)
const error = ref('')
const canBack = ref(false)
const canForward = ref(false)

async function navigate(raw: string): Promise<void> {
  const target = raw.trim()
  if (!target) return
  const check = await window.haoyue.web.checkUrl(target)
  if (!check.ok || !check.url) {
    error.value = check.reason ?? '无法访问该地址'
    return
  }
  const element = view.value
  if (!element) return
  error.value = ''
  address.value = check.url
  currentUrl.value = check.url
  lastUrl = check.url
  void element.loadURL(check.url).catch(() => undefined)
}

function reload(): void {
  const element = view.value
  if (!element) return
  if (loading.value) {
    element.stop()
    return
  }
  element.reload()
}

function goBack(): void {
  if (canBack.value) view.value?.goBack()
}

function goForward(): void {
  if (canForward.value) view.value?.goForward()
}

function openExternal(): void {
  const url = currentUrl.value
  if (url.startsWith('http://') || url.startsWith('https://')) void window.haoyue.web.openExternal(url)
}

function syncNavState(): void {
  const element = view.value
  if (!element) return
  try {
    canBack.value = element.canGoBack()
    canForward.value = element.canGoForward()
  } catch {
    // guest 尚未 attach 时这些方法会抛，忽略：attach 完成后 did-navigate 会再同步一次。
  }
}

function handleStartLoading(): void {
  loading.value = true
}

function handleStopLoading(): void {
  loading.value = false
  syncNavState()
}

/** did-navigate / did-navigate-in-page 的载荷里带 url，比 getURL() 更及时。 */
function handleNavigate(event: Event): void {
  const url = (event as Event & { url?: string }).url ?? view.value?.getURL() ?? ''
  if (!url) return
  error.value = ''
  currentUrl.value = url
  address.value = url
  lastUrl = url
  syncNavState()
}

function handleFailLoad(event: Event): void {
  const detail = event as Event & { errorCode?: number; errorDescription?: string; validatedURL?: string }
  // -3 = ERR_ABORTED：被新的导航取代，不是失败（刷新、连点地址栏都会出现）。
  if (detail.errorCode === -3) return
  loading.value = false
  error.value = `无法加载 ${detail.validatedURL ?? ''}：${detail.errorDescription ?? '未知错误'}`
}

function handleBlocked(notice: { url: string; reason?: string }): void {
  error.value = notice.reason ?? `已拦截跳转到白名单外站点：${notice.url}`
}

const listeners: Array<[string, EventListener]> = [
  ['did-start-loading', handleStartLoading],
  ['did-stop-loading', handleStopLoading],
  ['did-navigate', handleNavigate],
  ['did-navigate-in-page', handleNavigate],
  ['did-fail-load', handleFailLoad]
]

let disposeBlocked: (() => void) | null = null

onMounted(() => {
  const element = view.value
  for (const [name, listener] of listeners) element?.addEventListener(name, listener)
  disposeBlocked = window.haoyue.web.onBlocked(handleBlocked)
  if (props.open && lastUrl) void navigate(lastUrl)
})

onBeforeUnmount(() => {
  const element = view.value
  for (const [name, listener] of listeners) element?.removeEventListener(name, listener)
  disposeBlocked?.()
})

watch(() => props.open, (open) => {
  if (open && lastUrl && !currentUrl.value) void navigate(lastUrl)
})
</script>

<template>
  <div class="browser-panel">
    <div class="browser-toolbar">
      <button class="icon-button" title="后退" aria-label="后退" :disabled="!canBack" @click="goBack">
        <ArrowLeft :size="16" />
      </button>
      <button class="icon-button" title="前进" aria-label="前进" :disabled="!canForward" @click="goForward">
        <ArrowRight :size="16" />
      </button>
      <button class="icon-button" :title="loading ? '停止加载' : '重新加载'"
        :aria-label="loading ? '停止加载' : '重新加载'" @click="reload">
        <X v-if="loading" :size="16" />
        <RotateCw v-else :size="16" />
      </button>
      <form class="browser-address-form" @submit.prevent="navigate(address)">
        <Globe2 :size="14" class="browser-address-icon" aria-hidden="true" />
        <input v-model="address" class="browser-address" type="text" spellcheck="false"
          placeholder="输入网址，如 localhost:5173 或 https://example.com" aria-label="网址" />
      </form>
      <button class="icon-button" title="在系统浏览器中打开" aria-label="在系统浏览器中打开"
        :disabled="!currentUrl" @click="openExternal">
        <ExternalLink :size="16" />
      </button>
    </div>

    <p v-if="error" class="browser-error" role="alert">{{ error }}</p>

    <!-- allowpopups 仅为不丢弃 target=_blank：主进程 setWindowOpenHandler 一律 deny，
         放行的目标改在同一视图内加载（见 web-guard.ts）。 -->
    <webview ref="view" class="browser-view" partition="persist:haoyue-browser" allowpopups />
  </div>
</template>
