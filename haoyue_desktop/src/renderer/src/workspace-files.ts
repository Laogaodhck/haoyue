import type { DaemonMessage } from '../../shared/ipc'
import { MENTION_LIMIT } from './composer-mentions'

/// `@` 文件引用的候选来源（P1-2.3）。workspace.files 只回工作区相对路径，
/// 构建产物与凭证目录已在 runtime 侧挡掉；这里只负责拼参数与容错，
/// 任何失败都退化成空候选——选择器显示「没有匹配」比报错打断输入更合适。
export interface WorkspaceFileSuggester {
  request(method: string, params?: Record<string, unknown>): Promise<DaemonMessage>
}

export function createWorkspaceFileSuggester(options: {
  daemon: () => WorkspaceFileSuggester | null
  root: () => string | null
}): (query: string) => Promise<string[]> {
  return async (query: string): Promise<string[]> => {
    const root = options.root()
    const daemon = root ? options.daemon() : null
    if (!root || !daemon) return []
    try {
      const response = await daemon.request('workspace.files', {
        workspace: root,
        query,
        limit: MENTION_LIMIT
      })
      const files: unknown = JSON.parse(response.data)
      return Array.isArray(files) ? files.filter((item): item is string => typeof item === 'string') : []
    } catch {
      return []
    }
  }
}
