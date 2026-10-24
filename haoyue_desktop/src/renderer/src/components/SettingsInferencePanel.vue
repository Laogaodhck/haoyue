<script setup lang="ts">
import { Download, Gauge, LoaderCircle, Save, Trash2, TriangleAlert, Zap } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { requestJson } from '../daemon-json'
import { confirmAction } from '../confirmation'
import FieldLabel from './FieldLabel.vue'
import SelectMenu from './SelectMenu.vue'

/**
 * 设置 → 推理加速。
 *
 * 旧版这里是「本地推理参数」的**第二个写入口**：它写 `provider.GpuLayers/FlashAttention/
 * KvCacheQuantization`，而「模型与提供商 → 模型加载设置」写 `model.Load.*`；生效优先级是
 * 模型级覆盖提供商级，于是用户只要动过一次加载设置，本页的 FA/KV 就**永久失效**，
 * 界面没有任何提示。两个页面还各自写了不同字段来表达「上下文长度」。
 *
 * 现在的分工：
 * - **看板**：每个本地模型最终生效的参数、来源层级、驻留状态（模型级 → 提供商级 → 默认）；
 * - **写入口唯一**：模型级参数在「模型与提供商 → 模型加载设置」，本页只写「提供商级默认」
 *   并明确标注它会被模型级覆盖；
 * - **运维动作**：预热 / 卸载 / 基准测试，以及显存治理的四个配置项。
 */
const props = defineProps<{ connected: boolean }>()

interface LocalModel {
  ref: string
  provider: string
  id: string
  alias?: string
  contextWindow: number
}

interface LoadSettings {
  autoOptimize?: boolean
  gpuOffload?: number | null
  threads?: number | null
  contextLength?: number | null
  flashAttention?: boolean | null
  kCacheQuantType?: string | null
  vCacheQuantType?: string | null
  offloadKvCacheToGpu?: boolean | null
}

interface ModelConfigDetail {
  provider: string
  id: string
  alias?: string
  contextWindow: number
  load: LoadSettings | null
  providerDefaults: {
    gpuLayers?: number | null
    threads?: number | null
    flashAttention?: boolean
    kvCacheQuantization?: string | null
  }
  resolvedPath?: string
  fileExists?: boolean
  fileSizeBytes?: number
  autoMmprojPath?: string | null
  fallbackModelId?: string | null
  residentInMemory?: boolean
  residentCount?: number
  maxResidentModels?: number
  concurrency?: number
  supportsPrefixReuse?: boolean
  idleUnloadMinutes?: number
  estimatedLoadMs?: number
  slots?: Array<{
    path: string
    loadSignature?: string
    weightBytes?: number
    lastUsedUtc?: string
    report?: { effectiveGpuLayers?: string; degradedToCpu?: boolean; loadDurationMs?: number }
  }>
}

interface BenchAggregate {
  steadyRuns?: number
  ttftMsMean?: number
  ttftMsP50?: number
  decodeTokensPerSecondMean?: number
  decodeTokensPerSecondP50?: number
  prefillTokensPerSecondMean?: number
  cacheHitRateMean?: number
}

const models = ref<LocalModel[]>([])
const selected = ref('')
const detail = ref<ModelConfigDetail | null>(null)
const loading = ref(false)
const busy = ref('')
const note = ref('')
const error = ref('')

/** 显存治理（Agent.LocalModel*）：8GB 卡的刚需，此前只在 RPC 层。 */
const governance = reactive({
  localModelPreload: true,
  localModelMaxResident: 2,
  localModelConcurrency: 1,
  localModelIdleUnloadMinutes: 20
})

/** 提供商级默认（会写回 provider.*，被模型级覆盖）。 */
const providerDefaults = reactive({
  gpuLayers: 0,
  contextLength: 8192,
  kvCacheQuantization: 'none',
  flashAttention: false
})
const providerLevelAvailable = ref(false)
const providerLevelOpen = ref(false)
const providerId = ref('')

const kvOptions = [
  { value: 'none', label: 'f16（原生，不量化）', description: '最高精度，显存占用最大' },
  { value: 'q8_0', label: 'q8_0', description: '接近 f16 的精度，显存减半' },
  { value: 'q4_0', label: 'q4_0', description: '显存占用最小，精度损失明显' }
]

const bench = reactive({ runs: 3, maxTokens: 256 })
const benchResult = ref<BenchAggregate | null>(null)
const benchModel = ref('')

const modelOptions = computed(() => models.value.map((model) => ({
  value: model.ref,
  label: model.alias ? `${model.alias}（${model.ref}）` : model.ref,
  description: `${model.contextWindow.toLocaleString()} 上下文 · ${model.provider}`
})))

/** 模型级显式设置了上下文时，提供商级的「上下文长度」对它是无效的——必须点名提示。 */
const overriddenByModel = computed(() => {
  const loaded = detail.value?.load
  if (!loaded) return [] as string[]
  const fields: string[] = []
  if (loaded.autoOptimize === false && loaded.gpuOffload !== null && loaded.gpuOffload !== undefined)
    fields.push('GPU 层卸载')
  if (loaded.flashAttention !== null && loaded.flashAttention !== undefined) fields.push('Flash Attention')
  if (loaded.kCacheQuantType) fields.push('KV 量化')
  return fields
})

function formatBytes(bytes?: number): string {
  if (!bytes) return '—'
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(2)} GB`
  return `${Math.round(bytes / 1024 ** 2)} MB`
}

function relativeTime(iso?: string): string {
  if (!iso) return '—'
  const time = new Date(iso).getTime()
  if (Number.isNaN(time)) return '—'
  const minutes = Math.max(0, Math.round((Date.now() - time) / 60000))
  if (minutes < 1) return '刚刚'
  if (minutes < 60) return `${minutes} 分钟前`
  const hours = Math.round(minutes / 60)
  if (hours < 24) return `${hours} 小时前`
  return `${Math.round(hours / 24)} 天前`
}

/**
 * 生效参数的展示口径：模型级（load，且 autoOptimize 为 false）→ 提供商级 → 内置默认。
 * 开启自适应调优时运行时按显存探测选档，配置层看不到最终值，因此额外显示驻留槽的
 * 加载报告（report.effectiveGpuLayers）作为「实测」列。
 */
const effective = computed(() => {
  const config = detail.value
  if (!config) return []
  const loaded = config.load
  const auto = loaded?.autoOptimize !== false
  const measured = config.slots?.[0]?.report?.effectiveGpuLayers
  return [
    {
      label: 'GPU 层卸载',
      value: auto
        ? (loaded?.gpuOffload ?? config.providerDefaults.gpuLayers ?? 0)
        : (loaded?.gpuOffload ?? config.providerDefaults.gpuLayers ?? 0),
      source: auto ? '自适应调优' : (loaded?.gpuOffload !== null && loaded?.gpuOffload !== undefined ? '模型级' : '提供商级'),
      measured
    },
    {
      label: '上下文长度',
      value: (loaded?.autoOptimize === false && loaded.contextLength ? loaded.contextLength : config.contextWindow).toLocaleString(),
      source: loaded?.autoOptimize === false && loaded.contextLength ? '模型级' : '模型注册值',
      measured: ''
    },
    {
      label: 'Flash Attention',
      value: (loaded?.flashAttention ?? config.providerDefaults.flashAttention) === true ? '开启' : '关闭',
      source: loaded?.flashAttention !== null && loaded?.flashAttention !== undefined ? '模型级' : '提供商级',
      measured: ''
    },
    {
      label: 'KV 缓存量化',
      value: loaded?.kCacheQuantType ?? config.providerDefaults.kvCacheQuantization ?? 'none',
      source: loaded?.kCacheQuantType ? '模型级' : '提供商级',
      measured: ''
    },
    {
      label: '推理线程',
      value: String(loaded?.threads ?? config.providerDefaults.threads ?? '自动'),
      source: loaded?.threads !== undefined && loaded?.threads !== null ? '模型级' : '提供商级',
      measured: ''
    }
  ]
})

async function loadModels(): Promise<void> {
  if (!props.connected) return
  try {
    const [catalog, providers] = await Promise.all([
      requestJson<Array<{ ref: string; provider: string; id: string; alias?: string; contextWindow: number; providerEnabled: boolean }>>('model.catalog'),
      requestJson<Array<{ id: string; kind: string }>>('provider.list')
    ])
    const localProviders = new Set(providers.filter((provider) => provider.kind === 'local').map((provider) => provider.id))
    models.value = catalog
      .filter((model) => localProviders.has(model.provider) && model.providerEnabled)
      .map((model) => ({
        ref: model.ref, provider: model.provider, id: model.id, alias: model.alias, contextWindow: model.contextWindow
      }))
    if (!selected.value || !models.value.some((model) => model.ref === selected.value))
      selected.value = models.value[0]?.ref ?? ''
    await loadDetail()
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  }
}

async function loadDetail(): Promise<void> {
  if (!selected.value) {
    detail.value = null
    return
  }
  const [provider, id] = selected.value.split('/')
  if (!provider || !id) return
  loading.value = true
  error.value = ''
  try {
    detail.value = await requestJson<ModelConfigDetail>('model.config.get', { provider, id })
    providerId.value = provider
    if (!providerLevelOpen.value) await loadProviderLevel()
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
    detail.value = null
  } finally {
    loading.value = false
  }
}

async function loadGovernance(): Promise<void> {
  if (!props.connected) return
  try {
    const config = await requestJson<typeof governance>('agent.config.get')
    governance.localModelPreload = config.localModelPreload ?? true
    governance.localModelMaxResident = config.localModelMaxResident ?? 2
    governance.localModelConcurrency = config.localModelConcurrency ?? 1
    governance.localModelIdleUnloadMinutes = config.localModelIdleUnloadMinutes ?? 20
  } catch {
    // 老版本 daemon 未暴露这些字段时保留默认展示
  }
}

async function loadProviderLevel(): Promise<void> {
  try {
    const config = await requestJson<{
      localInference?: { providerId: string; gpuLayers: number; contextLength: number; kvCacheQuantization: string; flashAttention: boolean } | null
    }>('advanced.get')
    if (config.localInference) {
      providerDefaults.gpuLayers = config.localInference.gpuLayers
      providerDefaults.contextLength = config.localInference.contextLength
      providerDefaults.kvCacheQuantization = config.localInference.kvCacheQuantization
      providerDefaults.flashAttention = config.localInference.flashAttention
      providerId.value = config.localInference.providerId
      providerLevelAvailable.value = true
    } else {
      providerLevelAvailable.value = false
    }
  } catch {
    providerLevelAvailable.value = false
  }
}

async function saveGovernance(): Promise<void> {
  busy.value = 'governance'
  note.value = ''
  error.value = ''
  try {
    const config = await requestJson<typeof governance>('agent.config.set', {
      localModelPreload: governance.localModelPreload,
      localModelMaxResident: governance.localModelMaxResident,
      localModelConcurrency: governance.localModelConcurrency,
      localModelIdleUnloadMinutes: governance.localModelIdleUnloadMinutes
    })
    governance.localModelMaxResident = config.localModelMaxResident ?? governance.localModelMaxResident
    governance.localModelConcurrency = config.localModelConcurrency ?? governance.localModelConcurrency
    governance.localModelIdleUnloadMinutes = config.localModelIdleUnloadMinutes ?? governance.localModelIdleUnloadMinutes
    note.value = '驻留策略已保存；并发槽与空闲回收由运行时在数百毫秒内接管'
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    busy.value = ''
  }
}

// ---------------------------------------------------------------- 参数预设
// 旧「推理加速」页的常用配置芯片：把一组加载参数命名保存，一键回填。搬到本页后依然
// 只回填「提供商级默认」，模型级数值仍需到模型加载设置里改（唯一写入口不变）。

interface InferencePreset {
  name: string
  gpuLayers: number
  contextLength: number
  kvCacheQuantization: string
  flashAttention: boolean
}

const PRESETS_KEY = 'haoyue.inference-presets'
const presets = ref<InferencePreset[]>(readPresets())
const presetName = ref('')

function readPresets(): InferencePreset[] {
  try {
    const raw = JSON.parse(localStorage.getItem(PRESETS_KEY) ?? '[]') as InferencePreset[]
    return Array.isArray(raw) ? raw.filter((preset) => preset && typeof preset.name === 'string') : []
  } catch {
    return []
  }
}

function persistPresets(): void {
  try {
    localStorage.setItem(PRESETS_KEY, JSON.stringify(presets.value))
  } catch { /* 隐私模式：仅本次运行有效 */ }
}

function savePreset(): void {
  const name = presetName.value.trim() || `预设 ${presets.value.length + 1}`
  if (presets.value.some((preset) => preset.name === name)) {
    error.value = `预设「${name}」已存在，请换个名称`
    return
  }
  presets.value = [...presets.value, {
    name,
    gpuLayers: providerDefaults.gpuLayers,
    contextLength: providerDefaults.contextLength,
    kvCacheQuantization: providerDefaults.kvCacheQuantization,
    flashAttention: providerDefaults.flashAttention
  }]
  persistPresets()
  presetName.value = ''
  note.value = `已保存预设「${name}」`
}

function applyPreset(preset: InferencePreset): void {
  providerDefaults.gpuLayers = preset.gpuLayers
  providerDefaults.contextLength = preset.contextLength
  providerDefaults.kvCacheQuantization = preset.kvCacheQuantization
  providerDefaults.flashAttention = preset.flashAttention
  note.value = `已应用预设「${preset.name}」，点「保存提供商级默认」后生效`
}

async function removePreset(preset: InferencePreset): Promise<void> {
  // 删除不可撤销（名字能重建，调好的数值不能），与旧页一致走二次确认。
  if (!await confirmAction({
    title: '删除推理预设',
    message: `删除预设「${preset.name}」？此操作无法撤销。`,
    confirmLabel: '删除',
    danger: true
  })) return
  presets.value = presets.value.filter((entry) => entry !== preset)
  persistPresets()
}

async function saveProviderLevel(): Promise<void> {
  busy.value = 'provider'
  note.value = ''
  error.value = ''
  try {
    await requestJson('advanced.set', {
      localInference: {
        providerId: providerId.value,
        gpuLayers: Math.max(0, Math.floor(providerDefaults.gpuLayers) || 0),
        contextLength: Math.max(512, Math.floor(providerDefaults.contextLength) || 8192),
        kvCacheQuantization: providerDefaults.kvCacheQuantization,
        flashAttention: providerDefaults.flashAttention
      }
    })
    note.value = '提供商级默认已保存：只对没有模型级设置的模型生效'
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    busy.value = ''
  }
}

async function preload(): Promise<void> {
  if (!selected.value) return
  busy.value = 'preload'
  note.value = ''
  error.value = ''
  try {
    await requestJson('model.preload', { model: selected.value }, 300_000)
    note.value = '预热完成：权重已装入驻留槽，下一条消息无需等加载'
    await loadDetail()
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    busy.value = ''
  }
}

async function unload(): Promise<void> {
  busy.value = 'unload'
  note.value = ''
  error.value = ''
  try {
    await requestJson('model.unload')
    note.value = '已卸载全部常驻权重（活跃推理结束后释放）'
    await loadDetail()
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    busy.value = ''
  }
}

async function runBenchmark(): Promise<void> {
  if (!selected.value) return
  busy.value = 'bench'
  note.value = ''
  error.value = ''
  benchResult.value = null
  benchModel.value = selected.value
  try {
    const result = await requestJson<{ aggregate?: BenchAggregate }>('model.benchmark', {
      model: selected.value,
      runs: bench.runs,
      maxTokens: bench.maxTokens
    }, 600_000)
    benchResult.value = result.aggregate ?? null
    note.value = `基准完成（${bench.runs} 轮，稳态样本 ${result.aggregate?.steadyRuns ?? 0} 轮）`
    await loadDetail()
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    busy.value = ''
  }
}

let unsubscribe: (() => void) | null = null

onMounted(() => {
  void loadGovernance()
  void loadModels()
  unsubscribe = window.haoyue.daemon.onEvent((message) => {
    // 预热/加载结束后刷新驻留状态；进度事件本身由 App 的 preload 条负责展示。
    if (message.event === 'model.load.progress') void loadDetail()
  })
})

onBeforeUnmount(() => {
  unsubscribe?.()
  unsubscribe = null
})

watch(() => props.connected, (connected) => {
  if (!connected) return
  void loadGovernance()
  void loadModels()
})
watch(selected, () => { void loadDetail() })
</script>

<template>
  <div class="inference-panel">
    <p v-if="error" class="inference-note is-error">{{ error }}</p>
    <p v-else-if="note" class="inference-note is-ok">{{ note }}</p>

    <section v-if="models.length === 0" class="settings-group">
      <label class="provider-enabled-row">
        <span>
          <strong>暂无可配置的本地模型</strong>
          <small>本页只对 `kind: local` 的提供商与已注册的 GGUF 模型生效。请先在「模型与提供商」
            中添加本地提供商并注册模型。</small>
        </span>
      </label>
    </section>

    <template v-else>
      <section class="settings-group">
        <div class="inference-head">
          <div>
            <strong>当前生效参数</strong>
            <small>取值优先级：模型级（模型加载设置）→ 提供商级 → 内置默认。自适应调优开启时最终档位由运行时按显存探测决定。</small>
          </div>
          <SelectMenu v-model="selected" class="settings-select" label="本地模型"
            :options="modelOptions" :menu-min-width="360" searchable />
        </div>

        <p v-if="loading" class="inference-note"><LoaderCircle class="spin" :size="14" /> 正在读取模型配置</p>

        <template v-else-if="detail">
          <div class="inference-params">
            <div v-for="row in effective" :key="row.label" class="inference-param">
              <span class="inference-param-label">{{ row.label }}</span>
              <span class="inference-param-value">{{ row.value }}</span>
              <span class="inference-param-source">{{ row.source }}</span>
              <span v-if="row.measured" class="inference-param-measured">实测 {{ row.measured }}</span>
            </div>
          </div>

          <p v-if="overriddenByModel.length > 0" class="inference-warning">
            <TriangleAlert :size="13" aria-hidden="true" />
            该模型已设置模型级参数（{{ overriddenByModel.join('、') }}），下方「提供商级默认」对它的这些字段<b>不生效</b>。
            要改它的这些参数，请到「模型与提供商 → 编辑该模型 → 本地模型加载设置」。
          </p>

          <div class="inference-actions">
            <button type="button" class="secondary-button" :disabled="busy !== ''" @click="preload">
              <LoaderCircle v-if="busy === 'preload'" class="spin" :size="14" />
              <Download v-else :size="14" /> 预热权重
            </button>
            <button type="button" class="secondary-button" :disabled="busy !== ''" @click="unload">
              <LoaderCircle v-if="busy === 'unload'" class="spin" :size="14" />
              <Trash2 v-else :size="14" /> 卸载全部常驻
            </button>
            <span class="inference-inline-field">
              <input v-model.number="bench.runs" type="number" min="1" max="10" step="1" title="基准轮数" />
              <input v-model.number="bench.maxTokens" type="number" min="16" max="4096" step="16" title="每轮生成上限" />
              <button type="button" class="secondary-button" :disabled="busy !== ''" @click="runBenchmark">
                <LoaderCircle v-if="busy === 'bench'" class="spin" :size="14" />
                <Gauge v-else :size="14" /> 跑基准
              </button>
            </span>
          </div>

          <div v-if="benchResult && benchModel" class="inference-bench">
            <strong>{{ benchModel }}</strong>
            <span v-if="benchResult.steadyRuns === 0">轮数不足，无稳态样本（首轮含权重加载）</span>
            <template v-else>
              <span>TTFT 均值 {{ benchResult.ttftMsMean }} ms · P50 {{ benchResult.ttftMsP50 }} ms</span>
              <span>decode {{ benchResult.decodeTokensPerSecondMean }} tok/s · P50 {{ benchResult.decodeTokensPerSecondP50 }}</span>
              <span>prefill {{ benchResult.prefillTokensPerSecondMean }} tok/s</span>
            </template>
          </div>
        </template>
      </section>

      <section class="settings-group">
        <div class="settings-group-title">
          <strong>驻留与显存治理</strong>
          <small>8GB 显卡上「同时驻留几个权重、闲置多久归还显存」比参数微调更影响体验。</small>
        </div>

        <div class="inference-grid">
          <label>
            <FieldLabel en="Max Resident" zh="同时驻留上限"
              help="本地权重同时驻留的最大个数（1-16，默认 2）。聊天模型与嵌入模型交替请求时不再互相卸载重载；超出后按最久未用驱逐。" />
            <input v-model.number="governance.localModelMaxResident" type="number" min="1" max="16" step="1" />
          </label>
          <label>
            <FieldLabel en="Concurrency" zh="推理并发槽"
              help="本地推理并发数（1-16，默认 1 = 严格串行、KV 前缀复用可用）。调大后每请求独立 context，显存随槽位增长，KV 前缀复用自动关闭。" />
            <input v-model.number="governance.localModelConcurrency" type="number" min="1" max="16" step="1" />
          </label>
          <label>
            <FieldLabel en="Idle Unload" zh="空闲回收（分钟）"
              help="权重空闲超过该时长后由运行时归还内存/显存（0 = 不回收，默认 20）。下一次请求按原签名重新加载。" />
            <input v-model.number="governance.localModelIdleUnloadMinutes" type="number" min="0" max="1440" step="5" />
          </label>
        </div>

        <label class="provider-enabled-row">
          <span>
            <strong>启动时预热活跃本地模型</strong>
            <small>运行时启动后在后台装入活跃模型权重（不阻塞启动、不占推理槽），第一条消息无需等加载。关闭则保持纯懒加载。</small>
          </span>
          <input v-model="governance.localModelPreload" class="sr-only" type="checkbox" />
          <span class="toggle-switch" aria-hidden="true"><span /></span>
        </label>

        <div v-if="detail?.residentInMemory" class="inference-resident">
          <Zap :size="13" aria-hidden="true" />
          <span>当前驻留 {{ detail.residentCount ?? 1 }}/{{ detail.maxResidentModels ?? governance.localModelMaxResident }} 个权重
            · <template v-for="(slot, index) in detail.slots ?? []" :key="slot.path">
              <template v-if="index > 0">、</template>{{ slot.path?.split(/[\\/]/).pop() }}
              （{{ formatBytes(slot.weightBytes) }}，最后使用 {{ relativeTime(slot.lastUsedUtc) }}）
            </template>
          </span>
        </div>
        <p v-else class="inference-note">当前没有常驻权重（首次请求会触发冷加载{{ detail?.estimatedLoadMs ? `，预计约 ${Math.round((detail.estimatedLoadMs ?? 0) / 1000)} 秒` : '' }}）。</p>

        <button type="button" class="secondary-button primary-action" :disabled="busy !== ''" @click="saveGovernance">
          <LoaderCircle v-if="busy === 'governance'" class="spin" :size="14" />
          <Save v-else :size="14" /> 保存驻留策略
        </button>
      </section>

      <section v-if="providerLevelAvailable" class="settings-group">
        <button type="button" class="inference-disclosure" :aria-expanded="providerLevelOpen"
          @click="providerLevelOpen = !providerLevelOpen">
          <strong>提供商级默认（{{ providerId }}）</strong>
          <small>{{ providerLevelOpen ? '收起' : '展开' }}：仅对<b>没有</b>模型级设置的模型生效。模型级设置存在时这里改了也不会生效——这正是旧版「改了没用」的来源。</small>
        </button>

        <div v-if="providerLevelOpen">
          <div class="inference-grid">
            <label>
              <FieldLabel en="GPU Offload" zh="GPU 层卸载"
                help="卸载到 GPU 的层数（0-999，0 = 仅 CPU）。自适应调优开启时该值只作为上限参考。" />
              <input v-model.number="providerDefaults.gpuLayers" type="number" min="0" max="999" step="1" />
            </label>
            <label>
              <FieldLabel en="Context Length" zh="上下文长度"
                help="提供商级默认上下文（512-32768）。注意：模型注册的 contextWindow 与模型级加载设置都可能覆盖它。" />
              <input v-model.number="providerDefaults.contextLength" type="number" min="512" max="32768" step="512" />
            </label>
            <label>
              <FieldLabel en="KV Cache Quantization" zh="KV 缓存量化"
                help="量化 KV 缓存以降低显存占用；需要 Flash Attention 开启，否则保持 f16。" />
              <SelectMenu v-model="providerDefaults.kvCacheQuantization" class="settings-select"
                label="KV 缓存量化" :options="kvOptions" />
            </label>
          </div>
          <label class="provider-enabled-row">
            <span>
              <strong>Flash Attention（注意力加速）</strong>
              <small>启用 llama.cpp flash attention 内核，并解锁 KV 缓存量化。长上下文下对 decode/prefill 与显存都有明显收益。</small>
            </span>
            <input v-model="providerDefaults.flashAttention" class="sr-only" type="checkbox" />
            <span class="toggle-switch" aria-hidden="true"><span /></span>
          </label>
          <div v-if="presets.length > 0" class="inference-presets">
            <span v-for="preset in presets" :key="preset.name" class="inference-preset-chip">
              <button type="button" class="preset-apply"
                :title="`GPU ${preset.gpuLayers} 层 · 上下文 ${preset.contextLength} · KV ${preset.kvCacheQuantization}`"
                @click="applyPreset(preset)">{{ preset.name }}</button>
              <button type="button" class="preset-remove" :title="`删除预设 ${preset.name}`"
                @click="removePreset(preset)"><Trash2 :size="12" /></button>
            </span>
          </div>
          <div class="inference-preset-save">
            <input v-model="presetName" placeholder="把当前提供商级默认存为预设，例如：GPU 全量卸载"
              @keydown.enter.prevent="savePreset" />
            <button type="button" class="secondary-button compact-button" @click="savePreset">
              <Save :size="13" /> 存为预设
            </button>
          </div>
          <button type="button" class="secondary-button" :disabled="busy !== ''" @click="saveProviderLevel">
            <LoaderCircle v-if="busy === 'provider'" class="spin" :size="14" />
            <Save v-else :size="14" /> 保存提供商级默认
          </button>
        </div>
      </section>
    </template>
  </div>
</template>

<style scoped>
.inference-panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.inference-note {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 0;
  color: var(--text-muted);
  font-size: 12px;
}

.inference-note.is-ok { color: var(--accent); }
.inference-note.is-error { color: var(--danger); }

.inference-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

.inference-head > div {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}

.inference-head small {
  color: var(--text-muted);
  font-size: 11px;
  line-height: 1.5;
}

.inference-params {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(230px, 1fr));
  gap: 8px;
  margin: 12px 0;
}

.inference-param {
  display: flex;
  align-items: baseline;
  gap: 6px;
  padding: 8px 10px;
  font-size: 12px;
  background: color-mix(in srgb, var(--surface-hover) 55%, transparent);
  border: 1px solid var(--border);
  border-radius: 8px;
}

.inference-param-label { color: var(--text-muted); }
.inference-param-value { color: var(--text); font-weight: 600; font-variant-numeric: tabular-nums; }

.inference-param-source,
.inference-param-measured {
  margin-left: auto;
  padding: 1px 6px;
  color: var(--text-muted);
  font-size: 10px;
  background: var(--surface);
  border-radius: 999px;
}

.inference-param-measured { color: var(--accent); }

.inference-warning {
  display: flex;
  align-items: flex-start;
  gap: 6px;
  margin: 0 0 10px;
  padding: 8px 10px;
  color: var(--danger);
  font-size: 12px;
  line-height: 1.55;
  background: color-mix(in srgb, var(--danger) 7%, transparent);
  border: 1px solid color-mix(in srgb, var(--danger) 25%, transparent);
  border-radius: 8px;
}

.inference-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.inference-inline-field {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.inference-inline-field input {
  width: 68px;
  padding: 5px 7px;
  color: var(--text);
  font-size: 12px;
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 7px;
}

.inference-bench {
  display: flex;
  flex-direction: column;
  gap: 3px;
  margin-top: 10px;
  padding: 9px 11px;
  font-size: 12px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 8px;
}

.inference-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(210px, 1fr));
  gap: 12px;
  margin: 10px 0;
}

.inference-grid label {
  display: flex;
  flex-direction: column;
  gap: 5px;
}

.inference-grid input {
  width: 100%;
  padding: 7px 9px;
  color: var(--text);
  font-size: 13px;
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 8px;
}

.inference-resident {
  display: flex;
  align-items: flex-start;
  gap: 6px;
  margin: 8px 0;
  padding: 8px 10px;
  color: var(--text-secondary);
  font-size: 12px;
  line-height: 1.55;
  background: color-mix(in srgb, var(--accent) 7%, transparent);
  border-radius: 8px;
}

.inference-disclosure {
  display: flex;
  flex-direction: column;
  gap: 3px;
  align-items: flex-start;
  width: 100%;
  padding: 0;
  color: inherit;
  text-align: left;
  background: none;
  border: 0;
  cursor: pointer;
}

.inference-disclosure small {
  color: var(--text-muted);
  font-size: 11px;
  line-height: 1.5;
}

.inference-presets {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin: 10px 0 8px;
}

.inference-preset-chip {
  display: inline-flex;
  align-items: center;
  overflow: hidden;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 999px;
}

.inference-preset-chip .preset-apply,
.inference-preset-chip .preset-remove {
  padding: 4px 9px;
  color: var(--text-secondary);
  font-size: 11px;
  background: transparent;
  border: 0;
  cursor: pointer;
}

.inference-preset-chip .preset-apply:hover { color: var(--text); background: var(--surface-hover); }
.inference-preset-chip .preset-remove { padding: 4px 7px; border-left: 1px solid var(--border); }
.inference-preset-chip .preset-remove:hover { color: var(--danger); }

.inference-preset-save {
  display: flex;
  gap: 6px;
  margin-bottom: 10px;
}

.inference-preset-save input {
  flex: 1 1 auto;
  min-width: 0;
  padding: 6px 9px;
  color: var(--text);
  font-size: 12px;
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 8px;
}
</style>
