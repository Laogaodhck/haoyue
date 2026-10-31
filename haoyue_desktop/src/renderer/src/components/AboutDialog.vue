<script setup lang="ts">
import { ExternalLink, X } from '@lucide/vue'
import { ref, watch } from 'vue'
import type { AppInfo } from '../../../shared/ipc'
import logoUrl from '../../../../resources/logo.png?url'
import { useEscapeClose } from '../use-escape-close'

const props = defineProps<{
  open: boolean
  appInfo: AppInfo | null
  /** Runtime 侧协议版本（握手回报；7.7 让「桌面 ↔ daemon 版本匹配」在关于页可见）。 */
  protocolVersion?: string
}>()
const emit = defineEmits<{
  close: []
}>()

useEscapeClose(() => props.open, () => emit('close'))

const platformNames: Record<string, string> = {
  win32: 'Windows',
  darwin: 'macOS',
  linux: 'Linux'
}

function platformLabel(): string {
  const raw = props.appInfo?.platform
  return (raw && platformNames[raw]) || raw || '未知'
}

const updateState = ref<'idle' | 'checking' | 'update' | 'latest' | 'unavailable'>('idle')
const updateVersion = ref('')
const updateUrl = ref('')
const updateError = ref('')

watch(
  () => props.open,
  (open) => {
    if (open) updateState.value = 'idle'
  }
)

async function checkForUpdates(): Promise<void> {
  if (updateState.value === 'checking') return
  updateState.value = 'checking'
  try {
    const result = await window.haoyue.updates.check()
    updateState.value = result.status
    updateVersion.value = result.version ?? ''
    updateUrl.value = result.url ?? ''
    updateError.value = result.error ?? ''
  } catch (error) {
    updateState.value = 'unavailable'
    updateError.value = error instanceof Error ? error.message : String(error)
  }
}

function openUpdatePage(): void {
  if (updateUrl.value) void window.haoyue.web.openExternal(updateUrl.value)
}
</script>

<template>
  <Transition name="modal-fade">
    <div
      v-if="open"
      class="modal-backdrop about-backdrop"
      @mousedown.self="emit('close')"
    >
      <section class="about-dialog" role="dialog" aria-modal="true" aria-labelledby="about-title">
        <header class="about-header">
          <div class="brand-lockup about-lockup">
            <img :src="logoUrl" alt="" />
            <div>
              <h2 id="about-title">Haoyue（浩玥）</h2>
              <p>AI Agent</p>
            </div>
          </div>
          <button class="icon-button" title="关闭" @click="emit('close')"><X :size="18" /></button>
        </header>

        <dl class="about-meta">
          <div class="about-row">
            <dt>版本</dt>
            <dd class="about-version">
              v{{ appInfo?.version ?? '—' }}
              <button
                class="about-update-button"
                type="button"
                :disabled="updateState === 'checking'"
                @click="checkForUpdates"
              >
                {{ updateState === 'checking' ? '检查中…' : '检查更新' }}
              </button>
            </dd>
          </div>
          <div class="about-row">
            <dt>平台</dt>
            <dd>{{ platformLabel() }}</dd>
          </div>
          <div class="about-row">
            <dt>Runtime 协议</dt>
            <dd>v{{ protocolVersion ?? '未连接' }}</dd>
          </div>
          <div class="about-row">
            <dt>作者</dt>
            <dd>老高（QQ：846193）</dd>
          </div>
        </dl>

        <p class="about-description">
          基于 .NET 构建的现代化、高性能 AI Agent，为 AI 驱动的编码助手提供完整平台。
        </p>

        <p v-if="updateState === 'latest'" class="about-update-status">
          已是最新版本<span v-if="updateVersion">（v{{ updateVersion }}）</span>。
        </p>
        <p v-else-if="updateState === 'update'" class="about-update-status about-update-available">
          发现新版本 v{{ updateVersion }}。
          <button type="button" class="about-update-link" @click="openUpdatePage">前往下载</button>
        </p>
        <p v-else-if="updateState === 'unavailable'" class="about-update-status">
          暂时无法检查更新{{ updateError ? `（${updateError}）` : '' }}，可稍后重试或直接访问
          <button type="button" class="about-update-link" @click="openUpdatePage">GitHub Releases</button>。
        </p>

        <nav class="about-links" aria-label="Haoyue 相关链接">
          <a href="https://haoyue.hoilai.com" target="_blank" rel="noreferrer">
            官网 <ExternalLink :size="13" />
          </a>
          <a href="https://haoyue.hoilai.com/doc/" target="_blank" rel="noreferrer">
            文档 <ExternalLink :size="13" />
          </a>
          <a href="https://github.com/Laogaodhck/haoyue" target="_blank" rel="noreferrer">
            GitHub <ExternalLink :size="13" />
          </a>
        </nav>

        <p class="about-description">
          新版本检查只读取 GitHub Releases 的最新正式版信息；下载安装仍以发布页说明为准。
        </p>

        <footer class="about-footer">MIT License · © 2026 Haoyue</footer>
      </section>
    </div>
  </Transition>
</template>
