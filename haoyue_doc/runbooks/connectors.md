## connectors

- **用途**：连接器生态 CLI——浏览官方连接器目录、一键安装/卸载（S5）。
- **命令**：`haoyue connectors list` / `install <id> [--set K=V ...]` / `uninstall <id>`。
- **安装语义**：解析 connector.json 的 `${KEY}` 占位符 → secret 项经 SecretResolver 加密 → 写入全局配置 `mcp.servers.<id>` 并持久化；重启会话或 `haoyue mcp test` 后生效。
- **目录解析**：环境变量 `HAOYUE_CONNECTORS_DIR` → exe 相对 `connectors/` → cwd `connectors/`。
- **护栏**：
  - 缺必填项时安装失败且**不得写入**配置（ConnectorException 可读报错）。
  - secret 输入项落盘必须带 `secret:` 前缀，禁止明文。
  - 失败/卸载不得影响其他已配置的 MCP 服务器。
  - 目录内单个损坏包只跳过自身，不阻断其余包加载。
- **测试**：`haoyue_tests/ConnectorCatalogTests.cs`（5 项：目录完整性/内置服务器引用/安装占位符与加密/缺必填失败/卸载闭环）。

## Daemon RPC（桌面端/编辑器客户端，S5 后端配套）

| 方法 | 参数 | 语义 |
|---|---|---|
| `connectors.list` | — | 目录元数据 + 安装状态（`inputs` 只给键名与元数据，不给已存值） |
| `connectors.install` | `id` 必填；`values` 可选（键名对应 inputs.key） | 占位符填充 → secret 项加密落盘 → 写全局 mcp.servers → 后台重连 MCP |
| `connectors.uninstall` | `id` 必填 | 从全局配置移除并后台重连；未安装时返回 `notFound` |

- 启/停已安装连接器复用 `mcp.upsert`（`scope=global`，仅传 `server.enabled`，其余配置合并保留）。
- 契约治理：三个方法已登记 `DaemonContract`（分类 `connectors`），契约快照与 TS 生成物（桌面 shared + sdks/typescript，117 方法）已同步再生。
- RPC 护栏测试：`haoyue_tests/ConnectorDaemonApiTests.cs`（2 项：install→uninstall 全链路含 LOCAL_SERVER 自动解析、缺必填可读错误且不写配置）。

## 桌面端 UI（扩展中心 → 连接器）

- 默认视图为 `ConnectorsPanel` 卡片商店：图标/名称/描述/分类徽章/离线可用标记/安装状态，顶部计数与搜索；发现到可用 ≤2 次点击。
- 一键安装：无收集项直接装；有 `inputs` 时卡片内展开表单（secret 项密码框），必填校验后提交。
- 启停为卡片开关（经 `mcp.upsert` 合并语义，不回传也不清空既有配置）；卸载为卡片按钮；变更后触发 `mcp.reload` 刷新面板。
- 「MCP 配置」二级视图保留原 `McpPanel`（自定义服务器、凭据、OAuth、专家字段）；普通设置页仍直入 MCP 面板。
- 打包分发：`connectors/` 随桌面 runtime 快照（`runtime/win-x64/connectors/`）落盘，`ConnectorCatalog.ResolveRoot` 经 exe 相对目录命中；开发期可设 `HAOYUE_CONNECTORS_DIR` 指向仓库目录。
