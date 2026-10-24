<script setup lang="ts">
import { AlertTriangle, LoaderCircle, RotateCcw, Save } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { diffPatch, loadConfigDocument, saveConfigPatch, type ConfigObject } from '../config-patch'

/**
 * 高级设置 → 专家参数（JSON 编辑器）。
 *
 * 这是最后一块兜底：即使某些字段没有专门控件（新增的实验性配置、第三方扩展配置），
 * 用户也不必再去翻 `~/.haoyue/config.json`——那正是「配置项存在但界面没有入口」的根源。
 *
 * 安全约束有两条：
 * 1) 读侧不回传密钥（`ApiKey` 被移除、Authorization 类头被置空），因此**不能整份写回**；
 *    保存时只提交相对载入快照的变更叶子（`diffPatch`），未改动的字段一概不发。
 * 2) 写的是全局配置；工作区级覆盖在「项目属性 → 智能体」里改。
 */
const props = defineProps<{ connected: boolean }>()

const loaded = ref<ConfigObject | null>(null)
const text = ref('')
const parsed = ref<ConfigObject | null>(null)
const parseError = ref('')
const loading = ref(false)
const saving = ref(false)
const note = ref('')
const error = ref('')
const path = ref('')

const dirty = computed(() => {
  if (!loaded.value || !parsed.value) return false
  return Object.keys(diffPatch(loaded.value, parsed.value)).length > 0
})

const pendingCount = computed(() => {
  if (!loaded.value || !parsed.value) return 0
  return countLeaves(diffPatch(loaded.value, parsed.value))
})

function countLeaves(patch: ConfigObject): number {
  let total = 0
  for (const value of Object.values(patch)) {
    if (value && typeof value === 'object' && !Array.isArray(value)) total += countLeaves(value as ConfigObject)
    else total += 1
  }
  return total
}

async function load(): Promise<void> {
  if (!props.connected) return
  loading.value = true
  error.value = ''
  note.value = ''
  try {
    const document = await loadConfigDocument()
    loaded.value = document.config
    path.value = document.path
    text.value = JSON.stringify(document.config, null, 2)
    parsed.value = document.config
    parseError.value = ''
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    loading.value = false
  }
}

function onInput(): void {
  parseError.value = ''
  note.value = ''
  try {
    const value = JSON.parse(text.value) as unknown
    if (!value || typeof value !== 'object' || Array.isArray(value)) {
      parsed.value = null
      parseError.value = '顶层必须是一个 JSON 对象（{ … }）'
      return
    }
    parsed.value = value as ConfigObject
  } catch (reason) {
    parsed.value = null
    parseError.value = reason instanceof Error ? reason.message : String(reason)
  }
}

function formatText(): void {
  if (!parsed.value) return
  text.value = JSON.stringify(parsed.value, null, 2)
}

async function save(): Promise<void> {
  if (!props.connected || !loaded.value || !parsed.value) return
  saving.value = true
  error.value = ''
  note.value = ''
  try {
    const patch = diffPatch(loaded.value, parsed.value)
    const count = countLeaves(patch)
    await saveConfigPatch(patch)
    await load()
    note.value = count === 0 ? '没有改动' : `已写入 ${count} 处变更；重启运行时后完全生效的字段会在下次请求时读取`
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    saving.value = false
  }
}

watch(() => props.connected, (connected) => { if (connected) void load() }, { immediate: true })
</script>

<template>
  <div class="config-editor">
    <p v-if="error" class="config-note is-error">{{ error }}</p>
    <p v-else-if="note" class="config-note is-ok">{{ note }}</p>
    <p v-if="loading" class="config-note"><LoaderCircle class="spin" :size="14" /> 正在读取配置</p>

    <p class="config-path">
      配置文件：<code>{{ path || '—' }}</code>
    </p>

    <p class="config-warning">
      <AlertTriangle :size="13" aria-hidden="true" />
      保存只提交你实际改动的字段（当前 <b>{{ pendingCount }}</b> 处）。密钥类字段（`ApiKey`、
      Authorization 头）不会回传也不在此显示，因此在这里删改与它们无关的字段不会导致凭据丢失。
    </p>

    <textarea v-model="text" class="config-textarea" spellcheck="false" wrap="off" @input="onInput" />

    <p v-if="parseError" class="config-note is-error">JSON 解析失败：{{ parseError }}</p>

    <div class="config-actions">
      <button type="button" class="secondary-button compact-button" :disabled="loading || saving" @click="load">
        <RotateCcw :size="13" /> 重新载入
      </button>
      <button type="button" class="secondary-button compact-button" :disabled="!parsed" @click="formatText">
        格式化
      </button>
      <button type="button" class="secondary-button primary-action"
        :disabled="saving || !parsed || !dirty || !connected" @click="save">
        <LoaderCircle v-if="saving" class="spin" :size="14" />
        <Save v-else :size="14" />
        {{ saving ? '保存中…' : dirty ? `保存 ${pendingCount} 处变更` : '没有改动' }}
      </button>
    </div>
  </div>
</template>

<style scoped>
.config-editor {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.config-note {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 0;
  color: var(--text-muted);
  font-size: 12px;
}

.config-note.is-ok { color: var(--accent); }
.config-note.is-error { color: var(--danger); }

.config-path {
  margin: 0;
  color: var(--text-muted);
  font-size: 11px;
}

.config-path code {
  font-family: var(--font-mono, monospace);
  word-break: break-all;
}

.config-warning {
  display: flex;
  align-items: flex-start;
  gap: 6px;
  margin: 0;
  padding: 8px 10px;
  color: var(--text-secondary);
  font-size: 11px;
  line-height: 1.6;
  background: color-mix(in srgb, var(--surface-hover) 60%, transparent);
  border: 1px solid var(--border);
  border-radius: 8px;
}

.config-textarea {
  width: 100%;
  min-height: 320px;
  max-height: 52vh;
  padding: 10px 12px;
  color: var(--text);
  font-family: var(--font-mono, monospace);
  font-size: 12px;
  line-height: 1.6;
  white-space: pre;
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 8px;
  resize: vertical;
}

.config-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>
