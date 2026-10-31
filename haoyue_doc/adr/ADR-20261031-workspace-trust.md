# ADR-20261031-工作区信任门禁

- 状态：已接受
- 日期：2026-10-31
- 关联代码：`haoyue_runtime/Workspaces/WorkspaceTrustStore.cs`、`WorkspaceManager.cs`、`haoyue_runtime/Daemon/DaemonServer.cs`、`haoyue_desktop/src/renderer/src/components/WorkspaceTrustNotice.vue`

## 背景（Context）

工作区的 `.haoyue/config.json` 与 `mcp/servers.json` 可以声明 MCP stdio 服务器、verify 命令等**会在用户回合中被自动执行**的配置。若「打开工作区」即等同于「信任其配置」，克隆任意仓库再打开一次就能取得本机执行权；而桌面端与 CLI 的常规操作正是打开项目。

## 决策（Decision）

引入显式工作区信任：

- 受信清单存于 `~/.haoyue/trusted-workspaces.json`，按真实路径比较；
- 未受信工作区加载时整体忽略上述两份配置，MCP 服务器不连接、verify 不执行；
- 通过 `workspace.trust` / `workspace.untrust` 显式放行或收回，作用于当前工作区或指定路径；
- 信任清单损坏或不可读时按「无任何受信工作区」处理（fail closed）；
- `workspace.get` 回传 `trusted`、`hasWorkspaceConfig`、`hasWorkspaceMcp`，桌面端据此显示放行提示。

## 备选方案与取舍（Options & Trade-offs）

- **打开时弹窗询问**：交互成本低但容易被习惯性确认，且 CLI / 自动化场景无法可靠提问。
- **只信任带 `.git` 的仓库**：来源与执行权无关，公共仓库同样不可信。
- **所有工作区命令一律沙箱**：沙箱覆盖不了 MCP 子进程与任意工具网络访问，不能替代信任决策。
- **永不加载工作区配置**：牺牲项目级模型、模式、verify 等既有能力。
- 选定方案：**默认不信任 + 显式放行**，把执行权交还给用户的一次性动作，同时保留项目级配置能力。

## 后果（Consequences）

- 新建或克隆项目首次使用时需显式放行；未放行期间项目级配置不生效，桌面端会明确提示而不是静默失效。
- 信任清单是安全边界的一部分：损坏即收紧，不会因为文件异常而放行。
- 信任状态按路径记录，工作区移动或重建后需重新放行。
- 未受信只是「不加载」，不会删除或改写用户的配置文件；撤销信任后下一次加载即恢复忽略。
