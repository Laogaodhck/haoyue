<script setup lang="ts">
import { daemonRequest } from '../daemon-rpc'
import {
  Archive,
  Blocks,
  BookOpen,
  CalendarClock,
  ChevronDown,
  ChevronRight,
  Clock,
  Folder,
  FolderCog,
  FolderPlus,
  Info,
  LoaderCircle,
  MoreHorizontal,
  Plus,
  Search,
  Settings2,
  SlidersHorizontal,
  SquarePen,
  Trash2
} from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import logoUrl from '../../../../resources/logo.png?url'
import type { ProjectItem, SessionSearchHit, ThreadItem } from '../types'
import { capTaskList, filterUnloadedHits, shouldSearchRemotely } from '../sidebar-search'
import { groupThreadsByTime, type SessionGroup } from '../session-groups'

const props = defineProps<{
  projects: ProjectItem[]
  threads: ThreadItem[]
  activeThreadId: string
  activeProjectId?: string
  version: string
}>()

const emit = defineEmits<{
  newTask: [projectId?: string]
  openWorkspace: []
  selectThread: [id: string]
  openSession: [hit: SessionSearchHit]
  taskSettings: [thread: ThreadItem]
  renameTask: [thread: ThreadItem, title: string]
  archiveTask: [thread: ThreadItem]
  deleteTask: [thread: ThreadItem]
  openProjectProperties: [project: ProjectItem]
  deleteProject: [project: ProjectItem]
  initializeProjectWorkspace: [project: ProjectItem]
  archiveProjectTasks: [project: ProjectItem]
  deleteProjectTasks: [project: ProjectItem]
  archiveGlobalTasks: []
  deleteGlobalTasks: []
  openArchived: []
  openScheduledTasks: []
  openExtensions: []
  openKnowledge: []
  openSettings: []
}>()

const searching = ref(false)
const query = ref('')
const searchInput = ref<HTMLInputElement | null>(null)

// H11: autofocus inside a Transition is unreliable — focus programmatically instead.
function focusSearch(): void {
  searching.value = true
  void nextTick(() => searchInput.value?.focus())
}

defineExpose({ focusSearch })
const taskSectionExpanded = ref(true)
const expandedProjects = ref(new Set<string>())

/** 项目区在时间视图下默认折叠，但保留标题与加项目入口。 */
const projectSectionExpanded = ref(true)
const primaryTaskScopeHint = computed(() => props.activeProjectId
  ? `在项目「${projectDisplayName(props.activeProjectId) || props.activeProjectId}」中新建任务`
  : '新建任务（不绑定项目）')

// ---------------------------------------------------------------- 豆包式「按时间」会话视图
// 侧栏会话列表的两种组织轴：按项目（默认，工作区配置的组织单位）与按时间
// （豆包式历史会话管理：今天/昨天/近 7 天/近 30 天/更早）。选择持久化到 localStorage。
const SIDEBAR_VIEW_KEY = 'haoyue-sidebar-view'
const viewMode = ref<'projects' | 'time'>(
  localStorage.getItem(SIDEBAR_VIEW_KEY) === 'time' ? 'time' : 'projects')

watch(viewMode, (value) => {
  try { localStorage.setItem(SIDEBAR_VIEW_KEY, value) } catch { /* 配额满不影响使用 */ }
  // 时间视图里任务已按时间平铺，项目区只保留标题，默认收起省空间（可手动展开）。
  projectSectionExpanded.value = value === 'projects'
}, { immediate: true })

function projectDisplayName(projectId?: string): string {
  if (!projectId) return ''
  return props.projects.find((project) => project.id === projectId)?.name ?? ''
}

const timeGroups = computed<Array<SessionGroup & { list: { rows: ThreadItem[]; hidden: number } }>>(() => {
  if (viewMode.value !== 'time') return []
  return groupThreadsByTime(props.threads).map((group) => ({
    ...group,
    list: buildTaskList(`time:${group.key}`, group.rows)
  }))
})

function expandTimeGroup(key: string): void {
  expandTaskList(`time:${key}`)
}
const menuKey = ref('')
const menuPlacement = ref<'up' | 'down'>('down')
const knownThreadIds = new Set<string>()

// H4: inline rename — the 3-step heavy dialog is overkill for the highest-frequency
// lightweight action. Double-click / F2 turns the row into an input; commit on
// Enter/blur, cancel on Escape, rollback on save failure (handled by the parent).
const renamingThreadId = ref('')
const renameValue = ref('')
const renameInput = ref<HTMLInputElement | null>(null)

function startRename(thread: ThreadItem): void {
  renamingThreadId.value = thread.id
  renameValue.value = thread.title
  void nextTick(() => renameInput.value?.focus())
}

function commitRename(thread: ThreadItem): void {
  if (renamingThreadId.value !== thread.id) return
  renamingThreadId.value = ''
  const title = renameValue.value.trim()
  if (!title || title === thread.title) return
  emit('renameTask', thread, title)
}

watch(() => props.projects, (items) => {
  const next = new Set(expandedProjects.value)
  items.forEach((project) => next.add(project.id))
  expandedProjects.value = next
}, { immediate: true, deep: true })

watch(() => props.threads.map((thread) => thread.id), (ids) => {
  const current = new Set(ids)
  for (const id of ids) {
    if (knownThreadIds.has(id)) continue
    const thread = props.threads.find((item) => item.id === id)
    if (!thread) continue
    if (thread.projectId) {
      const next = new Set(expandedProjects.value)
      next.add(thread.projectId)
      expandedProjects.value = next
    } else {
      taskSectionExpanded.value = true
    }
  }
  knownThreadIds.clear()
  current.forEach((id) => knownThreadIds.add(id))
}, { immediate: true })

const normalizedQuery = computed(() => query.value.trim().toLocaleLowerCase())

// 跨会话全文检索：本地列表只覆盖已加载的项目/全局任务，键入 >=2 字且本地无匹配时
// 调 session.search 补搜全部会话（含未加载项目与归档），命中已加载的本地过滤掉。
const remoteHits = ref<SessionSearchHit[]>([])
const remoteSearching = ref(false)
const remoteError = ref('')
let remoteSearchSeq = 0
let remoteSearchTimer: ReturnType<typeof setTimeout> | undefined

function clearRemoteSearch(): void {
  remoteSearchSeq += 1
  remoteHits.value = []
  remoteSearching.value = false
  remoteError.value = ''
}

async function runRemoteSearch(term: string): Promise<void> {
  const seq = ++remoteSearchSeq
  remoteSearching.value = true
  remoteError.value = ''
  try {
    const response = await daemonRequest('session.search', {
      query: term,
      limit: 20,
      includeArchived: true,
      all: true
    })
    if (seq !== remoteSearchSeq) return
    const hits = JSON.parse(response.data) as SessionSearchHit[]
    const knownSessionIds = new Set(
      props.threads.map((thread) => thread.sessionId).filter((id): id is string => Boolean(id))
    )
    remoteHits.value = filterUnloadedHits(hits, knownSessionIds)
  } catch (error) {
    if (seq !== remoteSearchSeq) return
    remoteHits.value = []
    remoteError.value = error instanceof Error ? error.message : String(error)
  } finally {
    if (seq === remoteSearchSeq) remoteSearching.value = false
  }
}

watch(normalizedQuery, (value) => {
  if (remoteSearchTimer) clearTimeout(remoteSearchTimer)
  if (!shouldSearchRemotely(value)) {
    clearRemoteSearch()
    return
  }
  remoteSearchTimer = setTimeout(() => void runRemoteSearch(value), 300)
})

interface TaskList {
  rows: ThreadItem[]
  hidden: number
}

const openTaskLists = ref<Set<string>>(new Set())

function buildTaskList(key: string, source: ThreadItem[]): TaskList {
  return capTaskList(source, {
    query: normalizedQuery.value,
    expanded: openTaskLists.value.has(key),
    activeThreadId: props.activeThreadId
  })
}

function expandTaskList(key: string): void {
  const next = new Set(openTaskLists.value)
  next.add(key)
  openTaskLists.value = next
}

const globalTaskList = computed(() => buildTaskList('global', props.threads.filter((thread) => !thread.projectId)))
const projectTaskLists = computed(() => {
  const lists = new Map<string, TaskList>()
  for (const project of props.projects) {
    lists.set(project.id, buildTaskList(`project:${project.id}`, props.threads.filter((thread) => thread.projectId === project.id)))
  }
  return lists
})

function visibleThreads(projectId: string): ThreadItem[] {
  return projectTaskLists.value.get(projectId)?.rows ?? []
}

function projectHasRunningTask(projectId: string): boolean {
  return props.threads.some((thread) => thread.projectId === projectId && !thread.archived && thread.running)
}

function createTask(projectId?: string): void {
  if (projectId) {
    const next = new Set(expandedProjects.value)
    next.add(projectId)
    expandedProjects.value = next
  } else {
    taskSectionExpanded.value = true
  }
  emit('newTask', projectId)
}

function toggleProject(project: ProjectItem): void {
  const next = new Set(expandedProjects.value)
  if (next.has(project.id)) next.delete(project.id)
  else next.add(project.id)
  expandedProjects.value = next
}

function toggleMenu(key: string, event?: MouseEvent): void {
  if (menuKey.value === key) {
    menuKey.value = ''
    return
  }

  const trigger = event?.currentTarget instanceof HTMLElement ? event.currentTarget : null
  const row = trigger?.closest<HTMLElement>('.thread-heading-row, .project-heading-row')
  const section = trigger?.closest<HTMLElement>('.sidebar-scroll')
  const triggerRect = trigger?.getBoundingClientRect()
  const rowRect = row?.getBoundingClientRect()
  const sectionRect = section?.getBoundingClientRect()
  const menuHeight = 190
  const spaceBelow = sectionRect && triggerRect ? sectionRect.bottom - triggerRect.bottom : Number.POSITIVE_INFINITY
  const spaceAbove = sectionRect && rowRect ? rowRect.top - sectionRect.top : Number.POSITIVE_INFINITY
  menuPlacement.value = spaceBelow < menuHeight && spaceAbove > spaceBelow ? 'up' : 'down'
  menuKey.value = key
}

function runAction(action: () => void): void {
  menuKey.value = ''
  action()
}

function closeMenuWhenFocusLeaves(event: Event): void {
  const target = event.target
  if (!(target instanceof Element) || !target.closest('.sidebar-context-menu, .row-menu-button'))
    menuKey.value = ''
}

function closeMenuOnEscape(event: KeyboardEvent): void {
  if (event.key === 'Escape') menuKey.value = ''
}

onMounted(() => {
  document.addEventListener('pointerdown', closeMenuWhenFocusLeaves)
  document.addEventListener('focusin', closeMenuWhenFocusLeaves)
  document.addEventListener('keydown', closeMenuOnEscape)
  window.addEventListener('blur', closeMenuWhenFocusLeaves)
})

onBeforeUnmount(() => {
  if (remoteSearchTimer) clearTimeout(remoteSearchTimer)
  document.removeEventListener('pointerdown', closeMenuWhenFocusLeaves)
  document.removeEventListener('focusin', closeMenuWhenFocusLeaves)
  document.removeEventListener('keydown', closeMenuOnEscape)
  window.removeEventListener('blur', closeMenuWhenFocusLeaves)
})

</script>

<template>
  <aside class="sidebar">
    <div class="sidebar-brand-row">
      <div class="brand-lockup">
        <img :src="logoUrl" alt="" />
        <span>Haoyue</span>
      </div>
      <button class="icon-button" title="搜索任务" @click="searching = !searching">
        <Search :size="18" />
      </button>
    </div>

    <Transition name="collapse">
      <div v-if="searching" class="sidebar-search">
        <Search :size="15" />
        <input ref="searchInput" v-model="query" placeholder="搜索任务（Ctrl+K）" />
      </div>
    </Transition>

    <nav class="primary-nav">
      <!-- 1.1 作用域显性化：主入口建在「当前项目」里，而任务区标题旁那个建的是全局任务。
           两处外观相同却落点不同，必须把落点写出来。 -->
      <button class="nav-item is-primary" :title="primaryTaskScopeHint" @click="createTask(activeProjectId)">
        <SquarePen :size="18" />
        <span>新建任务</span>
        <em class="nav-scope">{{ activeProjectId ? '当前项目' : '全局' }}</em>
      </button>
      <button class="nav-item" @click="emit('openArchived')">
        <Archive :size="18" />
        <span>已归档</span>
      </button>
      <button class="nav-item" @click="emit('openScheduledTasks')">
        <CalendarClock :size="18" />
        <span>计划任务</span>
      </button>
      <button class="nav-item" @click="emit('openExtensions')">
        <Blocks :size="18" />
        <span>扩展</span>
      </button>
      <button class="nav-item" @click="emit('openKnowledge')">
        <BookOpen :size="18" />
        <span>知识库</span>
      </button>
    </nav>

    <div class="sidebar-scroll" :class="{ 'menu-open': menuKey }">
      <section class="sidebar-section tasks-section">
        <div class="section-heading task-section-heading">
          <button style="padding-left: unset;" class="task-section-toggle" type="button"
            :aria-expanded="taskSectionExpanded" @click="taskSectionExpanded = !taskSectionExpanded">
            <span>{{ viewMode === 'time' ? '会话' : '任务' }}</span>
            <ChevronDown v-if="taskSectionExpanded" :size="15" />
            <ChevronRight v-else :size="15" />

          </button>
          <div class="section-heading-actions">
          <button class="icon-button compact" title="新建全局任务（不绑定项目）" @click="createTask()">
              <Plus :size="15" />
            </button>
            <div class="sidebar-view-toggle" role="group" aria-label="会话列表分组方式">
              <button class="icon-button compact" :class="{ 'is-active': viewMode === 'projects' }"
                title="按项目分组" :aria-pressed="viewMode === 'projects'" @click="viewMode = 'projects'">
                <Folder :size="15" />
              </button>
              <button class="icon-button compact" :class="{ 'is-active': viewMode === 'time' }"
                title="按时间分组" :aria-pressed="viewMode === 'time'" @click="viewMode = 'time'">
                <Clock :size="15" />
              </button>
            </div>
            <button class="icon-button compact" aria-label="任务菜单" @click.stop="toggleMenu('global', $event)">
              <MoreHorizontal :size="16" />
            </button>
          </div>
          <Transition name="context-menu">
            <div v-if="menuKey === 'global'" class="sidebar-context-menu project-menu"
              :class="{ 'menu-up': menuPlacement === 'up' }">
              <button @click="runAction(() => createTask())">
                <SquarePen :size="15" />新建全局任务
              </button>
              <button @click="runAction(() => emit('archiveGlobalTasks'))">
                <Archive :size="15" />归档全部任务
              </button>
              <button class="danger" @click="runAction(() => emit('deleteGlobalTasks'))">
                <Trash2 :size="15" />删除全部任务
              </button>
            </div>
          </Transition>
        </div>

        <div v-if="taskSectionExpanded && viewMode === 'projects'" class="task-list">
          <div v-for="thread in globalTaskList.rows" :key="thread.id" class="thread-heading-row"
            :class="{ active: thread.id === activeThreadId }">
            <button v-if="renamingThreadId !== thread.id" class="thread-row"
              @click="emit('selectThread', thread.id)" @dblclick.prevent="startRename(thread)"
              @keydown.f2.prevent="startRename(thread)">
              <span>{{ thread.title }}</span>
              <LoaderCircle v-if="thread.running" class="thread-running-spinner" :size="15" aria-label="运行中" />
            </button>
            <div v-else class="thread-rename-box">
              <input ref="renameInput" v-model="renameValue" aria-label="重命名任务"
                @keydown.enter.prevent="commitRename(thread)" @keydown.esc.prevent="renamingThreadId = ''"
                @blur="commitRename(thread)" />
            </div>
            <button class="icon-button compact row-menu-button" :aria-label="`${thread.title} 任务菜单`"
              @click.stop="toggleMenu(`thread:${thread.id}`, $event)">
              <MoreHorizontal :size="15" />
            </button>
            <Transition name="context-menu">
              <div v-if="menuKey === `thread:${thread.id}`" class="sidebar-context-menu task-menu"
                :class="{ 'menu-up': menuPlacement === 'up' }">
                <button @click="runAction(() => startRename(thread))">
                  <SquarePen :size="15" />重命名
                </button>
                <button @click="runAction(() => emit('taskSettings', thread))">
                  <SlidersHorizontal :size="15" />任务设置
                </button>
                <button @click="runAction(() => emit('archiveTask', thread))">
                  <Archive :size="15" />归档任务
                </button>
                <button class="danger" @click="runAction(() => emit('deleteTask', thread))">
                  <Trash2 :size="15" />删除任务
                </button>
              </div>
            </Transition>
          </div>
          <button v-if="globalTaskList.hidden" class="thread-more-row" type="button"
            @click="expandTaskList('global')">显示全部（还有 {{ globalTaskList.hidden }} 个）</button>
          <p v-if="globalTaskList.rows.length === 0" class="project-empty">无任务</p>
        </div>

        <!-- 豆包式「按时间」视图：全部会话平铺进 今天/昨天/近 7 天/近 30 天/更早 分组，
             行尾展示所属项目名，方便在跨项目检索会话时不迷失工作目录。 -->
        <div v-if="taskSectionExpanded && viewMode === 'time'" class="task-list">
          <template v-if="timeGroups.length">
            <section v-for="group in timeGroups" :key="group.key" class="session-time-group">
              <div class="session-group-heading">{{ group.label }}</div>
              <div v-for="thread in group.list.rows" :key="thread.id" class="thread-heading-row"
                :class="{ active: thread.id === activeThreadId }">
                <button v-if="renamingThreadId !== thread.id" class="thread-row"
                  @click="emit('selectThread', thread.id)" @dblclick.prevent="startRename(thread)"
                  @keydown.f2.prevent="startRename(thread)">
                  <span>{{ thread.title }}</span>
                  <small v-if="projectDisplayName(thread.projectId)" class="thread-project-tag">
                    {{ projectDisplayName(thread.projectId) }}
                  </small>
                  <LoaderCircle v-if="thread.running" class="thread-running-spinner" :size="15" aria-label="运行中" />
                </button>
                <div v-else class="thread-rename-box">
                  <input ref="renameInput" v-model="renameValue" aria-label="重命名任务"
                    @keydown.enter.prevent="commitRename(thread)" @keydown.esc.prevent="renamingThreadId = ''"
                    @blur="commitRename(thread)" />
                </div>
                <button class="icon-button compact row-menu-button" :aria-label="`${thread.title} 任务菜单`"
                  @click.stop="toggleMenu(`thread:${thread.id}`, $event)">
                  <MoreHorizontal :size="15" />
                </button>
                <Transition name="context-menu">
                  <div v-if="menuKey === `thread:${thread.id}`" class="sidebar-context-menu task-menu"
                    :class="{ 'menu-up': menuPlacement === 'up' }">
                    <button @click="runAction(() => startRename(thread))">
                      <SquarePen :size="15" />重命名
                    </button>
                    <button @click="runAction(() => emit('taskSettings', thread))">
                      <SlidersHorizontal :size="15" />任务设置
                    </button>
                    <button @click="runAction(() => emit('archiveTask', thread))">
                      <Archive :size="15" />归档任务
                    </button>
                    <button class="danger" @click="runAction(() => emit('deleteTask', thread))">
                      <Trash2 :size="15" />删除任务
                    </button>
                  </div>
                </Transition>
              </div>
              <button v-if="group.list.hidden" class="thread-more-row" type="button"
                @click="expandTimeGroup(group.key)">显示全部（还有 {{ group.list.hidden }} 个）</button>
            </section>
          </template>
          <p v-else class="project-empty">无会话</p>
        </div>
      </section>

      <section v-if="shouldSearchRemotely(normalizedQuery)" class="sidebar-section search-results-section">
        <div class="section-heading">
          <span>全部会话</span>
          <LoaderCircle v-if="remoteSearching" class="thread-running-spinner" :size="14" aria-label="搜索中" />
        </div>
        <div v-if="remoteHits.length" class="task-list">
          <div v-for="hit in remoteHits" :key="hit.id" class="thread-heading-row">
            <button class="thread-row search-hit-row" :title="hit.snippet || undefined" @click="emit('openSession', hit)">
              <span>{{ hit.title || '未命名会话' }}</span>
              <span v-if="hit.snippet" class="search-hit-snippet">{{ hit.snippet }}</span>
            </button>
            <small v-if="hit.matchCount" class="search-hit-meta">命中 {{ hit.matchCount }} 条</small>
          </div>
        </div>
        <p v-else-if="remoteSearching" class="project-empty">正在搜索全部会话…</p>
        <p v-else-if="remoteError" class="project-empty">搜索失败：{{ remoteError }}</p>
        <p v-else class="project-empty">无更多会话</p>
      </section>

      <!-- 1.1：时间视图下项目区不再整块消失，而是折叠起来。整块隐藏会让「切到时间视图
           就找不到项目了」——项目本身仍可展开看标题/进项目属性，只是任务列表交给时间分组。 -->
      <section class="sidebar-section project-section">
        <div class="section-heading">
          <button class="task-section-toggle" type="button" :aria-expanded="projectSectionExpanded"
            @click="projectSectionExpanded = !projectSectionExpanded">
            <span>项目</span>
            <ChevronDown v-if="projectSectionExpanded" :size="15" />
            <ChevronRight v-else :size="15" />
          </button>
          <button class="icon-button compact" title="添加项目" @click="emit('openWorkspace')">
            <Plus :size="15" />
          </button>
        </div>

        <div v-show="projectSectionExpanded" v-for="project in projects" :key="project.id" class="project-group">
          <div class="project-heading-row">
            <button class="project-row" :title="project.path" @click="toggleProject(project)">
              <Folder :size="18" />
              <span>{{ project.name }}</span>
              <!-- H15: running spinner must not depend on the collapsed state — a running
                   project reads as "idle" when expanded. -->
              <LoaderCircle v-if="projectHasRunningTask(project.id)"
                class="thread-running-spinner" :size="15" aria-label="项目中有任务正在运行" />
            </button>
            <button class="icon-button compact row-menu-button" :aria-label="`${project.name} 项目菜单`"
              @click.stop="toggleMenu(`project:${project.id}`, $event)">
              <MoreHorizontal :size="16" />
            </button>
            <Transition name="context-menu">
              <div v-if="menuKey === `project:${project.id}`" class="sidebar-context-menu project-menu"
                :class="{ 'menu-up': menuPlacement === 'up' }">
                <button @click="runAction(() => createTask(project.id))">
                  <SquarePen :size="15" />在此项目中新建任务
                </button>
                <button @click="runAction(() => emit('openProjectProperties', project))">
                  <Info :size="15" />项目属性
                </button>
                <button @click="runAction(() => emit('initializeProjectWorkspace', project))">
                  <FolderCog :size="15" />初始化工作区元数据
                </button>
                <button @click="runAction(() => emit('archiveProjectTasks', project))">
                  <Archive :size="15" />归档全部任务
                </button>
                <button class="danger" @click="runAction(() => emit('deleteProjectTasks', project))">
                  <Trash2 :size="15" />删除全部任务
                </button>
                <button class="danger" @click="runAction(() => emit('deleteProject', project))">
                  <Trash2 :size="15" />删除项目
                </button>
              </div>
            </Transition>
          </div>

          <Transition name="project-collapse">
            <div v-if="expandedProjects.has(project.id) && viewMode === 'projects'" class="project-tasks">
              <div v-for="thread in visibleThreads(project.id)" :key="thread.id" class="thread-heading-row"
                :class="{ active: thread.id === activeThreadId }">
                <button v-if="renamingThreadId !== thread.id" class="thread-row"
                  @click="emit('selectThread', thread.id)" @dblclick.prevent="startRename(thread)"
                  @keydown.f2.prevent="startRename(thread)">
                  <span>{{ thread.title }}</span>
                  <LoaderCircle v-if="thread.running" class="thread-running-spinner" :size="15" aria-label="运行中" />
                </button>
                <div v-else class="thread-rename-box">
                  <input ref="renameInput" v-model="renameValue" aria-label="重命名任务"
                    @keydown.enter.prevent="commitRename(thread)" @keydown.esc.prevent="renamingThreadId = ''"
                    @blur="commitRename(thread)" />
                </div>
                <button class="icon-button compact row-menu-button" :aria-label="`${thread.title} 任务菜单`"
                  @click.stop="toggleMenu(`thread:${thread.id}`, $event)">
                  <MoreHorizontal :size="15" />
                </button>
                <Transition name="context-menu">
                  <div v-if="menuKey === `thread:${thread.id}`" class="sidebar-context-menu task-menu"
                    :class="{ 'menu-up': menuPlacement === 'up' }">
                    <button @click="runAction(() => startRename(thread))">
                      <SquarePen :size="15" />重命名
                    </button>
                    <button @click="runAction(() => emit('taskSettings', thread))">
                      <SlidersHorizontal :size="15" />任务设置
                    </button>
                    <button @click="runAction(() => emit('archiveTask', thread))">
                      <Archive :size="15" />归档任务
                    </button>
                    <button class="danger" @click="runAction(() => emit('deleteTask', thread))">
                      <Trash2 :size="15" />删除任务
                    </button>
                  </div>
                </Transition>
              </div>
              <button v-if="projectTaskLists.get(project.id)?.hidden" class="thread-more-row" type="button"
                @click="expandTaskList(`project:${project.id}`)">显示全部（还有
                {{ projectTaskLists.get(project.id)?.hidden }} 个）</button>
              <p v-if="visibleThreads(project.id).length === 0" class="project-empty">
                {{ project.loaded === false ? '正在读取任务…' : '无任务' }}
              </p>
            </div>
          </Transition>
        </div>

        <div v-if="projectSectionExpanded && projects.length === 0" class="sidebar-add-project">
          <p class="empty-recent">还没有项目</p>
          <button type="button" class="sidebar-add-project-button" @click="emit('openWorkspace')">
            <FolderPlus :size="16" />
            <span>选择文件夹，添加项目</span>
          </button>
        </div>
      </section>
    </div>

    <footer class="sidebar-footer">
      <button class="account-row" @click="emit('openSettings')">
        <span class="account-copy">
          <strong>Haoyue Desktop</strong>
          <small>v{{ version }}</small>
        </span>
        <Settings2 :size="17" />
      </button>
    </footer>
  </aside>
</template>
