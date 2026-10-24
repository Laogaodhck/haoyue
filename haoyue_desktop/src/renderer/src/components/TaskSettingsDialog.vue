<script setup lang="ts">
import { Archive, FolderOpen, Globe, GlobeOff, RotateCcw, Save, Settings2, Trash2, X } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { ReasoningLevel } from '../types'
import type { ProjectItem, ThreadItem } from '../types'
import ReasoningDepthMenu from './ReasoningDepthMenu.vue'
import { useEscapeClose } from '../use-escape-close'

const props = defineProps<{
  open: boolean
  thread?: ThreadItem
  project?: ProjectItem
  /** 全局活动模型与 Agent 模式：这两项不是任务属性，这里只做说明。 */
  model?: string
  mode?: string
}>()

const emit = defineEmits<{
  close: []
  saveTitle: [title: string]
  changeReasoningLevel: [level: ReasoningLevel]
  changeNetwork: [enabled: boolean]
  openSettings: []
  archive: []
  restore: []
  delete: []
}>()

const title = ref('')

/**
 * 2.4：「任务设置」原来只有标题与工作目录，真正的任务级开关却在输入框工具条里，
 * 入口名与内容对不上。现在把确实按任务保存的两项（推理深度、联网）搬进来，
 * 并把「模型 / Agent 模式」这两个全局项标明作用域，避免让人以为改的是当前任务。
 */
const modeLabel = computed(() => ({
  edit: '编辑', plan: '计划', readonly: '只读', auto: '自动'
} as Record<string, string>)[props.mode ?? ''] ?? (props.mode || '未设置'))
const reasoningLevel = computed(() => props.thread?.reasoningLevel)
const networkEnabled = computed(() => props.thread?.networkEnabled ?? true)

useEscapeClose(() => props.open, () => emit('close'))

watch(() => [props.open, props.thread?.id, props.thread?.title] as const, () => {
  if (props.open) title.value = props.thread?.title ?? ''
}, { immediate: true })

function save(): void {
  const value = title.value.trim()
  if (!value) return
  emit('saveTitle', value)
}

function showWorkspace(): void {
  if (props.project) void window.haoyue.showItemInFolder(props.project.path)
}
</script>

<template>
  <Transition name="modal-fade">
    <div v-if="open && thread" class="modal-backdrop task-settings-backdrop" @mousedown.self="emit('close')">
      <section class="task-settings-dialog" role="dialog" aria-modal="true" aria-labelledby="task-settings-title">
      <header class="task-settings-header">
        <div>
          <h2 id="task-settings-title">任务设置</h2>
        </div>
        <button class="icon-button" title="关闭" @click="emit('close')"><X :size="18" /></button>
      </header>

      <div class="task-settings-body">
        <label class="task-settings-field">
          <span>标题</span>
          <input v-model="title" maxlength="120" @keydown.enter="save" />
        </label>

        <div class="task-settings-field">
          <span>推理深度<em class="task-scope-tag">本任务</em></span>
          <ReasoningDepthMenu :model-value="reasoningLevel ?? ReasoningLevel.Medium" :disabled="Boolean(thread?.running)"
            @update:model-value="(level) => level && emit('changeReasoningLevel', level)" />
          <small>按任务保存：切换任务不会互相影响。</small>
        </div>

        <div class="task-settings-field">
          <span>联网<em class="task-scope-tag">本任务</em></span>
          <button type="button" class="secondary-button compact-button" @click="emit('changeNetwork', !networkEnabled)">
            <Globe v-if="networkEnabled" :size="14" />
            <GlobeOff v-else :size="14" />
            {{ networkEnabled ? '本任务联网已开启' : '本任务联网已关闭' }}
          </button>
          <small>真正生效还要叠加运行时全局开关（设置 → 智能体 → 网页搜索）；全局关闭时这里打开也不会联网。</small>
        </div>

        <div class="task-settings-field">
          <span>模型 / Agent 模式<em class="task-scope-tag is-global">全局</em></span>
          <small>
            当前模型 {{ model || '未配置' }} · 模式 {{ modeLabel }}。这两项是全局设置，在所有任务间共用
            （模式还会写入当前项目的 .haoyue/config.json），改它们请去设置或输入框工具条。
          </small>
          <button type="button" class="secondary-button compact-button" @click="emit('openSettings')">
            <Settings2 :size="14" /> 去设置
          </button>
        </div>

        <div class="task-settings-field">
          <span>工作目录</span>
          <div class="workspace-path-control">
            <input :value="project?.path || '无工作目录（任务）'" readonly />
            <button v-if="project" class="secondary-button" @click="showWorkspace">
              <FolderOpen :size="16" />打开
            </button>
          </div>
          <small>{{ project ? '工作目录由所属项目决定，避免任务在项目之间意外漂移。' : '任务不绑定项目，但可以正常读写文件和运行终端，只是没有固定的项目工作目录。' }}</small>
        </div>
      </div>

      <footer class="task-settings-footer">
        <div class="task-danger-actions">
          <button v-if="thread.archived" class="secondary-button" @click="emit('restore')"><RotateCcw :size="16" />恢复</button>
          <button v-else class="secondary-button" @click="emit('archive')"><Archive :size="16" />归档</button>
          <button class="danger-button" @click="emit('delete')"><Trash2 :size="16" />删除</button>
        </div>
        <button class="secondary-button primary-action" :disabled="!title.trim()" @click="save"><Save :size="16" />保存标题</button>
      </footer>
      </section>
    </div>
  </Transition>
</template>
