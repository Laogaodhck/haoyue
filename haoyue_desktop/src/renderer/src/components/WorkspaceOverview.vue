<script setup lang="ts">
import { ChevronDown, FolderOpen, LoaderCircle, RefreshCw } from '@lucide/vue'
import { onMounted, ref } from 'vue'
import { summarizeWorkspaceFiles, type WorkspaceDirEntry } from '../workspace-overview'

/// 欢迎页的「工作区速览」卡片：复用 workspace.files RPC（与 @ 引用同一数据源，
/// 凭证与构建产物已在 runtime 侧挡掉），把项目加载结果与目录结构摊给用户看。
/// 任何失败都折叠成一行提示，不打断欢迎页主流程。

const props = defineProps<{ workspacePath: string }>()

const loading = ref(false)
const error = ref('')
const entries = ref<WorkspaceDirEntry[]>([])
const open = ref(true)
let fetchSeq = 0

async function refresh(): Promise<void> {
  const seq = ++fetchSeq
  loading.value = true
  error.value = ''
  try {
    const response = await window.haoyue.daemon.request('workspace.files', {
      workspace: props.workspacePath,
      query: '',
      limit: 60
    })
    if (seq !== fetchSeq) return
    const paths = JSON.parse(response.data) as unknown
    entries.value = summarizeWorkspaceFiles(Array.isArray(paths) ? paths.map(String) : [])
  } catch (reason) {
    if (seq !== fetchSeq) return
    entries.value = []
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    if (seq === fetchSeq) loading.value = false
  }
}

onMounted(() => { void refresh() })
</script>

<template>
  <section class="workspace-overview" aria-label="工作区速览">
    <button class="workspace-overview-header" type="button" :aria-expanded="open"
      :title="workspacePath" @click="open = !open">
      <FolderOpen :size="15" aria-hidden="true" />
      <strong>工作区速览</strong>
      <span class="workspace-overview-path">{{ workspacePath }}</span>
      <span class="workspace-overview-tools">
        <span v-if="loading" class="workspace-overview-state">
          <LoaderCircle class="spin" :size="13" aria-hidden="true" /> 正在读取目录…
        </span>
        <button v-else type="button" class="workspace-overview-refresh" title="重新读取目录"
          @click.stop="refresh">
          <RefreshCw :size="13" aria-hidden="true" />
        </button>
        <ChevronDown :size="14" class="workspace-overview-chevron" :class="{ rotated: open }" aria-hidden="true" />
      </span>
    </button>
    <p v-if="error" class="workspace-overview-error">目录读取失败：{{ error }}</p>
    <ul v-if="open && entries.length" class="workspace-overview-list">
      <li v-for="entry in entries" :key="entry.name" class="workspace-overview-row"
        :class="`is-${entry.kind}`">
        <span class="workspace-overview-name" :title="entry.name">{{ entry.name }}</span>
        <small v-if="entry.kind === 'dir'" class="workspace-overview-meta">
          {{ entry.fileCount }} 个文件
        </small>
        <small v-else class="workspace-overview-meta">文件</small>
      </li>
    </ul>
    <p v-else-if="open && !loading && !error" class="workspace-overview-empty">
      暂未发现可展示的文件
    </p>
  </section>
</template>
