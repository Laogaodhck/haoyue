<script setup lang="ts">
import { AlertTriangle, LoaderCircle, RefreshCw, Save, ShieldCheck, Trash2 } from '@lucide/vue'
import { computed, onMounted, ref, watch } from 'vue'
import {
  diffPatch,
  loadConfigDocument,
  saveConfigPatch,
  type ConfigObject,
  type ConfigSaveReason,
  type ConfigSaveResult
} from '../config-patch'
import {
  describeToolPermissionSave,
  parseToolPatterns,
  readToolPermissions,
  sameToolPatterns
} from '../tool-permissions'

/**
 * 设置 → 工具权限。
 *
 * 编辑的是**项目级** `<workspace>/.haoyue/config.json`：
 * - `allowedTools`：白名单（留空 = 不限制）；
 * - `disabledTools`：硬禁用（deny-overrides 最高优先级，命中即拒，白名单也救不回来）；
 * - `allowToolFallbackToGlobal`：降级开关（默认关闭 = fail-closed）。
 *
 * 读写复用既有 `config.get` / `config.save { workspace }` 通道，不新增 RPC：
 * - 保存只提交相对载入快照的变更叶子（`diffPatch`），不会把密钥字段回写；
 * - 回执里的 `applied` 决定提示口径：true = 已同步进当前引擎内存（下一回合生效）；
 *   false = 只落盘（当前引擎未驻留该工作区，切换过去后才生效）；
 * - 非法模式在提交前逐行报错，服务端 `ToolPermissionPatchValidator` 再兜一层，
 *   两个客户端（CLI/桌面）都写不进垃圾模式。
 */
const props = defineProps<{ connected: boolean; workspacePath?: string }>()

const loaded = ref<ConfigObject | null>(null)
const path = ref('')
const patternsText = ref('')
const disabledText = ref('')
const originalPatterns = ref<string[]>([])
const originalDisabled = ref<string[]>([])
const originalFallback = ref(false)
const fallback = ref(false)
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const note = ref('')
/** 磁盘上历史遗留的非法条目（如早先手工写坏的 disabledTools），单独展示、可一键清理。 */
const legacyAllowed = ref<string[]>([])
const legacyDisabled = ref<string[]>([])
const clearingLegacy = ref(false)

const canEdit = computed(() => props.connected && Boolean(props.workspacePath))

const validation = computed(() => parseToolPatterns(patternsText.value))
const disabledValidation = computed(() => parseToolPatterns(disabledText.value))

const totalErrors = computed(() => validation.value.errors.length + disabledValidation.value.errors.length)

const legacyInvalid = computed(() => [...legacyAllowed.value, ...legacyDisabled.value])
const hasLegacy = computed(() => legacyInvalid.value.length > 0)

const dirty = computed(() =>
  !sameToolPatterns(validation.value.patterns, originalPatterns.value)
  || !sameToolPatterns(disabledValidation.value.patterns, originalDisabled.value)
  || fallback.value !== originalFallback.value)

/** 旧版 daemon 只回 `applied` 布尔值：true 即已应用，false 只能按「别的工作区」兜底。 */
function appliedReasonOf(result: ConfigSaveResult | string): ConfigSaveReason {
  if (typeof result === 'string') return 'applied'
  if (result.appliedReason) return result.appliedReason
  return result.applied ? 'applied' : 'other-workspace'
}

/** 把写入回执翻译成用户能分辨的三种结果：热生效 / 别的工作区 / 当前工作区未受信。 */
function describeSaved(result: ConfigSaveResult | string): string {
  return describeToolPermissionSave({
    allowedCount: validation.value.patterns.length,
    disabledCount: disabledValidation.value.patterns.length,
    reason: appliedReasonOf(result),
    path: typeof result === 'string' ? path.value : result.path || path.value
  })
}

async function load(): Promise<void> {
  if (!props.connected || !props.workspacePath) {
    loaded.value = null
    path.value = ''
    patternsText.value = ''
    disabledText.value = ''
    originalPatterns.value = []
    originalDisabled.value = []
    originalFallback.value = false
    fallback.value = false
    legacyAllowed.value = []
    legacyDisabled.value = []
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
    originalDisabled.value = view.disabledPatterns
    originalFallback.value = view.fallback
    patternsText.value = view.patterns.join('\n')
    disabledText.value = view.disabledPatterns.join('\n')
    legacyAllowed.value = view.invalidPatterns
    legacyDisabled.value = view.invalidDisabledPatterns
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
  if (totalErrors.value > 0) {
    // 现场报错并拒绝保存：非法模式一旦落盘，用户会以为权限已经收紧/禁用生效。
    const parts: string[] = []
    if (validation.value.errors.length > 0) parts.push(`白名单 ${validation.value.errors.length} 处`)
    if (disabledValidation.value.errors.length > 0) parts.push(`硬禁用 ${disabledValidation.value.errors.length} 处`)
    error.value = `未保存：工具有 ${parts.join('、')}格式问题，修好后重试。`
    return
  }
  saving.value = true
  try {
    // 只把真正改动的字段放进候选文档，避免顺带写入从未配置过的空数组/默认值。
    // 一键清理历史非法条目时，即使编辑框内容没变也强制重写对应清单，非法项才会从文件里消失。
    const next: ConfigObject = { ...loaded.value }
    const clearAllowed = clearingLegacy.value && legacyAllowed.value.length > 0
    const clearDisabled = clearingLegacy.value && legacyDisabled.value.length > 0
    if (clearAllowed || !sameToolPatterns(validation.value.patterns, originalPatterns.value))
      next.allowedTools = validation.value.patterns
    if (clearDisabled || !sameToolPatterns(disabledValidation.value.patterns, originalDisabled.value))
      next.disabledTools = disabledValidation.value.patterns
    if (fallback.value !== originalFallback.value)
      next.allowToolFallbackToGlobal = fallback.value
    const patch = diffPatch(loaded.value, next)
    const result = await saveConfigPatch(patch, props.workspacePath)
    if (!result) {
      note.value = '没有改动。'
      return
    }
    await load()
    note.value = describeSaved(result)
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    saving.value = false
  }
}

/**
 * 一键清理磁盘上的历史非法条目：把编辑框里的有效清单原样写回，非法项不再出现在文件里。
 * 编辑框自身有格式错误时不允许清理——避免把用户刚输入但还没修正的条目一起丢掉。
 */
async function cleanupLegacy(): Promise<void> {
  if (!canEdit.value || saving.value || !loaded.value || totalErrors.value > 0) return
  clearingLegacy.value = true
  try {
    await save()
  } finally {
    clearingLegacy.value = false
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
        <strong><ShieldCheck :size="15" aria-hidden="true" /> 工具权限</strong>
        <small>项目级 <code>allowedTools</code> 白名单 + <code>disabledTools</code> 硬禁用，
          一行一条，支持精确名字（如 <code>read_file</code>）、MCP 的 <code>server/tool</code>
          与 <code>*</code> 通配（如 <code>github/*</code>、<code>*/create_issue</code>）。
          白名单留空 = 不限制；<strong>硬禁用优先级最高（deny-overrides）：命中即拒，白名单也放不回来。</strong>
          智能体与子智能体只能在此基础上继续收窄，无法放大。</small>
      </div>
      <button type="button" class="secondary-button" :disabled="!canEdit || loading" @click="load">
        <RefreshCw :size="14" /> 刷新
      </button>
    </div>

    <p v-if="!workspacePath" class="toolperm-note is-blocked">
      当前未打开工作区，因此这里只读：工具权限是<strong>项目级</strong>配置，只存在于
      &lt;工作区&gt;/.haoyue/config.json；没有打开项目就没有可读写的目标文件（全局配置里没有这两组字段）。
      请先在桌面端打开一个工作区，本面板会自动加载并开放编辑。
    </p>
    <p v-else-if="!connected" class="toolperm-note is-blocked">
      运行时未连接：读不到 &lt;工作区&gt;/.haoyue/config.json。请等待 daemon 连接后点「刷新」。
    </p>
    <p v-else-if="path" class="toolperm-path">{{ path }}</p>

    <p v-if="loading" class="toolperm-note"><LoaderCircle class="spin" :size="14" /> 正在读取项目配置</p>
    <p v-if="error" class="toolperm-note is-error">{{ error }}</p>
    <p v-else-if="note" class="toolperm-note is-ok">{{ note }}</p>

    <template v-if="canEdit">
      <template v-if="hasLegacy">
        <p class="toolperm-note is-warning">
          <AlertTriangle :size="14" aria-hidden="true" />
          <span>配置文件里有 {{ legacyInvalid.length }} 条历史遗留的非法条目（很可能是早先手工写入的），
            已从编辑框剔除，<strong>不会阻止其它字段保存</strong>；不清理则原样留在文件里，引擎仍会忽略它们。</span>
        </p>
        <ul class="toolperm-errors toolperm-legacy" role="alert">
          <li v-for="(item, index) in legacyInvalid" :key="index">{{ item }}</li>
        </ul>
        <div class="row-actions toolperm-legacy-actions">
          <button type="button" class="secondary-button"
            :disabled="saving || totalErrors > 0" @click="cleanupLegacy">
            <LoaderCircle v-if="saving && clearingLegacy" class="spin" :size="14" />
            <Trash2 v-else :size="14" />
            一键清理非法条目
          </button>
          <small v-if="totalErrors > 0">先修正编辑框里的格式问题，再清理磁盘上的非法条目。</small>
        </div>
      </template>

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

      <div class="toolperm-editor">
        <label for="tool-permissions-disabled">
          硬禁用模式 <span class="toolperm-deny-badge">最高优先级</span>
        </label>
        <textarea id="tool-permissions-disabled" v-model="disabledText" class="toolperm-textarea"
          rows="6" spellcheck="false" :disabled="saving"
          placeholder="bash&#10;github/delete_*&#10;mcp__dangerous__*" />
        <small class="toolperm-hint">
          已识别 {{ disabledValidation.patterns.length }} 条有效模式；命中任一条即拒绝该工具，
          优先于白名单与智能体/子智能体的工具清单。留空 = 不额外禁用。
        </small>
        <ul v-if="disabledValidation.errors.length" class="toolperm-errors" role="alert">
          <li v-for="item in disabledValidation.errors" :key="item">{{ item }}</li>
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
          :disabled="!dirty || saving || totalErrors > 0" @click="save">
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

.toolperm-note.is-blocked {
  padding: 8px 10px;
  border-radius: 8px;
  background: rgba(127, 127, 127, 0.1);
  color: var(--text-secondary, #555);
  line-height: 1.6;
}

.toolperm-note.is-warning {
  align-items: flex-start;
  color: var(--warning, #b7791f);
  line-height: 1.55;
}

.toolperm-deny-badge {
  margin-left: 4px;
  padding: 1px 6px;
  border-radius: 999px;
  background: rgba(214, 69, 69, 0.12);
  color: var(--danger, #d64545);
  font-size: 10px;
  font-weight: 600;
  vertical-align: 1px;
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

.toolperm-legacy {
  margin-top: 6px;
}

.toolperm-legacy-actions {
  margin-top: 8px;
  align-items: center;
  gap: 8px;
}

.toolperm-legacy-actions small {
  font-size: 11px;
  color: var(--text-tertiary, #888);
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
