<script setup lang="ts">
import { Bug, Hammer, RefreshCw, Telescope } from '@lucide/vue'
import { ref } from 'vue'
import type { ProjectItem, ThreadItem } from '../types'
import { dayGreeting } from '../greeting'
import WorkspaceOverview from './WorkspaceOverview.vue'

/// 豆包式欢迎页（新会话空态）：时段问候 + 未绑定项目时的「绑定工作目录」引导 +
/// 工作区速览卡片 + 推荐提示卡片。从 App.vue 拆出，以守住巨类行数预算
/// （ADR-20261015-giant-class-budget.md），欢迎页语义内聚于此。

const props = defineProps<{
  project?: ProjectItem
  thread?: ThreadItem
  /** 当前是未绑定项目的全局任务：显示「绑定工作目录」主按钮。 */
  showBindAction: boolean
  daemonConnected: boolean
}>()

const emit = defineEmits<{
  useStarterPrompt: [label: string]
  openWorkspace: []
}>()

// 时段问候在组件挂载时取一次即可；欢迎页常驻期通常不会跨时段。
const greeting = ref(dayGreeting(new Date().getHours()))

const starterPrompts = [
  { label: '探索并理解代码', icon: Telescope, tone: 'blue' },
  { label: '构建新功能、应用或工具', icon: Hammer, tone: 'purple' },
  { label: '审查代码并提出修改建议', icon: RefreshCw, tone: 'green' },
  { label: '修复问题和失败', icon: Bug, tone: 'orange' }
] as const
</script>

<template>
  <h1>{{ greeting }}</h1>
  <p>{{ project ? `${project.name} · ${project.path}` : '未绑定工作目录' }}</p>
  <div v-if="showBindAction" class="welcome-workspace-actions">
    <button class="primary-button empty-state-action" @click="emit('openWorkspace')">
      选择文件夹，绑定工作目录
    </button>
  </div>
  <WorkspaceOverview v-if="project && daemonConnected && !thread?.archived" :key="project.id"
    :workspace-path="project.path" />
  <div v-if="!thread?.archived" class="starter-prompts" aria-label="快速开始">
    <button v-for="prompt in starterPrompts" :key="prompt.label" type="button" class="starter-prompt-card"
      :data-tone="prompt.tone" @click="emit('useStarterPrompt', prompt.label)">
      <component :is="prompt.icon" :size="20" aria-hidden="true" />
      <span>{{ prompt.label }}</span>
    </button>
  </div>
</template>
