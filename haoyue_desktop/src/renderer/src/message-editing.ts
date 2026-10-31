import { ref, type Ref } from 'vue'
import type { AppInfo } from '../../shared/ipc'
import { sessionScope } from './app-helpers'
import { confirmAction } from './confirmation'
import { preserveTruncatedVersions } from './conversation-state'
import { daemonRequest } from './daemon-rpc'
import type { ChatMessage, ProjectItem, ThreadItem } from './types'

/** Composer 只需这几个方法，避免把组件实例类型拖进本模块。 */
export interface ComposerHandle {
  populate?: (text: string, images?: ChatMessage['images'], files?: ChatMessage['files']) => void
  setValue?: (text: string) => void
  focus?: () => void
}

interface MessageEditingDeps {
  activeThread: Ref<ThreadItem | undefined>
  projects: Ref<ProjectItem[]>
  appInfo: Ref<AppInfo>
  composer: Ref<ComposerHandle | null>
}

/**
 * 「编辑用户消息」流程：收集该消息之后产生的文件补丁 → 可选反向撤销 → 截断后端会话 →
 * 截断前端消息 → 回填输入框。拆出 App.vue 以隔离这一组强顺序、强副作用的状态机。
 */
export function createMessageEditing({
  activeThread,
  projects,
  appInfo,
  composer
}: MessageEditingDeps) {
  const editingUserMessage = ref<ChatMessage | null>(null)
  const editMessageModifiedFiles = ref<string[]>([])
  const editMessagePatches = ref<Array<{ filePath: string; diff: string }>>([])
  const editMessageDialogOpen = ref(false)

  function onEditUserMessage(message: ChatMessage): void {
    const thread = activeThread.value
    if (!thread || thread.archived) return
    if (thread.running || thread.queueDraining) {
      void window.haoyue?.notify?.('无法编辑', '当前回合正在执行，请先等待或停止生成后再进行编辑。')
      return
    }

    const promptIndex = thread.messages.findIndex((item) => item.id === message.id)
    if (promptIndex < 0) return

    // Collect subsequent file patches & modified files
    const patches: Array<{ filePath: string; diff: string }> = []
    const filesSet = new Set<string>()

    for (let i = promptIndex + 1; i < thread.messages.length; i++) {
      const msg = thread.messages[i]
      if (!msg) continue
      for (const tool of msg.tools ?? []) {
        if (tool.filePath && tool.diff) {
          patches.push({ filePath: tool.filePath, diff: tool.diff })
          filesSet.add(tool.filePath)
        }
      }
    }

    editingUserMessage.value = message
    editMessageModifiedFiles.value = Array.from(filesSet)
    editMessagePatches.value = patches
    editMessageDialogOpen.value = true
  }

  async function handleEditConfirm(revertFiles: boolean): Promise<void> {
    const thread = activeThread.value
    const message = editingUserMessage.value
    const patches = editMessagePatches.value
    editMessageDialogOpen.value = false
    if (!thread || !message) return

    const promptIndex = thread.messages.findIndex((item) => item.id === message.id)
    if (promptIndex < 0) return

    const project = projects.value.find((item) => item.id === thread.projectId)
    const workspace = project?.path || appInfo.value.defaultWorkspace

    // Step 1: If reverting file modifications, apply diffs in reverse
    if (revertFiles && patches.length > 0) {
      try {
        const res = await window.haoyue.project.revertFileDiffs(workspace, patches)
        if (res.failed.length > 0) {
          const failedSummary = res.failed
            .map((f) => `• ${f.filePath} (${f.reason})`)
            .join('\n')
          await confirmAction({
            title: '部分文件撤销失败',
            message: `以下文件未能成功自动撤回（可能已被外部修改或存在冲突），建议您手动检查：\n${failedSummary}`,
            confirmLabel: '我知道了',
            danger: true
          })
        }
      } catch (err) {
        await confirmAction({
          title: '撤销文件修改失败',
          message: `撤回文件修改时出错：${err instanceof Error ? err.message : String(err)}`,
          confirmLabel: '我知道了',
          danger: true
        })
      }
    }

    // Step 2: Truncate backend session in SQLite
    if (thread.sessionId) {
      const match = message.id ? /^.*:(\d+)$/.exec(message.id) : null
      if (!match?.[1]) {
        // 无持久化行号时禁止用本地索引猜测后端行号（tool 行合并会导致错位）。
        void window.haoyue?.notify?.('无法编辑', '该消息尚未与后端会话同步，请等待会话加载完成后重试。')
        return
      }
      try {
        await daemonRequest('session.truncate', {
          id: thread.sessionId,
          ...sessionScope(thread, project),
          keepCount: parseInt(match[1], 10)
        })
      } catch (err) {
        // 截断失败必须中止：否则前端删了消息、后端还在，下次 reload 全部复活。
        void window.haoyue?.notify?.('无法编辑', `会话截断失败：${err instanceof Error ? err.message : String(err)}`)
        return
      }
    }

    // Step 3: Truncate frontend messages（1.2：截断前把被替换的整段历史压入版本链）
    preserveTruncatedVersions(thread, promptIndex, 'edit')
    thread.messages = thread.messages.slice(0, promptIndex)
    thread.phase = undefined
    thread.assistantId = undefined

    // Step 4: Populate Composer and focus
    if (typeof composer.value?.populate === 'function') {
      composer.value.populate(message.content || '', message.images, message.files)
    } else {
      composer.value?.setValue?.(message.content || '')
      composer.value?.focus?.()
    }
  }

  return {
    editingUserMessage,
    editMessageModifiedFiles,
    editMessagePatches,
    editMessageDialogOpen,
    onEditUserMessage,
    handleEditConfirm
  }
}
