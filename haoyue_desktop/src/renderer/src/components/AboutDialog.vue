<script setup lang="ts">
import { ExternalLink, X } from '@lucide/vue'
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
            <dd>v{{ appInfo?.version ?? '—' }}</dd>
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
          <a href="https://github.com/Laogaodhck/haoyue/releases" target="_blank" rel="noreferrer">
            检查更新 <ExternalLink :size="13" />
          </a>
        </nav>

        <p class="about-description">
          当前版本 v{{ appInfo?.version ?? '—' }}。检查更新会在浏览器打开 GitHub Releases 页面
          （应用内自动更新尚在接入中，以发布页说明为准）。
        </p>

        <footer class="about-footer">MIT License · © 2026 Haoyue</footer>
      </section>
    </div>
  </Transition>
</template>
