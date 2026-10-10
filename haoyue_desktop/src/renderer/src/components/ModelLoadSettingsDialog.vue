<script setup lang="ts">
import { AlertCircle, Brain, CircleHelp, Gauge, Layers, Save, SlidersHorizontal, X, Zap } from '@lucide/vue'
import { computed, reactive, ref, watch } from 'vue'

/**
 * 模型加载配置（与后端 LocalModelSettings 一一对应）。
 * null 字段 = 「自动」：沿用提供商级配置或内置默认。
 */
export interface LocalModelLoadSettings {
  autoOptimize?: boolean
  contextLength?: number | null
  gpuOffload?: number | null
  threads?: number | null
  evaluationBatchSize?: number | null
  physicalBatchSize?: number | null
  maxConcurrentPredictions?: number | null
  flashAttention?: boolean | null
  temperature?: number | null
  limitResponseLength?: boolean
  contextOverflow?: string
  stopStrings?: string[] | null
  enableThinking?: boolean | null
  reasoningBudget?: number | null
  reasoningBudgetMessage?: string | null
  offloadKvCacheToGpu?: boolean
  unifiedKvCache?: boolean | null
  contextCheckpoints?: number | null
  kCacheQuantType?: string | null
  vCacheQuantType?: string | null
  keepModelInMemory?: boolean
  tryMmap?: boolean | null
  speculativeDecoding?: string | null
  topK?: number | null
  topP?: number | null
  minP?: number | null
  repeatPenalty?: number | null
  presencePenalty?: number | null
  chatTemplate?: string | null
  ropeFrequencyBase?: number | null
  ropeFrequencyScale?: number | null
  seed?: number | null
  llamaCppOverrideEnabled?: boolean
  llamaCppOverride?: string | null
}

const props = defineProps<{
  open: boolean
  providerId: string | null
  modelId: string | null
  initialLoad?: LocalModelLoadSettings | null
}>()

const emit = defineEmits<{
  close: []
  saved: [load: LocalModelLoadSettings | null]
}>()

const saving = ref(false)
const verifying = ref(false)
const error = ref('')
const notice = ref('')
const loadReport = ref<Record<string, unknown> | null>(null)

/** 表单模型：null 用 '' 表示（输入框留空 = 自动）。 */
const form = reactive({
  autoOptimize: true,
  contextLength: '',
  gpuOffload: '',
  threads: '',
  evaluationBatchSize: '2048',
  physicalBatchSize: '512',
  maxConcurrentPredictions: '',
  flashAttention: true,
  temperature: '0.6',
  limitResponseLength: false,
  contextOverflow: 'truncateMiddle',
  stopStringInput: '',
  stopStrings: [] as string[],
  enableThinking: false,
  reasoningBudgetEnabled: false,
  reasoningBudget: '8192',
  reasoningBudgetMessage: '',
  offloadKvCacheToGpu: true,
  unifiedKvCache: true,
  contextCheckpoints: '32',
  kCacheQuantEnabled: false,
  kCacheQuantType: 'none',
  vCacheQuantEnabled: false,
  vCacheQuantType: 'none',
  keepModelInMemory: true,
  tryMmap: true,
  speculativeDecoding: 'off',
  draftModelPath: '',
  topK: '40',
  topPEnabled: true,
  topP: '0.95',
  minPEnabled: true,
  minP: '0.05',
  repeatPenaltyEnabled: true,
  repeatPenalty: '1.1',
  presencePenaltyEnabled: false,
  presencePenalty: '0',
  chatTemplate: '',
  ropeBaseEnabled: false,
  ropeFrequencyBase: '',
  ropeScaleEnabled: false,
  ropeFrequencyScale: '',
  seedEnabled: false,
  seed: '',
  llamaCppOverrideEnabled: false,
  llamaCppOverride: ''
})

function num(value: string): number | null {
  const parsed = Number(value)
  return value !== '' && Number.isFinite(parsed) ? parsed : null
}

/** 表单 → 传输对象（'' 还原为 null = 自动）。 */
function toLoad(): LocalModelLoadSettings {
  return {
    autoOptimize: form.autoOptimize,
    contextLength: form.autoOptimize ? null : num(form.contextLength),
    gpuOffload: form.autoOptimize ? null : num(form.gpuOffload),
    threads: form.autoOptimize ? null : num(form.threads),
    evaluationBatchSize: num(form.evaluationBatchSize),
    physicalBatchSize: num(form.physicalBatchSize),
    maxConcurrentPredictions: num(form.maxConcurrentPredictions),
    flashAttention: form.flashAttention,
    temperature: num(form.temperature),
    limitResponseLength: form.limitResponseLength,
    contextOverflow: form.contextOverflow,
    stopStrings: form.stopStrings.length > 0 ? [...form.stopStrings] : null,
    enableThinking: form.enableThinking,
    reasoningBudget: form.reasoningBudgetEnabled ? num(form.reasoningBudget) : null,
    reasoningBudgetMessage: form.reasoningBudgetMessage.trim() || null,
    offloadKvCacheToGpu: form.offloadKvCacheToGpu,
    unifiedKvCache: form.unifiedKvCache,
    contextCheckpoints: num(form.contextCheckpoints),
    kCacheQuantType: form.kCacheQuantEnabled ? form.kCacheQuantType : null,
    vCacheQuantType: form.vCacheQuantEnabled ? form.vCacheQuantType : null,
    keepModelInMemory: form.keepModelInMemory,
    tryMmap: form.tryMmap,
    speculativeDecoding: form.speculativeDecoding === 'custom' ? form.draftModelPath.trim() || null : null,
    topK: num(form.topK),
    topP: form.topPEnabled ? num(form.topP) : null,
    minP: form.minPEnabled ? num(form.minP) : null,
    repeatPenalty: form.repeatPenaltyEnabled ? num(form.repeatPenalty) : null,
    presencePenalty: form.presencePenaltyEnabled ? num(form.presencePenalty) : null,
    chatTemplate: form.chatTemplate.trim() || null,
    ropeFrequencyBase: form.ropeBaseEnabled ? num(form.ropeFrequencyBase) : null,
    ropeFrequencyScale: form.ropeScaleEnabled ? num(form.ropeFrequencyScale) : null,
    seed: form.seedEnabled ? num(form.seed) : null,
    llamaCppOverrideEnabled: form.llamaCppOverrideEnabled,
    llamaCppOverride: form.llamaCppOverrideEnabled ? form.llamaCppOverride.trim() || null : null
  }
}

function hydrate(load: LocalModelLoadSettings | null | undefined): void {
  form.autoOptimize = load?.autoOptimize !== false
  form.contextLength = load?.contextLength?.toString() ?? ''
  form.gpuOffload = load?.gpuOffload?.toString() ?? ''
  form.threads = load?.threads?.toString() ?? ''
  form.evaluationBatchSize = load?.evaluationBatchSize?.toString() ?? '2048'
  form.physicalBatchSize = load?.physicalBatchSize?.toString() ?? '512'
  form.maxConcurrentPredictions = load?.maxConcurrentPredictions?.toString() ?? ''
  form.flashAttention = load?.flashAttention !== false
  form.temperature = load?.temperature?.toString() ?? '0.6'
  form.limitResponseLength = load?.limitResponseLength === true
  form.contextOverflow = load?.contextOverflow ?? 'truncateMiddle'
  form.stopStrings = [...(load?.stopStrings ?? [])]
  form.stopStringInput = ''
  form.enableThinking = load?.enableThinking === true
  form.reasoningBudgetEnabled = load?.reasoningBudget != null
  form.reasoningBudget = load?.reasoningBudget?.toString() ?? '8192'
  form.reasoningBudgetMessage = load?.reasoningBudgetMessage ?? ''
  form.offloadKvCacheToGpu = load?.offloadKvCacheToGpu !== false
  form.unifiedKvCache = load?.unifiedKvCache !== false
  form.contextCheckpoints = load?.contextCheckpoints?.toString() ?? '32'
  form.kCacheQuantEnabled = load?.kCacheQuantType != null
  form.kCacheQuantType = load?.kCacheQuantType ?? 'none'
  form.vCacheQuantEnabled = load?.vCacheQuantType != null
  form.vCacheQuantType = load?.vCacheQuantType ?? 'none'
  form.keepModelInMemory = load?.keepModelInMemory !== false
  form.tryMmap = load?.tryMmap !== false
  form.speculativeDecoding = load?.speculativeDecoding ? 'custom' : 'off'
  form.draftModelPath = load?.speculativeDecoding ?? ''
  form.topK = load?.topK?.toString() ?? '40'
  form.topPEnabled = load?.topP != null
  form.topP = load?.topP?.toString() ?? '0.95'
  form.minPEnabled = load?.minP != null
  form.minP = load?.minP?.toString() ?? '0.05'
  form.repeatPenaltyEnabled = load?.repeatPenalty != null
  form.repeatPenalty = load?.repeatPenalty?.toString() ?? '1.1'
  form.presencePenaltyEnabled = load?.presencePenalty != null
  form.presencePenalty = load?.presencePenalty?.toString() ?? '0'
  form.chatTemplate = load?.chatTemplate ?? ''
  form.ropeBaseEnabled = load?.ropeFrequencyBase != null
  form.ropeFrequencyBase = load?.ropeFrequencyBase?.toString() ?? ''
  form.ropeScaleEnabled = load?.ropeFrequencyScale != null
  form.ropeFrequencyScale = load?.ropeFrequencyScale?.toString() ?? ''
  form.seedEnabled = load?.seed != null
  form.seed = load?.seed?.toString() ?? ''
  form.llamaCppOverrideEnabled = load?.llamaCppOverrideEnabled === true
  form.llamaCppOverride = load?.llamaCppOverride ?? ''
}

watch(() => props.open, (open) => {
  if (!open) return
  error.value = ''
  notice.value = ''
  loadReport.value = null
  hydrate(props.initialLoad)
})

function addStopString(): void {
  const value = form.stopStringInput.trim()
  if (!value || form.stopStrings.includes(value)) {
    form.stopStringInput = ''
    return
  }
  form.stopStrings.push(value)
  form.stopStringInput = ''
}

function removeStopString(index: number): void {
  form.stopStrings.splice(index, 1)
}

const validationError = computed((): string => {
  if (!form.autoOptimize) {
    const context = num(form.contextLength)
    if (context !== null && context < 512) return '上下文长度不能小于 512（留空 = 自动）'
    const gpu = num(form.gpuOffload)
    if (gpu !== null && gpu < 0) return 'GPU 卸载层数不能为负数（留空 = 自动）'
  }
  const nBatch = num(form.evaluationBatchSize)
  if (nBatch !== null && (nBatch < 16 || nBatch > 8192)) return '评估批大小取值范围 16 - 8192'
  const uBatch = num(form.physicalBatchSize)
  if (uBatch !== null && (uBatch < 16 || uBatch > 8192)) return '物理批大小取值范围 16 - 8192'
  const temperature = num(form.temperature)
  if (temperature !== null && (temperature < 0 || temperature > 2)) return '温度取值范围 0 - 2'
  return ''
})

async function handleSave(): Promise<void> {
  if (validationError.value || saving.value || !props.providerId || !props.modelId) return
  saving.value = true
  error.value = ''
  notice.value = ''
  try {
    const load = toLoad()
    await window.haoyue.daemon.request('model.update', {
      provider: props.providerId,
      id: props.modelId,
      load
    })
    notice.value = '加载配置已保存，下一次对话或加载验证即按新配置生效'
    emit('saved', load)
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    saving.value = false
  }
}

/**
 * 加载并验证：走真实加载流程（含后端重试 / CPU 降级 / 加载后推理自检），
 * 结果直接反映新配置是否可用。
 */
async function handleVerify(): Promise<void> {
  if (verifying.value || !props.modelId) return
  verifying.value = true
  error.value = ''
  notice.value = '正在按当前配置加载模型并试运行…'
  loadReport.value = null
  try {
    const result = JSON.parse(
      (await window.haoyue.daemon.request('model.test', { model: props.modelId })).data
    ) as { success: boolean; detail: string; latencyMs: number }
    if (result.success) {
      notice.value = `加载并推理验证通过 · ${Math.round(result.latencyMs)} ms · ${result.detail}`
    } else {
      error.value = `加载验证失败：${result.detail}`
    }
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    verifying.value = false
  }
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && !saving.value) emit('close')
  if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
    event.preventDefault()
    void handleSave()
  }
}

function onSectionKeydown(event: KeyboardEvent): void {
  handleKeydown(event)
}
</script>

<template>
  <Teleport to="body">
    <Transition name="modal-fade">
      <div
        v-if="open"
        class="modal-backdrop load-config-backdrop"
        @mousedown.self="!saving && emit('close')"
      >
        <div
          class="load-config-dialog"
          role="dialog"
          aria-modal="true"
          aria-labelledby="load-config-title"
          tabindex="-1"
          @keydown="onSectionKeydown"
        >
          <header class="load-config-header">
            <div class="load-config-title-block">
              <h2 id="load-config-title">模型加载配置</h2>
              <p class="load-config-subtitle">{{ modelId }} · 留空的字段保持「自动」</p>
            </div>
            <button class="icon-button" type="button" title="关闭" @click="!saving && emit('close')">
              <X :size="18" />
            </button>
          </header>

          <div class="load-config-body">
            <!-- 上下文与性能 -->
            <section class="load-section">
              <h3 class="load-section-heading"><Gauge :size="15" /> 上下文与性能</h3>

              <div class="load-row">
                <span class="load-label">
                  Automatic Optimize Based on Hardware
                  <CircleHelp :size="13" class="help-icon" />
                  <span class="badge recommended">RECOMMENDED</span>
                </span>
                <label class="load-switch">
                  <input v-model="form.autoOptimize" class="sr-only" type="checkbox" />
                  <span class="switch-track" aria-hidden="true"><span /></span>
                </label>
              </div>

              <div class="load-row indented">
                <span class="load-label muted">Context Length <CircleHelp :size="13" class="help-icon" /></span>
                <input
                  v-model="form.contextLength"
                  class="load-input auto-input"
                  :class="{ 'is-auto': form.autoOptimize }"
                  :disabled="form.autoOptimize"
                  :placeholder="form.autoOptimize ? 'AUTO' : '例如 8192'"
                  type="number"
                  min="512"
                />
              </div>

              <div class="load-row indented">
                <span class="load-label muted">GPU Offload <CircleHelp :size="13" class="help-icon" /></span>
                <input
                  v-model="form.gpuOffload"
                  class="load-input auto-input"
                  :class="{ 'is-auto': form.autoOptimize }"
                  :disabled="form.autoOptimize"
                  :placeholder="form.autoOptimize ? 'AUTO' : '卸载层数，例如 24'"
                  type="number"
                  min="0"
                />
              </div>

              <div class="load-row">
                <span class="load-label">CPU Thread Pool Size <CircleHelp :size="13" class="help-icon" /></span>
                <input
                  v-model="form.threads"
                  class="load-input auto-input"
                  :class="{ 'is-auto': form.autoOptimize }"
                  :disabled="form.autoOptimize"
                  :placeholder="form.autoOptimize ? 'AUTO' : '例如 6'"
                  type="number"
                  min="1"
                />
              </div>

              <div class="load-row">
                <span class="load-label">Evaluation Batch Size <CircleHelp :size="13" class="help-icon" /></span>
                <input v-model="form.evaluationBatchSize" class="load-input" type="number" min="16" max="8192" />
              </div>
              <div class="load-row">
                <span class="load-label">Physical Batch Size <CircleHelp :size="13" class="help-icon" /></span>
                <input v-model="form.physicalBatchSize" class="load-input" type="number" min="16" max="8192" />
              </div>

              <div class="load-row">
                <span class="load-label">
                  Max Concurrent Predictions <CircleHelp :size="13" class="help-icon" />
                  <span class="badge experimental">Experimental</span>
                </span>
                <input v-model="form.maxConcurrentPredictions" class="load-input" type="number" min="1" max="64" placeholder="AUTO" />
              </div>

              <div class="load-row">
                <span class="load-label">Flash Attention <CircleHelp :size="13" class="help-icon" /></span>
                <label class="load-switch">
                  <input v-model="form.flashAttention" class="sr-only" type="checkbox" />
                  <span class="switch-track" aria-hidden="true"><span /></span>
                </label>
              </div>
            </section>

            <!-- 生成 -->
            <section class="load-section">
              <h3 class="load-section-heading"><SlidersHorizontal :size="15" /> 生成</h3>

              <div class="load-row vertical">
                <span class="load-label">Temperature <CircleHelp :size="13" class="help-icon" /></span>
                <div class="slider-row">
                  <input v-model.number="form.temperature" class="slider" type="range" min="0" max="2" step="0.05" />
                  <input v-model="form.temperature" class="load-input slider-value" type="number" min="0" max="2" step="0.05" />
                </div>
              </div>

              <div class="load-row">
                <span class="load-label">Limit Response Length <CircleHelp :size="13" class="help-icon" /></span>
                <input v-model="form.limitResponseLength" class="load-checkbox" type="checkbox" />
              </div>

              <div class="load-row">
                <span class="load-label">Context Overflow <CircleHelp :size="13" class="help-icon" /></span>
                <select v-model="form.contextOverflow" class="load-input select-input">
                  <option value="truncateMiddle">Truncate Middle</option>
                </select>
              </div>

              <div class="load-row vertical">
                <span class="load-label">Stop Strings <CircleHelp :size="13" class="help-icon" /></span>
                <input
                  v-model="form.stopStringInput"
                  class="load-input"
                  placeholder="输入后按回车添加"
                  @keydown.enter.prevent="addStopString"
                />
                <div v-if="form.stopStrings.length" class="stop-chips">
                  <button
                    v-for="(stop, idx) in form.stopStrings"
                    :key="stop + idx"
                    type="button"
                    class="stop-chip"
                    :title="'点击移除'"
                    @click="removeStopString(idx)"
                  >
                    {{ stop }} ×
                  </button>
                </div>
              </div>
            </section>

            <!-- 思考 -->
            <section class="load-section">
              <h3 class="load-section-heading"><Brain :size="15" /> 思考</h3>

              <div class="load-row">
                <span class="load-label">Enable Thinking</span>
                <label class="load-switch">
                  <input v-model="form.enableThinking" class="sr-only" type="checkbox" />
                  <span class="switch-track" aria-hidden="true"><span /></span>
                </label>
              </div>
              <p class="load-hint">Controls thinking for chat templates that support enable_thinking.</p>

              <div class="load-row">
                <span class="load-label">
                  Reasoning Budget <CircleHelp :size="13" class="help-icon" />
                  <input v-model="form.reasoningBudgetEnabled" class="load-checkbox inline-check" type="checkbox" />
                </span>
                <input v-model="form.reasoningBudget" class="load-input" :disabled="!form.reasoningBudgetEnabled" type="number" min="256" />
              </div>

              <div class="load-row vertical">
                <span class="load-label">Reasoning Budget Message <CircleHelp :size="13" class="help-icon" /></span>
                <input v-model="form.reasoningBudgetMessage" class="load-input" placeholder="I have to answer now." />
              </div>
            </section>

            <!-- 内存 -->
            <section class="load-section">
              <h3 class="load-section-heading"><Layers :size="15" /> 内存</h3>

              <div class="load-row">
                <span class="load-label">Offload KV Cache to GPU Memory <CircleHelp :size="13" class="help-icon" /></span>
                <label class="load-switch">
                  <input v-model="form.offloadKvCacheToGpu" class="sr-only" type="checkbox" />
                  <span class="switch-track" aria-hidden="true"><span /></span>
                </label>
              </div>
              <div class="load-row">
                <span class="load-label">
                  Unified KV Cache <CircleHelp :size="13" class="help-icon" />
                  <span class="badge experimental">Experimental</span>
                </span>
                <label class="load-switch">
                  <input v-model="form.unifiedKvCache" class="sr-only" type="checkbox" />
                  <span class="switch-track" aria-hidden="true"><span /></span>
                </label>
              </div>
              <div class="load-row">
                <span class="load-label">Context Checkpoints <CircleHelp :size="13" class="help-icon" /></span>
                <input v-model="form.contextCheckpoints" class="load-input" type="number" min="1" placeholder="AUTO" />
              </div>
              <div class="load-row">
                <span class="load-label">
                  K Cache Quantization Type <CircleHelp :size="13" class="help-icon" />
                  <input v-model="form.kCacheQuantEnabled" class="load-checkbox inline-check" type="checkbox" />
                  <span class="badge experimental">Experimental</span>
                </span>
                <select v-model="form.kCacheQuantType" class="load-input select-input" :disabled="!form.kCacheQuantEnabled">
                  <option value="none">f16（不量化）</option>
                  <option value="q8_0">q8_0</option>
                  <option value="q4_0">q4_0</option>
                </select>
              </div>
              <div class="load-row">
                <span class="load-label">
                  V Cache Quantization Type <CircleHelp :size="13" class="help-icon" />
                  <input v-model="form.vCacheQuantEnabled" class="load-checkbox inline-check" type="checkbox" />
                  <span class="badge experimental">Experimental</span>
                </span>
                <select v-model="form.vCacheQuantType" class="load-input select-input" :disabled="!form.vCacheQuantEnabled">
                  <option value="none">f16（不量化）</option>
                  <option value="q8_0">q8_0</option>
                  <option value="q4_0">q4_0</option>
                </select>
              </div>
              <div class="load-row">
                <span class="load-label">Keep Model in Memory <CircleHelp :size="13" class="help-icon" /></span>
                <label class="load-switch">
                  <input v-model="form.keepModelInMemory" class="sr-only" type="checkbox" />
                  <span class="switch-track" aria-hidden="true"><span /></span>
                </label>
              </div>
              <div class="load-row">
                <span class="load-label">Try mmap() <CircleHelp :size="13" class="help-icon" /></span>
                <label class="load-switch">
                  <input v-model="form.tryMmap" class="sr-only" type="checkbox" />
                  <span class="switch-track" aria-hidden="true"><span /></span>
                </label>
              </div>
            </section>

            <!-- 推测解码 -->
            <section class="load-section">
              <h3 class="load-section-heading"><Zap :size="15" /> 推测解码</h3>
              <div class="load-row">
                <span class="load-label">Speculative Decoding <CircleHelp :size="13" class="help-icon" /></span>
                <select v-model="form.speculativeDecoding" class="load-input select-input">
                  <option value="off">Off</option>
                  <option value="custom">草稿模型路径…</option>
                </select>
              </div>
              <div v-if="form.speculativeDecoding === 'custom'" class="load-row vertical">
                <input v-model="form.draftModelPath" class="load-input" placeholder="草稿 GGUF 模型文件路径（实验性，当前版本仅保存）" />
              </div>
            </section>

            <!-- 高级 -->
            <section class="load-section">
              <h3 class="load-section-heading"><AlertCircle :size="15" /> 高级</h3>

              <div class="load-row">
                <span class="load-label">Top K Sampling <CircleHelp :size="13" class="help-icon" /></span>
                <input v-model="form.topK" class="load-input" type="number" min="1" max="512" />
              </div>

              <div class="load-row">
                <span class="load-label">
                  Top P Sampling <CircleHelp :size="13" class="help-icon" />
                  <input v-model="form.topPEnabled" class="load-checkbox inline-check" type="checkbox" />
                </span>
                <input v-model="form.topP" class="load-input" :disabled="!form.topPEnabled" type="number" min="0.01" max="1" step="0.01" />
              </div>
              <div v-if="form.topPEnabled" class="load-row vertical">
                <input v-model.number="form.topP" class="slider" type="range" min="0.01" max="1" step="0.01" />
              </div>

              <div class="load-row">
                <span class="load-label">
                  Min P Sampling <CircleHelp :size="13" class="help-icon" />
                  <input v-model="form.minPEnabled" class="load-checkbox inline-check" type="checkbox" />
                </span>
                <input v-model="form.minP" class="load-input" :disabled="!form.minPEnabled" type="number" min="0" max="1" step="0.01" />
              </div>
              <div v-if="form.minPEnabled" class="load-row vertical">
                <input v-model.number="form.minP" class="slider" type="range" min="0" max="1" step="0.01" />
              </div>

              <div class="load-row">
                <span class="load-label">
                  Repeat Penalty <CircleHelp :size="13" class="help-icon" />
                  <input v-model="form.repeatPenaltyEnabled" class="load-checkbox inline-check" type="checkbox" />
                </span>
                <input v-model="form.repeatPenalty" class="load-input" :disabled="!form.repeatPenaltyEnabled" type="number" min="0.5" max="2" step="0.05" />
              </div>

              <div class="load-row">
                <span class="load-label">
                  Presence Penalty
                  <input v-model="form.presencePenaltyEnabled" class="load-checkbox inline-check" type="checkbox" />
                </span>
                <input v-model="form.presencePenalty" class="load-input" :disabled="!form.presencePenaltyEnabled" type="number" min="-2" max="2" step="0.1" />
              </div>

              <div class="load-row">
                <span class="load-label">Chat Template <CircleHelp :size="13" class="help-icon" /></span>
                <span class="load-label muted">跟随 GGUF 内嵌模板</span>
              </div>
              <div v-if="form.chatTemplate" class="load-row vertical">
                <input v-model="form.chatTemplate" class="load-input" placeholder="模板覆盖（当前版本仅保存）" />
              </div>

              <div class="load-row">
                <span class="load-label">
                  RoPE Frequency Base <CircleHelp :size="13" class="help-icon" />
                  <input v-model="form.ropeBaseEnabled" class="load-checkbox inline-check" type="checkbox" />
                </span>
                <input
                  v-model="form.ropeFrequencyBase"
                  class="load-input auto-input"
                  :class="{ 'is-auto': !form.ropeBaseEnabled }"
                  :disabled="!form.ropeBaseEnabled"
                  placeholder="Auto"
                  type="number"
                  min="1"
                />
              </div>
              <div class="load-row">
                <span class="load-label">
                  RoPE Frequency Scale <CircleHelp :size="13" class="help-icon" />
                  <input v-model="form.ropeScaleEnabled" class="load-checkbox inline-check" type="checkbox" />
                </span>
                <input
                  v-model="form.ropeFrequencyScale"
                  class="load-input auto-input"
                  :class="{ 'is-auto': !form.ropeScaleEnabled }"
                  :disabled="!form.ropeScaleEnabled"
                  placeholder="Auto"
                  type="number"
                  min="0.1"
                  step="0.05"
                />
              </div>

              <div class="load-row">
                <span class="load-label">
                  Seed <CircleHelp :size="13" class="help-icon" />
                  <input v-model="form.seedEnabled" class="load-checkbox inline-check" type="checkbox" />
                </span>
                <input
                  v-model="form.seed"
                  class="load-input auto-input"
                  :class="{ 'is-auto': !form.seedEnabled }"
                  :disabled="!form.seedEnabled"
                  placeholder="Random Seed"
                  type="number"
                />
              </div>

              <div class="load-row">
                <span class="load-label">llama.cpp 参数覆盖</span>
                <label class="load-switch">
                  <input v-model="form.llamaCppOverrideEnabled" class="sr-only" type="checkbox" />
                  <span class="switch-track" aria-hidden="true"><span /></span>
                </label>
              </div>
              <div v-if="form.llamaCppOverrideEnabled" class="load-row vertical">
                <textarea
                  v-model="form.llamaCppOverride"
                  class="load-input override-text"
                  rows="3"
                  placeholder="原始 llama.cpp 参数串（实验性，当前版本仅保存）"
                  spellcheck="false"
                />
              </div>
            </section>
          </div>

          <div v-if="validationError || error" class="load-config-error" role="alert">{{ validationError || error }}</div>
          <div v-else-if="notice" class="load-config-notice" role="status">{{ notice }}</div>

          <footer class="load-config-footer">
            <span>按 Ctrl + Enter 保存 · 保存后自动使常驻权重失效并按新配置重载</span>
            <div class="footer-buttons">
              <button class="secondary-button" type="button" :disabled="verifying" @click="handleVerify">
                <Zap :size="15" /> {{ verifying ? '加载验证中…' : '加载并验证' }}
              </button>
              <button class="secondary-button" type="button" :disabled="saving" @click="emit('close')">取消</button>
              <button
                class="secondary-button primary-action"
                type="button"
                :disabled="saving || Boolean(validationError)"
                @click="handleSave"
              >
                <Save :size="15" /> {{ saving ? '保存中…' : '保存配置' }}
              </button>
            </div>
          </footer>
        </div>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.load-config-backdrop {
  position: fixed;
  z-index: 210;
  inset: 0;
  display: grid;
  place-items: center;
  padding: 20px;
  background: rgba(0, 0, 0, 0.45);
  backdrop-filter: blur(8px);
}

.load-config-dialog {
  display: flex;
  flex-direction: column;
  width: min(100%, 640px);
  max-height: min(820px, calc(100vh - 40px));
  background: var(--surface-raised, #ffffff);
  border: 1px solid var(--border);
  border-radius: 12px;
  box-shadow: 0 24px 70px rgba(0, 0, 0, 0.28), 0 2px 10px rgba(0, 0, 0, 0.1);
  overflow: hidden;
}

.load-config-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 20px;
  padding: 16px 24px;
  border-bottom: 1px solid var(--border);
}

.load-config-title-block h2 {
  margin: 0;
  font-size: 17px;
  font-weight: 650;
  color: var(--text);
}

.load-config-subtitle {
  margin: 3px 0 0;
  color: var(--text-muted);
  font-size: 12px;
}

.load-config-body {
  flex: 1;
  padding: 18px 24px;
  overflow-y: auto;
  overflow-x: hidden;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.load-section {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 14px 16px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: color-mix(in srgb, var(--surface-hover, var(--surface)) 45%, transparent);
}

.load-section-heading {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0 0 2px;
  font-size: 13px;
  font-weight: 650;
  color: var(--text);
}

.load-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 14px;
  min-height: 32px;
}

.load-row.indented {
  padding-left: 14px;
}

.load-row.vertical {
  flex-direction: column;
  align-items: stretch;
  gap: 6px;
}

.load-label {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12.5px;
  color: var(--text);
}

.load-label.muted {
  color: var(--text-muted);
}

.help-icon {
  color: var(--text-muted);
  opacity: 0.75;
}

.badge {
  display: inline-flex;
  align-items: center;
  padding: 1px 7px;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.03em;
}

.badge.recommended {
  color: var(--accent);
  background: color-mix(in srgb, var(--accent) 12%, transparent);
  border: 1px solid color-mix(in srgb, var(--accent) 35%, transparent);
}

.badge.experimental {
  color: var(--text-muted);
  background: color-mix(in srgb, var(--text-muted) 10%, transparent);
  border: 1px solid var(--border);
}

.load-input {
  width: 180px;
  box-sizing: border-box;
  min-height: 30px;
  padding: 5px 10px;
  color: var(--text);
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 7px;
  outline: none;
  font-size: 12.5px;
  font-family: inherit;
}

.load-row.vertical .load-input {
  width: 100%;
}

.load-input:focus {
  border-color: color-mix(in srgb, var(--accent) 66%, var(--border));
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 18%, transparent);
}

.load-input:disabled {
  color: var(--text-muted);
  background: var(--surface-hover);
  cursor: not-allowed;
}

.load-input.is-auto {
  color: var(--text-muted);
  text-align: right;
}

.select-input {
  width: 200px;
}

.load-checkbox {
  width: 16px;
  height: 16px;
  accent-color: var(--accent);
}

.inline-check {
  margin-left: 2px;
}

.load-switch {
  display: inline-flex;
  cursor: pointer;
}

.switch-track {
  display: flex;
  width: 36px;
  height: 21px;
  align-items: center;
  padding: 2px;
  background: var(--border-strong);
  border-radius: 999px;
  transition: background-color 160ms ease;
}

.switch-track > span {
  width: 17px;
  height: 17px;
  background: white;
  border-radius: 50%;
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.2);
  transition: transform 180ms cubic-bezier(0.2, 0.8, 0.2, 1);
}

.load-switch input:checked + .switch-track {
  background: var(--accent);
}

.load-switch input:checked + .switch-track > span {
  transform: translateX(15px);
}

.slider-row {
  display: flex;
  flex: 1;
  gap: 12px;
  align-items: center;
}

.slider {
  flex: 1;
  accent-color: var(--accent);
}

.slider-value {
  width: 110px;
}

.stop-chips {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.stop-chip {
  padding: 3px 9px;
  color: var(--text);
  font-size: 11.5px;
  background: var(--surface-hover);
  border: 1px solid var(--border);
  border-radius: 999px;
  cursor: pointer;
}

.stop-chip:hover {
  border-color: var(--danger);
  color: var(--danger);
}

.load-hint {
  margin: -4px 0 0;
  color: var(--text-muted);
  font-size: 11.5px;
}

.override-text {
  width: 100%;
  resize: vertical;
  font-family: ui-monospace, Consolas, monospace;
}

.load-config-error {
  padding: 9px 24px;
  color: var(--danger);
  font-size: 12px;
  background: color-mix(in srgb, var(--danger) 9%, transparent);
  border-top: 1px solid color-mix(in srgb, var(--danger) 25%, transparent);
}

.load-config-notice {
  padding: 9px 24px;
  color: var(--text);
  font-size: 12px;
  background: color-mix(in srgb, var(--accent) 9%, transparent);
  border-top: 1px solid color-mix(in srgb, var(--accent) 25%, transparent);
}

.load-config-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 13px 24px;
  background: color-mix(in srgb, var(--sidebar) 72%, var(--surface-raised));
  border-top: 1px solid var(--border);
}

.load-config-footer > span {
  color: var(--text-muted);
  font-size: 11.5px;
}

.footer-buttons {
  display: flex;
  gap: 8px;
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  white-space: nowrap;
  border: 0;
  clip: rect(0, 0, 0, 0);
}
</style>
