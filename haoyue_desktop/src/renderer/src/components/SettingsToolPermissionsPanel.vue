<script setup lang="ts">
import { AlertTriangle, LoaderCircle, RefreshCw, Save, ShieldCheck } from '@lucide/vue'
import { computed, onMounted, ref, watch } from 'vue'
import { diffPatch, loadConfigDocument, saveConfigPatch, type ConfigObject } from '../config-patch'
import { parseToolPatterns, readToolPermissions, sameToolPatterns } from '../tool-permissions'

/**
 * 设置 → 工具权限。
 *
 * 编辑的是**项目级** `<workspace>/.haoyue/config.json`：`allowedTools` 白名单与
 * `allowToolFallbackToGlobal` 降级开关（默认关闭 = fail-closed）。
 *
 * 读写复用既有 `config.get` / `config.save { workspace }` 通道，不新增 RPC：
 * - 保存只提交相对载入快照的变更叶子（`diffPatch`），不会把密钥字段回写；
 * - daemon 写完盘会把结果同步回内存 `Workspace.Config`，因此下一回合立即生效；
 * - 非法模式在提交前逐行报错，服务端 `ValidateToolPermissionPatch` 再兜一层，
 *   两个客户端（CLI/桌面）都写不进垃圾模式。
 */
const props = defineProps<{ connected: boolean; workspacePath?: string }>()

const loaded = ref<ConfigObject | null>(null)
const path = ref('')
const patternsText = ref('')
const originalPatterns = ref<string[]>([])
const originalFallback = ref(false)
const fallback = ref(false)
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const note = ref('')

const canEdit = computed(() => props.connected && Boolean(props.workspacePath))

const validation = computed(() => parseToolPatterns(patternsText.value))

const dirty = computed(() =>
  !sameToolPatterns(validation.value.patterns, originalPatterns.value)
  || fallback.value !== originalFallback.value)

async function load(): Promise<void> {
  if (!props.connected || !props.workspacePath) {
    loaded.value = null
    path.value = ''
    patternsText.value = ''
    originalPatterns.value = []
    originalFallback.value = false
    fallback.value = false
    return
  }
  loading.value = true
  error.value = ''
  note.value = ''
  try {
    const document = await loadConfigDocument(props.workspacePath)
    loaded.value = document.config
    path.value = document.path
    const view = readToolPermissions(document.config)
    originalPatterns.value = view.patterns
    originalFallback.value = view.fallback
    patternsText.value = view.patterns.join('\n')
    fallback.value = view.fallback
    if (view.problem) error.value = view.problem
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    loading.value = false
  }
}

async function save(): Promise<void> {
  if (!canEdit.value || saving.value || !loaded.value) return
  note.value = ''
  error.value = ''
  if (validation.value.errors.length > 0) {
    // 现场报错并拒绝保存：非法模式一旦落盘，用户会以为权限已经收紧。
    error.value = `未保存：工具白名单有 ${validation.value.errors.length} 处格式问题。`
    return
  }
  saving.value = true
  try {
    const next: ConfigObject = {
      ...loaded.value,
      allowedTools: validation.value.patterns,
      allowToolFallbackToGlobal: fallback.value
    }
    const patch = diffPatch(loaded.value, next)
    if (Object.keys(patch).length === 0) {
      note.value = '没有改动。'
      return
    }
    await saveConfigPatch(patch, props.workspacePath)
    await load()
    note.value = validation.value.patterns.length === 0
      ? '已清空白名单（= 不限制工具）；项目配置已同步到内存，下一回合生效。'
      : `已保存 ${validation.value.patterns.length} 条白名单模式；项目配置已同步到内存，下一回合生效。`
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    saving.value = false
  }
}

onMounted(() => { void load() })
watch(() => props.connected, () => { void load() })
watch(() => props.workspacePath, () => { void load() })
</script>

<template>
  <section class="settings-group" aria-label="工具权限">
    <div class="settings-group-title toolperm-head">
      <div>
        <strong><ShieldCheck :size="15" aria-hidden="true" /> 工具白名单</strong>
        <small>项目级 <code>allowedTools</code>：一行一条，支持精确名字（如 <code>read_file</code>）、
          MCP 的 <code>server/tool</code> 与 <code>*</code> 通配（如 <code>github/*</code>、
          <code>*/create_issue</code>）。留空 = 不限制；智能体与子智能体只能在此清单内继续收窄，
          无法放大。工作区 <code>disabledTools</code> 的硬禁用优先于本白名单。</small>
      </div>
      <button type="button" class="secondary-button" :disabled="!canEdit || loading" @click="load">
        <RefreshCw :size="14" /> 刷新
      </button>
    </div>

    <p v-if="!workspacePath" class="toolperm-note">
      需要先打开一个工作区：这里编辑的是项目级配置，写入 &lt;工作区&gt;/.haoyue/config.json。
    </p>
    <p v-else-if="!connected" class="toolperm-note">运行时未连接，读不到项目配置。</p>
    <p v-else-if="path" class="toolperm-path">{{ path }}</p>

    <p v-if="loading" class="toolperm-note"><LoaderCircle class="spin" :size="14" /> 正在读取项目配置</p>
    <p v-if="error" class="toolperm-note is-error">{{ error }}</p>
    <p v-else-if="note" class="toolperm-note is-ok">{{ note }}</p>

    <template v-if="canEdit">
      <div class="toolperm-editor">
        <label for="tool-permissions-patterns">白名单模式</label>
        <textarea id="tool-permissions-patterns" v-model="patternsText" class="toolperm-textarea"
          rows="8" spellcheck="false" :disabled="saving"
          placeholder="read_file&#10;github/*&#10;*/create_issue&#10;mcp__github__create_issue" />
        <small class="toolperm-hint">
          已识别 {{ validation.patterns.length }} 条有效模式；空行会被忽略，<code>server/tool</code>
          会映射到 <code>mcp__server__tool</code>。
        </small>
        <ul v-if="validation.errors.length" class="toolperm-errors" role="alert">
          <li v-for="item in validation.errors" :key="item">{{ item }}</li>
        </ul>
      </div>

      <label class="provider-enabled-row toolperm-fallback">
        <span>
          <strong>允许降级到工作区清单</strong>
          <small>角色预置或团体成员的工具清单把工具集裁成空时，退回仅受工作区白名单约束的清单。
            默认关闭（fail-closed：宁可不给工具，也不静默放权）。</small>
        </span>
        <input v-model="fallback" class="sr-only" type="checkbox" :disabled="saving" />
        <span class="toggle-switch" aria-hidden="true"><span /></span>
      </label>

      <p v-if="fallback" class="toolperm-warning">
        <AlertTriangle :size="14" aria-hidden="true" />
        <span><strong>已开启放权降级：</strong>当角色/成员清单把工具裁空时，本轮会退回工作区白名单，
          等于在该场景下额外放权。只有在你确认「裁空是误伤」时才开启。</span>
      </p>

      <div class="row-actions toolperm-actions">
        <button type="button" class="secondary-button primary-action"
          :disabled="!dirty || saving || validation.errors.length > 0" @click="save">
          <LoaderCircle v-if="saving" class="spin" :size="14" />
          <Save v-else :size="14" />
          保存
        </button>
      </div>
    </template>
  </section>
</template>

<style scoped>
.toolperm-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.toolperm-head strong {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.toolperm-head small code,
.toolperm-hint code {
  padding: 0 3px;
  border-radius: 4px;
  background: var(--surface-subtle, rgba(127, 127, 127, 0.12));
  font-size: 11px;
}

.toolperm-note,
.toolperm-path {
  margin: 6px 0 0;
  font-size: 12px;
  color: var(--text-tertiary, #888);
  display: flex;
  align-items: center;
  gap: 6px;
}

.toolperm-path {
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
  word-break: break-all;
}

.toolperm-note.is-error {
  color: var(--danger, #d64545);
}

.toolperm-note.is-ok {
  color: var(--success, #2f9e44);
}

.toolperm-editor {
  margin-top: 10px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.toolperm-editor label {
  font-size: 12px;
  font-weight: 600;
}

.toolperm-textarea {
  width: 100%;
  min-height: 132px;
  padding: 8px 10px;
  resize: vertical;
  border: 1px solid var(--border-default, rgba(127, 127, 127, 0.3));
  border-radius: 8px;
  background: var(--surface-input, transparent);
  color: inherit;
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
  font-size: 12px;
  line-height: 1.6;
}

.toolperm-textarea:focus-visible {
  outline: 2px solid var(--accent, #4c8dff);
  outline-offset: 1px;
}

.toolperm-hint {
  font-size: 11px;
  color: var(--text-tertiary, #888);
}

.toolperm-errors {
  margin: 0;
  padding: 6px 10px 6px 24px;
  border-radius: 8px;
  background: rgba(214, 69, 69, 0.08);
  color: var(--danger, #d64545);
  font-size: 12px;
  line-height: 1.6;
}

.toolperm-fallback {
  margin-top: 12px;
}

.toolperm-warning {
  display: flex;
  align-items: flex-start;
  gap: 6px;
  margin: 8px 0 0;
  padding: 8px 10px;
  border-radius: 8px;
  background: rgba(224, 155, 34, 0.1);
  color: var(--warning, #b7791f);
  font-size: 12px;
  line-height: 1.55;
}

.toolperm-actions {
  margin-top: 12px;
  justify-content: flex-end;
}
</style>
