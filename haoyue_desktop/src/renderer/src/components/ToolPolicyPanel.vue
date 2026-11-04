<script setup lang="ts">
/**
 * 5.6 工具执行策略面板：allow/deny 规则表格 + Bash 沙箱三开关 + 风险分级闸门。
 * 读写走 agent.config.get/set（宽松 RPC，partial 更新、缺省键保持原值）。
 * 独立组件的原因：规则表格的增删改状态机较重，放 SettingsDialog 会突破其行数预算。
 */
import { computed, onMounted, ref } from 'vue'
import { LoaderCircle, Plus, RefreshCw, Trash2 } from '@lucide/vue'
import SelectMenu from './SelectMenu.vue'
import { requestJson } from '../daemon-json'

interface PolicyRule {
  tool: string
  pattern: string
  reason: string
}

interface PolicyView {
  toolPolicy: {
    enabled: boolean
    builtinBashGuard: boolean
    riskGating: boolean
    consentRiskTiers: string[]
    requireConsentFor: string[]
    allow: PolicyRule[]
    deny: PolicyRule[]
  }
  bashSandbox: {
    enabled: boolean
    memoryLimitMb: number
    maxProcesses: number
    uiRestrictions: boolean
  }
}

const emit = defineEmits<{ changed: [] }>()

const loading = ref(false)
const saving = ref(false)
const error = ref('')
const notice = ref('')
const policy = ref<PolicyView | null>(null)

const RISK_TIER_LABELS: Record<string, string> = {
  system: '系统',
  cost: '费用',
  network: '外发',
  write: '写入',
  read: '读取',
}

const consentTierLabels = computed(() =>
  (policy.value?.toolPolicy.consentRiskTiers ?? []).map((tier) => RISK_TIER_LABELS[tier] ?? tier))

/** Bash 沙箱 UI 限制：放开 / 限制（Job Object 的剪贴板/系统参数/显示设置/关机/桌面切换约束）。 */
const UI_RESTRICTION_OPTIONS = [
  { value: 'release', label: '放开', description: '不限制 UI 操作' },
  { value: 'limit', label: 'UI 限', description: '限制剪贴板 / 系统参数 / 关机等' },
]

async function load(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    const view = await requestJson<PolicyView>('agent.config.get')
    policy.value = view
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    loading.value = false
  }
}

async function save(patch: Record<string, unknown>, successNotice: string): Promise<void> {
  saving.value = true
  error.value = ''
  try {
    const view = await requestJson<PolicyView>('agent.config.set', patch)
    policy.value = view
    notice.value = successNotice
    emit('changed')
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    saving.value = false
  }
}

function savePolicyFlag(key: 'enabled' | 'builtinBashGuard' | 'riskGating', value: boolean, label: string): void {
  void save({ toolPolicy: { [key]: value } }, `${label}已${value ? '开启' : '关闭'}`)
}

function saveSandboxFlag(key: 'enabled', value: boolean): void {
  void save({ bashSandbox: { [key]: value } }, `Bash 沙箱${value ? '已启用' : '已停用'}`)
}

function saveUiRestriction(value: string): void {
  const limited = value === 'limit'
  void save({ bashSandbox: { uiRestrictions: limited } }, `UI 限制已设为${limited ? 'UI 限' : '放开'}`)
}

function saveSandboxNumber(key: 'memoryLimitMb' | 'maxProcesses', raw: string): void {
  const value = Number.parseInt(raw, 10)
  if (Number.isNaN(value)) return
  const labels: Record<string, string> = { memoryLimitMb: '内存上限', maxProcesses: '进程数上限' }
  void save({ bashSandbox: { [key]: value } }, `沙箱${labels[key]}已更新`)
}

function addRule(kind: 'allow' | 'deny'): void {
  const list = policy.value?.toolPolicy[kind] ?? []
  list.push({ tool: '*', pattern: '', reason: '' })
  if (policy.value) policy.value.toolPolicy[kind] = list
}

function removeRule(kind: 'allow' | 'deny', index: number): void {
  const list = policy.value?.toolPolicy[kind] ?? []
  list.splice(index, 1)
}

function saveRules(kind: 'allow' | 'deny'): void {
  const rules = policy.value?.toolPolicy[kind] ?? []
  const pending = rules.filter((rule) => rule.tool.trim().length === 0)
  if (pending.length > 0) {
    error.value = '存在没有填写工具名的规则，请补全或删除后再保存。'
    return
  }
  void save(
    { toolPolicy: { [kind]: rules } },
    kind === 'allow' ? '放行规则已保存' : '拒绝规则已保存')
}

onMounted(load)
</script>

<template>
  <div>
    <p v-if="loading" class="toolpolicy-hint"><LoaderCircle class="spin" :size="13" /> 正在读取工具执行策略…</p>
    <p v-if="error" class="toolpolicy-hint error">{{ error }}</p>
    <p v-if="notice && !error" class="toolpolicy-hint ok">{{ notice }}</p>

    <template v-if="policy">
      <label class="settings-row toolpolicy-toggle">
        <span class="toolpolicy-toggle-copy">
          <strong>工具执行策略</strong>
          <small>运行时强制门：关闭后跳过 allow/deny 与内置 bash 守卫（仅保留模式层约束）</small>
        </span>
        <input class="sr-only" type="checkbox" :checked="policy.toolPolicy.enabled" :disabled="saving"
          @change="savePolicyFlag('enabled', ($event.target as HTMLInputElement).checked, '工具执行策略')" />
        <span class="toggle-switch" aria-hidden="true"><span /></span>
      </label>
      <label class="settings-row toolpolicy-toggle">
        <span class="toolpolicy-toggle-copy">
          <strong>内置高危命令守卫</strong>
          <small>拦截「像破坏性命令」的 shell 调用；命中后弹审批卡（可本次放行）</small>
        </span>
        <input class="sr-only" type="checkbox" :checked="policy.toolPolicy.builtinBashGuard" :disabled="saving"
          @change="savePolicyFlag('builtinBashGuard', ($event.target as HTMLInputElement).checked, '内置高危命令守卫')" />
        <span class="toggle-switch" aria-hidden="true"><span /></span>
      </label>
      <label class="settings-row toolpolicy-toggle">
        <span class="toolpolicy-toggle-copy">
          <strong>风险分级闸门</strong>
          <small>高风险操作本回合首次调用前弹审批卡。当前维度：<span
            v-for="tier in consentTierLabels" :key="tier" class="inline-badge">{{ tier }}</span>
          </small>
        </span>
        <input class="sr-only" type="checkbox" :checked="policy.toolPolicy.riskGating" :disabled="saving"
          @change="savePolicyFlag('riskGating', ($event.target as HTMLInputElement).checked, '风险分级闸门')" />
        <span class="toggle-switch" aria-hidden="true"><span /></span>
      </label>

      <label class="settings-row toolpolicy-toggle">
        <span class="toolpolicy-toggle-copy">
          <strong>Bash 沙箱（Job Object，仅 Windows）</strong>
          <small>启用后 bash 子进程树受内存/进程数/UI 限制并有 kill-on-close 兜底</small>
        </span>
        <input class="sr-only" type="checkbox" :checked="policy.bashSandbox.enabled" :disabled="saving"
          @change="saveSandboxFlag('enabled', ($event.target as HTMLInputElement).checked)" />
        <span class="toggle-switch" aria-hidden="true"><span /></span>
      </label>
      <div v-if="policy.bashSandbox.enabled" class="settings-row">
        <div><strong>沙箱限额</strong><small>内存上限（MB）与活动进程数；0 = 不限制</small></div>
        <div class="toolpolicy-numbers">
          <label>内存 <input type="number" min="0" max="65536" :value="policy.bashSandbox.memoryLimitMb"
            @change="saveSandboxNumber('memoryLimitMb', ($event.target as HTMLInputElement).value)" /></label>
          <label>进程 <input type="number" min="0" max="4096" :value="policy.bashSandbox.maxProcesses"
            @change="saveSandboxNumber('maxProcesses', ($event.target as HTMLInputElement).value)" /></label>
          <SelectMenu
            :model-value="policy.bashSandbox.uiRestrictions ? 'limit' : 'release'"
            :options="UI_RESTRICTION_OPTIONS"
            label="UI 限制"
            :disabled="saving"
            @update:model-value="saveUiRestriction" />
        </div>
      </div>

      <div v-for="kind in ['allow', 'deny'] as const" :key="kind" class="toolpolicy-rules">
        <div class="toolpolicy-rules-head">
          <strong>{{ kind === 'allow' ? '放行规则（优先于拒绝）' : '拒绝规则' }}</strong>
          <span class="toolbar-spacer" />
          <button class="icon-button" :title="`添加${kind === 'allow' ? '放行' : '拒绝'}规则`" @click="addRule(kind)">
            <Plus :size="14" />
          </button>
          <button class="icon-button" :disabled="saving" title="保存规则" @click="saveRules(kind)">
            <RefreshCw :class="{ spin: saving }" :size="14" />
          </button>
        </div>
        <div v-for="(rule, index) in policy.toolPolicy[kind]" :key="index" class="toolpolicy-rule-row">
          <input v-model="rule.tool" class="toolpolicy-rule-tool" placeholder="工具名（* = 任意）" />
          <input v-model="rule.pattern" class="toolpolicy-rule-pattern" placeholder="参数正则（可空）" />
          <input v-model="rule.reason" class="toolpolicy-rule-reason" placeholder="命中理由（可空）" />
          <button class="icon-button" :title="`删除第 ${index + 1} 条`" @click="removeRule(kind, index)">
            <Trash2 :size="13" />
          </button>
        </div>
        <p v-if="policy.toolPolicy[kind].length === 0" class="toolpolicy-hint">暂无规则</p>
      </div>
    </template>
  </div>
</template>

<style scoped>
.toolpolicy-toggle {
  cursor: pointer;
}
.toolpolicy-toggle-copy {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 4px;
}
.toolpolicy-toggle input:checked + .toggle-switch {
  background: var(--accent);
}
.toolpolicy-toggle input:checked + .toggle-switch > span {
  transform: translateX(16px);
}
.toolpolicy-toggle input:focus-visible + .toggle-switch {
  outline: 2px solid color-mix(in srgb, var(--accent) 58%, transparent);
  outline-offset: 2px;
}
.toolpolicy-toggle input:disabled + .toggle-switch {
  opacity: .55;
}
.toolpolicy-hint {
  margin: 4px 0;
  font-size: 12px;
  color: var(--color-text-secondary, #888);
}
.toolpolicy-hint.error {
  color: var(--color-danger, #b3261e);
}
.toolpolicy-hint.ok {
  color: var(--color-success, #0f6e56);
}
.toolpolicy-numbers {
  display: flex;
  align-items: center;
  gap: 12px;
}
.toolpolicy-numbers label {
  display: flex;
  align-items: center;
  gap: 4px;
  font-size: 12px;
}
.toolpolicy-numbers input {
  width: 88px;
}
.toolpolicy-rules {
  margin-top: 8px;
  padding-top: 8px;
  border-top: 1px solid var(--color-border, #e5e5e5);
}
.toolpolicy-rules-head {
  display: flex;
  align-items: center;
  gap: 8px;
}
.toolpolicy-rule-row {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 6px;
}
.toolpolicy-rule-tool {
  width: 130px;
}
.toolpolicy-rule-pattern {
  flex: 1;
  min-width: 160px;
}
.toolpolicy-rule-reason {
  width: 180px;
}
</style>
