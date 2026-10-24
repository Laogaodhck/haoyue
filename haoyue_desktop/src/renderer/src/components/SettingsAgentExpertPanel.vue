<script setup lang="ts">
import { LoaderCircle, RotateCcw, Save } from '@lucide/vue'
import { reactive, ref, watch } from 'vue'
import { requestJson } from '../daemon-json'
import FieldLabel from './FieldLabel.vue'

/**
 * 智能体 → 专家参数。
 *
 * 这批字段（步数上限、思考预算、工具输出上限、bash 超时、计划任务超时、修复/续写次数、
 * 委派配额、事实注入条数、额外可读目录、会话保留期、记忆固化、计划任务回调）此前既无
 * 界面、也不在 `agent.config.set` 的参数白名单里，只能手改 `~/.haoyue/config.json`。
 * 其中 MaxSteps / ThinkingBudgetTokens / SessionRetentionDays / ExtraReadableRoots 直接
 * 影响成本、安全与数据留存，必须能被看见和改。
 *
 * 面板自包含：自己读、自己写、自己报状态，宿主只要挂进来即可。
 */
const props = defineProps<{ connected: boolean }>()

interface ExpertConfig {
  maxSteps: number
  thinkingBudgetTokens: number
  maxToolOutputChars: number
  bashTimeoutSeconds: number
  scheduledTurnTimeoutSeconds: number
  maxRepairAttempts: number
  maxOutputContinuations: number
  enableContextCompaction: boolean
  personality: string
  systemPrompt: string
  delegationMaxDepth: number
  delegationMaxPerTurn: number
  factsInjectTopK: number
  extraReadableRoots: string[]
  sessionRetentionDays: number
  memoryConsolidationEnabled: boolean
  memoryConsolidationLookbackDays: number
  scheduleWebhookUrl: string
}

const defaults: ExpertConfig = {
  maxSteps: 40,
  thinkingBudgetTokens: 16384,
  maxToolOutputChars: 60000,
  bashTimeoutSeconds: 180,
  scheduledTurnTimeoutSeconds: 1800,
  maxRepairAttempts: 3,
  maxOutputContinuations: 6,
  enableContextCompaction: true,
  personality: 'pragmatic',
  systemPrompt: 'system/default',
  delegationMaxDepth: 1,
  delegationMaxPerTurn: 8,
  factsInjectTopK: 12,
  extraReadableRoots: [],
  sessionRetentionDays: 90,
  memoryConsolidationEnabled: true,
  memoryConsolidationLookbackDays: 3,
  scheduleWebhookUrl: ''
}

const form = reactive<ExpertConfig>({ ...defaults })
/** 额外可读目录用多行文本编辑：一行一个绝对路径，比动态行列表更好粘贴。 */
const rootsText = ref('')
const loading = ref(false)
const saving = ref(false)
const note = ref('')
const error = ref('')

function apply(config: Partial<ExpertConfig>): void {
  Object.assign(form, {
    ...defaults,
    ...Object.fromEntries(Object.entries(config).filter(([, value]) => value !== undefined && value !== null))
  })
  form.extraReadableRoots = Array.isArray(config.extraReadableRoots) ? config.extraReadableRoots : []
  rootsText.value = form.extraReadableRoots.join('\n')
}

async function load(): Promise<void> {
  if (!props.connected) return
  loading.value = true
  error.value = ''
  try {
    apply(await requestJson<Partial<ExpertConfig>>('agent.config.get'))
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
    const roots = rootsText.value
      .split('\n')
      .map((line) => line.trim())
      .filter((line) => line.length > 0)
    const config = await requestJson<Partial<ExpertConfig>>('agent.config.set', {
      maxSteps: form.maxSteps,
      thinkingBudgetTokens: form.thinkingBudgetTokens,
      maxToolOutputChars: form.maxToolOutputChars,
      bashTimeoutSeconds: form.bashTimeoutSeconds,
      scheduledTurnTimeoutSeconds: form.scheduledTurnTimeoutSeconds,
      maxRepairAttempts: form.maxRepairAttempts,
      maxOutputContinuations: form.maxOutputContinuations,
      enableContextCompaction: form.enableContextCompaction,
      personality: form.personality,
      systemPrompt: form.systemPrompt,
      delegationMaxDepth: form.delegationMaxDepth,
      delegationMaxPerTurn: form.delegationMaxPerTurn,
      factsInjectTopK: form.factsInjectTopK,
      extraReadableRoots: roots,
      sessionRetentionDays: form.sessionRetentionDays,
      memoryConsolidationEnabled: form.memoryConsolidationEnabled,
      memoryConsolidationLookbackDays: form.memoryConsolidationLookbackDays,
      scheduleWebhookUrl: form.scheduleWebhookUrl.trim()
    })
    apply(config)
    note.value = '专家参数已保存，下一次回合生效'
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    saving.value = false
  }
}

function resetLocal(): void {
  apply({ ...defaults })
  note.value = '已填入出厂默认值，点击下方「保存专家参数」后生效'
}

watch(() => props.connected, (connected) => { if (connected) void load() }, { immediate: true })
</script>

<template>
  <div class="expert-params">
    <p v-if="error" class="settings-inline-error">{{ error }}</p>
    <p v-else-if="note" class="settings-inline-note">{{ note }}</p>
    <p v-if="loading" class="settings-inline-note"><LoaderCircle class="spin" :size="14" /> 正在读取参数</p>

    <div class="expert-grid">
      <label>
        <FieldLabel en="Max Steps" zh="单回合最大步数"
          help="一个回合内允许的工具调用轮次上限（1-500，默认 40）。调大能完成更长的任务，但也会放大单回合的 token 与时间成本。" />
        <input v-model.number="form.maxSteps" type="number" min="1" max="500" step="1" />
      </label>
      <label>
        <FieldLabel en="Thinking Budget" zh="本地思考预算（token）"
          help="本地思考模型的思考段预算（0-200000，默认 16384）。耗尽后自动收束思考；云端模型由服务端决定，此值不生效。" />
        <input v-model.number="form.thinkingBudgetTokens" type="number" min="0" max="200000" step="1024" />
      </label>
      <label>
        <FieldLabel en="Max Tool Output" zh="工具输出硬上限（字符）"
          help="单次工具结果回灌给模型的硬上限（默认 60000）。有效预算还会按模型上下文窗口自适应；调小能省上下文，但可能截掉关键信息。" />
        <input v-model.number="form.maxToolOutputChars" type="number" min="1000" max="500000" step="1000" />
      </label>
      <label>
        <FieldLabel en="Bash Timeout" zh="bash 超时（秒）"
          help="单次 bash 命令的墙钟上限（默认 180）。长构建/长测试需要调大，否则会被判为超时。" />
        <input v-model.number="form.bashTimeoutSeconds" type="number" min="1" max="3600" step="30" />
      </label>
      <label>
        <FieldLabel en="Scheduled Turn Timeout" zh="计划任务回合上限（秒）"
          help="计划任务单次运行的墙钟上限（默认 1800）。超时会被取消并记为该次运行失败。" />
        <input v-model.number="form.scheduledTurnTimeoutSeconds" type="number" min="30" max="86400" step="60" />
      </label>
      <label>
        <FieldLabel en="Repair Attempts" zh="自动修复次数上限"
          help="自动验证失败后把错误回灌给模型重试的次数（0-10，默认 3）。0 = 验证失败直接结束。" />
        <input v-model.number="form.maxRepairAttempts" type="number" min="0" max="10" step="1" />
      </label>
      <label>
        <FieldLabel en="Output Continuations" zh="输出截断续写次数"
          help="模型输出触顶被截断时自动续写的次数上限（0-50，默认 6）。设为 0 则截断即结束。" />
        <input v-model.number="form.maxOutputContinuations" type="number" min="0" max="50" step="1" />
      </label>
      <label>
        <FieldLabel en="Delegation Depth" zh="子代理嵌套深度"
          help="子代理还能不能再委派（1-4，默认 1 = 子代理不能再委派）。每增加一层都会放大并行成本。" />
        <input v-model.number="form.delegationMaxDepth" type="number" min="1" max="4" step="1" />
      </label>
      <label>
        <FieldLabel en="Delegation Per Turn" zh="每回合子任务配额"
          help="一个回合内允许派发的子任务总数（1-64，默认 8）。配额把最坏情况下的并发放大钉住。" />
        <input v-model.number="form.delegationMaxPerTurn" type="number" min="1" max="64" step="1" />
      </label>
      <label>
        <FieldLabel en="Facts Top K" zh="项目事实注入条数"
          help="每回合按相关性挑多少条项目事实进提示词（0-100，默认 12）。库越大提示越稀释，无关事实还会挤占上下文。" />
        <input v-model.number="form.factsInjectTopK" type="number" min="0" max="100" step="1" />
      </label>
      <label>
        <FieldLabel en="Session Retention" zh="会话保留天数"
          help="超过 N 天未更新的会话在维护时自动归档（0 = 关闭）。只从默认列表隐藏，数据不删除，仍可检索与找回。默认 90 天。" />
        <input v-model.number="form.sessionRetentionDays" type="number" min="0" max="3650" step="30" />
      </label>
      <label>
        <FieldLabel en="Consolidation Window" zh="记忆固化扫描窗口（天）"
          help="只把最近 N 天更新过的会话经验沉淀为项目事实（1-30，默认 3）。" />
        <input v-model.number="form.memoryConsolidationLookbackDays" type="number" min="1" max="30" step="1" />
      </label>
      <label class="span-2">
        <FieldLabel en="Personality" zh="人格"
          help="系统提示词的人格风格：pragmatic = 务实的工程口吻；friendly = 更口语、解释更多。" />
        <select v-model="form.personality" class="settings-select">
          <option value="pragmatic">pragmatic（务实工程口吻）</option>
          <option value="friendly">friendly（亲切、解释更多）</option>
        </select>
      </label>
      <label class="span-2">
        <FieldLabel en="System Prompt" zh="主系统提示词键"
          help="指向 prompts/ 下的提示词文件（不含扩展名），例如 system/default。改动它等于换掉智能体的核心行为，非必要不要动。" />
        <input v-model="form.systemPrompt" placeholder="system/default" spellcheck="false" />
      </label>
      <label class="span-2">
        <FieldLabel en="Extra Readable Roots" zh="额外可读目录"
          help="工作区之外额外授权智能体读取的绝对路径，一行一个。文件读取工具默认只能访问工作区内容；凭据文件仍被排除，缩进/空行会被忽略。" />
        <textarea v-model="rootsText" rows="3" spellcheck="false"
          placeholder="D:\\datasets&#10;E:\\reference-repos" />
      </label>
      <label class="span-2">
        <FieldLabel en="Schedule Webhook" zh="计划任务结果回调"
          help="每次计划任务结束向该地址 POST 一份结果（taskId/name/status/error/sessionId/output）。没有桌面端连接时这是唯一的通知通道；留空关闭。" />
        <input v-model="form.scheduleWebhookUrl" placeholder="https://example.com/haoyue/hook" spellcheck="false" />
      </label>
    </div>

    <label class="provider-enabled-row">
      <span>
        <strong>上下文压缩</strong>
        <small>上下文即将溢出窗口时，把更早的对话压成摘要以延续单回合。关闭后超窗即失败；只有「保留比例」可调显然不足以表达这个意图。</small>
      </span>
      <input v-model="form.enableContextCompaction" class="sr-only" type="checkbox" />
      <span class="toggle-switch" aria-hidden="true"><span /></span>
    </label>

    <label class="provider-enabled-row">
      <span>
        <strong>记忆固化</strong>
        <small>定期把近期会话经验沉淀为项目事实。关闭后只保留回合内模型自觉写入的事实。</small>
      </span>
      <input v-model="form.memoryConsolidationEnabled" class="sr-only" type="checkbox" />
      <span class="toggle-switch" aria-hidden="true"><span /></span>
    </label>

    <div class="expert-actions">
      <button type="button" class="secondary-button compact-button" :disabled="saving" @click="resetLocal">
        <RotateCcw :size="13" /> 填入默认值
      </button>
      <button type="button" class="secondary-button primary-action" :disabled="saving || !connected" @click="save">
        <LoaderCircle v-if="saving" class="spin" :size="14" />
        <Save v-else :size="14" />
        {{ saving ? '保存中…' : '保存专家参数' }}
      </button>
    </div>
  </div>
</template>

<style scoped>
.expert-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 12px;
  margin: 10px 0 14px;
}

.expert-grid label {
  display: flex;
  flex-direction: column;
  gap: 5px;
  min-width: 0;
}

.expert-grid .span-2 {
  grid-column: 1 / -1;
}

.expert-grid input,
.expert-grid select,
.expert-grid textarea {
  width: 100%;
  padding: 7px 9px;
  color: var(--text);
  font-size: 13px;
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 8px;
}

.expert-grid textarea {
  font-family: var(--font-mono, monospace);
  font-size: 12px;
  resize: vertical;
}

.expert-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  margin-top: 12px;
}

.settings-inline-note,
.settings-inline-error {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 0 0 10px;
  font-size: 12px;
}

.settings-inline-note { color: var(--text-muted); }
.settings-inline-error { color: var(--danger); }
</style>
