# haoyue 连接器生态（S5）

连接器 = 以 MCP 为分发格式的「即装即用外部能力包」。一个连接器包 = 一个 MCP server 配置 + 元数据 + 输入声明，安装后由运行时既有 `McpManager` 机制接管传输与工具注册。

## 包规范

每个连接器是一个目录，内含 `connector.json`，结构由 [schema/connector.schema.json](schema/connector.schema.json) 约束：

```jsonc
{
  "schemaVersion": 1,
  "id": "github",                    // kebab-case，安装后作为 mcp.servers 键
  "name": "GitHub",
  "description": "一句话中文说明",
  "icon": "🐙",
  "category": "dev",                 // web|dev|communication|data|productivity|system
  "offline": false,                  // true = 随 haoyue 分发、无 npm/uvx 外部依赖
  "server": {                        // 安装后写入 mcp.servers，支持 ${KEY} 占位符
    "transport": "stdio",
    "command": "npx",
    "args": ["-y", "@modelcontextprotocol/server-github"],
    "env": { "GITHUB_PERSONAL_ACCESS_TOKEN": "${GITHUB_TOKEN}" }
  },
  "inputs": [                        // 安装时收集的配置项；secret 项加密落盘
    { "key": "GITHUB_TOKEN", "title": "GitHub 个人访问令牌", "required": true, "secret": true }
  ],
  "mutatingTools": ["create_issue"], // 可选：显式 mutating / readonly 工具白名单
  "readOnlyTools": ["search_issues"]
}
```

## 使用

```bash
haoyue connectors list                 # 目录 + 安装状态
haoyue connectors install github       # 交互式收集必填项（secret 项隐藏回显）
haoyue connectors install sqlite --set DB_PATH=./data.db   # 非交互
haoyue connectors uninstall github
haoyue mcp test                        # 验证连接与工具发现
```

安装即写入全局配置 `mcp.servers.<id>`；重启会话或重连 MCP 后工具以 `mcp__<id>__<tool>` 形式进入工具列表。发现 → 可用只需：`list` → `install`（≤2 次交互）。

## 首批目录（11 个）

| ID | 名称 | 类型 | 依赖 |
|---|---|---|---|
| browser | 浏览器控制 | npm（@playwright/mcp） | npx |
| git | Git 仓库 | uvx（mcp-server-git） | uvx |
| github | GitHub | npm（官方 server-github） | npx |
| sqlite | SQLite 数据库 | uvx（mcp-server-sqlite） | uvx |
| notion | Notion | npm（官方 notion-mcp-server） | npx |
| email | 邮箱 IMAP/SMTP | 内置本地服务器 | 无 |
| calendar | 日历 ICS | 内置本地服务器 | 无 |
| rss | RSS 订阅 | 内置本地服务器 | 无 |
| clipboard | 剪贴板 | 内置本地服务器 | 无 |
| http-api | HTTP API 通用器 | 内置本地服务器 | 无 |
| notify | 系统通知 | 内置本地服务器 | 无 |

## 内置本地服务器

`connectors/servers/haoyue_local_server.py`：单文件、纯标准库的 stdio MCP 服务器，承载六个免依赖连接器（clipboard / notify / ics / rss / http / email）。安全约定：

- 网络访问仅由显式工具调用触发，不常驻后台；
- 邮件凭据经安装器以 `secret:` 前缀加密存入配置，传输前一刻解析，不落日志；
- `http_request` 强制超时（默认 30s，上限 120s），响应体超过 2 万字符截断。

## 目录解析顺序

1. 环境变量 `HAOYUE_CONNECTORS_DIR`
2. 可执行文件相对目录 `connectors/`（CLI/桌面随包分发路径）
3. 当前工作目录 `connectors/`（仓库内开发）

## 分发与社区贡献

- 目录随 `haoyue_cli.csproj` 拷贝进 CLI/桌面输出目录，官方仓库维护；
- 社区连接器按同一规范向 `connectors/` 提 PR：一个目录 + 一个 connector.json + （可选）一个离线 server 实现；
- 桌面端「设置 → 连接器」卡片化启停复用 `mcp.servers.<id>.enabled` 字段。
