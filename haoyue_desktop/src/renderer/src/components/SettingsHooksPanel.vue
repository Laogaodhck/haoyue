<script setup lang="ts">
import { LoaderCircle, Pencil, Plus, ShieldAlert, Trash2, Webhook, X } from '@lucide/vue'
import { onMounted, ref, watch } from 'vue'
import { requestJson } from '../daemon-json'
import type { HookDraft, HookInfo, HooksListResult, HookTestResult } from '../types'
import SelectMenu from './SelectMenu.vue'

/**
 * 设置 → 钩子。
 *
 * 事件钩子：task.completed / schedule.completed 触发时自动执行 Webhook 或本地命令。
 * 「查看钩子配置和问题」= hooks.list 返回的配置校验（problems）与进程内最近执行状态（status）；
 * 增删改走 hooks.save / hooks.delete，试运行走 hooks.test。密钥只读回「是否已配置」标记，
 * 不在设置页回显明文。
 */
const props = defineProps<{ connected: boolean }>()

const emit = defineEmits<{
  notice: [message: string]
  error: [message: string]
}>()

const loading = ref(false)
const loaded = ref(false)
const busy = ref('')
const hooks = ref<HookInfo[]>([])
const problems = ref<HooksListResult['problems']>([])
const status = ref<HooksListResult['status']>([])
const testResults = ref<Record<string, HookTestResult>>({})

/** 当前编辑中的钩子草稿；null = 不在编辑状态。 */
const editing = ref<HookDraft | null>(null)
const editingOriginal = ref<HookInfo | null>(null)

const TRIGGER_OPTIONS = [
  { value: 'task.completed', label: 'task.completed', description: '回合完成时触发（成功、失败、取消都会触发）' },
  { value: 'schedule.completed', label: 'schedule.completed', description: '定时任务完成时触发' }
]

const ACTION_OPTIONS = [
  { value: 'webhook', label: 'Webhook', description: '向指定地址发送 POST JSON 请求（可选 Bearer 密钥）' },
  { value: 'command', label: '本地命令', description: '在本机执行命令（30 秒超时，不经过 shell 拼装）' }
]

function blankDraft(): HookDraft {
  return {
    name: '',
    trigger: 'task.completed',
    action: 'webhook',
    enabled: true,
    webhookUrl: '',
    webhookSecret: '',
    command: ''
  }
}

function applyList(result: HooksListResult): void {
  hooks.value = result.hooks
  problems.value = result.problems
  status.value = result.status
}

async function load(): Promise<void> {
  if (!props.connected) return
  loading.value = true
  try {
    applyList(await requestJson<HooksListResult>('hooks.list'))
    loaded.value = true
  } catch (reason) {
    emit('error', reason instanceof Error ? reason.message : String(reason))
  } finally {
    loading.value = false
  }
}

function statusOf(name: string): HooksListResult['status'][number] | undefined {
  return status.value.find((item) => item.name === name)
}

function problemOf(name: string): string | undefined {
  return problems.value.find((item) => item.name === name)?.problem
}

function openCreate(): void {
  editing.value = blankDraft()
  editingOriginal.value = null
}

function openEdit(hook: HookInfo): void {
  editing.value = {
    name: hook.name,
    trigger: hook.trigger,
    action: hook.action,
    enabled: hook.enabled,
    webhookUrl: hook.webhookUrl,
    webhookSecret: '',
    command: hook.command
  }
  editingOriginal.value = hook
}

function closeEditor(): void {
  editing.value = null
  editingOriginal.value = null
}

async function saveDraft(): Promise<void> {
  if (!editing.value) return
  const draft = editing.value
  if (!draft.name.trim()) {
    emit('error', '钩子名称不能为空')
    return
  }
  if (draft.action === 'webhook' && !/^https?:\/\/\S+$/.test(draft.webhookUrl.trim())) {
    emit('error', 'Webhook 地址必须是完整的 http(s) 地址')
    return
  }
  if (draft.action === 'command' && !draft.command.trim()) {
    emit('error', '本地命令不能为空')
    return
  }

  busy.value = 'hooks.save'
  const payload: Record<string, unknown> = {
    name: draft.name.trim(),
    trigger: draft.trigger,
    action: draft.action,
    enabled: draft.enabled
  }
  if (draft.action === 'webhook') {
    payload.webhookUrl = draft.webhookUrl.trim()
    if (draft.webhookSecret !== '') payload.webhookSecret = draft.webhookSecret
  } else {
    payload.command = draft.command.trim()
  }
  try {
    applyList(await requestJson<HooksListResult>('hooks.save', payload))
    delete testResults.value[draft.name.trim()]
    emit('notice', editingOriginal.value ? `钩子「${draft.name.trim()}」已更新` : `钩子「${draft.name.trim()}」已添加`)
    closeEditor()
  } catch (reason) {
    emit('error', reason instanceof Error ? reason.message : String(reason))
  } finally {
    busy.value = ''
  }
}

async function toggleEnabled(hook: HookInfo, checked: boolean): Promise<void> {
  const previous = hook.enabled
  hook.enabled = checked
  busy.value = `toggle.${hook.name}`
  try {
    applyList(await requestJson<HooksListResult>('hooks.save', {
      name: hook.name,
      enabled: checked
    }))
    emit('notice', `钩子「${hook.name}」已${checked ? '启用' : '停用'}`)
  } catch (reason) {
    hook.enabled = previous
    emit('error', reason instanceof Error ? reason.message : String(reason))
  } finally {
    busy.value = ''
  }
}

async function testHook(hook: HookInfo): Promise<void> {
  busy.value = `test.${hook.name}`
  try {
    const result = await requestJson<HookTestResult>('hooks.test', { name: hook.name })
    testResults.value = { ...testResults.value, [hook.name]: result }
    // 试运行也算一次真实执行：回读状态并刷新问题诊断。
    applyList(await requestJson<HooksListResult>('hooks.list'))
    emit('notice', result.ok
      ? `钩子「${hook.name}」测试成功（${result.durationMs}ms）`
      : `钩子「${hook.name}」测试失败：${result.error ?? '未知错误'}`)
  } catch (reason) {
    emit('error', reason instanceof Error ? reason.message : String(reason))
  } finally {
    busy.value = ''
  }
}

async function deleteHook(hook: HookInfo): Promise<void> {
  busy.value = `delete.${hook.name}`
  try {
    applyList(await requestJson<HooksListResult>('hooks.delete', { name: hook.name }))
    delete testResults.value[hook.name]
    emit('notice', `钩子「${hook.name}」已删除`)
  } catch (reason) {
    emit('error', reason instanceof Error ? reason.message : String(reason))
  } finally {
    busy.value = ''
  }
}

onMounted(() => { void load() })
watch(() => props.connected, (connected) => { if (connected) void load() })
</script>

<template>
  <div v-if="loading && !loaded" class="settings-loading">
    <LoaderCircle class="spin" :size="18" /> 正在读取钩子配置…
  </div>

  <template v-else>
    <div class="settings-section-heading">
      <div>
        <h3>钩子</h3>
        <p>事件钩子：任务完成或定时任务完成时自动执行 Webhook 请求或本地命令</p>
      </div>
    </div>

    <div class="hooks-warning">
      <ShieldAlert :size="16" />
      <span>使用前请确认自动执行的操作及其文件、网络访问安全</span>
    </div>

    <section class="settings-group">
      <div class="settings-group-title">
        <strong>已配置的 Hooks</strong>
        <small>启用后，满足触发事件的回合会自动执行对应动作，不经过本设置页</small>
        <button class="secondary-button hooks-add" :disabled="busy !== '' || editing !== null" @click="openCreate">
          <Plus :size="14" /> 添加钩子
        </button>
      </div>

      <div v-if="editing" class="hook-editor">
        <div class="hook-editor-title">
          <strong>{{ editingOriginal ? `编辑「${editingOriginal.name}」` : '添加钩子' }}</strong>
          <button class="icon-button compact" title="取消编辑" :disabled="busy !== ''" @click="closeEditor">
            <X :size="15" />
          </button>
        </div>
        <div class="hook-form-grid">
          <label class="field-label hook-name-field" for="hook-name">
            名称
            <input id="hook-name" v-model="editing.name" class="text-input" placeholder="例如：通知飞书机器人"
              :disabled="busy !== ''" />
          </label>
          <label class="field-label" for="hook-trigger">
            触发事件
            <SelectMenu :model-value="editing.trigger" class="settings-select select-compact"
              :options="TRIGGER_OPTIONS" label="触发事件" :disabled="busy !== ''"
              @update:model-value="editing.trigger = $event as HookDraft['trigger']" />
          </label>
          <label class="field-label" for="hook-action">
            动作类型
            <SelectMenu :model-value="editing.action" class="settings-select select-compact"
              :options="ACTION_OPTIONS" label="动作类型" :disabled="busy !== ''"
              @update:model-value="editing.action = $event as HookDraft['action']" />
          </label>
          <template v-if="editing.action === 'webhook'">
            <label class="field-label hook-url-field" for="hook-url">
              Webhook 地址
              <input id="hook-url" v-model="editing.webhookUrl" class="text-input" type="url"
                placeholder="https://example.com/hook" :disabled="busy !== ''" />
            </label>
            <label class="field-label" for="hook-secret">
              Bearer 密钥
              <input id="hook-secret" v-model="editing.webhookSecret" class="text-input" type="password"
                :placeholder="editingOriginal?.hasSecret ? '留空保持已存密钥' : '可选；随请求发送 Authorization: Bearer'"
                :disabled="busy !== ''" />
            </label>
          </template>
          <label v-else class="field-label hook-command-field" for="hook-command">
            本地命令
            <input id="hook-command" v-model="editing.command" class="text-input"
              placeholder="例如：notepad.exe（参数用空格分隔，30 秒超时）" :disabled="busy !== ''" />
          </label>
        </div>
        <div class="hook-editor-actions">
          <label class="provider-enabled-row hook-enabled-row">
            <span>
              <strong>启用</strong>
              <small>保存后立即生效</small>
            </span>
            <input :checked="editing.enabled" class="sr-only" type="checkbox" :disabled="busy !== ''"
              @change="editing.enabled = Boolean(($event.target as HTMLInputElement).checked)" />
            <span class="toggle-switch" aria-hidden="true"><span /></span>
          </label>
          <div class="row-controls">
            <button class="secondary-button" :disabled="busy !== ''" @click="closeEditor">取消</button>
            <button class="secondary-button primary-action" :disabled="busy !== ''" @click="saveDraft">
              <LoaderCircle v-if="busy === 'hooks.save'" class="spin" :size="14" />
              {{ editingOriginal ? '保存更改' : '添加' }}
            </button>
          </div>
        </div>
      </div>

      <div v-if="problems.length > 0" class="hooks-problems">
        <div v-for="problem in problems" :key="problem.name" class="hook-problem">
          <ShieldAlert :size="14" />
          <strong>{{ problem.name }}</strong>
          <span>{{ problem.problem }}</span>
        </div>
      </div>

      <div v-if="hooks.length === 0 && editing === null" class="empty-settings">尚未配置钩子：点击「添加钩子」创建第一条</div>

      <div v-for="hook in hooks" :key="hook.name" class="hook-card">
        <div class="settings-list-row">
          <Webhook :size="17" />
          <div class="list-main">
            <div>
              <strong :title="hook.name">{{ hook.name }}</strong>
              <span class="inline-badge">{{ hook.trigger }}</span>
              <span class="inline-badge" :class="hook.action === 'webhook' ? '' : 'command-badge'">
                {{ hook.action === 'webhook' ? 'Webhook' : '本地命令' }}
              </span>
              <span v-if="hook.hasSecret" class="inline-badge">已设置密钥</span>
              <span v-if="problemOf(hook.name)" class="inline-badge problem-badge">配置问题</span>
            </div>
            <small :title="hook.action === 'webhook' ? hook.webhookUrl : hook.command">
              {{ hook.action === 'webhook'
                ? (hook.webhookUrl || '未填写 Webhook 地址')
                : (hook.command || '未填写命令') }}
            </small>
            <small v-if="statusOf(hook.name)" class="hook-status">
              已触发 {{ statusOf(hook.name)?.runCount ?? 0 }} 次 ·
              <template v-if="statusOf(hook.name)?.lastTriggeredAt">
                最近 {{ statusOf(hook.name)?.lastTriggeredAt?.slice(0, 19).replace('T', ' ') }}
              </template>
              <template v-if="statusOf(hook.name)?.lastOk !== undefined">
                · {{ statusOf(hook.name)?.lastOk ? '上次成功' : `上次失败${statusOf(hook.name)?.lastError ? `：${statusOf(hook.name)?.lastError}` : ''}` }}
              </template>
              <template v-if="statusOf(hook.name)?.lastDurationMs != null">
                · {{ statusOf(hook.name)?.lastDurationMs }}ms
              </template>
            </small>
            <small v-if="problemOf(hook.name)" class="hook-problem-text">{{ problemOf(hook.name) }}</small>
          </div>
          <div class="row-controls">
            <button class="secondary-button" :disabled="busy !== ''" title="立即触发一次（不要求启用）"
              @click="testHook(hook)">测试</button>
            <button class="icon-button compact" title="编辑钩子" :disabled="busy !== '' || editing !== null"
              @click="openEdit(hook)">
              <Pencil :size="15" />
            </button>
            <button class="icon-button compact" title="删除钩子" :disabled="busy !== ''" @click="deleteHook(hook)">
              <Trash2 :size="15" />
            </button>
          </div>
        </div>
        <label class="provider-enabled-row hook-enable-row">
          <span>
            <strong>启用</strong>
            <small>满足触发事件时自动执行</small>
          </span>
          <input :checked="hook.enabled" class="sr-only" type="checkbox" :disabled="busy !== ''"
            @change="toggleEnabled(hook, Boolean(($event.target as HTMLInputElement).checked))" />
          <span class="toggle-switch" aria-hidden="true"><span /></span>
        </label>
        <div v-if="testResults[hook.name]" class="hook-test-result" :class="{ ok: testResults[hook.name]?.ok }">
          <strong>{{ testResults[hook.name]?.ok ? '测试成功' : '测试失败' }}</strong>
          <span>{{ testResults[hook.name]?.ok
            ? `耗时 ${testResults[hook.name]?.durationMs}ms · ${testResults[hook.name]?.output || '已执行'}`
            : (testResults[hook.name]?.error || '未知错误') }}</span>
        </div>
      </div>
    </section>
  </template>
</template>

<style scoped>
.hooks-warning {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 12px;
  border: 1px solid color-mix(in srgb, var(--warning, #d29922) 45%, var(--border));
  border-radius: 8px;
  background: color-mix(in srgb, var(--warning, #d29922) 10%, transparent);
  color: color-mix(in srgb, var(--warning, #d29922) 75%, var(--text));
  font-size: 13px;
  margin: 4px 0 16px;
}

.settings-group-title {
  align-items: flex-start;
}

.hooks-add {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.hook-editor {
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 12px 14px;
  margin: 10px 0 14px;
  background: var(--accent-soft);
}

.hook-editor-title {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 10px;
}

.hook-form-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 10px 12px;
}

.hook-name-field, .hook-url-field, .hook-command-field {
  grid-column: 1 / -1;
}

.hook-editor-actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-top: 12px;
}

.hook-enabled-row {
  margin: 0;
  padding: 0;
}

.hooks-problems {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin: 8px 0;
}

.hook-problem {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 10px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--danger) 12%, transparent);
  color: var(--danger);
  font-size: 13px;
}

.hook-card {
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 10px 12px;
  margin: 10px 0;
}

.hook-enable-row {
  margin: 8px 0 0;
  padding: 0;
}

.hook-status {
  color: var(--text-muted);
}

.hook-problem-text {
  color: var(--danger);
}

.hook-test-result {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 8px;
  padding: 6px 10px;
  border-radius: 8px;
  font-size: 13px;
  background: color-mix(in srgb, var(--danger) 10%, transparent);
  color: var(--danger);
}

.hook-test-result.ok {
  background: color-mix(in srgb, var(--success, #3fb950) 10%, transparent);
  color: var(--success, #3fb950);
}

.inline-badge.command-badge {
  background: color-mix(in srgb, var(--accent) 14%, transparent);
  color: var(--accent);
}

.inline-badge.problem-badge {
  background: color-mix(in srgb, var(--danger) 14%, transparent);
  color: var(--danger);
}

@media (max-width: 720px) {
  .hook-form-grid {
    grid-template-columns: 1fr;
  }
  .hook-editor-actions {
    flex-direction: column;
    align-items: stretch;
  }
  .hook-editor-actions .row-controls {
    justify-content: flex-end;
  }
}
</style>
