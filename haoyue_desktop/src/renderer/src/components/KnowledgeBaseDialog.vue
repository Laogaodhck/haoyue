<script setup lang="ts">
import { ArrowLeft, BookOpen, ChevronDown, ChevronRight, FileUp, LoaderCircle, Pencil, Plus, RefreshCw, Search, Trash2 } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { confirmAction } from '../confirmation'
import SelectMenu from './SelectMenu.vue'

const props = defineProps<{
  open: boolean
}>()

const emit = defineEmits<{
  close: []
}>()

/** 知识条目，由 daemon 的 knowledge.* 接口返回。 */
interface KnowledgeEntry {
  id: number
  title: string
  content: string
  tags?: string | null
  createdAt: string
  updatedAt: string
}

interface KnowledgePayload {
  workspace: string
  isGlobal: boolean
  count: number
  entries: KnowledgeEntry[]
}

const SCOPE_OPTIONS = [
  { value: 'workspace', label: '当前工作区', description: '仅此工作区可用' },
  { value: 'global', label: '全局', description: '跨工作区共享' }
]

const CONTENT_LIMIT = 8000

const entries = ref<KnowledgeEntry[]>([])
const workspaceLabel = ref('')
const loading = ref(false)
const saving = ref(false)
const importing = ref(false)
const error = ref('')
const notice = ref('')
const scope = ref<'workspace' | 'global'>('workspace')
const query = ref('')
const expandedIds = ref(new Set<number>())
/** null = 新增；数字 = 正在编辑的条目 id。 */
const editingId = ref<number | null>(null)
/** 表单显式开关：新增/编辑时为 true，避免「添加知识」清空表单后 v-if 失效。 */
const showForm = ref(false)
const form = reactive({ title: '', content: '', tags: '' })

interface ImportPayload extends KnowledgePayload {
  importedFiles: number
  importedEntries: number
}

const hasQuery = computed(() => query.value.trim().length > 0)
const contentLength = computed(() => form.content.length)
const formInvalid = computed(() => !form.title.trim() || !form.content.trim() || contentLength.value > CONTENT_LIMIT)

async function requestJson<T>(method: string, params: Record<string, unknown> = {}): Promise<T> {
  const response = await window.haoyue.daemon.request(method, params)
  return JSON.parse(response.data) as T
}

function scopeParams(): Record<string, unknown> {
  return scope.value === 'global' ? { global: true } : {}
}

async function loadEntries(): Promise<void> {
  loading.value = true
  error.value = ''
  notice.value = ''
  try {
    const payload = hasQuery.value
      ? await requestJson<KnowledgePayload>('knowledge.search', { ...scopeParams(), query: query.value.trim() })
      : await requestJson<KnowledgePayload>('knowledge.list', scopeParams())
    entries.value = payload.entries
    workspaceLabel.value = payload.workspace
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    loading.value = false
  }
}

function toggleExpanded(id: number): void {
  const next = new Set(expandedIds.value)
  if (next.has(id)) next.delete(id)
  else next.add(id)
  expandedIds.value = next
}

// SelectMenu 的 v-model 直接更新 scope；这里只负责切换后刷新数据。
watch(scope, (next, previous) => {
  if (previous === next || !props.open) return
  query.value = ''
  expandedIds.value = new Set()
  void loadEntries()
})

function startCreate(): void {
  editingId.value = null
  showForm.value = true
  form.title = ''
  form.content = ''
  form.tags = ''
}

function startEdit(entry: KnowledgeEntry): void {
  editingId.value = entry.id
  showForm.value = true
  form.title = entry.title
  form.content = entry.content
  form.tags = entry.tags ?? ''
  expandedIds.value = new Set([...expandedIds.value, entry.id])
}

function cancelEdit(): void {
  editingId.value = null
  showForm.value = false
}

async function importFiles(): Promise<void> {
  if (importing.value) return
  const paths = await window.haoyue.selectFiles()
  if (paths.length === 0) return
  importing.value = true
  error.value = ''
  notice.value = ''
  try {
    const payload = await requestJson<ImportPayload>('knowledge.import', { ...scopeParams(), paths })
    entries.value = payload.entries
    workspaceLabel.value = payload.workspace
    notice.value = `已从 ${payload.importedFiles} 个文件导入 ${payload.importedEntries} 条知识，Agent 即可检索到它们`
    if (query.value.trim()) {
      // 导入后回到完整列表，避免新条目被旧搜索词过滤掉。
      query.value = ''
    }
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    importing.value = false
  }
}

async function saveForm(): Promise<void> {
  if (saving.value || formInvalid.value) return
  saving.value = true
  error.value = ''
  try {
    const payload = await requestJson<KnowledgePayload>('knowledge.save', {
      ...scopeParams(),
      title: form.title.trim(),
      content: form.content.trim(),
      ...(form.tags.trim() ? { tags: form.tags.trim() } : {})
    })
    entries.value = payload.entries
    workspaceLabel.value = payload.workspace
    if (query.value.trim()) {
      // 保存后回到完整列表，避免新条目被旧搜索词过滤掉。
      query.value = ''
    }
    editingId.value = null
    showForm.value = false
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    saving.value = false
  }
}

async function removeEntry(entry: KnowledgeEntry): Promise<void> {
  const confirmed = await confirmAction({
    title: '删除知识条目',
    message: `确定删除「${entry.title}」？Agent 之后将无法再检索到这条知识。`,
    confirmLabel: '删除',
    danger: true
  })
  if (!confirmed) return
  error.value = ''
  try {
    const payload = await requestJson<KnowledgePayload>('knowledge.delete', { ...scopeParams(), id: entry.id })
    entries.value = payload.entries
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  }
}

function formatTime(iso: string): string {
  const time = new Date(iso)
  return Number.isNaN(time.getTime()) ? iso : time.toLocaleString()
}

watch(() => props.open, (open) => {
  if (!open) return
  scope.value = 'workspace'
  query.value = ''
  expandedIds.value = new Set()
  notice.value = ''
  showForm.value = false
  editingId.value = null
  form.title = ''
  form.content = ''
  form.tags = ''
  void loadEntries()
})

function closeOnEscape(event: KeyboardEvent): void {
  if (props.open && event.key === 'Escape') emit('close')
}

onMounted(() => document.addEventListener('keydown', closeOnEscape))
onBeforeUnmount(() => document.removeEventListener('keydown', closeOnEscape))
</script>

<template>
  <section v-if="open" class="knowledge-dialog embedded-page" role="region" aria-labelledby="knowledge-title">
    <header class="knowledge-header">
      <div class="knowledge-heading">
        <button class="page-back-button" type="button" @click="emit('close')">
          <ArrowLeft :size="18" />
          <span>返回应用</span>
        </button>
        <div class="knowledge-title-copy">
          <h2 id="knowledge-title">
            <BookOpen :size="17" /> 知识库
          </h2>
          <p v-if="workspaceLabel" :title="workspaceLabel">{{ scope === 'global' ? '全局范围' : workspaceLabel }} · {{ entries.length }} 条</p>
        </div>
      </div>
    </header>

    <div class="knowledge-toolbar">
      <SelectMenu v-model="scope" :options="SCOPE_OPTIONS" label="知识库范围" class="knowledge-scope" />
      <label class="knowledge-search">
        <Search :size="16" />
        <input v-model="query" placeholder="搜索标题、内容与标签" aria-label="搜索知识" @keydown.enter="loadEntries" />
      </label>
      <button class="icon-button" title="搜索" :disabled="loading" @click="loadEntries">
        <Search :size="16" />
      </button>
      <button class="icon-button" title="刷新" :disabled="loading" @click="loadEntries">
        <RefreshCw :size="16" :class="{ spinning: loading }" />
      </button>
      <button class="secondary-button" :disabled="saving || importing" @click="showForm ? cancelEdit() : startCreate()">
        <Plus v-if="!showForm" :size="15" /> {{ showForm ? '取消编辑' : '添加知识' }}
      </button>
      <button class="secondary-button" :disabled="importing || saving" @click="importFiles">
        <LoaderCircle v-if="importing" class="spin" :size="15" />
        <FileUp v-else :size="15" /> 导入文件
      </button>
    </div>

    <form v-if="showForm" class="knowledge-form" @submit.prevent="saveForm">
      <strong>{{ editingId === null ? '新增知识' : `编辑知识 #${editingId}` }}</strong>
      <input v-model="form.title" class="form-input" placeholder="标题（保存后同名更新，作为稳定键）" autocomplete="off" />
      <textarea v-model="form.content" class="form-input" rows="5"
        placeholder="知识内容：事实、决策、命令、路径、踩坑与修复方式。保持自包含，让下次对话无需上下文即可理解。" />
      <div class="knowledge-form-row">
        <input v-model="form.tags" class="form-input" placeholder="标签（可选，逗号分隔，如 build,偏好）" autocomplete="off" />
        <span class="knowledge-counter" :class="{ over: contentLength > CONTENT_LIMIT }">{{ contentLength }} / {{ CONTENT_LIMIT }}</span>
      </div>
      <div class="knowledge-form-actions">
        <button class="secondary-button" type="button" @click="cancelEdit">取消</button>
        <button class="secondary-button primary-action" type="submit" :disabled="saving || formInvalid">
          <LoaderCircle v-if="saving" class="spin" :size="14" /> 保存
        </button>
      </div>
    </form>

    <p v-if="error" class="knowledge-error">{{ error }}</p>
    <p v-else-if="notice" class="knowledge-notice">{{ notice }}</p>

    <div class="knowledge-list">
      <div v-if="loading && entries.length === 0" class="knowledge-empty">
        <RefreshCw :size="30" class="spinning" />
        <strong>正在加载知识…</strong>
      </div>
      <div v-else-if="entries.length === 0" class="knowledge-empty">
        <BookOpen :size="34" />
        <strong>{{ hasQuery ? '没有匹配的知识' : '暂无知识条目' }}</strong>
        <span>{{ hasQuery ? '换个关键词试试。' : 'Agent 会在对话中自动沉淀经验（knowledge_save），也可以点「添加知识」手动记录。' }}</span>
      </div>
      <div v-else class="knowledge-items">
        <div v-for="entry in entries" :key="entry.id" class="knowledge-row">
          <button class="knowledge-expand" type="button" :aria-label="expandedIds.has(entry.id) ? '收起' : '展开'"
            @click="toggleExpanded(entry.id)">
            <ChevronDown v-if="expandedIds.has(entry.id)" :size="15" />
            <ChevronRight v-else :size="15" />
          </button>
          <div class="knowledge-main" @click="toggleExpanded(entry.id)">
            <div class="knowledge-row-head">
              <strong>{{ entry.title }}</strong>
              <span v-for="tag in (entry.tags ?? '').split(',').map((tag) => tag.trim()).filter(Boolean)"
                :key="tag" class="inline-badge">{{ tag }}</span>
            </div>
            <p :class="{ clamped: !expandedIds.has(entry.id) }">{{ entry.content }}</p>
            <small>更新于 {{ formatTime(entry.updatedAt) }}</small>
          </div>
          <div class="knowledge-row-actions">
            <button class="icon-button compact" title="编辑" @click="startEdit(entry)">
              <Pencil :size="14" />
            </button>
            <button class="icon-button compact danger-icon" title="删除" @click="removeEntry(entry)">
              <Trash2 :size="14" />
            </button>
          </div>
        </div>
      </div>
    </div>

    <footer class="knowledge-footer">
      知识库由 Agent 在对话中自动沉淀与检索（knowledge_save / knowledge_search / knowledge_forget），也可在此手动维护
    </footer>
  </section>
</template>

<style scoped>
.knowledge-dialog.embedded-page {
  display: grid;
  width: 100%;
  height: 100%;
  grid-template-rows: auto auto minmax(0, 1fr) auto;
  min-height: 0;
  overflow: hidden;
  background: var(--bg);
}

.knowledge-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 20px;
  min-height: 62px;
  padding: 10px 22px;
  background: var(--surface-raised);
  border-bottom: 1px solid var(--border);
}

.knowledge-heading {
  display: flex;
  min-width: 0;
  align-items: center;
  gap: 12px;
}

.knowledge-title-copy {
  min-width: 0;
}

.knowledge-title-copy h2 {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0;
  font-size: 16px;
  font-weight: 650;
  letter-spacing: -.01em;
}

.knowledge-title-copy p {
  max-width: 560px;
  margin: 2px 0 0;
  overflow: hidden;
  color: var(--text-secondary);
  font-size: 11px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.knowledge-toolbar {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 16px 22px 4px;
}

.knowledge-scope {
  width: 150px;
  flex: 0 0 auto;
}

.knowledge-search {
  display: flex;
  flex: 1;
  min-width: 0;
  align-items: center;
  gap: 8px;
  padding: 0 12px;
  color: var(--text-muted);
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 9px;
}

.knowledge-search input {
  width: 100%;
  min-height: 36px;
  color: var(--text);
  font-size: 13px;
  background: transparent;
  border: none;
  outline: none;
}

.knowledge-form {
  display: flex;
  flex-direction: column;
  gap: 10px;
  margin: 12px 22px 0;
  padding: 14px;
  background: color-mix(in srgb, var(--accent) 5%, var(--surface-raised));
  border: 1px solid color-mix(in srgb, var(--accent) 22%, var(--border));
  border-radius: 12px;
}

.knowledge-form strong {
  font-size: 13px;
}

.knowledge-form-row {
  display: flex;
  align-items: center;
  gap: 12px;
}

.knowledge-form-row .form-input {
  flex: 1;
}

.knowledge-counter {
  flex: 0 0 auto;
  color: var(--text-muted);
  font-size: 11.5px;
}

.knowledge-form .form-input {
  box-sizing: border-box;
  width: 100%;
  min-height: 36px;
  padding: 7px 12px;
  color: var(--text);
  font-size: 13px;
  font-family: inherit;
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 8px;
  outline: none;
  transition: border-color 140ms ease, box-shadow 140ms ease;
}

.knowledge-form textarea.form-input {
  resize: vertical;
  line-height: 1.55;
}

.knowledge-form .form-input:focus {
  border-color: color-mix(in srgb, var(--accent) 66%, var(--border));
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 20%, transparent);
}

.knowledge-counter.over {
  color: var(--danger);
}

.knowledge-form-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}

.knowledge-error {
  margin: 10px 22px 0;
  padding: 9px 12px;
  color: var(--danger);
  font-size: 12px;
  background: color-mix(in srgb, var(--danger) 9%, transparent);
  border: 1px solid color-mix(in srgb, var(--danger) 25%, transparent);
  border-radius: 8px;
}

.knowledge-notice {
  margin: 10px 22px 0;
  padding: 9px 12px;
  color: var(--accent);
  font-size: 12px;
  background: color-mix(in srgb, var(--accent) 9%, transparent);
  border: 1px solid color-mix(in srgb, var(--accent) 25%, transparent);
  border-radius: 8px;
}

.knowledge-list {
  min-height: 0;
  padding: 14px 22px 18px;
  overflow-y: auto;
}

.knowledge-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  padding: 60px 20px;
  color: var(--text-muted);
  text-align: center;
}

.knowledge-empty strong {
  color: var(--text);
  font-size: 14px;
}

.knowledge-empty span {
  max-width: 420px;
  font-size: 12px;
  line-height: 1.6;
}

.knowledge-items {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.knowledge-row {
  display: flex;
  align-items: flex-start;
  gap: 4px;
  padding: 12px 12px 12px 6px;
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 10px;
}

.knowledge-row:hover {
  background: var(--surface-hover);
}

.knowledge-expand {
  display: flex;
  flex: 0 0 auto;
  align-items: center;
  justify-content: center;
  width: 26px;
  height: 28px;
  color: var(--text-muted);
  background: transparent;
  border: none;
  border-radius: 6px;
  cursor: pointer;
}

.knowledge-main {
  flex: 1;
  min-width: 0;
  cursor: pointer;
}

.knowledge-row-head {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}

.knowledge-row-head strong {
  font-size: 13.5px;
}

.knowledge-main p {
  margin: 5px 0 0;
  color: var(--text-secondary);
  font-size: 12.5px;
  line-height: 1.6;
  white-space: pre-wrap;
  word-break: break-word;
}

.knowledge-main p.clamped {
  display: -webkit-box;
  overflow: hidden;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
}

.knowledge-main small {
  display: inline-block;
  margin-top: 5px;
  color: var(--text-muted);
  font-size: 11px;
}

.knowledge-row-actions {
  display: flex;
  flex: 0 0 auto;
  gap: 2px;
}

.knowledge-footer {
  padding: 11px 22px;
  color: var(--text-muted);
  font-size: 11.5px;
  background: var(--surface-raised);
  border-top: 1px solid var(--border);
}
</style>
