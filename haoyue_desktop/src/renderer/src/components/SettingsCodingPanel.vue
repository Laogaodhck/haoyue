<script setup lang="ts">
import { GitBranch, RefreshCw, ShieldCheck, Workflow } from '@lucide/vue'
import { onMounted, ref, watch } from 'vue'
import { requestJson } from '../daemon-json'
import type { GitWorktreesResult } from '../../../shared/ipc'

/**
 * 设置 → 编码。
 *
 * 三块子功能（文案逐字保留用户原文）：
 * - Git 分支前缀（新建分支预填）与始终强制推送（--force-with-lease）；
 * - Worktrees 工作区索引：展示当前打开工作区的 Git 工作树清单；
 * - 安全：三层渐进式扫描与自动修复总开关。
 * 读写走 advanced.get / advanced.set 的 coding 节（与 runtime 契约同构，
 * 逐键 patch + 乐观更新 + 失败回滚）。行为端（新建分支预填 / 推送强制 / 安全推荐）
 * 落在 GitWorkspacePanel，本面板只负责配置与工作树索引展示。
 */
const props = defineProps<{
  connected: boolean
  /** 当前打开的工作区根目录（Worktrees 工作区索引的数据源）。 */
  workspacePath?: string
}>()

const emit = defineEmits<{
  notice: [message: string]
  error: [message: string]
}>()

interface CodingConfigView {
  git: { branchPrefix: string; forcePushWithLease: boolean }
  security: { progressiveScan: boolean }
}

function defaultCodingConfig(): CodingConfigView {
  return {
    git: { branchPrefix: '', forcePushWithLease: false },
    security: { progressiveScan: false }
  }
}

function mergeCoding(coding?: CodingConfigView): CodingConfigView {
  const defaults = defaultCodingConfig()
  return {
    git: { ...defaults.git, ...coding?.git },
    security: { ...defaults.security, ...coding?.security }
  }
}

const config = ref<CodingConfigView>(defaultCodingConfig())
const loading = ref(false)
const busy = ref('')

const worktrees = ref<GitWorktreesResult | null>(null)
const worktreesLoading = ref(false)

async function load(): Promise<void> {
  if (!props.connected) return
  loading.value = true
  try {
    const advanced = await requestJson<{ coding?: CodingConfigView }>('advanced.get')
    config.value = mergeCoding(advanced.coding)
  } catch (reason) {
    emit('error', reason instanceof Error ? reason.message : String(reason))
  } finally {
    loading.value = false
  }
}

/** 单键写回：乐观更新 → advanced.set 逐键 patch → 服务器完整读面回同步 → 失败回滚。 */
async function save(patch: Record<string, unknown>, busyKey: string, note: string): Promise<void> {
  busy.value = busyKey
  try {
    const advanced = await requestJson<{ coding?: CodingConfigView }>('advanced.set', {
      coding: patch
    })
    config.value = mergeCoding(advanced.coding)
    emit('notice', note)
  } catch (reason) {
    emit('error', reason instanceof Error ? reason.message : String(reason))
  } finally {
    busy.value = ''
  }
}

function changeBranchPrefix(event: Event): void {
  const value = (event.target as HTMLInputElement).value
  if (config.value.git.branchPrefix === value) return
  const previous = config.value.git.branchPrefix
  config.value.git.branchPrefix = value
  void save({ git: { branchPrefix: value } }, 'branchPrefix', '分支前缀已更新')
    .catch(() => { config.value.git.branchPrefix = previous })
}

function toggleForcePush(event: Event): void {
  const checked = (event.target as HTMLInputElement).checked
  if (config.value.git.forcePushWithLease === checked) return
  const previous = config.value.git.forcePushWithLease
  config.value.git.forcePushWithLease = checked
  void save({ git: { forcePushWithLease: checked } }, 'forcePush', `始终强制推送已${checked ? '开启' : '关闭'}`)
    .catch(() => { config.value.git.forcePushWithLease = previous })
}

function toggleProgressiveScan(event: Event): void {
  const checked = (event.target as HTMLInputElement).checked
  if (config.value.security.progressiveScan === checked) return
  const previous = config.value.security.progressiveScan
  config.value.security.progressiveScan = checked
  void save({ security: { progressiveScan: checked } }, 'scan', `安全扫描已${checked ? '开启' : '关闭'}`)
    .catch(() => { config.value.security.progressiveScan = previous })
}

async function refreshWorktrees(): Promise<void> {
  if (!props.workspacePath) return
  worktreesLoading.value = true
  try {
    worktrees.value = await window.haoyue.project.gitWorktrees(props.workspacePath)
  } catch (reason) {
    emit('error', reason instanceof Error ? reason.message : String(reason))
  } finally {
    worktreesLoading.value = false
  }
}

onMounted(() => {
  void load()
  void refreshWorktrees()
})
watch(() => props.connected, (connected) => { if (connected) void load() })
watch(() => props.workspacePath, () => { void refreshWorktrees() })
</script>

<template>
  <div class="settings-panel settings-coding-panel">
    <section class="settings-group">
      <div class="settings-group-title"><GitBranch :size="16" />Git</div>

      <div class="settings-row">
        <div>
          <strong>分支前缀</strong>
          <small class="coding-description">haoyue 新建分支时会预填此前缀。留空则不添加前缀。</small>
        </div>
        <input class="settings-text-input" type="text" placeholder="如 feature/" maxlength="80"
          :value="config.git.branchPrefix" :disabled="!connected || busy !== ''"
          @change="changeBranchPrefix" />
      </div>

      <label class="settings-row coding-toggle-row">
        <span>
          <strong>始终强制推送</strong>
          <small class="coding-description">haoyue 推送时将使用 --force-with-lease。它会改写远端分支历史，但会在远端已变化时拒绝覆盖。</small>
        </span>
        <input class="sr-only" type="checkbox" :checked="config.git.forcePushWithLease" :disabled="!connected || busy !== ''"
          @change="toggleForcePush" />
        <span class="toggle-switch" aria-hidden="true"><span /></span>
      </label>
    </section>

    <section class="settings-group">
      <div class="settings-group-title"><Workflow :size="16" />Worktrees</div>
      <div class="settings-row">
        <div>
          <strong>工作区索引</strong>
          <small class="coding-description">管理 haoyue 中已打开本地工作区的代码索引。</small>
        </div>
        <button class="coding-refresh" :disabled="!workspacePath || worktreesLoading"
          @click="refreshWorktrees">
          <RefreshCw :size="14" :class="{ spin: worktreesLoading }" />刷新索引
        </button>
      </div>

      <div v-if="worktrees" class="coding-worktrees">
        <div v-if="worktrees.error" class="coding-worktree-note is-error">{{ worktrees.error }}</div>
        <div v-else-if="worktrees.worktrees.length" class="coding-worktree-list">
          <div v-for="entry in worktrees.worktrees" :key="entry.path" class="coding-worktree-row">
            <span class="coding-worktree-branch"><GitBranch :size="13" />{{ entry.branch || 'detached HEAD' }}</span>
            <code>{{ entry.path }}</code>
            <small v-if="entry.head">{{ entry.head }}</small>
          </div>
        </div>
        <div v-else class="coding-worktree-note">没有可显示的工作树。</div>
      </div>
      <div v-else-if="!workspacePath" class="coding-worktree-note">
        当前未打开本地工作区，绑定项目后即可在此查看其 Git 工作树索引。
      </div>
    </section>

    <section class="settings-group">
      <div class="settings-group-title"><ShieldCheck :size="16" />安全</div>
      <label class="settings-row coding-toggle-row">
        <span>
          <strong>三层渐进式扫描与自动修复</strong>
          <small class="coding-description">haoyue将安全能力嵌入开发流程，通过三层渐进式扫描和自动修复，让每次代码提交更安全。</small>
          <small class="coding-description">开启开关后，haoyue 将会在合适节点，智能推荐对应的安全扫描能力，在你确认后执行扫描并修复。</small>
        </span>
        <input class="sr-only" type="checkbox" :checked="config.security.progressiveScan" :disabled="!connected || busy !== ''"
          @change="toggleProgressiveScan" />
        <span class="toggle-switch" aria-hidden="true"><span /></span>
      </label>
    </section>
  </div>
</template>

<style scoped>
.coding-description {
  max-width: 560px;
  overflow: visible;
  white-space: normal;
  text-overflow: clip;
}

.coding-toggle-row {
  align-items: flex-start;
}

.coding-toggle-row input:checked + .toggle-switch {
  background: var(--accent);
}

.coding-toggle-row input:checked + .toggle-switch > span {
  transform: translateX(16px);
}

.coding-toggle-row input:focus-visible + .toggle-switch {
  outline: 2px solid color-mix(in srgb, var(--accent) 58%, transparent);
  outline-offset: 2px;
}

.settings-text-input {
  width: 240px;
  padding: 6px 10px;
  color: var(--text-primary);
  font-size: 13px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 8px;
}

.settings-text-input:focus {
  outline: none;
  border-color: var(--accent);
}

.coding-refresh {
  flex: none;
  display: inline-flex;
  min-height: 30px;
  align-items: center;
  gap: 6px;
  padding: 0 11px;
  font-size: 12px;
  color: var(--text-secondary);
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 7px;
}

.coding-refresh:hover:not(:disabled) {
  color: var(--text);
  border-color: var(--border-strong, var(--border));
}

.coding-refresh:disabled {
  opacity: 0.5;
  cursor: default;
}

.coding-worktrees {
  padding: 0 2px 12px;
}

.coding-worktree-list {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.coding-worktree-row {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 8px 10px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 8px;
}

.coding-worktree-row code {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.coding-worktree-row small {
  color: var(--text-muted);
  font-size: 11px;
}

.coding-worktree-branch {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  flex: none;
  color: var(--text-secondary);
  font-size: 12px;
}

.coding-worktree-note {
  padding: 10px 12px;
  color: var(--text-muted);
  font-size: 12px;
}

.coding-worktree-note.is-error {
  color: var(--danger);
}
</style>
