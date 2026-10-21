import {
  buildConversationMarkdown,
  conversationExportFileName,
  normalizeReasoningLevel,
  resolveModelRef,
} from './app-helpers'
import { DeletionUndoBuffer, restoreAssistantVersion } from './conversation-state'
import type { ChatMessage, ReasoningLevel, ThreadItem } from './types'

/// 会话级动作集（1.2 删除撤销/版本恢复 + 1.5 slash 命令 + 1.6 会话导出）。
/// 从 App.vue 下沉的原因：这些处理器只依赖少量状态与回调，抽成依赖注入的工厂后
/// 既可单测，也让巨型组件守住行数预算。
export interface ChatActionDeps {
  getThread: () => ThreadItem | null
  /** 撤销浮条可见性（删除成功后显示、撤销/过期后隐藏）。 */
  setUndoVisible: (visible: boolean) => void
  switchModel: (model: string) => void
  switchMode: (mode: string) => void
  switchDepth: (level: ReasoningLevel) => void
  /** 清空当前会话草稿（slash /清空草稿）。 */
  clearDraft: (thread: ThreadItem) => void
  saveTextFile: (defaultName: string, content: string) => Promise<string | null>
  notify: (title: string, body: string) => void
  /** 当前可用模型列表（/模型 命令解析用）。 */
  models: () => readonly string[]
}

export interface ChatActions {
  deleteMessage: (message: ChatMessage) => void
  undoDeletion: () => void
  restoreVersion: (message: ChatMessage) => void
  slashCommand: (id: string, arg: string) => void
  exportConversation: () => Promise<void>
}

export function createChatActions(deps: ChatActionDeps): ChatActions {
  // 单条删除的 5 秒撤销窗口；过期回调负责收起浮条。
  const deletionUndo = new DeletionUndoBuffer(() => deps.setUndoVisible(false))

  async function exportConversation(): Promise<void> {
    const thread = deps.getThread()
    if (!thread) return
    const path = await deps.saveTextFile(
      conversationExportFileName(thread.title), buildConversationMarkdown(thread))
    if (path) deps.notify('已导出会话', path)
  }

  return {
    /** 1.2 单条删除：从会话视图移除并开启 5 秒撤销窗口。 */
    deleteMessage(message) {
      const thread = deps.getThread()
      if (!thread || !deletionUndo.delete(thread, message.id)) return
      if (thread.assistantId === message.id) thread.assistantId = undefined
      deps.setUndoVisible(true)
    },
    undoDeletion() {
      if (deletionUndo.undo()) deps.setUndoVisible(false)
    },
    /** 1.2 恢复历史回答：取版本链中最近一个 assistant 版本插回。 */
    restoreVersion(message) {
      const thread = deps.getThread()
      if (!thread) return
      if (restoreAssistantVersion(thread, message.id, (message.versions ?? []).length - 1)) {
        thread.assistantId = undefined
        deps.setUndoVisible(false)
      }
    },
    /** 1.5 slash 命令：参数式切换复用 change-* 通道，动作式（导出/清空）本地执行。 */
    slashCommand(id, arg) {
      const thread = deps.getThread()
      if (!thread) return
      if (id === 'export') { void exportConversation(); return }
      if (id === 'clear') { deps.clearDraft(thread); return }
      const modelRef = id === 'model' ? resolveModelRef(arg, deps.models()) : null
      if (modelRef) { deps.switchModel(modelRef); return }
      if (id === 'mode' && arg) { deps.switchMode(arg); return }
      if (id === 'depth' && arg) { deps.switchDepth(normalizeReasoningLevel(arg)); return }
      deps.notify('命令参数不合法', '输入 / 查看可用命令与用法。')
    },
    /** 1.6 会话导出：Markdown 组装在 app-helpers（可测），这里只负责落盘与提示。 */
    exportConversation,
  }
}
