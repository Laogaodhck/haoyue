<script setup lang="ts">
import { Circle, RefreshCw } from '@lucide/vue'
import { computed } from 'vue'
import type { ThreadItem } from '../types'

const props = withDefaults(defineProps<{
  connected: boolean
  endpoint: string
  reconnecting?: boolean
  model?: string
  /** 当前任务的工作阶段（thinking/tool/verify…），仅运行中由外层传入。 */
  phase?: string
  elapsedSeconds?: number
  threads?: ThreadItem[]
  version?: string
}>(), {
  reconnecting: false,
  model: '',
  phase: undefined,
  elapsedSeconds: 0,
  threads: () => [],
  version: ''
})

const emit = defineEmits<{ reconnect: [] }>()

const runningCount = computed(() => props.threads.filter((thread) => thread.running).length)
</script>

<template>
  <footer
    class="status-bar"
    :class="{ offline: !connected, 'is-accent': connected && runningCount > 0 }"
  >
    <button
      v-if="!connected"
      type="button"
      class="status-item status-runtime is-link"
      :title="`重连 Runtime：${endpoint || '未知地址'}`"
      :disabled="reconnecting"
      @click="emit('reconnect')"
    >
      <Circle :size="9" fill="currentColor" aria-hidden="true" />
      <span>{{ reconnecting ? 'Runtime 重连中…' : 'Runtime 未连接' }}</span>
      <RefreshCw :size="11" :class="{ spin: reconnecting }" aria-hidden="true" />
    </button>
    <span v-else class="status-item status-runtime" title="Runtime 已连接">
      <Circle :size="9" fill="currentColor" aria-hidden="true" />
      <span>Runtime</span>
    </span>

    <span v-if="endpoint" class="status-item status-endpoint" :title="endpoint">
      <span class="status-key">target</span>
      <code>{{ endpoint }}</code>
    </span>

    <span v-if="model" class="status-item" :title="`当前模型：${model}`">
      <span class="status-key">model</span>
      <code>{{ model }}</code>
    </span>

    <span v-if="phase" class="status-item status-phase" :title="`当前阶段：${phase}`">
      <span class="phase-dot" aria-hidden="true" />
      <span>{{ phase }}</span>
      <span v-if="elapsedSeconds > 0">· {{ elapsedSeconds }}s</span>
    </span>

    <span v-if="runningCount > 0" class="status-item" :title="`${runningCount} 个任务正在运行`">
      <span class="status-key">running</span>
      <code>{{ runningCount }}</code>
    </span>

    <span class="status-spacer" aria-hidden="true" />
    <span v-if="version" class="status-item status-version">v{{ version }}</span>
  </footer>
</template>
