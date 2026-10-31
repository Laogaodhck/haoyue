<script setup lang="ts">
import { daemonRequest } from '../daemon-rpc'
import { ShieldAlert, ShieldCheck, LoaderCircle } from '@lucide/vue'
import { onMounted, ref, watch } from 'vue'

/// 工作区信任提示：`.haoyue/config.json` 与 `mcp/servers.json` 会带来自动执行配置和
/// MCP 服务器，runtime 默认不加载它们，直到用户显式信任该工作区。组件自己查询
/// workspace.get（带 workspace 参数），不依赖父级状态，便于在任意面板复用。

const props = defineProps<{
  workspacePath?: string
  /** 已受信时也显示一行状态说明（MCP 面板需要，速览卡片不需要）。 */
  showTrusted?: boolean
}>()

const emit = defineEmits<{ changed: [trusted: boolean] }>()

const trusted = ref(true)
const hasWorkspaceConfig = ref(false)
const hasWorkspaceMcp = ref(false)
const loaded = ref(false)
const busy = ref(false)
const error = ref('')

interface WorkspaceSnapshot {
  trusted?: boolean
  hasWorkspaceConfig?: boolean
  hasWorkspaceMcp?: boolean
}

async function load(): Promise<void> {
  const path = props.workspacePath?.trim()
  if (!path) {
    loaded.value = false
    return
  }
  try {
    const response = await daemonRequest('workspace.get', { workspace: path })
    const snapshot = JSON.parse(response.data) as WorkspaceSnapshot
    trusted.value = snapshot.trusted !== false
    hasWorkspaceConfig.value = snapshot.hasWorkspaceConfig === true
    hasWorkspaceMcp.value = snapshot.hasWorkspaceMcp === true
    error.value = ''
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    loaded.value = true
  }
}

async function apply(nextTrusted: boolean): Promise<void> {
  const path = props.workspacePath?.trim()
  if (!path || busy.value) return
  busy.value = true
  error.value = ''
  try {
    const response = await daemonRequest(
      nextTrusted ? 'workspace.trust' : 'workspace.untrust',
      { path }
    )
    const snapshot = JSON.parse(response.data) as WorkspaceSnapshot
    trusted.value = snapshot.trusted !== false
    hasWorkspaceConfig.value = snapshot.hasWorkspaceConfig === true
    hasWorkspaceMcp.value = snapshot.hasWorkspaceMcp === true
    emit('changed', trusted.value)
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    busy.value = false
  }
}

onMounted(() => { void load() })
watch(() => props.workspacePath, () => { void load() })
</script>

<template>
  <div v-if="loaded && !trusted && (hasWorkspaceConfig || hasWorkspaceMcp)" class="workspace-trust-notice is-untrusted">
    <ShieldAlert :size="15" aria-hidden="true" />
    <div class="workspace-trust-body">
      <strong>此工作区未受信</strong>
      <p>
        该目录包含
        <template v-if="hasWorkspaceConfig">.haoyue/config.json</template>
        <template v-if="hasWorkspaceConfig && hasWorkspaceMcp"> 与 </template>
        <template v-if="hasWorkspaceMcp">mcp/servers.json</template>
        ，其中的自动执行配置与 MCP 服务器已被忽略。
      </p>
      <p v-if="error" class="workspace-trust-error">{{ error }}</p>
    </div>
    <button type="button" class="workspace-trust-action" :disabled="busy" @click="apply(true)">
      <LoaderCircle v-if="busy" class="spin" :size="13" aria-hidden="true" />
      <ShieldCheck v-else :size="13" aria-hidden="true" />
      信任此工作区
    </button>
  </div>
  <div v-else-if="loaded && showTrusted && trusted && (hasWorkspaceConfig || hasWorkspaceMcp)" class="workspace-trust-notice is-trusted">
    <ShieldCheck :size="14" aria-hidden="true" />
    <div class="workspace-trust-body">
      <span>已受信工作区 · 工作区级配置与 MCP 生效</span>
    </div>
    <button type="button" class="workspace-trust-action subtle" :disabled="busy" @click="apply(false)">
      撤销信任
    </button>
  </div>
</template>
