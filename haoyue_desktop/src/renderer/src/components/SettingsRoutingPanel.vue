<script setup lang="ts">
import { ArrowDown, ArrowUp, LoaderCircle, Plus, Save, Trash2, TriangleAlert } from '@lucide/vue'
import { computed, reactive, ref, watch } from 'vue'
import { requestJson } from '../daemon-json'
import FieldLabel from './FieldLabel.vue'
import SelectMenu from './SelectMenu.vue'

/**
 * 模型与提供商 → 路由与容灾。
 *
 * 这一批字段此前全部只在 RPC 层：`routing.get` 早就把 `localFailoverToCloud` 返回给
 * 前端，`SettingsDialog` 却只读两个布尔；回退链、负载均衡、重试与熔断则完全没有入口。
 * 其中 `LocalFailoverToCloud` 的注释明确写着「默认关是防 surprise billing 的有意设计」——
 * 一个为了防止意外账单而存在的开关，却没有任何界面能让用户主动开启，等于该设计对
 * GUI 用户不可达。
 */
const props = defineProps<{ connected: boolean }>()

interface RetryConfig {
  maxAttempts: number
  baseDelaySeconds: number
  maxDelaySeconds: number
  fallbackRetryAttempts: number
  circuitBreakThreshold: number
  circuitCooldownSeconds: number
  chainBudgetSeconds: number
}

interface RoutingConfig {
  failoverEnabled: boolean
  deepSeekOptimizationEnabled: boolean
  localFailoverToCloud: boolean
  fallback: string[]
  loadBalance: string
  retry: RetryConfig
}

const loadBalanceOptions = [
  { value: 'priority', label: '按优先级', description: 'priority：按提供商的 priority 字段顺序尝试，最可预期' },
  { value: 'roundRobin', label: '轮询', description: 'roundRobin：请求在候选之间轮流分配，摊平配额' },
  { value: 'leastUsed', label: '最少使用', description: 'leastUsed：优先派给当前用量最少的模型' },
  { value: 'lowestCost', label: '最低成本', description: 'lowestCost：按模型定价挑选最便宜的候选' },
  { value: 'fastest', label: '最快', description: 'fastest：按历史延迟挑选最快的候选' },
  { value: 'sticky', label: '粘性', description: 'sticky：同一会话内固定命中同一模型，命中前缀缓存' }
]

const form = reactive<RoutingConfig>({
  failoverEnabled: true,
  deepSeekOptimizationEnabled: false,
  localFailoverToCloud: false,
  fallback: [],
  loadBalance: 'priority',
  retry: {
    maxAttempts: 3,
    baseDelaySeconds: 1,
    maxDelaySeconds: 20,
    fallbackRetryAttempts: 0,
    circuitBreakThreshold: 4,
    circuitCooldownSeconds: 60,
    chainBudgetSeconds: 120
  }
})

const catalog = ref<string[]>([])
const retryOpen = ref(false)
const loading = ref(false)
const saving = ref(false)
const note = ref('')
const error = ref('')

/** 链里已失效的条目照常显示（保存时原样回传），但标红提示，不让它悄悄消失。 */
const known = computed(() => new Set(catalog.value))
const addable = computed(() => catalog.value.filter((ref) => !form.fallback.includes(ref)))
const addOptions = computed(() => addable.value.map((ref) => ({ value: ref, label: ref })))
const pendingAdd = ref('')

async function load(): Promise<void> {
  if (!props.connected) return
  loading.value = true
  error.value = ''
  try {
    const [routing, models] = await Promise.all([
      requestJson<RoutingConfig>('routing.get'),
      requestJson<Array<{ ref: string }>>('model.catalog')
    ])
    Object.assign(form, routing)
    form.fallback = [...(routing.fallback ?? [])]
    Object.assign(form.retry, routing.retry ?? {})
    catalog.value = models.map((model) => model.ref)
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    loading.value = false
  }
}

async function save(): Promise<void> {
  if (!props.connected) return
  saving.value = true
  error.value = ''
  note.value = ''
  try {
    const routing = await requestJson<RoutingConfig>('routing.set', {
      failoverEnabled: form.failoverEnabled,
      deepSeekOptimizationEnabled: form.deepSeekOptimizationEnabled,
      localFailoverToCloud: form.localFailoverToCloud,
      fallback: form.fallback,
      loadBalance: form.loadBalance,
      retry: {
        maxAttempts: form.retry.maxAttempts,
        baseDelaySeconds: form.retry.baseDelaySeconds,
        maxDelaySeconds: form.retry.maxDelaySeconds,
        fallbackRetryAttempts: form.retry.fallbackRetryAttempts,
        circuitBreakThreshold: form.retry.circuitBreakThreshold,
        circuitCooldownSeconds: form.retry.circuitCooldownSeconds,
        chainBudgetSeconds: form.retry.chainBudgetSeconds
      }
    })
    Object.assign(form, routing)
    form.fallback = [...(routing.fallback ?? [])]
    note.value = '路由与容灾设置已保存，下一次请求即生效'
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    saving.value = false
  }
}

function addFallback(): void {
  if (!pendingAdd.value || form.fallback.includes(pendingAdd.value)) return
  form.fallback = [...form.fallback, pendingAdd.value]
  pendingAdd.value = ''
}

function moveFallback(index: number, delta: number): void {
  const target = index + delta
  if (target < 0 || target >= form.fallback.length) return
  const next = [...form.fallback]
  const [item] = next.splice(index, 1)
  next.splice(target, 0, item!)
  form.fallback = next
}

function removeFallback(index: number): void {
  form.fallback = form.fallback.filter((_, position) => position !== index)
}

watch(() => props.connected, (connected) => { if (connected) void load() }, { immediate: true })
</script>

<template>
  <div class="routing-panel">
    <p v-if="error" class="routing-note is-error">{{ error }}</p>
    <p v-else-if="note" class="routing-note is-ok">{{ note }}</p>
    <p v-if="loading" class="routing-note"><LoaderCircle class="spin" :size="14" /> 正在读取路由配置</p>

    <label class="provider-enabled-row">
      <span>
        <strong>自动切换其他模型（故障转移）</strong>
        <small>激活模型请求失败时自动尝试回退链中的其他模型；关闭后失败即停止，并显示真实错误。</small>
      </span>
      <input v-model="form.failoverEnabled" class="sr-only" type="checkbox" />
      <span class="toggle-switch" aria-hidden="true"><span /></span>
    </label>

    <div class="routing-warning-block">
      <label class="provider-enabled-row">
        <span>
          <strong><TriangleAlert :size="13" aria-hidden="true" /> 本地模型失败时回退到云端</strong>
          <small>开启后本地模型的非可重试失败（模型文件缺失、加载失败、显存不足）也会走云回退链。
            <b>这可能产生未预期的账单</b>——默认关闭正是为此。请先确认云侧凭据与计费预期再开启。</small>
        </span>
        <input v-model="form.localFailoverToCloud" class="sr-only" type="checkbox" />
        <span class="toggle-switch" aria-hidden="true"><span /></span>
      </label>
    </div>

    <div class="routing-section">
      <div class="routing-section-head">
        <strong>回退链</strong>
        <small>激活模型失败后按此顺序尝试。顺序即优先级，越靠前越先试。</small>
      </div>
      <div v-if="form.fallback.length === 0" class="routing-empty">
        回退链为空——关闭故障转移时这没有影响；开启后建议至少放一个候选。
      </div>
      <ol v-else class="routing-chain">
        <li v-for="(ref, index) in form.fallback" :key="`${ref}-${index}`" class="routing-chain-row">
          <span class="routing-chain-index">{{ index + 1 }}</span>
          <span class="routing-chain-ref" :class="{ 'is-stale': !known.has(ref) }"
            :title="known.has(ref) ? ref : '该模型已不在模型目录中'">
            {{ ref }}<em v-if="!known.has(ref)">（已失效）</em>
          </span>
          <button type="button" class="icon-button compact" title="上移"
            :disabled="index === 0" @click="moveFallback(index, -1)"><ArrowUp :size="14" /></button>
          <button type="button" class="icon-button compact" title="下移"
            :disabled="index === form.fallback.length - 1" @click="moveFallback(index, 1)"><ArrowDown :size="14" /></button>
          <button type="button" class="icon-button compact danger-icon" title="从回退链移除"
            @click="removeFallback(index)"><Trash2 :size="14" /></button>
        </li>
      </ol>
      <div class="routing-add">
        <SelectMenu v-model="pendingAdd" class="settings-select" label="添加回退模型"
          :options="addOptions" :menu-min-width="320" placeholder="选择要追加的模型…" searchable />
        <button type="button" class="secondary-button compact-button" :disabled="!pendingAdd" @click="addFallback">
          <Plus :size="13" /> 加入回退链
        </button>
      </div>
    </div>

    <div class="routing-section">
      <label class="routing-field">
        <FieldLabel en="Load Balance" zh="负载均衡策略"
          help="多个候选模型之间如何挑选。sticky 让同一会话固定命中同一模型，从而命中前缀缓存；其余策略会分散缓存收益。" />
        <SelectMenu v-model="form.loadBalance" class="settings-select" label="负载均衡策略"
          :options="loadBalanceOptions" :menu-min-width="360" />
      </label>
    </div>

    <div class="routing-section">
      <button type="button" class="routing-disclosure" :aria-expanded="retryOpen" @click="retryOpen = !retryOpen">
        <strong>重试与熔断</strong>
        <small>{{ retryOpen ? '收起' : '展开' }}：单次尝试次数、退避、熔断阈值与整条链的墙钟预算</small>
      </button>
      <div v-if="retryOpen" class="routing-retry-grid">
        <label>
          <FieldLabel en="Max Attempts" zh="激活模型尝试次数" help="激活模型自身的最大尝试次数（1-10，默认 3）。" />
          <input v-model.number="form.retry.maxAttempts" type="number" min="1" max="10" step="1" />
        </label>
        <label>
          <FieldLabel en="Base Delay" zh="首次退避（秒）" help="第一次重试前等待多久（默认 1）。" />
          <input v-model.number="form.retry.baseDelaySeconds" type="number" min="0" max="60" step="0.5" />
        </label>
        <label>
          <FieldLabel en="Max Delay" zh="退避上限（秒）" help="指数退避的上限（默认 20）。" />
          <input v-model.number="form.retry.maxDelaySeconds" type="number" min="0" max="600" step="5" />
        </label>
        <label>
          <FieldLabel en="Fallback Attempts" zh="回退候选尝试次数"
            help="每个回退候选的额外尝试次数（0-5，默认 0 = 单次）。回退提供商经常 429 时可调 1-2。" />
          <input v-model.number="form.retry.fallbackRetryAttempts" type="number" min="0" max="5" step="1" />
        </label>
        <label>
          <FieldLabel en="Circuit Threshold" zh="熔断阈值"
            help="某模型连续失败多少次后熔断（默认 4）。熔断期间不再尝试它，冷却后自动恢复。" />
          <input v-model.number="form.retry.circuitBreakThreshold" type="number" min="0" max="100" step="1" />
        </label>
        <label>
          <FieldLabel en="Circuit Cooldown" zh="熔断冷却（秒）" help="熔断持续时长（默认 60）。" />
          <input v-model.number="form.retry.circuitCooldownSeconds" type="number" min="0" max="3600" step="10" />
        </label>
        <label>
          <FieldLabel en="Chain Budget" zh="整条链墙钟预算（秒）"
            help="所有候选与重试合计的时间上限（默认 120）。超出后不再发起新尝试，直接汇总已收集的错误——否则最坏情况会拖到几十分钟才给用户任何反馈。" />
          <input v-model.number="form.retry.chainBudgetSeconds" type="number" min="1" max="3600" step="10" />
        </label>
      </div>
    </div>

    <label class="provider-enabled-row">
      <span>
        <strong>优化 DeepSeek 模型</strong>
        <small>针对 DeepSeek 启用思维链选择性回传、空响应重试与空工具结果兜底。仅在该模型提供商 id/名称/模型名含 deepseek 时生效。</small>
      </span>
      <input v-model="form.deepSeekOptimizationEnabled" class="sr-only" type="checkbox" />
      <span class="toggle-switch" aria-hidden="true"><span /></span>
    </label>

    <button type="button" class="secondary-button primary-action routing-save"
      :disabled="saving || !connected" @click="save">
      <LoaderCircle v-if="saving" class="spin" :size="14" />
      <Save v-else :size="14" />
      {{ saving ? '保存中…' : '保存路由与容灾设置' }}
    </button>
  </div>
</template>

<style scoped>
.routing-panel {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.routing-note {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 0;
  color: var(--text-muted);
  font-size: 12px;
}

.routing-note.is-ok { color: var(--accent); }
.routing-note.is-error { color: var(--danger); }

.routing-warning-block {
  background: color-mix(in srgb, var(--danger) 6%, transparent);
  border: 1px solid color-mix(in srgb, var(--danger) 26%, transparent);
  border-radius: 10px;
  padding: 0 12px;
}

.routing-warning-block b { color: var(--danger); }

.routing-section {
  padding: 10px 12px;
  background: color-mix(in srgb, var(--surface-hover) 45%, transparent);
  border: 1px solid var(--border);
  border-radius: 10px;
}

.routing-section-head {
  display: flex;
  flex-direction: column;
  gap: 2px;
  margin-bottom: 8px;
}

.routing-section-head small,
.routing-disclosure small {
  color: var(--text-muted);
  font-size: 11px;
}

.routing-empty {
  padding: 8px 0;
  color: var(--text-muted);
  font-size: 12px;
}

.routing-chain {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.routing-chain-row {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 5px 8px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 8px;
}

.routing-chain-index {
  flex: 0 0 18px;
  color: var(--text-muted);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
}

.routing-chain-ref {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  font-family: var(--font-mono, monospace);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.routing-chain-ref.is-stale { color: var(--danger); }
.routing-chain-ref em { font-style: normal; }

.routing-add {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 8px;
}

.routing-field {
  display: flex;
  flex-direction: column;
  gap: 5px;
}

.routing-disclosure {
  display: flex;
  flex-direction: column;
  gap: 2px;
  align-items: flex-start;
  width: 100%;
  padding: 0;
  color: inherit;
  text-align: left;
  background: none;
  border: 0;
  cursor: pointer;
}

.routing-retry-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(190px, 1fr));
  gap: 12px;
  margin-top: 10px;
}

.routing-retry-grid label {
  display: flex;
  flex-direction: column;
  gap: 5px;
}

.routing-retry-grid input {
  width: 100%;
  padding: 7px 9px;
  color: var(--text);
  font-size: 13px;
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 8px;
}

.routing-save {
  align-self: flex-start;
}
</style>
