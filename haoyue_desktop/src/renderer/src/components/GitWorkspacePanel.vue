<script setup lang="ts">
import { Braces, GitBranch, History, RefreshCw, ShieldAlert, TerminalSquare, Upload, X } from '@lucide/vue'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import type { GitHistory, GitOverview, SecurityFinding } from '../../../shared/ipc'
import { requestJson } from '../daemon-json'
import type { ProjectItem } from '../types'

type PanelTab = 'diff' | 'history'

/**
 * 项目 Git 工具面板：更改（diff/status）/ 提交记录。
 *
 * 「编码 → Git」行为接入：
 * - 新建分支：按钮弹出内联输入，按配置的分支前缀预填（留空不添加前缀）；
 * - 推送：按钮按「始终强制推送」开关决定是否追加 --force-with-lease；
 * - 安全：开启「三层渐进式扫描」后，推送前先执行密钥/敏感信息快扫，
 *   命中高风险项时列出并需确认后再推送（推荐先处理）。
 * 编码配置经 advanced.get 的 coding 节读取（与设置页同源）。
 */
const props = withDefaults(defineProps<{
  open: boolean
  project?: ProjectItem
  initialTab: PanelTab
  diffOverride?: { path: string; diff: string } | null
  width?: number
  /** 嵌入 WorkDock：隐藏自身外壳（手柄/头部/内建 tab），由 dock 统一提供。 */
  embedded?: boolean
}>(), {
  width: 560,
  embedded: false
})

const emit = defineEmits<{
  close: []
  openTerminal: []
  resize: [width: number]
}>()

const tab = ref<PanelTab>('diff')
const overview = ref<GitOverview | null>(null)
const history = ref<GitHistory | null>(null)
const loading = ref(false)
const requestId = ref(0)
const resizing = ref(false)
let resizeStartX = 0
let resizeStartWidth = 0

/** 「编码 → Git / 安全」配置（advanced.get 的 coding 节）。 */
interface CodingView {
  git: { branchPrefix: string; forcePushWithLease: boolean }
  security: { progressiveScan: boolean }
}
const coding = ref<CodingView>({ git: { branchPrefix: '', forcePushWithLease: false }, security: { progressiveScan: false } })

// 新建分支内联表单
const branchDraft = ref('')
const branchBusy = ref(false)
const showBranchForm = ref(false)

// 推送与安全推荐
const pushing = ref(false)
const pushConfirmFindings = ref<SecurityFinding[] | null>(null)
const pushPending = ref(false)
const gitActionMessage = ref('')
const gitActionError = ref(false)

const MIN_PANEL_WIDTH = 280
const MAX_PANEL_WIDTH = 720

const displayedDiff = computed(() => props.diffOverride?.diff ?? overview.value?.diff ?? '')
const panelStyle = computed(() => ({ '--git-panel-width': `${props.width}px` }))
const diffLines = computed(() => displayedDiff.value.split(/\r?\n/).filter((line, index, lines) =>
  line.length > 0 || (index > 0 && index < lines.length - 1)))

function diffLineClass(line: string): string {
  if (line.startsWith('+++') || line.startsWith('---')) return 'diff-line-meta'
  if (line.startsWith('+')) return 'diff-line-addition'
  if (line.startsWith('-')) return 'diff-line-deletion'
  if (line.startsWith('@@')) return 'diff-line-hunk'
  if (line.startsWith('diff --git') || line.startsWith('# ')) return 'diff-line-file'
  return 'diff-line-context'
}

function statusLabel(line: string): string {
  const code = line.slice(0, 2).trim() || '?'
  const labels: Record<string, string> = {
    M: '修改', A: '新增', D: '删除', R: '重命名', C: '复制', U: '冲突', '??': '未跟踪'
  }
  return labels[code] ?? labels[code.at(-1) ?? ''] ?? code
}

function statusPath(line: string): string {
  return line.length > 3 ? line.slice(3) : line
}

function formatDate(value: string): string {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : new Intl.DateTimeFormat('zh-CN', {
    month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit'
  }).format(date)
}

/** 拉取「编码」配置（分支前缀 / 强制推送 / 渐进式扫描），与设置页同源。 */
async function loadCodingConfig(): Promise<void> {
  if (!props.project) return
  try {
    const advanced = await requestJson<{ coding?: Partial<CodingView> }>('advanced.get')
    coding.value = {
      git: { branchPrefix: '', forcePushWithLease: false, ...advanced.coding?.git },
      security: { progressiveScan: false, ...advanced.coding?.security }
    }
  } catch {
    // 编码配置读取失败不阻塞 Git 展示；保持默认值（不强制推送、不预填前缀）。
  }
}

async function refresh(): Promise<void> {
  if (!props.open || !props.project || props.diffOverride) return
  const currentRequest = ++requestId.value
  loading.value = true
  try {
    if (tab.value === 'diff') {
      const value = await window.haoyue.project.gitOverview(props.project.path)
      if (currentRequest === requestId.value) overview.value = value
    } else {
      const value = await window.haoyue.project.gitHistory(props.project.path)
      if (currentRequest === requestId.value) history.value = value
    }
  } finally {
    if (currentRequest === requestId.value) loading.value = false
  }
}

function selectTab(value: PanelTab): void {
  tab.value = value
}

/** 新建分支：按配置前缀预填草稿后展开内联表单。 */
function openCreateBranch(): void {
  branchDraft.value = coding.value.git.branchPrefix
  showBranchForm.value = true
  gitActionMessage.value = ''
  gitActionError.value = false
}

async function createBranch(): Promise<void> {
  if (!props.project || !branchDraft.value.trim()) return
  branchBusy.value = true
  try {
    const result = await window.haoyue.project.createGitBranch(props.project.path, branchDraft.value.trim())
    if (result.ok) {
      gitActionMessage.value = `已新建并切换到分支 ${result.branch}`
      gitActionError.value = false
      branchDraft.value = ''
      showBranchForm.value = false
      await refresh()
    } else {
      gitActionMessage.value = result.error || '新建分支失败'
      gitActionError.value = true
    }
  } catch (reason) {
    gitActionMessage.value = reason instanceof Error ? reason.message : String(reason)
    gitActionError.value = true
  } finally {
    branchBusy.value = false
  }
}

/**
 * 推送当前分支。开启「三层渐进式扫描」时先执行密钥/敏感信息快扫：
 * 无发现直接推送；有发现列出并等待确认（推荐先处理，仍要推送可继续）。
 */
async function pushBranch(): Promise<void> {
  if (!props.project || pushing.value) return
  gitActionMessage.value = ''
  gitActionError.value = false
  pushing.value = true
  try {
    if (coding.value.security.progressiveScan) {
      const scan = await window.haoyue.project.securityScan(props.project.path)
      if (scan.findings.length > 0) {
        pushConfirmFindings.value = scan.findings.slice(0, 50)
        pushPending.value = true
        return
      }
    }
    await doPush()
  } catch (reason) {
    gitActionMessage.value = reason instanceof Error ? reason.message : String(reason)
    gitActionError.value = true
  } finally {
    pushing.value = false
  }
}

async function doPush(): Promise<void> {
  if (!props.project) return
  pushing.value = true
  try {
    const result = await window.haoyue.project.pushGitBranch(
      props.project.path,
      coding.value.git.forcePushWithLease
    )
    if (result.ok) {
      gitActionMessage.value = coding.value.git.forcePushWithLease ? '推送成功（--force-with-lease）' : '推送成功'
      gitActionError.value = false
      await refresh()
    } else {
      gitActionMessage.value = result.error || '推送失败'
      gitActionError.value = true
    }
  } catch (reason) {
    gitActionMessage.value = reason instanceof Error ? reason.message : String(reason)
    gitActionError.value = true
  } finally {
    pushing.value = false
    pushPending.value = false
    pushConfirmFindings.value = null
  }
}

function cancelPush(): void {
  pushPending.value = false
  pushConfirmFindings.value = null
  pushing.value = false
  gitActionMessage.value = '已取消推送'
  gitActionError.value = false
}

function severityLabel(severity: SecurityFinding['severity']): string {
  return severity === 'high' ? '高' : '中'
}

function stopResize(): void {
  if (!resizing.value) return
  resizing.value = false
  window.removeEventListener('pointermove', handleResize)
  window.removeEventListener('pointerup', stopResize)
  window.removeEventListener('pointercancel', stopResize)
  document.body.classList.remove('is-resizing-git-panel')
}

function handleResize(event: PointerEvent): void {
  if (!resizing.value) return
  const viewportWidth = window.innerWidth
  const maxWidth = Math.min(MAX_PANEL_WIDTH, Math.max(MIN_PANEL_WIDTH, Math.floor(viewportWidth * 0.66)))
  const nextWidth = resizeStartWidth + resizeStartX - event.clientX
  if (nextWidth <= MIN_PANEL_WIDTH) {
    stopResize()
    emit('close')
    return
  }
  emit('resize', Math.min(maxWidth, Math.max(MIN_PANEL_WIDTH, nextWidth)))
}

function startResize(event: PointerEvent): void {
  if (event.button !== 0) return
  event.preventDefault()
  resizing.value = true
  resizeStartX = event.clientX
  resizeStartWidth = props.width
  document.body.classList.add('is-resizing-git-panel')
  window.addEventListener('pointermove', handleResize)
  window.addEventListener('pointerup', stopResize)
  window.addEventListener('pointercancel', stopResize)
}

watch(() => [props.open, props.project?.id, props.initialTab, props.diffOverride?.path] as const, ([open]) => {
  if (!open) {
    requestId.value++
    return
  }
  tab.value = props.initialTab
  void loadCodingConfig()
  void refresh()
}, { immediate: true })

watch(tab, () => {
  if (props.open) void refresh()
})

watch(() => props.open, (open) => {
  if (!open) stopResize()
})

onBeforeUnmount(stopResize)

/** WorkDock 头部的刷新按钮经由该句柄触发。 */
defineExpose({ refresh })
</script>

<template>
  <Transition name="side-panel">
    <aside
      v-if="open || embedded"
      class="git-panel"
      :class="{ 'is-resizing': resizing, 'is-embedded': embedded }"
      :style="panelStyle"
      aria-label="项目 Git 工具"
    >
      <div
        v-if="!embedded"
        class="git-panel-resize-handle"
        role="separator"
        aria-label="调整 Git 面板宽度"
        aria-orientation="vertical"
        @pointerdown="startResize"
      />
        <header v-if="!embedded" class="git-panel-header">
          <div class="git-panel-title">
            <Braces :size="19" />
            <div>
              <strong>{{ diffOverride ? '文件 Diff' : '项目更改' }}</strong>
              <small>{{ diffOverride?.path || project?.name }}</small>
            </div>
          </div>
          <div class="git-panel-actions">
            <button class="icon-button" title="在项目目录打开终端" :disabled="!project" @click="emit('openTerminal')">
              <TerminalSquare :size="18" />
            </button>
            <button class="icon-button" title="刷新" :disabled="loading || !project" @click="refresh">
              <RefreshCw :size="18" :class="{ spin: loading }" />
            </button>
            <button class="icon-button" title="关闭" @click="emit('close')"><X :size="18" /></button>
          </div>
        </header>

        <div v-if="!embedded" class="git-panel-tabs" role="tablist">
          <button :class="{ active: tab === 'diff' }" role="tab" @click="selectTab('diff')">
            <Braces :size="16" />更改
          </button>
          <button v-if="!diffOverride" :class="{ active: tab === 'history' }" role="tab" @click="selectTab('history')">
            <History :size="16" />提交记录
          </button>
        </div>

        <div class="git-panel-body">
          <template v-if="diffOverride">
            <div class="git-repository-summary tool-diff-summary">
              <span><Braces :size="15" />本次修改</span>
              <small>{{ diffOverride.path }}</small>
            </div>
            <pre v-if="diffLines.length" class="git-diff"><code><span
              v-for="(line, index) in diffLines"
              :key="`${index}:${line}`"
              class="diff-line"
              :class="diffLineClass(line)"
            >{{ line }}
</span></code></pre>
            <div v-else class="git-panel-state">没有可显示的 Diff。</div>
          </template>

          <div v-else-if="loading" class="git-panel-state"><RefreshCw :size="18" class="spin" />正在读取 Git 数据…</div>

          <template v-else-if="tab === 'diff'">
            <div v-if="overview?.isRepository" class="git-repository-summary">
              <span><GitBranch :size="15" />{{ overview.branch }}</span>
              <small>{{ overview.root }}</small>
            </div>
            <div v-if="overview?.isRepository" class="git-repo-actions">
              <button class="button button-compact" :disabled="!project || branchBusy"
                :title="coding.git.branchPrefix ? `新建分支（预填前缀 ${coding.git.branchPrefix}）` : '新建分支'"
                @click="openCreateBranch">
                <GitBranch :size="14" />新建分支
              </button>
              <button class="button button-compact" :disabled="!project || pushing || pushPending"
                :title="coding.git.forcePushWithLease ? '推送（--force-with-lease：远端已变化时拒绝覆盖）' : '推送到 origin'"
                @click="pushBranch">
                <Upload :size="14" :class="{ spin: pushing }" />
                {{ coding.git.forcePushWithLease ? '强制推送' : '推送' }}
              </button>
            </div>

            <form v-if="overview?.isRepository && showBranchForm" class="git-branch-form" @submit.prevent="createBranch">
              <input type="text" :value="branchDraft" placeholder="分支名（前缀已预填）" maxlength="100"
                :disabled="branchBusy" @input="branchDraft = ($event.target as HTMLInputElement).value" />
              <button type="submit" class="button button-primary button-compact" :disabled="branchBusy || !branchDraft.trim()">
                创建
              </button>
              <button type="button" class="button button-compact" :disabled="branchBusy" @click="showBranchForm = false">
                取消
              </button>
            </form>

            <div v-if="pushConfirmFindings" class="git-scan-confirm">
              <div class="git-scan-confirm-title">
                <ShieldAlert :size="15" />安全扫描发现 {{ pushConfirmFindings.length }}+ 项敏感信息（推送前建议先处理）
              </div>
              <ul class="git-scan-findings">
                <li v-for="(finding, index) in pushConfirmFindings" :key="`${finding.file}:${finding.line}:${index}`">
                  <span :class="{ high: finding.severity === 'high' }">{{ severityLabel(finding.severity) }}</span>
                  <code>{{ finding.kind }}</code>
                  <small>{{ finding.file }}:{{ finding.line }}</small>
                </li>
              </ul>
              <div class="git-scan-confirm-actions">
                <button class="button button-compact" :disabled="pushing" @click="cancelPush">取消推送</button>
                <button class="button button-danger button-compact" :disabled="pushing" @click="doPush">
                  仍要推送
                </button>
              </div>
            </div>

            <div v-if="gitActionMessage" class="git-action-message" :class="{ 'is-error': gitActionError }">
              {{ gitActionMessage }}
            </div>
            <div v-if="overview?.error" class="git-panel-state" :class="{ 'is-error': overview?.isRepository }">{{ overview.error }}</div>
            <template v-else-if="overview?.isRepository">
              <section v-if="overview.status.length" class="git-status-list">
                <div v-for="line in overview.status" :key="line" class="git-status-row">
                  <span>{{ statusLabel(line) }}</span><code>{{ statusPath(line) }}</code>
                </div>
              </section>
              <pre v-if="diffLines.length" class="git-diff"><code><span
                v-for="(line, index) in diffLines"
                :key="`${index}:${line}`"
                class="diff-line"
                :class="diffLineClass(line)"
              >{{ line }}
</span></code></pre>
              <div v-else-if="overview.status.length" class="git-panel-state">未跟踪文件尚无可显示的行级 Diff。</div>
              <div v-else class="git-panel-state">工作区是干净的，没有待查看的更改。</div>
            </template>
            <div v-else-if="overview" class="git-panel-state">当前项目不是 Git 仓库。</div>
          </template>

          <template v-else>
            <div v-if="history?.error" class="git-panel-state">{{ history.error }}</div>
            <ol v-else-if="history?.commits.length" class="git-history-list">
              <li v-for="commit in history.commits" :key="commit.hash">
                <span class="git-history-dot" />
                <div>
                  <strong>{{ commit.subject }}</strong>
                  <p><code>{{ commit.shortHash }}</code> · {{ commit.author }}</p>
                </div>
                <time :datetime="commit.authoredAt">{{ formatDate(commit.authoredAt) }}</time>
              </li>
            </ol>
            <div v-else-if="history" class="git-panel-state">没有可显示的提交记录。</div>
          </template>
        </div>
    </aside>
  </Transition>
</template>
