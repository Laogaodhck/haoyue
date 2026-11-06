/**
 * GitHub CLI 连接器状态文案。
 *
 * daemon 的 `connectors.list` 会附带 `ghCli` 快照（安装/版本/登录账号/可用性）。
 * 这里只做纯映射，便于单测；页面不展示任何令牌，登录引导也只给命令与官方文档。
 */
export interface GhCliStatus {
  installed: boolean
  version?: string | null
  authenticated: boolean
  account?: string | null
  hosts?: string[]
  scopes?: string[]
  available: boolean
  error?: string | null
  installUrl?: string
  loginCommand?: string
}

export interface GhCliPresentation {
  installedLine: string
  loginLine: string
  availabilityLine: string
  available: boolean
  needsInstall: boolean
  needsLogin: boolean
  error: string
}

export const GH_CLI_INSTALL_URL = 'https://cli.github.com/'
export const GH_CLI_LOGIN_URL = 'https://cli.github.com/manual/gh_auth_login'
export const GH_CLI_LOGIN_COMMAND = 'gh auth login'

export function describeGhCli(status: GhCliStatus | null | undefined): GhCliPresentation {
  if (!status) {
    return {
      installedLine: '状态未知',
      loginLine: '—',
      availabilityLine: '未知',
      available: false,
      needsInstall: false,
      needsLogin: false,
      error: '未能读取 GitHub CLI 状态，请重新打开连接器面板或检查运行时连接。'
    }
  }

  const installedLine = status.installed
    ? (status.version ? `已安装 v${status.version}` : '已安装')
    : '未安装'
  const loginLine = status.authenticated
    ? (status.account ? `已登录 ${status.account}` : '已登录')
    : '未登录'

  return {
    installedLine,
    loginLine,
    availabilityLine: status.available ? '可用' : '不可用',
    available: status.available,
    needsInstall: !status.installed,
    needsLogin: status.installed && !status.authenticated,
    error: status.available ? '' : (status.error || 'GitHub CLI 当前不可用')
  }
}
