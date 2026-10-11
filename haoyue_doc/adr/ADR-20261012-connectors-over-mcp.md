# ADR-20261012-连接器生态以 MCP 为分发格式

## 状态

已采纳（2026-10-12，随 S5 落地）

## 背景

对标豆包/WorkBuddy 的能力覆盖短板 S5：haoyue 缺少「连接器生态」——用户无法低门槛接入邮箱、日历、剪贴板等外部服务。运行时已有完整的 MCP 机制（stdio/SSE、自动发现、工具白名单、OAuth、secret 加密），CLI 已有 `haoyue connectors list/install/uninstall`。

## 决策

1. **不另造协议**：连接器包 = MCP server 配置 + 元数据（图标/中文说明/分类）+ inputs 收集项规范（`connectors/schema/connector.schema.json`）。安装即把实例化后的配置写进全局 `mcp.servers`，传输与工具注册全部复用既有 McpManager。
2. **首批 11 个官方连接器随仓库分发**：browser/git/github/sqlite/notion 走 npm/uvx 外部命令；email/calendar/rss/clipboard/http-api/notify 由随包分发的单文件内置本地 MCP 服务器（`haoyue_local_server.py`，纯标准库）承载，离线可用、零外部依赖。
3. **RPC 面向桌面端**：`connectors.list/install/uninstall` 登记进 DaemonContract（契约 First），安装后后台重连 MCP 即时生效；启停复用 `mcp.upsert` 的合并语义而非新增第四个方法。
4. **桌面端「扩展中心 → 连接器」卡片商店为默认视图**：一键安装、inputs 卡片内表单收集（secret 走密码框）、开关启停；原 McpPanel 降级为「MCP 配置」高级二级视图。

## 权衡

- **自建目录 vs 直接暴露 MCP 配置表单**：MCP 配置要求用户理解 command/args/env，门槛过高；连接器以「中文说明 + 预置参数 + 必填项收集」把门槛压到 ≤2 次点击。McpPanel 保留满足专家场景。
- **内置本地服务器 vs 全部外部包**：邮箱/日历/剪贴板/通知等本地能力无现成轻量 MCP 包且引入 Node/uvx 依赖会拖累开箱体验；单文件纯标准库实现保住「离线可用、零依赖」，代价是自维护（能力收敛在 6 类高频只读/本地场景，风险可控）。
- **secret 加密复用 SecretResolver**：与 mcp.upsert 凭据同一通道，避免第二套凭据机制。

## 影响

- 新增 RPC 三个（`connectors.*`），契约快照与桌面 shared、sdks/typescript 生成物同步（117 方法）。
- `connectors/` 目录随 CLI 输出与桌面 runtime 快照分发，`ConnectorCatalog.ResolveRoot` 按 env → exe 相对 → cwd 解析。
- 护栏：ConnectorCatalogTests（5）+ ConnectorDaemonApiTests（2）+ 契约治理测试（快照一致、分发 case 全登记）。
