<script setup lang="ts">
import { Check, Clock, History, LoaderCircle, RefreshCw, Telescope, Wrench, X, Zap } from '@lucide/vue'
import { computed, onMounted, ref } from 'vue'
import { confirmAction } from '../confirmation'
import { requestJson } from '../daemon-json'
import {
  EVOLUTION_INTERVAL_OPTIONS,
  clampIntervalMinutes,
  clampThresholdSignals,
  defectKindLabel,
  formatDateOnly,
  formatInterval,
  relativeTime,
  reflectionOutcomeText,
  severityClass,
  severityLabel,
  triggerLabel,
  trendMax,
  trendTitle,
  trendTotal,
  type EvolutionCandidate,
  type EvolutionCandidateFile,
  type EvolutionConfigValue,
  type EvolutionDefectReport,
  type EvolutionHealth,
  type EvolutionRun,
  type EvolutionStats,
  type EvolutionTrendPoint
} from '../evolution-form'
import SelectMenu from './SelectMenu.vue'

/**
 * 设置 → 进化。面板自带一次加载与所有写操作，父级只负责把提示语放进全局 toast；
 * 反思回合在后台完成时由父级转调 expose 的 refreshAfterReflect。
 */
const emit = defineEmits<{
  notice: [message: string]
  error: [message: string]
}>()

const evolutionAutoReflect = ref(false)
const evolutionIntervalMinutes = ref(360)
const evolutionThresholdEnabled = ref(false)
const evolutionThresholdSignals = ref(5)
const evolutionDefects = ref<EvolutionDefectReport[]>([])
const evolutionScanned = ref(0)
const evolutionHealth = ref<EvolutionHealth | null>(null)
const evolutionTrend = ref<EvolutionTrendPoint[]>([])
const evolutionByKind = ref<Record<string, number>>({})
const evolutionCandidates = ref<EvolutionCandidate[]>([])
const evolutionHistory = ref<EvolutionRun[]>([])
const evolutionStats = ref<EvolutionStats | null>(null)
/** 展开的草稿工作台（单选）；promptDraft 承载采纳前的提示词改写文本。 */
const expandedCandidateFingerprint = ref<string | null>(null)
const candidateActiveFile = ref<Record<string, string>>({})
const promptDraft = ref('')
const promptDirty = ref(false)
const action = ref('')

type EvolutionInspect = {
  scanned: number
  reports: EvolutionDefectReport[]
  health?: EvolutionHealth
  trend?: EvolutionTrendPoint[]
  distribution?: { byKind: Record<string, number> }
}

function beginAction(name: string): void {
  action.value = name
  emit('error', '')
}

function fail(reason: unknown): void {
  emit('error', reason instanceof Error ? reason.message : String(reason))
}

function applyInspect(inspect: EvolutionInspect): void {
  evolutionDefects.value = inspect.reports
  evolutionScanned.value = inspect.scanned
  evolutionHealth.value = inspect.health ?? null
  evolutionTrend.value = inspect.trend ?? []
  evolutionByKind.value = inspect.distribution?.byKind ?? {}
}

async function loadEvolution(): Promise<void> {
  const [config, inspect, pending, history, stats] = await Promise.all([
    requestJson<EvolutionConfigValue>('evolution.config.get'),
    requestJson<EvolutionInspect>('evolution.inspect'),
    requestJson<EvolutionCandidate[]>('evolution.pending-list'),
    requestJson<EvolutionRun[]>('evolution.history', { limit: 20 }),
    requestJson<EvolutionStats>('evolution.stats')
  ])
  evolutionAutoReflect.value = config.autoReflect
  evolutionIntervalMinutes.value = clampIntervalMinutes(config.intervalMinutes)
  evolutionThresholdEnabled.value = config.thresholdEnabled ?? false
  evolutionThresholdSignals.value = clampThresholdSignals(config.thresholdSignals ?? 5)
  applyInspect(inspect)
  evolutionCandidates.value = pending
  evolutionHistory.value = history
  evolutionStats.value = stats
}

async function setEvolutionConfig(payload: {
  autoReflect?: boolean
  intervalMinutes?: number
  thresholdEnabled?: boolean
  thresholdSignals?: number
}): Promise<void> {
  const previous = {
    autoReflect: evolutionAutoReflect.value,
    intervalMinutes: evolutionIntervalMinutes.value,
    thresholdEnabled: evolutionThresholdEnabled.value,
    thresholdSignals: evolutionThresholdSignals.value
  }
  if (payload.autoReflect !== undefined) evolutionAutoReflect.value = payload.autoReflect
  if (payload.intervalMinutes !== undefined)
    evolutionIntervalMinutes.value = clampIntervalMinutes(payload.intervalMinutes)
  if (payload.thresholdEnabled !== undefined) evolutionThresholdEnabled.value = payload.thresholdEnabled
  if (payload.thresholdSignals !== undefined)
    evolutionThresholdSignals.value = clampThresholdSignals(payload.thresholdSignals)
  beginAction('evolution.config')
  try {
    const config = await requestJson<EvolutionConfigValue>('evolution.config.set', {
      autoReflect: evolutionAutoReflect.value,
      intervalMinutes: evolutionIntervalMinutes.value,
      thresholdEnabled: evolutionThresholdEnabled.value,
      thresholdSignals: evolutionThresholdSignals.value
    })
    evolutionAutoReflect.value = config.autoReflect
    evolutionIntervalMinutes.value = clampIntervalMinutes(config.intervalMinutes)
    evolutionThresholdEnabled.value = config.thresholdEnabled ?? false
    evolutionThresholdSignals.value = clampThresholdSignals(config.thresholdSignals ?? 5)
    const parts: string[] = []
    if (evolutionAutoReflect.value)
      parts.push(`每 ${formatInterval(evolutionIntervalMinutes.value)} 复盘一次`)
    if (evolutionThresholdEnabled.value)
      parts.push(`缺陷累计 ${evolutionThresholdSignals.value} 条即提前复盘`)
    emit('notice', parts.length > 0
      ? `自动反思已开启：${parts.join('，或')}；技能草稿始终等待人工审阅`
      : '自动反思已关闭：仅手动触发反思回合')
  } catch (reason) {
    evolutionAutoReflect.value = previous.autoReflect
    evolutionIntervalMinutes.value = previous.intervalMinutes
    evolutionThresholdEnabled.value = previous.thresholdEnabled
    evolutionThresholdSignals.value = previous.thresholdSignals
    fail(reason)
  } finally {
    action.value = ''
  }
}

const intervalOptions = computed(() => {
  const options: Array<{ value: string; label: string }> = EVOLUTION_INTERVAL_OPTIONS.map((option) => ({
    value: String(option.value),
    label: option.label
  }))
  const current = String(evolutionIntervalMinutes.value)
  if (!options.some((option) => option.value === current))
    options.push({ value: current, label: formatInterval(evolutionIntervalMinutes.value) })
  return options
})

function changeInterval(value: string): void {
  void setEvolutionConfig({ intervalMinutes: Number(value) })
}

async function runReflection(): Promise<void> {
  beginAction('evolution.reflect')
  try {
    await requestJson<{ started: boolean }>('evolution.reflect')
    emit('notice', '反思回合已启动：正在后台复盘缺陷信号，完成后本页自动刷新')
  } catch (reason) {
    fail(reason)
  } finally {
    action.value = ''
  }
}

/**
 * Applies a human decision. Adopt (optionally with a rewritten prompt) installs
 * the draft as a live skill; defer parks it; reject discards it permanently, so
 * only reject asks for confirmation.
 */
async function decideEvolutionCandidate(
  candidate: EvolutionCandidate,
  decision: 'adopt' | 'reject' | 'defer',
  promptOverride?: string
): Promise<void> {
  if (decision === 'reject') {
    const confirmed = await confirmAction({
      title: '丢弃该候选技能？',
      message: `「${candidate.skillName}」的草稿将被永久删除，同一缺陷不会再生成候选。`,
      confirmLabel: '丢弃',
      danger: true
    })
    if (!confirmed) return
  }
  beginAction(`evolution.decide:${candidate.fingerprint}`)
  try {
    await requestJson('evolution.decide', {
      fingerprint: candidate.fingerprint,
      decision,
      ...(promptOverride !== undefined ? { prompt: promptOverride } : {})
    })
    evolutionCandidates.value = await requestJson<EvolutionCandidate[]>('evolution.pending-list')
    emit('notice',
      decision === 'adopt'
        ? `已采纳「${candidate.skillName}」：技能进入正式目录，下一次扫描即生效`
        : decision === 'defer'
          ? `已暂缓「${candidate.skillName}」：草稿保留在待审清单，随时可以继续审阅`
          : `已丢弃「${candidate.skillName}」`)
    collapseDraftWorkbench()
  } catch (reason) {
    fail(reason)
  } finally {
    action.value = ''
  }
}

// ------------------------------------------------ draft workbench (preview + rewrite)

function expandDraftWorkbench(candidate: EvolutionCandidate): void {
  expandedCandidateFingerprint.value = candidate.fingerprint
  const files = candidate.files ?? []
  if (!candidateActiveFile.value[candidate.fingerprint]) {
    candidateActiveFile.value[candidate.fingerprint] = files[0]?.name ?? ''
  }
  promptDraft.value = files.find((file) => file.name === 'prompt.txt')?.content ?? ''
  promptDirty.value = false
}

function collapseDraftWorkbench(): void {
  expandedCandidateFingerprint.value = null
  promptDirty.value = false
}

function activeDraftFile(candidate: EvolutionCandidate): EvolutionCandidateFile | null {
  const active = candidateActiveFile.value[candidate.fingerprint]
  return candidate.files?.find((file) => file.name === active) ?? candidate.files?.[0] ?? null
}

function selectDraftFile(candidate: EvolutionCandidate, name: string): void {
  candidateActiveFile.value[candidate.fingerprint] = name
}

/** Only an edited, non-blank prompt is sent as the adopt-time rewrite. */
function promptOverrideFor(): string | undefined {
  if (!promptDirty.value) return undefined
  return promptDraft.value.trim().length > 0 ? promptDraft.value : undefined
}

/** Reflection finished (manual or unattended): refresh the open page and surface the outcome. */
async function refreshAfterReflect(details: Record<string, unknown>): Promise<void> {
  if (action.value.startsWith('evolution.')) return
  try {
    evolutionCandidates.value = await requestJson<EvolutionCandidate[]>('evolution.pending-list')
    applyInspect(await requestJson<EvolutionInspect>('evolution.inspect'))
    evolutionHistory.value = await requestJson<EvolutionRun[]>('evolution.history', { limit: 20 })
    evolutionStats.value = await requestJson<EvolutionStats>('evolution.stats')
  } catch {
    return
  }
  const num = (key: string): number | undefined =>
    typeof details[key] === 'number' ? (details[key] as number) : undefined
  emit('notice', reflectionOutcomeText({
    processed: num('processed'),
    candidates: num('candidates'),
    noAction: num('noAction'),
    skipped: num('skipped'),
    error: typeof details.error === 'string' ? details.error : undefined
  }))
}

onMounted(loadEvolution)

defineExpose({ refreshAfterReflect })
</script>

<template>
  <div class="settings-section-heading">
    <div>
      <h3>进化</h3>
      <p>从失败信号中沉淀技能：缺陷聚合 → 反思回合 → 技能草稿 → 人工审阅后生效</p>
    </div>
    <div class="row-actions">
      <button class="icon-button" title="刷新" @click="loadEvolution">
        <RefreshCw :size="17" />
      </button>
      <button class="secondary-button" :disabled="action === 'evolution.reflect'" @click="runReflection">
        <LoaderCircle v-if="action === 'evolution.reflect'" class="spin" :size="15" />
        <Telescope v-else :size="15" />立即反思
      </button>
    </div>
  </div>

  <section class="settings-group">
    <div class="settings-row">
      <div>
        <strong>健康概览</strong>
        <small>缺陷信号折算成健康分（0-100）：负反馈与工具反复失败扣分最重，分数越低越值得安排一次反思</small>
      </div>
      <div v-if="evolutionHealth" class="health-summary">
        <span class="health-score"
          :class="evolutionHealth.score >= 90 ? 'good' : evolutionHealth.score >= 60 ? 'fair' : 'poor'">
          {{ evolutionHealth.score }}
        </span>
        <span class="health-grade">{{ evolutionHealth.grade }}</span>
      </div>
    </div>
    <div v-if="evolutionTrend.length > 0" class="trend-bars">
      <div v-for="point in evolutionTrend" :key="point.date" class="trend-bar-item" :title="trendTitle(point)">
        <div class="trend-bar" :class="{ empty: trendTotal(point) === 0 }"
          :style="{ height: `${Math.max(4, Math.round((trendTotal(point) / trendMax(evolutionTrend)) * 56))}px` }" />
        <span class="trend-bar-label">{{ point.date.slice(5) }}</span>
      </div>
    </div>
    <div v-if="Object.keys(evolutionByKind).length > 0" class="kind-chips">
      <span v-for="(count, kind) in evolutionByKind" :key="kind" class="inline-badge">
        {{ defectKindLabel(String(kind)) }} · {{ count }}
      </span>
    </div>
  </section>

  <section class="settings-group">
    <label class="provider-enabled-row">
      <span>
        <strong>定时反思</strong>
        <small>按固定间隔在后台运行反思回合：复盘最近的失败信号并起草技能。技能草稿始终等待人工采纳，不会自动生效。</small>
      </span>
      <input v-model="evolutionAutoReflect" class="sr-only" type="checkbox"
        :disabled="action === 'evolution.config'"
        @change="setEvolutionConfig({ autoReflect: evolutionAutoReflect })" />
      <span class="toggle-switch" aria-hidden="true"><span /></span>
    </label>
    <div class="settings-row">
      <div>
        <strong>反思间隔</strong>
        <small>定时反思的触发周期，当前每 {{ formatInterval(evolutionIntervalMinutes) }} 复盘一次</small>
      </div>
      <SelectMenu :model-value="String(evolutionIntervalMinutes)" class="settings-select select-compact"
        :options="intervalOptions" label="反思间隔" :disabled="action === 'evolution.config'"
        @update:model-value="changeInterval" />
    </div>
    <label class="provider-enabled-row">
      <span>
        <strong>阈值反思</strong>
        <small>与定时反思并存：待处理的缺陷信号达到阈值即提前触发反思回合（两次自动反思至少间隔 30 分钟），适合故障频发时加快进化节奏。</small>
      </span>
      <input v-model="evolutionThresholdEnabled" class="sr-only" type="checkbox"
        :disabled="action === 'evolution.config'"
        @change="setEvolutionConfig({ thresholdEnabled: evolutionThresholdEnabled })" />
      <span class="toggle-switch" aria-hidden="true"><span /></span>
    </label>
    <div v-if="evolutionThresholdEnabled" class="settings-row">
      <div>
        <strong>触发阈值</strong>
        <small>待处理的缺陷信号达到 {{ evolutionThresholdSignals }} 条即自动反思</small>
      </div>
      <input class="text-input threshold-input" type="number" min="1" max="50"
        :value="evolutionThresholdSignals" :disabled="action === 'evolution.config'"
        @change="setEvolutionConfig({ thresholdSignals: Number(($event.target as HTMLInputElement).value) })" />
    </div>
  </section>

  <section class="settings-group">
    <div class="settings-row">
      <div>
        <strong>缺陷信号（{{ evolutionDefects.length }}）</strong>
        <small>最近 {{ evolutionScanned }} 条运行事件聚合出的可改进点；「高」严重度的信号值得优先关注</small>
      </div>
    </div>
    <div v-if="evolutionDefects.length === 0" class="empty-settings">暂无缺陷信号——保持这样很好</div>
    <div v-for="report in evolutionDefects" :key="report.fingerprint" class="settings-list-row">
      <Wrench :size="17" />
      <div class="list-main">
        <div>
          <strong>{{ defectKindLabel(report.kind) }}</strong>
          <span class="inline-badge" :class="severityClass(report.severity)">{{ severityLabel(report.severity) }}</span>
          <span v-if="report.occurrences > 1" class="inline-badge">{{ report.occurrences }} 次</span>
        </div>
        <small :title="report.errorSummary ?? ''">{{ report.errorSummary || report.toolName || report.fingerprint }}</small>
      </div>
      <span class="version-text" :title="report.lastSeen">{{ relativeTime(report.lastSeen) }}</span>
    </div>
  </section>

  <section class="settings-group">
    <div class="settings-row">
      <div>
        <strong>待审技能草稿（{{ evolutionCandidates.length }}）</strong>
        <small>反思回合产出的候选技能等待人工裁决：可预览草稿文件、采纳前改写提示词，或暂缓 / 丢弃</small>
      </div>
    </div>
    <div v-if="evolutionCandidates.length === 0" class="empty-settings">没有待审的技能草稿</div>
    <div v-for="candidate in evolutionCandidates" :key="candidate.fingerprint" class="candidate-block">
      <div class="settings-list-row">
        <Telescope :size="17" />
        <div class="list-main">
          <div>
            <strong :title="candidate.skillName">{{ candidate.skillName }}</strong>
            <span v-if="candidate.status === 'deferred'" class="inline-badge">暂缓</span>
          </div>
          <small :title="candidate.summary ?? ''">{{ candidate.summary || '反思产出候选技能' }}</small>
        </div>
        <div class="row-controls">
          <button class="secondary-button" :disabled="action.startsWith('evolution.decide')"
            @click="expandedCandidateFingerprint === candidate.fingerprint
              ? collapseDraftWorkbench()
              : expandDraftWorkbench(candidate)">
            {{ expandedCandidateFingerprint === candidate.fingerprint ? '收起' : '查看草稿' }}
          </button>
          <button v-if="candidate.status !== 'deferred'" class="icon-button compact" title="暂缓：保留草稿稍后审阅"
            :disabled="action.startsWith('evolution.decide')"
            @click="decideEvolutionCandidate(candidate, 'defer')">
            <Clock :size="15" />
          </button>
          <button class="icon-button compact" title="丢弃该草稿"
            :disabled="action.startsWith('evolution.decide')"
            @click="decideEvolutionCandidate(candidate, 'reject')">
            <X :size="15" />
          </button>
        </div>
      </div>
      <div v-if="expandedCandidateFingerprint === candidate.fingerprint" class="draft-workbench">
        <div v-if="(candidate.files?.length ?? 0) > 0" class="draft-file-tabs" role="tablist">
          <button v-for="file in candidate.files" :key="file.name" role="tab"
            :class="{ active: activeDraftFile(candidate)?.name === file.name }"
            @click="selectDraftFile(candidate, file.name)">{{ file.name }}</button>
        </div>
        <pre v-if="activeDraftFile(candidate)" class="draft-file-preview">{{ activeDraftFile(candidate)?.content }}</pre>
        <div class="draft-prompt-editor">
          <label class="field-label" for="draft-prompt-rewrite">提示词改写（可选）</label>
          <textarea id="draft-prompt-rewrite" v-model="promptDraft" class="text-input" rows="5"
            :placeholder="candidate.files?.some((file) => file.name === 'prompt.txt')
              ? '修改后点「保存并采纳」，改写内容会替换草稿的 prompt.txt'
              : '该草稿没有 prompt.txt 文件'"
            @input="promptDirty = true" />
        </div>
        <div class="draft-workbench-actions">
          <button class="primary-button" :disabled="action.startsWith('evolution.decide')"
            @click="decideEvolutionCandidate(candidate, 'adopt', promptOverrideFor())">
            <Check :size="14" /> {{ promptDirty ? '保存并采纳' : '采纳' }}
          </button>
        </div>
      </div>
    </div>
  </section>

  <section class="settings-group">
    <div class="settings-row">
      <div>
        <strong>反思历史</strong>
        <small>每次反思回合的触发方式与处理结果：手动、定时或缺陷阈值触发</small>
      </div>
    </div>
    <div v-if="evolutionHistory.length === 0" class="empty-settings">还没有反思回合记录</div>
    <div v-for="run in evolutionHistory" :key="run.id" class="settings-list-row">
      <History :size="17" />
      <div class="list-main">
        <div>
          <strong>{{ triggerLabel(run.trigger) }}</strong>
          <span class="inline-badge">处理 {{ run.processed }}</span>
          <span v-if="run.candidates > 0" class="inline-badge">产出 {{ run.candidates }}</span>
          <span v-if="run.failed > 0" class="inline-badge severity-high">失败 {{ run.failed }}</span>
        </div>
        <small v-if="run.error">{{ run.error }}</small>
      </div>
      <span class="version-text" :title="run.createdAt">{{ relativeTime(run.createdAt) }}</span>
    </div>
  </section>

  <section class="settings-group">
    <div class="settings-row">
      <div>
        <strong>效果追踪</strong>
        <small>已采纳技能的实际使用次数，以及对应缺陷是否复发——衡量进化是否真正解决了问题</small>
      </div>
    </div>
    <div v-if="!evolutionStats" class="empty-settings">暂无统计数据</div>
    <template v-else>
      <div class="stats-summary">
        <span class="inline-badge">反思 {{ evolutionStats.runs }}</span>
        <span class="inline-badge">草稿 {{ evolutionStats.candidatesProduced }}</span>
        <span class="inline-badge">采纳 {{ evolutionStats.adopted }}</span>
        <span class="inline-badge">拒绝 {{ evolutionStats.rejected }}</span>
        <span class="inline-badge">暂缓 {{ evolutionStats.deferred }}</span>
        <span class="inline-badge">采纳率 {{ evolutionStats.adoptionRate }}%</span>
      </div>
      <div v-if="evolutionStats.skills.length === 0" class="empty-settings">还没有已采纳的进化技能</div>
      <div v-for="skill in evolutionStats.skills" :key="skill.fingerprint" class="settings-list-row">
        <Zap :size="17" />
        <div class="list-main">
          <div>
            <strong>{{ skill.skillName }}</strong>
            <span class="inline-badge">使用 {{ skill.usageCount }} 次</span>
          </div>
          <small>缺陷类型：{{ defectKindLabel(skill.kind) }} · 采纳于 {{ formatDateOnly(skill.adoptedAt) }}</small>
        </div>
        <span class="version-text" :class="{ unresolved: !skill.resolved }">
          {{ skill.resolved ? '缺陷未复发' : '缺陷有复发' }}
        </span>
      </div>
    </template>
  </section>
</template>
