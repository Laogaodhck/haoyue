<script setup lang="ts">
import { Bot, LoaderCircle, Plus, RefreshCw, Save, Trash2, X } from '@lucide/vue'
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { confirmAction } from '../confirmation'
import { requestJson } from '../daemon-json'
import SelectMenu from './SelectMenu.vue'

/**
 * 设置 → 智能体 → 子智能体。
 *
 * 与 Qoder 的自定义智能体一致：每个子智能体是一份 Markdown 文件，frontmatter 声明
 * name/description/model/tools，正文即工作指令。用户级放在 ~/.haoyue/agents，
 * 项目级放在 <workspace>/.haoyue/agents；项目级覆盖用户级，自定义同名条目覆盖内置。
 * 运行时每次委派都重新读盘，所以这里保存后无需重启 daemon。
 */
const props = defineProps<{ connected: boolean; workspacePath?: string }>()

type Scope = 'builtin' | 'user' | 'project'

interface SubAgentRecord {
  id: string
  name: string
  description: string
  prompt: string
  model: string | null
  tools: string[]
  scope: Scope
  path: string | null
  readOnly: boolean
}

const TOOL_OPTIONS = [
  { name: 'read_file', label: '读取文件' },
  { name: 'write_file', label: '写入文件' },
  { name: 'edit_file', label: '编辑文件' },
  { name: 'list_dir', label: '列目录' },
  { name: 'glob', label: '文件匹配' },
  { name: 'grep', label: '内容搜索' },
  { name: 'bash', label: 'Bash' },
  { name: 'terminal', label: '终端' },
  { name: 'web_search', label: '网页搜索' },
  { name: 'web_fetch', label: '网页抓取' },
  { name: 'knowledge_search', label: '知识检索' },
  { name: 'knowledge_save', label: '知识保存' },
  { name: 'fact_search', label: '事实检索' },
  { name: 'fact_save', label: '事实保存' },
  { name: 'memory_save', label: '记忆保存' },
  { name: 'update_plan', label: '更新计划' },
  { name: 'capture_screen', label: '截屏' },
  { name: 'image_generate', label: '图像生成' },
  { name: 'doc_render', label: '文档渲染' },
  { name: 'browser', label: '浏览器' },
]

const agents = ref<SubAgentRecord[]>([])
const modelOptions = ref<Array<{ value: string; label: string; description?: string }>>([
  { value: '', label: '继承活动模型', description: '使用智能体当前的活动模型' }
])
const loading = ref(false)
const busy = ref('')
const error = ref('')
const note = ref('')
const showEditor = ref(false)
const editing = ref<SubAgentRecord | null>(null)
const extraTool = ref('')

const form = reactive({
  name: '',
  description: '',
  model: '',
  tools: [] as string[],
  prompt: '',
  scope: 'user' as 'user' | 'project'
})

const scopeOptions = computed(() => [
  { value: 'user', label: '用户级', description: '~/.haoyue/agents，所有工作区可用' },
  {
    value: 'project',
    label: '项目级',
    description: props.workspacePath ? '.haoyue/agents，仅当前工作区可用且覆盖同名用户级' : '需要先打开一个工作区',
    disabled: !props.workspacePath
  }
])

const customTools = computed(() => form.tools.filter((tool) => !TOOL_OPTIONS.some((option) => option.name === tool)))

function scopeLabel(scope: Scope): string {
  return scope === 'builtin' ? '内置' : scope === 'project' ? '项目级' : '用户级'
}

function describeTools(tools: string[]): string {
  const names = tools
    .map((tool) => TOOL_OPTIONS.find((option) => option.name === tool)?.label ?? tool)
    .slice(0, 6)
  return tools.length > names.length ? `${names.join('、')} 等 ${tools.length} 个` : names.join('、')
}

async function load(): Promise<void> {
  if (!props.connected) return
  loading.value = true
  error.value = ''
  try {
    const params = props.workspacePath ? { workspace: props.workspacePath } : {}
    const result = await requestJson<{ agents: SubAgentRecord[] }>('agent.subagents.list', params)
    agents.value = result.agents ?? []
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
    agents.value = []
  } finally {
    loading.value = false
  }
}

async function loadModels(): Promise<void> {
  try {
    const catalog = await requestJson<Array<{ ref: string; providerEnabled: boolean }>>('model.catalog')
    modelOptions.value = [
      { value: '', label: '继承活动模型', description: '使用智能体当前的活动模型' },
      ...catalog
        .filter((model) => model.providerEnabled)
        .map((model) => ({ value: model.ref, label: model.ref }))
    ]
  } catch {
    // 模型目录不可用时仍可编辑名称/工具/指令，模型保持继承。
  }
}

function startCreate(): void {
  editing.value = null
  form.name = ''
  form.description = ''
  form.model = ''
  form.tools = []
  form.prompt = ''
  form.scope = 'user'
  extraTool.value = ''
  error.value = ''
  note.value = ''
  showEditor.value = true
}

function startEdit(agent: SubAgentRecord): void {
  editing.value = agent
  form.name = agent.name
  form.description = agent.description === '自定义子智能体' ? '' : agent.description
  form.model = agent.model ?? ''
  form.tools = [...agent.tools]
  form.prompt = agent.prompt
  form.scope = agent.scope === 'project' ? 'project' : 'user'
  extraTool.value = ''
  error.value = ''
  note.value = ''
  showEditor.value = true
}

function addExtraTool(): void {
  const name = extraTool.value.trim()
  if (!name) return
  if (!form.tools.some((tool) => tool.toLowerCase() === name.toLowerCase())) form.tools.push(name)
  extraTool.value = ''
}

function removeTool(tool: string): void {
  form.tools = form.tools.filter((entry) => entry !== tool)
}

async function save(): Promise<void> {
  if (!form.name.trim()) {
    error.value = '请填写子智能体名称。'
    return
  }
  if (!form.prompt.trim()) {
    error.value = '请填写工作指令（Markdown 正文）。'
    return
  }
  busy.value = 'save'
  error.value = ''
  note.value = ''
  try {
    await requestJson('agent.subagents.save', {
      workspace: props.workspacePath,
      scope: form.scope,
      name: form.name.trim(),
      description: form.description.trim(),
      model: form.model,
      tools: [...form.tools],
      prompt: form.prompt,
      previousName: editing.value?.id ?? ''
    })
    note.value = `已保存子智能体「${form.name.trim()}」。下一次 delegate_task 立即生效。`
    showEditor.value = false
    editing.value = null
    await load()
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    busy.value = ''
  }
}

async function remove(agent: SubAgentRecord): Promise<void> {
  if (agent.readOnly) return
  if (!await confirmAction({
    title: '删除子智能体',
    message: `删除${scopeLabel(agent.scope)}子智能体「${agent.name}」？对应的 Markdown 文件会被删除，此操作无法撤销。`,
    confirmLabel: '删除',
    danger: true
  })) return
  busy.value = `delete:${agent.id}`
  error.value = ''
  note.value = ''
  try {
    await requestJson('agent.subagents.delete', {
      workspace: props.workspacePath,
      scope: agent.scope,
      name: agent.id
    })
    note.value = `已删除「${agent.name}」；同名的内置预设（若有）会重新生效。`
    if (editing.value?.id === agent.id) {
      showEditor.value = false
      editing.value = null
    }
    await load()
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    busy.value = ''
  }
}

onMounted(() => {
  void load()
  void loadModels()
})

watch(() => props.connected, (connected) => {
  if (connected) {
    void load()
    void loadModels()
  }
})
watch(() => props.workspacePath, () => { void load() })
</script>

<template>
  <section class="settings-group" aria-label="子智能体">
    <div class="settings-group-title subagent-head">
      <div>
        <strong>子智能体</strong>
        <small>delegate_task / delegate_tasks 可用的角色。自定义条目是 Markdown 文件：frontmatter 声明
          名称、描述、模型与工具白名单，正文即工作指令；项目级覆盖用户级，自定义同名条目覆盖内置。</small>
      </div>
      <div class="subagent-head-actions">
        <button type="button" class="secondary-button" :disabled="loading" @click="load">
          <RefreshCw :size="14" /> 刷新
        </button>
        <button type="button" class="secondary-button primary-action" @click="startCreate">
          <Plus :size="14" /> 新建子智能体
        </button>
      </div>
    </div>

    <p v-if="error" class="subagent-note is-error">{{ error }}</p>
    <p v-else-if="note" class="subagent-note is-ok">{{ note }}</p>
    <p v-if="loading" class="subagent-note"><LoaderCircle class="spin" :size="14" /> 正在读取子智能体目录</p>

    <div v-else class="subagent-list" role="list">
      <div v-for="agent in agents" :key="`${agent.scope}:${agent.id}`" class="subagent-row" role="listitem">
        <div class="subagent-main">
          <div class="subagent-name">
            <Bot :size="14" aria-hidden="true" />
            <strong>{{ agent.name }}</strong>
            <span class="inline-badge" :class="`scope-${agent.scope}`">{{ scopeLabel(agent.scope) }}</span>
            <span v-if="agent.model" class="subagent-model">{{ agent.model }}</span>
          </div>
          <small>{{ agent.description || '未填写描述；模型只能按名称判断是否使用。' }}</small>
          <small v-if="agent.tools.length" class="subagent-tools">工具：{{ describeTools(agent.tools) }}</small>
          <small v-else class="subagent-tools">工具：不限制（继承默认工具视图）</small>
        </div>
        <div class="row-actions">
          <button v-if="!agent.readOnly" type="button" class="secondary-button compact-button"
            :disabled="busy !== ''" @click="startEdit(agent)">编辑</button>
          <button v-if="!agent.readOnly" type="button" class="secondary-button compact-button danger-button"
            :disabled="busy !== ''" @click="remove(agent)">
            <LoaderCircle v-if="busy === `delete:${agent.id}`" class="spin" :size="13" />
            <Trash2 v-else :size="13" /> 删除
          </button>
          <span v-else class="subagent-readonly">只读</span>
        </div>
      </div>
    </div>

    <div v-if="agents.length === 0 && !loading" class="subagent-empty">
      暂无可用的子智能体；内置预设通常至少包含 researcher / implementer / reviewer。
    </div>
  </section>

  <section v-if="showEditor" class="settings-group subagent-editor" aria-label="编辑子智能体">
    <div class="settings-group-title">
      <strong>{{ editing ? `编辑「${editing.name}」` : '新建子智能体' }}</strong>
      <small>保存后写入 {{ form.scope === 'project' ? '.haoyue/agents' : '~/.haoyue/agents' }}；
        运行时在下一次 delegate_task 时重新读盘，无需重启。</small>
    </div>

    <div class="subagent-fields">
      <label>
        <span>名称</span>
        <input v-model="form.name" type="text" maxlength="64" placeholder="例如 code-explorer" />
      </label>
      <label>
        <span>描述</span>
        <input v-model="form.description" type="text" placeholder="模型据此判断何时委派；写清楚适用场景" />
      </label>
      <label>
        <span>模型</span>
        <SelectMenu v-model="form.model" :options="modelOptions" label="子智能体模型" :menu-min-width="320" searchable />
      </label>
      <label v-if="!editing">
        <span>作用域</span>
        <SelectMenu v-model="form.scope" :options="scopeOptions" label="保存位置" :menu-min-width="300" />
      </label>
    </div>

    <div class="subagent-tools-field">
      <span class="subagent-field-label">工具白名单</span>
      <small>不勾选任何工具 = 不限制。勾选后子智能体只能使用这些工具（含 delegate_task 也会被收窄）。</small>
      <div class="subagent-tool-grid">
        <label v-for="tool in TOOL_OPTIONS" :key="tool.name" class="subagent-tool">
          <input v-model="form.tools" type="checkbox" :value="tool.name" />
          <span>{{ tool.label }}</span>
          <code>{{ tool.name }}</code>
        </label>
      </div>
      <div class="subagent-extra-tool">
        <input v-model="extraTool" type="text" placeholder="其他工具名（如 MCP 工具）" @keyup.enter.prevent="addExtraTool" />
        <button type="button" class="secondary-button compact-button" @click="addExtraTool">
          <Plus :size="13" /> 添加
        </button>
      </div>
      <div v-if="customTools.length" class="subagent-tool-chips">
        <span v-for="tool in customTools" :key="tool" class="tool-chip">
          {{ tool }}
          <button type="button" title="移除" @click="removeTool(tool)"><X :size="11" /></button>
        </span>
      </div>
    </div>

    <label class="subagent-prompt">
      <span class="subagent-field-label">工作指令</span>
      <textarea v-model="form.prompt" rows="8"
        placeholder="描述子智能体的视角、方法、纪律与输出要求。子任务描述会追加在这段指令之后。" />
    </label>

    <div class="subagent-actions">
      <button type="button" class="secondary-button primary-action" :disabled="busy === 'save'" @click="save">
        <LoaderCircle v-if="busy === 'save'" class="spin" :size="14" />
        <Save v-else :size="14" /> 保存
      </button>
      <button type="button" class="secondary-button" :disabled="busy === 'save'" @click="showEditor = false">
        取消
      </button>
    </div>
  </section>
</template>

<style scoped>
.subagent-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
}
.subagent-head-actions {
  display: flex;
  gap: 8px;
  flex-shrink: 0;
}
.subagent-note {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 8px 0 0;
  color: var(--text-muted);
  font-size: 12px;
}
.subagent-note.is-error { color: var(--danger); }
.subagent-note.is-ok { color: var(--success, var(--accent)); }
.subagent-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-top: 10px;
}
.subagent-row {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 14px;
  padding: 10px 12px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: var(--surface-2, var(--surface));
}
.subagent-main {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}
.subagent-main small {
  color: var(--text-muted);
  font-size: 12px;
  overflow-wrap: anywhere;
}
.subagent-name {
  display: flex;
  align-items: center;
  gap: 7px;
  flex-wrap: wrap;
}
.subagent-model {
  font-family: var(--font-mono, monospace);
  font-size: 11px;
  color: var(--text-muted);
}
.scope-builtin { opacity: 0.75; }
.scope-user { color: var(--accent); border-color: color-mix(in srgb, var(--accent) 35%, transparent); }
.scope-project { color: var(--warning, #d97706); border-color: color-mix(in srgb, var(--warning, #d97706) 35%, transparent); }
.subagent-readonly {
  font-size: 12px;
  color: var(--text-muted);
}
.subagent-empty {
  margin-top: 10px;
  color: var(--text-muted);
  font-size: 12px;
}
.subagent-editor {
  margin-top: 14px;
}
.subagent-fields {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
  gap: 12px;
  margin-top: 10px;
}
.subagent-fields label {
  display: flex;
  flex-direction: column;
  gap: 5px;
  font-size: 12px;
  color: var(--text-muted);
}
.subagent-fields input[type='text'],
.subagent-extra-tool input,
.subagent-prompt textarea {
  width: 100%;
  padding: 7px 9px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--surface);
  color: var(--text);
  font: inherit;
}
.subagent-prompt textarea {
  resize: vertical;
  line-height: 1.5;
}
.subagent-tools-field {
  margin-top: 14px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.subagent-tools-field > small {
  color: var(--text-muted);
  font-size: 12px;
}
.subagent-field-label {
  font-size: 12px;
  font-weight: 600;
  color: var(--text);
}
.subagent-tool-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(170px, 1fr));
  gap: 4px 10px;
  margin-top: 2px;
}
.subagent-tool {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--text-muted);
}
.subagent-tool code {
  font-size: 11px;
  opacity: 0.7;
}
.subagent-extra-tool {
  display: flex;
  gap: 8px;
  margin-top: 4px;
}
.subagent-extra-tool input { flex: 1; }
.subagent-tool-chips {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}
.tool-chip {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 2px 7px;
  border-radius: 999px;
  border: 1px solid var(--border);
  font-size: 11px;
  font-family: var(--font-mono, monospace);
}
.tool-chip button {
  display: inline-flex;
  padding: 0;
  border: 0;
  background: none;
  color: var(--text-muted);
  cursor: pointer;
}
.subagent-prompt {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-top: 14px;
}
.subagent-actions {
  display: flex;
  gap: 8px;
  margin-top: 12px;
}
.danger-button { color: var(--danger); }
</style>
