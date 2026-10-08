<script setup lang="ts">
import { ArrowLeft, BookMarked, BookOpen, ChevronDown, ChevronRight, Download, FileText, FileUp, FolderOpen, Link2, LoaderCircle, Pencil, Plus, RefreshCw, Search, SlidersHorizontal, Trash2, X } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import type { Component } from 'vue'
import { confirmAction } from '../confirmation'
import SelectMenu from './SelectMenu.vue'

const props = defineProps<{
  open: boolean
}>()

const emit = defineEmits<{
  close: []
}>()

/** 知识条目（含笔记本/来源归属），由 daemon 的 knowledge.* 接口返回。 */
interface KnowledgeEntry {
  id: number
  title: string
  content: string
  tags?: string | null
  createdAt: string
  updatedAt: string
  notebookId?: number
  notebookName?: string | null
  sourceId?: number | null
  sourceTitle?: string | null
  sourceKind?: string | null
  sourceLocator?: string | null
  chunkOrdinal?: number | null
}

interface KnowledgePayload {
  workspace: string
  isGlobal: boolean
  count: number
  entries: KnowledgeEntry[]
}

interface KnowledgeNotebook {
  id: number
  name: string
  description?: string | null
  isDefault: boolean
  entryCount: number
  sourceCount: number
  createdAt: string
  updatedAt: string
}

interface NotebooksPayload {
  workspace: string
  isGlobal: boolean
  count: number
  notebooks: KnowledgeNotebook[]
}

type SourceKind = 'text' | 'url' | 'file'

interface KnowledgeSource {
  id: number
  notebookId: number
  kind: SourceKind
  title: string
  locator?: string | null
  chunkCount: number
  charCount: number
  createdAt: string
  updatedAt: string
  content?: string
}

interface SourcesPayload {
  workspace: string
  isGlobal: boolean
  notebookId: number
  count: number
  sources: KnowledgeSource[]
}

interface SourceReadPayload {
  workspace: string
  isGlobal: boolean
  source: KnowledgeSource
}

interface SourceActionPayload {
  workspace: string
  isGlobal: boolean
  notebookId: number
  source?: KnowledgeSource
  entries: number
}

interface ImportPayload extends KnowledgePayload {
  importedFiles: number
  importedEntries: number
}

interface TagCount {
  tag: string
  count: number
}

interface TagsPayload {
  workspace: string
  isGlobal: boolean
  total: number
  tags: TagCount[]
}

interface ExportPayload {
  workspace: string
  isGlobal: boolean
  count: number
  markdown: string
}

interface SynonymsPayload {
  path: string
  exists: boolean
  content: string
}

const SCOPE_OPTIONS = [
  { value: 'workspace', label: '当前工作区', description: '仅此工作区可用' },
  { value: 'global', label: '全局', description: '跨工作区共享' }
]

const SORT_OPTIONS = [
  { value: 'updated', label: '按更新时间' },
  { value: 'created', label: '按创建时间' },
  { value: 'title', label: '按标题' }
]

const CONTENT_LIMIT = 8000
/** 来源预览最多取回的字符数；完整正文靠后续窗口化读取按需扩量。 */
const SOURCE_PREVIEW_CHARS = 4000

const entries = ref<KnowledgeEntry[]>([])
const workspaceLabel = ref('')
const loading = ref(false)
const saving = ref(false)
const importing = ref(false)
const error = ref('')
const notice = ref('')
const scope = ref<'workspace' | 'global'>('workspace')
const query = ref('')
const sort = ref<'updated' | 'created' | 'title'>('updated')
const expandedIds = ref(new Set<number>())
/** null = 新增；数字 = 正在编辑的条目 id。 */
const editingId = ref<number | null>(null)
/** 表单显式开关：新增/编辑时为 true，避免「添加知识」清空表单后 v-if 失效。 */
const showForm = ref(false)
const form = reactive({ title: '', content: '', tags: '' })

// 标签面板：聚合当前范围的标签与计数，点击即精确筛选（不是子串搜索）。
const tagCounts = ref<TagCount[]>([])
const activeTag = ref('')
const exporting = ref(false)

// 同义词表编辑器：读取/保存 ~/.haoyue/knowledge/synonyms.txt，保存后检索立即生效。
const showSynonyms = ref(false)
const synonymsPath = ref('')
const synonymsContent = ref('')
const synonymsBaseline = ref('')
const savingSynonyms = ref(false)
const searchInput = ref<HTMLInputElement | null>(null)

// 知识中心三区：笔记本侧栏 → 来源 → 条目。
const notebooks = ref<KnowledgeNotebook[]>([])
const activeNotebookId = ref<number | null>(null)
const sources = ref<KnowledgeSource[]>([])
const expandedSourceIds = ref(new Set<number>())
const sourcePreviews = ref(new Map<number, string>())
const previewLoading = ref(new Set<number>())
const showNotebookForm = ref(false)
const notebookEditingId = ref<number | null>(null)
const savingNotebook = ref(false)
const notebookForm = reactive({ name: '', description: '' })
const sourceMode = ref<'' | 'text' | 'url'>('')
const addingSource = ref(false)
const sourceForm = reactive({ title: '', content: '', url: '', tags: '' })

const hasQuery = computed(() => query.value.trim().length > 0)
const contentLength = computed(() => form.content.length)
const formInvalid = computed(() => !form.title.trim() || !form.content.trim() || contentLength.value > CONTENT_LIMIT)
const totalEntries = computed(() => notebooks.value.reduce((sum, notebook) => sum + notebook.entryCount, 0))
const activeNotebook = computed(() =>
  activeNotebookId.value === null ? null : notebooks.value.find((notebook) => notebook.id === activeNotebookId.value) ?? null)

/** 搜索词列表（空格分词），用于结果高亮与本地兜底过滤。 */
const searchTerms = computed(() => query.value.trim().toLowerCase().split(/\s+/).filter(Boolean))

/** 列表排序：默认按更新时间（与服务端顺序一致），也可按创建时间或标题。 */
const sortedEntries = computed(() => {
  const list = [...entries.value]
  if (sort.value === 'title') {
    list.sort((a, b) => a.title.localeCompare(b.title, 'zh-Hans-CN'))
  } else {
    const key = sort.value === 'created' ? 'createdAt' : 'updatedAt'
    list.sort((a, b) => (b[key] ?? '').localeCompare(a[key] ?? ''))
  }
  return list
})

async function requestJson<T>(method: string, params: Record<string, unknown> = {}): Promise<T> {
  const response = await window.haoyue.daemon.request(method, params)
  return JSON.parse(response.data) as T
}

function scopeParams(): Record<string, unknown> {
  return scope.value === 'global' ? { global: true } : {}
}

/** 当前检索/列取的笔记本限定参数：选中笔记本时只看它。 */
function notebookParams(): Record<string, unknown> {
  return activeNotebookId.value !== null
    ? { notebookId: activeNotebookId.value, notebookIds: [activeNotebookId.value] }
    : {}
}

async function loadEntries(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    const tagParams = activeTag.value ? { tag: activeTag.value } : {}
    const payload = hasQuery.value
      ? await requestJson<KnowledgePayload>('knowledge.retrieve', {
          ...scopeParams(), query: query.value.trim(), limit: 50, ...tagParams, ...notebookParams()
        })
      : await requestJson<KnowledgePayload>('knowledge.list', { ...scopeParams(), ...tagParams, ...notebookParams() })
    entries.value = payload.entries
    workspaceLabel.value = payload.workspace
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    loading.value = false
  }
}

async function loadNotebooks(): Promise<void> {
  try {
    const payload = await requestJson<NotebooksPayload>('knowledge.notebook.list', scopeParams())
    notebooks.value = payload.notebooks
    workspaceLabel.value = payload.workspace
  } catch {
    notebooks.value = []
  }
}

async function loadSources(): Promise<void> {
  if (activeNotebookId.value === null) {
    sources.value = []
    return
  }
  try {
    const payload = await requestJson<SourcesPayload>('knowledge.source.list', {
      ...scopeParams(), notebookId: activeNotebookId.value
    })
    sources.value = payload.sources
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  }
}

/** 标签聚合是辅助视图：加载失败静默降级为空，不打断主列表。 */
async function loadTags(): Promise<void> {
  try {
    const payload = await requestJson<TagsPayload>('knowledge.tags', { ...scopeParams(), ...notebookParams() })
    tagCounts.value = payload.tags
  } catch {
    tagCounts.value = []
  }
}

async function refreshAll(): Promise<void> {
  await Promise.all([loadEntries(), loadTags(), loadNotebooks(), loadSources()])
}

/** 选中笔记本：条目与标签按笔记本过滤，搜索词保留继续在该笔记本内检索。 */
async function selectNotebook(id: number | null): Promise<void> {
  if (activeNotebookId.value === id) return
  activeNotebookId.value = id
  expandedIds.value = new Set()
  expandedSourceIds.value = new Set()
  sourcePreviews.value = new Map()
  await Promise.all([loadEntries(), loadSources(), loadTags()])
}

/** 点击标签：精确筛选该标签（再次点击取消），与服务端标签匹配语义一致。 */
async function toggleTag(tag: string): Promise<void> {
  activeTag.value = activeTag.value === tag ? '' : tag
  expandedIds.value = new Set()
  await loadEntries()
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
  const hadQuery = query.value.trim().length > 0
  query.value = ''
  activeTag.value = ''
  activeNotebookId.value = null
  sources.value = []
  expandedIds.value = new Set()
  expandedSourceIds.value = new Set()
  sourcePreviews.value = new Map()
  // 清空搜索词会触发 query watcher 重载列表；只有原本没有搜索词时才需要显式刷新。
  if (hadQuery) void Promise.all([loadTags(), loadNotebooks()])
  else void refreshAll()
})

/** 输入即搜：350ms 防抖触发服务端语义检索；清空时立即恢复完整列表。 */
let searchTimer: ReturnType<typeof setTimeout> | null = null
watch(query, (next, previous) => {
  if (!props.open || next === previous) return
  if (searchTimer) clearTimeout(searchTimer)
  if (!next.trim()) {
    void loadEntries()
    return
  }
  searchTimer = setTimeout(() => void loadEntries(), 350)
})

function clearSearch(): void {
  query.value = ''
}

function escapeHtml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
}

/**
 * 高亮文本中的检索命中片段：先转义再包 mark，避免注入。
 * 命中判定与内容一样走小写包含（与服务端归一化近似的本地兜底）。
 */
function highlightHtml(text: string): string {
  const escaped = escapeHtml(text)
  if (searchTerms.value.length === 0) return escaped
  let result = escaped
  for (const term of searchTerms.value) {
    if (!term) continue
    const safeTerm = escapeHtml(term).replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
    result = result.replace(new RegExp(safeTerm, 'gi'), (match) => `<mark>${match}</mark>`)
  }
  return result
}

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

// ────────────────────────────── 笔记本 ──────────────────────────────

function startCreateNotebook(): void {
  notebookEditingId.value = null
  showNotebookForm.value = true
  notebookForm.name = ''
  notebookForm.description = ''
}

function startRenameNotebook(notebook: KnowledgeNotebook): void {
  notebookEditingId.value = notebook.id
  showNotebookForm.value = true
  notebookForm.name = notebook.name
  notebookForm.description = notebook.description ?? ''
}

function cancelNotebookForm(): void {
  showNotebookForm.value = false
  notebookEditingId.value = null
}

async function saveNotebookForm(): Promise<void> {
  if (savingNotebook.value || !notebookForm.name.trim()) return
  savingNotebook.value = true
  error.value = ''
  try {
    const payload = await requestJson<NotebooksPayload>('knowledge.notebook.save', {
      ...scopeParams(),
      name: notebookForm.name.trim(),
      ...(notebookForm.description.trim() ? { description: notebookForm.description.trim() } : {}),
      ...(notebookEditingId.value !== null ? { id: notebookEditingId.value } : {})
    })
    notebooks.value = payload.notebooks
    workspaceLabel.value = payload.workspace
    notice.value = notebookEditingId.value !== null ? '笔记本已更新' : `笔记本「${notebookForm.name.trim()}」已创建`
    showNotebookForm.value = false
    notebookEditingId.value = null
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    savingNotebook.value = false
  }
}

async function removeNotebook(notebook: KnowledgeNotebook): Promise<void> {
  const confirmed = await confirmAction({
    title: '删除笔记本',
    message: `确定删除「${notebook.name}」？其中的 ${notebook.sourceCount} 个来源与 ${notebook.entryCount} 条知识将一并删除。`,
    confirmLabel: '删除',
    danger: true
  })
  if (!confirmed) return
  error.value = ''
  try {
    const payload = await requestJson<NotebooksPayload>('knowledge.notebook.delete', { ...scopeParams(), id: notebook.id })
    notebooks.value = payload.notebooks
    if (activeNotebookId.value === notebook.id) {
      activeNotebookId.value = null
      await Promise.all([loadEntries(), loadSources(), loadTags()])
    }
    notice.value = `笔记本「${notebook.name}」已删除`
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  }
}

// ────────────────────────────── 来源 ────────────────────────────────

function sourceIcon(kind: string | null | undefined): Component {
  if (kind === 'url') return Link2
  if (kind === 'file') return FileUp
  return FileText
}

function sourceKindLabel(kind: string): string {
  if (kind === 'url') return '网页'
  if (kind === 'file') return '文件'
  return '文本'
}

function toggleSourceMode(mode: 'text' | 'url'): void {
  sourceMode.value = sourceMode.value === mode ? '' : mode
  sourceForm.title = ''
  sourceForm.content = ''
  sourceForm.url = ''
  sourceForm.tags = ''
}

/** 展开来源时窗口化读取正文预览；收起仅折叠不重读。 */
async function toggleSource(id: number): Promise<void> {
  const next = new Set(expandedSourceIds.value)
  if (next.has(id)) {
    next.delete(id)
    expandedSourceIds.value = next
    return
  }
  next.add(id)
  expandedSourceIds.value = next
  if (sourcePreviews.value.has(id)) return
  const loadingSet = new Set(previewLoading.value)
  loadingSet.add(id)
  previewLoading.value = loadingSet
  try {
    const payload = await requestJson<SourceReadPayload>('knowledge.source.read', {
      ...scopeParams(), id, maxChars: SOURCE_PREVIEW_CHARS
    })
    const previews = new Map(sourcePreviews.value)
    previews.set(id, payload.source.content ?? '')
    sourcePreviews.value = previews
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    const settled = new Set(previewLoading.value)
    settled.delete(id)
    previewLoading.value = settled
  }
}

async function addTextSource(): Promise<void> {
  if (addingSource.value || activeNotebookId.value === null) return
  addingSource.value = true
  error.value = ''
  try {
    const payload = await requestJson<SourceActionPayload>('knowledge.source.add', {
      ...scopeParams(),
      kind: 'text',
      notebookId: activeNotebookId.value,
      title: sourceForm.title.trim(),
      content: sourceForm.content.trim(),
      ...(sourceForm.tags.trim() ? { tags: sourceForm.tags.trim() } : {})
    })
    notice.value = `已收录「${payload.source?.title ?? sourceForm.title.trim()}」，分块 ${payload.entries} 条入库`
    toggleSourceMode('text')
    await Promise.all([loadEntries(), loadSources(), loadNotebooks()])
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    addingSource.value = false
  }
}

async function addUrlSource(): Promise<void> {
  if (addingSource.value || activeNotebookId.value === null) return
  addingSource.value = true
  error.value = ''
  try {
    const payload = await requestJson<SourceActionPayload>('knowledge.source.add', {
      ...scopeParams(),
      kind: 'url',
      notebookId: activeNotebookId.value,
      url: sourceForm.url.trim()
    })
    notice.value = `已抓取「${payload.source?.title ?? sourceForm.url.trim()}」，分块 ${payload.entries} 条入库`
    toggleSourceMode('url')
    await Promise.all([loadEntries(), loadSources(), loadNotebooks()])
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    addingSource.value = false
  }
}

async function importFiles(): Promise<void> {
  if (importing.value) return
  const paths = await window.haoyue.selectFiles()
  if (paths.length === 0) return
  importing.value = true
  error.value = ''
  notice.value = ''
  try {
    if (activeNotebookId.value !== null) {
      const payload = await requestJson<SourceActionPayload>('knowledge.source.add', {
        ...scopeParams(), kind: 'file', notebookId: activeNotebookId.value, paths
      })
      notice.value = `已导入 ${paths.length} 个文件，分块 ${payload.entries} 条入库`
    } else {
      await requestJson<ImportPayload>('knowledge.import', { ...scopeParams(), paths })
      notice.value = `已从 ${paths.length} 个文件导入知识，Agent 即可检索到它们`
    }
    if (query.value.trim()) {
      // 导入后回到完整列表，避免新条目被旧搜索词过滤掉。
      query.value = ''
    } else {
      await loadEntries()
    }
    await Promise.all([loadTags(), loadNotebooks(), loadSources()])
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    importing.value = false
  }
}

async function refreshSource(source: KnowledgeSource): Promise<void> {
  error.value = ''
  try {
    const payload = await requestJson<SourceActionPayload>('knowledge.source.refresh', {
      ...scopeParams(), id: source.id
    })
    notice.value = `已刷新「${payload.source?.title ?? source.title}」，重建 ${payload.entries} 条分块`
    await Promise.all([loadEntries(), loadSources(), loadNotebooks()])
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  }
}

async function removeSource(source: KnowledgeSource): Promise<void> {
  const confirmed = await confirmAction({
    title: '删除来源',
    message: `确定删除来源「${source.title}」？它派生的 ${source.chunkCount} 条分块知识将一并删除，手工添加的条目保留。`,
    confirmLabel: '删除',
    danger: true
  })
  if (!confirmed) return
  error.value = ''
  try {
    await requestJson<SourcesPayload>('knowledge.source.delete', { ...scopeParams(), id: source.id })
    const previews = new Map(sourcePreviews.value)
    previews.delete(source.id)
    sourcePreviews.value = previews
    notice.value = `来源「${source.title}」已删除`
    await Promise.all([loadEntries(), loadSources(), loadNotebooks()])
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  }
}

// ────────────────────────────── 条目 ────────────────────────────────

async function saveForm(): Promise<void> {
  if (saving.value || formInvalid.value) return
  saving.value = true
  error.value = ''
  try {
    await requestJson<KnowledgePayload>('knowledge.save', {
      ...scopeParams(),
      // 编辑时带 id 原地更新（含改名），否则同标题编辑会分叉出新条目。
      ...(editingId.value !== null ? { id: editingId.value } : {}),
      // 新增到当前选中的笔记本；「全部知识」视图下交给服务端默认笔记本。
      ...(editingId.value === null && activeNotebookId.value !== null ? { notebookId: activeNotebookId.value } : {}),
      title: form.title.trim(),
      content: form.content.trim(),
      ...(form.tags.trim() ? { tags: form.tags.trim() } : {})
    })
    if (query.value.trim()) {
      // 保存后回到完整列表，避免新条目被旧搜索词过滤掉。
      query.value = ''
    } else {
      await loadEntries()
    }
    editingId.value = null
    showForm.value = false
    await Promise.all([loadTags(), loadNotebooks()])
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
    await requestJson<KnowledgePayload>('knowledge.delete', { ...scopeParams(), id: entry.id })
    // 删除后统一重载：payload 是全量列表，不能覆盖当前可能的筛选视图。
    if (query.value.trim()) query.value = ''
    else await loadEntries()
    await loadTags()
    // 被筛标签的最后一条被删时，清掉失效的筛选避免留下空列表。
    if (activeTag.value && !tagCounts.value.some((item) => item.tag === activeTag.value)) {
      activeTag.value = ''
      await loadEntries()
    }
    await loadNotebooks()
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  }
}

function formatTime(iso: string): string {
  const time = new Date(iso)
  return Number.isNaN(time.getTime()) ? iso : time.toLocaleString()
}

function exportFileName(): string {
  const now = new Date()
  const pad = (value: number): string => String(value).padStart(2, '0')
  return `haoyue-knowledge-${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}-${pad(now.getHours())}${pad(now.getMinutes())}.md`
}

/** 导出当前范围为 Markdown：daemon 只渲染文本，落盘走桌面端保存对话框。 */
async function exportKnowledge(): Promise<void> {
  if (exporting.value) return
  exporting.value = true
  error.value = ''
  try {
    const payload = await requestJson<ExportPayload>('knowledge.export', scopeParams())
    if (payload.count === 0) {
      notice.value = '当前范围还没有知识条目可导出'
      return
    }
    const path = await window.haoyue.saveTextFile(exportFileName(), payload.markdown)
    if (path) notice.value = `已导出 ${payload.count} 条知识到 ${path}`
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    exporting.value = false
  }
}

const synonymsDirty = computed(() => synonymsContent.value !== synonymsBaseline.value)

async function toggleSynonyms(): Promise<void> {
  if (showSynonyms.value) {
    await closeSynonyms()
    return
  }
  error.value = ''
  try {
    const payload = await requestJson<SynonymsPayload>('knowledge.synonyms.get')
    synonymsPath.value = payload.path
    synonymsContent.value = payload.content
    synonymsBaseline.value = payload.content
    showSynonyms.value = true
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  }
}

async function closeSynonyms(): Promise<void> {
  if (!synonymsDirty.value) {
    showSynonyms.value = false
    return
  }
  const confirmed = await confirmAction({
    title: '放弃同义词修改',
    message: '同义词表有未保存的修改，关闭后将丢失这些改动。',
    confirmLabel: '放弃修改'
  })
  if (confirmed) showSynonyms.value = false
}

async function saveSynonyms(): Promise<void> {
  if (savingSynonyms.value) return
  savingSynonyms.value = true
  error.value = ''
  try {
    const payload = await requestJson<SynonymsPayload>('knowledge.synonyms.save', { content: synonymsContent.value })
    synonymsContent.value = payload.content
    synonymsBaseline.value = payload.content
    notice.value = '同义词表已保存，检索立即生效（无需重启）'
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    savingSynonyms.value = false
  }
}

function openSynonymsFolder(): void {
  if (synonymsPath.value) void window.haoyue.showItemInFolder(synonymsPath.value)
}

watch(() => props.open, (open) => {
  if (!open) return
  scope.value = 'workspace'
  query.value = ''
  activeTag.value = ''
  tagCounts.value = []
  expandedIds.value = new Set()
  notice.value = ''
  showForm.value = false
  showSynonyms.value = false
  showNotebookForm.value = false
  sourceMode.value = ''
  activeNotebookId.value = null
  notebooks.value = []
  sources.value = []
  expandedSourceIds.value = new Set()
  sourcePreviews.value = new Map()
  editingId.value = null
  form.title = ''
  form.content = ''
  form.tags = ''
  void refreshAll()
})

function onKeydown(event: KeyboardEvent): void {
  if (!props.open) return
  if (event.key === 'Escape') {
    // Esc 分层：先收起同义词面板（有改动先确认），再收起来源/笔记本/条目表单，最后关闭页面。
    if (showSynonyms.value) {
      void closeSynonyms()
      return
    }
    if (sourceMode.value !== '') {
      toggleSourceMode(sourceMode.value)
      return
    }
    if (showNotebookForm.value) {
      cancelNotebookForm()
      return
    }
    if (showForm.value) {
      cancelEdit()
      return
    }
    emit('close')
    return
  }
  if (!(event.ctrlKey || event.metaKey)) return
  const key = event.key.toLowerCase()
  if (key === 'n' && !showForm.value && !showSynonyms.value && !showNotebookForm.value && sourceMode.value === '') {
    event.preventDefault()
    startCreate()
  } else if (key === 'f') {
    event.preventDefault()
    searchInput.value?.focus()
  }
}

onMounted(() => document.addEventListener('keydown', onKeydown))
onBeforeUnmount(() => document.removeEventListener('keydown', onKeydown))
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
            <BookOpen :size="17" /> 知识中心
          </h2>
          <div v-if="workspaceLabel" class="knowledge-stat-chips">
            <span class="stat-chip" :title="workspaceLabel">{{ scope === 'global' ? '全局范围' : workspaceLabel }}</span>
            <span class="stat-chip">{{ totalEntries }} 条知识</span>
            <span class="stat-chip">{{ notebooks.length }} 个笔记本</span>
          </div>
        </div>
      </div>
    </header>

    <div class="knowledge-body">
      <aside class="kb-sidebar">
        <div class="kb-sidebar-head">
          <strong>笔记本</strong>
          <button class="icon-button compact" title="新建笔记本" @click="startCreateNotebook">
            <Plus :size="14" />
          </button>
        </div>

        <form v-if="showNotebookForm" class="kb-notebook-form" @submit.prevent="saveNotebookForm">
          <input v-model="notebookForm.name" class="form-input" placeholder="笔记本名称" autocomplete="off" />
          <input v-model="notebookForm.description" class="form-input" placeholder="描述（可选）" autocomplete="off" />
          <div class="kb-notebook-form-actions">
            <button class="secondary-button" type="button" @click="cancelNotebookForm">取消</button>
            <button class="secondary-button primary-action" type="submit" :disabled="savingNotebook || !notebookForm.name.trim()">
              <LoaderCircle v-if="savingNotebook" class="spin" :size="13" /> 保存
            </button>
          </div>
        </form>

        <nav class="kb-notebook-list">
          <button type="button" class="kb-notebook-item" :class="{ active: activeNotebookId === null }"
            title="所有笔记本的知识与检索" @click="selectNotebook(null)">
            <BookOpen :size="14" />
            <span class="kb-notebook-name">全部知识</span>
            <span class="kb-notebook-count">{{ totalEntries }}</span>
          </button>
          <div v-for="notebook in notebooks" :key="notebook.id" class="kb-notebook-row"
            :class="{ active: activeNotebookId === notebook.id }">
            <button type="button" class="kb-notebook-item" :class="{ active: activeNotebookId === notebook.id }"
              :title="notebook.description || notebook.name" @click="selectNotebook(notebook.id)">
              <BookMarked :size="14" />
              <span class="kb-notebook-name">
                {{ notebook.name }}
                <em v-if="notebook.isDefault" class="kb-default-badge">默认</em>
              </span>
              <span class="kb-notebook-count">{{ notebook.entryCount }}</span>
            </button>
            <div v-if="activeNotebookId === notebook.id" class="kb-notebook-actions">
              <button class="icon-button compact" title="重命名或修改描述" @click="startRenameNotebook(notebook)">
                <Pencil :size="13" />
              </button>
              <button v-if="!notebook.isDefault" class="icon-button compact danger-icon" title="删除笔记本"
                @click="removeNotebook(notebook)">
                <Trash2 :size="13" />
              </button>
            </div>
          </div>
        </nav>

        <p class="kb-sidebar-hint">
          Agent 对话中沉淀的知识（knowledge_save）会进入默认笔记本；按主题新建笔记本，用来源和条目整理你的知识。
        </p>
      </aside>

      <div class="kb-main">
        <div class="knowledge-toolbar">
          <SelectMenu v-model="scope" :options="SCOPE_OPTIONS" label="知识库范围" class="knowledge-scope" />
          <label class="knowledge-search">
            <Search :size="16" />
            <input ref="searchInput" v-model="query"
              :placeholder="activeNotebookId !== null ? `在「${activeNotebook?.name ?? ''}」内检索（输入即搜）` : '跨笔记本检索（输入即搜）'"
              aria-label="检索知识" @keydown.enter="loadEntries" />
            <button v-if="hasQuery" class="knowledge-search-clear" title="清空检索" @click="clearSearch">
              <X :size="14" />
            </button>
          </label>
          <button class="icon-button" title="刷新" :disabled="loading" @click="loadEntries">
            <RefreshCw :size="16" :class="{ spinning: loading }" />
          </button>
          <button class="icon-button" title="导出为 Markdown 备份" :disabled="exporting" @click="exportKnowledge">
            <LoaderCircle v-if="exporting" class="spin" :size="16" />
            <Download v-else :size="16" />
          </button>
          <button class="icon-button" :class="{ toggled: showSynonyms }" title="搜索同义词表" @click="toggleSynonyms">
            <SlidersHorizontal :size="16" />
          </button>
          <button class="secondary-button knowledge-push" :disabled="saving || importing"
            @click="showForm ? cancelEdit() : startCreate()">
            <Plus v-if="!showForm" :size="15" /> {{ showForm ? '取消编辑' : '添加知识' }}
          </button>
          <SelectMenu v-model="sort" :options="SORT_OPTIONS" label="排序方式" class="knowledge-sort" />
        </div>

        <div v-if="tagCounts.length > 0" class="knowledge-tags" role="group" aria-label="按标签筛选">
          <button v-for="item in tagCounts" :key="item.tag" type="button" class="knowledge-tag-chip"
            :class="{ active: activeTag === item.tag }"
            :title="`精确筛选标签「${item.tag}」`" @click="toggleTag(item.tag)">
            {{ item.tag }}<span class="tag-count">{{ item.count }}</span>
          </button>
        </div>

        <form v-if="showForm" class="knowledge-form" @submit.prevent="saveForm">
          <strong>{{ editingId === null ? `新增知识${activeNotebookId !== null ? ` → ${activeNotebook?.name ?? ''}` : ''}` : `编辑知识 #${editingId}` }}</strong>
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

        <section v-if="showSynonyms" class="knowledge-synonyms">
          <div class="synonyms-head">
            <strong>搜索同义词表</strong>
            <span class="synonyms-path" :title="synonymsPath">{{ synonymsPath }}</span>
            <button class="icon-button compact" title="打开所在文件夹" @click="openSynonymsFolder">
              <FolderOpen :size="14" />
            </button>
          </div>
          <textarea v-model="synonymsContent" class="synonyms-input" rows="7" spellcheck="false"
            placeholder="# 每行一个同义词组，# 开头是注释&#10;部署 = 发布, 上线&#10;密钥: key" />
          <div class="synonyms-actions">
            <span class="synonyms-hint">让检索识别同义说法：每行「关键词 = 同义词1, 同义词2」（也支持冒号分隔），保存后立即生效。</span>
            <button class="secondary-button" type="button" @click="closeSynonyms">关闭</button>
            <button class="secondary-button primary-action" type="button" :disabled="savingSynonyms || !synonymsDirty" @click="saveSynonyms">
              <LoaderCircle v-if="savingSynonyms" class="spin" :size="14" /> 保存
            </button>
          </div>
        </section>

        <p v-if="error" class="knowledge-error">{{ error }}</p>
        <p v-else-if="notice" class="knowledge-notice">{{ notice }}</p>

        <section v-if="activeNotebookId !== null" class="kb-sources">
          <div class="kb-sources-head">
            <strong>来源</strong>
            <span class="kb-sources-meta">{{ sources.length }} 个</span>
            <div class="kb-sources-actions">
              <button class="icon-button compact" title="导入文件（自动分块，重复导入即刷新）" :disabled="importing"
                @click="importFiles">
                <LoaderCircle v-if="importing" class="spin" :size="14" />
                <FileUp v-else :size="14" />
              </button>
              <button class="icon-button compact" title="抓取网页入库" :class="{ toggled: sourceMode === 'url' }"
                @click="toggleSourceMode('url')">
                <Link2 :size="14" />
              </button>
              <button class="icon-button compact" title="收录文本" :class="{ toggled: sourceMode === 'text' }"
                @click="toggleSourceMode('text')">
                <FileText :size="14" />
              </button>
            </div>
          </div>

          <form v-if="sourceMode === 'url'" class="kb-source-url" @submit.prevent="addUrlSource">
            <input v-model="sourceForm.url" class="form-input" placeholder="https://…（抓取网页正文入库）" autocomplete="off" />
            <button class="secondary-button" type="submit" :disabled="addingSource || !sourceForm.url.trim()">
              <LoaderCircle v-if="addingSource" class="spin" :size="13" /> 抓取
            </button>
          </form>
          <form v-else-if="sourceMode === 'text'" class="kb-source-text" @submit.prevent="addTextSource">
            <input v-model="sourceForm.title" class="form-input" placeholder="文本来源标题" autocomplete="off" />
            <textarea v-model="sourceForm.content" class="form-input" rows="3"
              placeholder="要收录的文本，长文会自动分块入库并可被检索" />
            <div class="kb-source-text-row">
              <input v-model="sourceForm.tags" class="form-input" placeholder="标签（可选，逗号分隔）" autocomplete="off" />
              <button class="secondary-button" type="submit"
                :disabled="addingSource || !sourceForm.title.trim() || !sourceForm.content.trim()">
                <LoaderCircle v-if="addingSource" class="spin" :size="13" /> 收录
              </button>
            </div>
          </form>

          <div v-if="sources.length > 0" class="kb-source-items">
            <div v-for="source in sources" :key="source.id" class="kb-source-row">
              <button class="knowledge-expand" type="button"
                :aria-label="expandedSourceIds.has(source.id) ? '收起' : '展开'" @click="toggleSource(source.id)">
                <ChevronDown v-if="expandedSourceIds.has(source.id)" :size="14" />
                <ChevronRight v-else :size="14" />
              </button>
              <span class="kb-source-kind" :title="sourceKindLabel(source.kind)">
                <component :is="sourceIcon(source.kind)" :size="14" />
              </span>
              <div class="kb-source-main" @click="toggleSource(source.id)">
                <strong>{{ source.title }}</strong>
                <small>
                  {{ sourceKindLabel(source.kind) }} · {{ source.chunkCount }} 段 · {{ source.charCount }} 字
                  <span v-if="source.locator" class="kb-source-locator" :title="source.locator">{{ source.locator }}</span>
                </small>
                <p v-if="expandedSourceIds.has(source.id)" class="kb-source-preview">
                  <template v-if="previewLoading.has(source.id)">正在读取…</template>
                  <template v-else>{{ sourcePreviews.get(source.id) || '（空）' }}</template>
                </p>
              </div>
              <div class="kb-source-actions">
                <button class="icon-button compact" title="重新抓取/读取并重建分块" @click="refreshSource(source)">
                  <RefreshCw :size="13" />
                </button>
                <button class="icon-button compact danger-icon" title="删除来源" @click="removeSource(source)">
                  <Trash2 :size="13" />
                </button>
              </div>
            </div>
          </div>
          <p v-else-if="sourceMode === ''" class="kb-sources-empty">
            还没有来源。导入文件、抓取网页或收录文本，长文会自动分块入库并可被检索。
          </p>
        </section>

        <div class="knowledge-list">
          <div v-if="loading && entries.length === 0" class="knowledge-empty">
            <RefreshCw :size="30" class="spinning" />
            <strong>正在加载知识…</strong>
          </div>
          <div v-else-if="entries.length === 0" class="knowledge-empty">
            <BookOpen :size="34" />
            <strong>{{ hasQuery ? '没有匹配的知识' : '暂无知识条目' }}</strong>
            <span>{{ hasQuery ? '换个关键词试试。' : 'Agent 会在对话中自动沉淀经验（knowledge_save），也可以点「添加知识」手动记录，或在笔记本中导入文件、抓取网页。' }}</span>
          </div>
          <div v-else class="knowledge-items">
            <div v-for="entry in sortedEntries" :key="entry.id" class="knowledge-row">
              <button class="knowledge-expand" type="button" :aria-label="expandedIds.has(entry.id) ? '收起' : '展开'"
                @click="toggleExpanded(entry.id)">
                <ChevronDown v-if="expandedIds.has(entry.id)" :size="15" />
                <ChevronRight v-else :size="15" />
              </button>
              <div class="knowledge-main" @click="toggleExpanded(entry.id)">
                <div class="knowledge-row-head">
                  <strong v-html="highlightHtml(entry.title)" />
                  <span v-if="activeNotebookId === null && entry.notebookName" class="inline-badge kb-attribution"
                    :title="`来自笔记本「${entry.notebookName}」`">
                    <BookMarked :size="11" /> {{ entry.notebookName }}
                  </span>
                  <span v-if="entry.sourceId && entry.sourceTitle && entry.sourceTitle !== entry.title"
                    class="inline-badge kb-attribution"
                    :title="entry.sourceLocator ? `来源：${entry.sourceLocator}` : '来自收录来源'">
                    <component :is="sourceIcon(entry.sourceKind)" :size="11" /> {{ entry.sourceTitle }}
                  </span>
                  <button v-for="tag in (entry.tags ?? '').split(/[,，;；]/).map((tag) => tag.trim()).filter(Boolean)"
                    :key="tag" class="inline-badge knowledge-tag" type="button"
                    :title="`精确筛选标签「${tag}」`" @click.stop="toggleTag(tag)">{{ tag }}</button>
                </div>
                <p v-if="!expandedIds.has(entry.id)" class="clamped" v-html="highlightHtml(entry.content)" />
                <p v-else v-html="highlightHtml(entry.content)" />
                <small>
                  更新于 {{ formatTime(entry.updatedAt) }}
                  <template v-if="expandedIds.has(entry.id)">
                    · 创建于 {{ formatTime(entry.createdAt) }} · {{ entry.content.length }} 字
                  </template>
                </small>
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
      </div>
    </div>

    <footer class="knowledge-footer">
      知识中心：笔记本 → 来源（文件 / 网页 / 文本）→ 条目。Agent 对话中自动沉淀与检索（knowledge_save / knowledge_search），来源内容更新后点「重新抓取」即可重建分块
    </footer>
  </section>
</template>

<style scoped>
.knowledge-dialog.embedded-page {
  display: grid;
  width: 100%;
  height: 100%;
  grid-template-rows: auto minmax(0, 1fr) auto;
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

/* 标题下方的统计 chips（范围、知识数与笔记本数），与 MCP 页 stat-chip 风格统一。 */
.knowledge-stat-chips {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  margin-top: 7px;
}

/* 三区主体：左侧笔记本栏 + 右侧（来源/条目）滚动列。 */
.knowledge-body {
  display: grid;
  grid-template-columns: 236px minmax(0, 1fr);
  min-height: 0;
  overflow: hidden;
}

.kb-sidebar {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px;
  overflow-y: auto;
  background: var(--surface-raised);
  border-right: 1px solid var(--border);
}

.kb-sidebar-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.kb-sidebar-head strong {
  font-size: 12.5px;
  letter-spacing: .02em;
}

.kb-notebook-form {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px;
  background: color-mix(in srgb, var(--accent) 5%, var(--surface));
  border: 1px solid color-mix(in srgb, var(--accent) 22%, var(--border));
  border-radius: 10px;
}

.kb-notebook-form .form-input {
  box-sizing: border-box;
  width: 100%;
  min-height: 32px;
  padding: 6px 10px;
  color: var(--text);
  font-size: 12.5px;
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 8px;
  outline: none;
  transition: border-color 140ms ease, box-shadow 140ms ease;
}

.kb-notebook-form .form-input:focus {
  border-color: color-mix(in srgb, var(--accent) 66%, var(--border));
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 20%, transparent);
}

.kb-notebook-form-actions {
  display: flex;
  justify-content: flex-end;
  gap: 6px;
}

.kb-notebook-list {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.kb-notebook-row {
  border-radius: 8px;
}

.kb-notebook-row.active {
  background: color-mix(in srgb, var(--accent) 8%, transparent);
}

.kb-notebook-item {
  display: flex;
  width: 100%;
  align-items: center;
  gap: 8px;
  min-height: 32px;
  padding: 5px 8px;
  color: var(--text-secondary);
  font-size: 12.5px;
  text-align: left;
  background: transparent;
  border: none;
  border-radius: 8px;
  cursor: pointer;
  transition: color 120ms ease, background 120ms ease;
}

.kb-notebook-item:hover {
  color: var(--text);
  background: var(--surface-hover);
}

.kb-notebook-item.active {
  color: var(--accent);
  background: color-mix(in srgb, var(--accent) 10%, transparent);
}

.kb-notebook-name {
  display: inline-flex;
  flex: 1;
  min-width: 0;
  align-items: center;
  gap: 5px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.kb-default-badge {
  flex: 0 0 auto;
  padding: 0 5px;
  color: var(--text-muted);
  font-size: 10px;
  font-style: normal;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 999px;
}

.kb-notebook-count {
  flex: 0 0 auto;
  color: var(--text-muted);
  font-size: 11px;
}

.kb-notebook-actions {
  display: flex;
  gap: 2px;
  padding: 0 4px 5px 30px;
}

.kb-sidebar-hint {
  margin: auto 0 0;
  padding-top: 8px;
  color: var(--text-muted);
  font-size: 11px;
  line-height: 1.55;
}

.kb-main {
  display: flex;
  flex-direction: column;
  min-width: 0;
  overflow-y: auto;
}

.knowledge-toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 10px;
  padding: 14px 22px 6px;
}

.knowledge-scope {
  width: 150px;
  flex: 0 0 auto;
}

/* 排序方式选择器，靠右与工具栏动作同层。 */
.knowledge-sort {
  width: 128px;
  flex: 0 0 auto;
}

.knowledge-search-clear {
  display: flex;
  flex: 0 0 auto;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  color: var(--text-muted);
  background: transparent;
  border: none;
  border-radius: 5px;
  cursor: pointer;
  transition: color 120ms ease, background 120ms ease;
}

.knowledge-search-clear:hover {
  color: var(--text);
  background: var(--surface-hover);
}

/* 搜索命中高亮：跟随主题强调色，弱化背景保持可读。 */
.knowledge-main :deep(mark) {
  color: var(--accent);
  font-weight: 600;
  background: color-mix(in srgb, var(--accent) 16%, transparent);
  border-radius: 3px;
  padding: 0 1px;
}

/* 标签可点击筛选：视觉沿用 inline-badge，交互上给出按钮反馈。 */
.knowledge-tag {
  cursor: pointer;
  transition: background 120ms ease, border-color 120ms ease;
}

.knowledge-tag:hover {
  background: color-mix(in srgb, var(--accent) 16%, transparent);
  border-color: color-mix(in srgb, var(--accent) 45%, var(--border));
}

/* 工具栏下方的标签聚合行：点击精确筛选，激活态用强调色标记。 */
.knowledge-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  padding: 10px 22px 0;
}

.knowledge-tag-chip {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 3px 10px;
  color: var(--text-secondary);
  font-size: 11.5px;
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 999px;
  cursor: pointer;
  transition: color 120ms ease, background 120ms ease, border-color 120ms ease;
}

.knowledge-tag-chip:hover {
  color: var(--text);
  background: var(--surface-hover);
}

.knowledge-tag-chip.active {
  color: var(--accent);
  background: color-mix(in srgb, var(--accent) 14%, transparent);
  border-color: color-mix(in srgb, var(--accent) 45%, var(--border));
}

.tag-count {
  color: var(--text-muted);
  font-size: 10.5px;
}

.knowledge-tag-chip.active .tag-count {
  color: var(--accent);
  opacity: .75;
}

/* 条目归属徽标：笔记本与来源出处在「全部知识」视图下展示。 */
.kb-attribution {
  display: inline-flex;
  flex: 0 0 auto;
  align-items: center;
  gap: 4px;
  max-width: 260px;
  overflow: hidden;
  color: var(--text-muted);
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* 同义词表编辑器：与新增/编辑表单同一视觉语言，独立开关。 */
.knowledge-synonyms {
  display: flex;
  flex-direction: column;
  gap: 10px;
  margin: 12px 22px 0;
  padding: 14px;
  background: color-mix(in srgb, var(--accent) 5%, var(--surface-raised));
  border: 1px solid color-mix(in srgb, var(--accent) 22%, var(--border));
  border-radius: 12px;
}

.synonyms-head {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
}

.synonyms-head strong {
  flex: 0 0 auto;
  font-size: 13px;
}

.synonyms-path {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--text-muted);
  font-size: 11px;
  text-align: left;
  text-overflow: ellipsis;
  white-space: nowrap;
  direction: rtl;
}

.synonyms-input {
  box-sizing: border-box;
  width: 100%;
  padding: 9px 12px;
  color: var(--text);
  font-size: 12.5px;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  line-height: 1.6;
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 8px;
  outline: none;
  resize: vertical;
  transition: border-color 140ms ease, box-shadow 140ms ease;
}

.synonyms-input:focus {
  border-color: color-mix(in srgb, var(--accent) 66%, var(--border));
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 20%, transparent);
}

.synonyms-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.synonyms-hint {
  flex: 1;
  min-width: 0;
  color: var(--text-muted);
  font-size: 11px;
  line-height: 1.5;
}

.icon-button.toggled {
  color: var(--accent);
  background: color-mix(in srgb, var(--accent) 12%, transparent);
}

.knowledge-search {
  display: flex;
  flex: 1;
  min-width: 200px;
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

/* 「添加知识」动作组推到工具栏右侧，与搜索区分层。 */
.knowledge-push {
  margin-left: auto;
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
  min-width: 0;
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

/* 来源区：只在选中具体笔记本时出现，展示该笔记本的收录单元。 */
.kb-sources {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin: 12px 22px 0;
  padding: 12px 14px;
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 12px;
}

.kb-sources-head {
  display: flex;
  align-items: center;
  gap: 8px;
}

.kb-sources-head strong {
  font-size: 13px;
}

.kb-sources-meta {
  color: var(--text-muted);
  font-size: 11px;
}

.kb-sources-actions {
  display: flex;
  gap: 4px;
  margin-left: auto;
}

.kb-source-url,
.kb-source-text {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px;
  background: color-mix(in srgb, var(--accent) 5%, var(--surface));
  border: 1px solid color-mix(in srgb, var(--accent) 22%, var(--border));
  border-radius: 10px;
}

.kb-source-url {
  flex-direction: row;
  align-items: center;
}

.kb-source-url .form-input,
.kb-source-text .form-input {
  box-sizing: border-box;
  width: 100%;
  min-height: 34px;
  padding: 6px 10px;
  color: var(--text);
  font-size: 12.5px;
  font-family: inherit;
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 8px;
  outline: none;
  transition: border-color 140ms ease, box-shadow 140ms ease;
}

.kb-source-url .form-input:focus,
.kb-source-text .form-input:focus {
  border-color: color-mix(in srgb, var(--accent) 66%, var(--border));
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 20%, transparent);
}

.kb-source-text textarea.form-input {
  resize: vertical;
  line-height: 1.55;
}

.kb-source-url .form-input {
  flex: 1;
  min-width: 0;
}

.kb-source-text-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.kb-source-text-row .form-input {
  flex: 1;
  min-width: 0;
}

.kb-source-items {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.kb-source-row {
  display: flex;
  align-items: flex-start;
  gap: 6px;
  padding: 8px 8px 8px 4px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 10px;
  transition: background 140ms ease, border-color 140ms ease;
}

.kb-source-row:hover {
  background: var(--surface-hover);
}

.kb-source-kind {
  display: flex;
  flex: 0 0 auto;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 26px;
  color: var(--text-muted);
}

.kb-source-main {
  flex: 1;
  min-width: 0;
  cursor: pointer;
}

.kb-source-main strong {
  display: block;
  overflow-wrap: anywhere;
  font-size: 12.5px;
}

.kb-source-main small {
  display: flex;
  flex-wrap: wrap;
  gap: 2px 6px;
  margin-top: 2px;
  color: var(--text-muted);
  font-size: 11px;
}

.kb-source-locator {
  max-width: 320px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.kb-source-preview {
  margin: 6px 0 0;
  padding: 8px 10px;
  color: var(--text-secondary);
  font-size: 12px;
  line-height: 1.6;
  white-space: pre-wrap;
  word-break: break-word;
  background: var(--bg);
  border: 1px solid var(--border);
  border-radius: 8px;
}

.kb-source-actions {
  display: flex;
  flex: 0 0 auto;
  gap: 2px;
}

.kb-sources-empty {
  margin: 0;
  padding: 14px;
  color: var(--text-muted);
  font-size: 12px;
  line-height: 1.6;
  border: 1px dashed var(--border);
  border-radius: 10px;
}

.knowledge-list {
  min-height: 0;
  padding: 14px 22px 18px;
}

.knowledge-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  padding: 48px 20px;
  color: var(--text-muted);
  text-align: center;
  border: 1px dashed var(--border);
  border-radius: 12px;
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
  gap: 10px;
}

.knowledge-row {
  display: flex;
  align-items: flex-start;
  gap: 6px;
  padding: 12px 12px 12px 6px;
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 10px;
  transition: background 140ms ease, border-color 140ms ease;
}

.knowledge-row:hover {
  background: var(--surface-hover);
  border-color: color-mix(in srgb, var(--border-strong) 70%, var(--border));
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
  transition: color 120ms ease, background 120ms ease;
}

.knowledge-expand:hover {
  color: var(--text);
  background: var(--surface-hover);
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
  overflow-wrap: anywhere;
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

/* 窄窗口：笔记本栏折叠为顶部横条，搜索框独占一行。 */
@media (max-width: 760px) {
  .knowledge-body {
    grid-template-columns: minmax(0, 1fr);
    grid-template-rows: auto minmax(0, 1fr);
  }

  .kb-sidebar {
    gap: 8px;
    padding: 10px 16px;
    border-right: none;
    border-bottom: 1px solid var(--border);
  }

  .kb-notebook-list {
    flex-direction: row;
    flex-wrap: wrap;
    gap: 6px;
  }

  .kb-notebook-item {
    width: auto;
    min-height: 28px;
    padding: 3px 10px;
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: 999px;
  }

  .kb-notebook-actions,
  .kb-sidebar-hint {
    display: none;
  }

  .knowledge-header {
    padding: 10px 16px;
  }

  .knowledge-toolbar {
    padding: 12px 16px 6px;
  }

  .knowledge-search {
    flex: 1 1 100%;
    min-width: 0;
  }

  .knowledge-tags {
    padding: 8px 16px 0;
  }

  .knowledge-synonyms,
  .kb-sources {
    margin: 12px 16px 0;
  }

  .knowledge-push {
    margin-left: 0;
  }

  .knowledge-form {
    margin: 12px 16px 0;
  }

  .knowledge-error,
  .knowledge-notice {
    margin: 10px 16px 0;
  }

  .knowledge-list {
    padding: 12px 16px 14px;
  }

  .knowledge-row {
    padding: 10px 10px 10px 4px;
  }

  .knowledge-footer {
    padding: 10px 16px;
  }
}
</style>
