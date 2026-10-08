# Model Context Protocol（MCP）

Haoyue 作为 MCP Client 连接外部 Server，并把发现的工具和 Prompt 注册到 Runtime。当前实现 MCP 协议版本 `2024-11-05`。

## 支持范围

- **stdio**：启动本地子进程，通过 stdin / stdout 交换 JSON-RPC 2.0 消息；
- **HTTP SSE**：以 Server-Sent Events 长连接接收消息，并按 Server 声明的 POST 端点发送请求；
- **Streamable HTTP**：直接向 `POST /mcp` 发送请求，响应可以是 JSON，也可以是流式 SSE；
- 自动调用 `tools/list` 并把工具注册到 `IToolRegistry`；
- 自动调用 `prompts/list`，支持的 Server 可通过 `prompts/get` 提供 Prompt；
- Client 能列出资源，但当前 `McpManager` 不会把资源自动注入 Agent 上下文；
- `websocket` 连接方式已保留，但尚未实现。

## 在 Desktop 中配置

打开“设置 → MCP”，可以添加全局或当前工作区 Server，选择连接方式（stdio / HTTP SSE / Streamable HTTP）、填写命令或 URL、参数、环境变量和启用状态，然后“保存并重载”。

连接内容一栏支持智能识别：直接粘贴包名（如 `@modelcontextprotocol/server-github` 自动补全为 `npx -y …`、`mcp-server-fetch` 自动补全为 `uvx …`）、完整命令行或远程 URL，保存时按内容自动判定传输方式；也支持直接粘贴 Claude / Cursor 风格的 JSON 配置，字段会自动填充。

内置一键预设覆盖常用场景：GitHub、文件系统、网页抓取（fetch）、记忆图谱（memory）、顺序思考（sequential-thinking）、Git、浏览器自动化（Playwright）、网页搜索（Brave Search）、框架文档（context7）、PostgreSQL、Slack、时间与时区。预设自动填好连接命令与凭证键名，凭证值由用户自行填写。

![Desktop MCP Server 配置](/screenshots/desktop/mcp-servers.png)

Desktop 查询现有配置时只返回环境变量键名，不返回敏感值。编辑已有 Server 时，需要重新填写希望保存的环境变量内容。

## 远程 Server 的 OAuth 自动授权

支持 MCP 授权规范的远程 Server 在返回 401 时，Haoyue 会自动发起本地 OAuth 授权助手（RFC 9728 / RFC 8414 元数据发现 → RFC 7591 动态客户端注册 → PKCE 授权码流程）：打开系统浏览器完成登录后，本地环回端口接收回调，令牌交换成功即自动重连，无需手动配置请求头。

- 令牌（含后续静默刷新所得）以 `secret:` 前缀加密存储，仅保存在本机，不回显、不进日志；
- 令牌过期后重连会先尝试静默刷新（refresh_token），失败才重新打开浏览器；
- 若授权服务器不支持动态客户端注册，或希望沿用已有令牌，可改用 `Authorization` 请求头方式；
- 高级选项中的“禁用 OAuth 浏览器授权”可整体关闭该行为。

## 高级选项（专家模式）

Server 编辑器的“高级”折叠区提供以下调优项，默认全部留空 / 关闭，保持运行时默认行为：

| 选项 | 说明 |
| --- | --- |
| 传输方式 | 显式覆盖 stdio / HTTP SSE / Streamable HTTP（通常由连接内容自动判定） |
| 连接超时 | 远程连接超时秒数，1~120，默认 10 秒 |
| 始终视为写入的工具 | 名称不含写入关键词但会真实改动的工具（如 `send_email`、`deploy`），逐行填写 |
| 始终视为只读的工具 | 强制按只读处理的工具名，优先级高于上一项 |
| 信任该 Server 只读工具 | 放开默认的安全保守策略：未知名称的工具在只读 / 计划模式下按只读放行 |
| 禁用 OAuth 浏览器授权 | 远程 Server 返回 401 时不再打开浏览器，直接按连接失败提示 |

## JSON 配置

全局 Server 可放在 `~/.haoyue/config.json` 的 `mcp.servers` 中。工作区可使用 `.haoyue/mcp/servers.json` 或 `.haoyue/config.json` 的 `mcp` 字段；工作区同名配置覆盖全局项。

```json
{
  "servers": {
    "filesystem": {
      "transport": "stdio",
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "E:\\Project"],
      "env": {
        "TOKEN": "..."
      },
      "enabled": true
    },
    "remote-tools": {
      "transport": "sse",
      "url": "https://mcp.example.com/sse",
      "enabled": true
    }
  }
}
```

远程 Server 支持自定义 HTTP `headers`（如 `Authorization`）；值以 `secret:` 前缀加密存储（Windows DPAPI；Linux 优先 Secret Service，无密钥环时退回 `~/.haoyue/secrets.json` AES-GCM 加密文件，密钥派生自机器标识，安全性弱于 DPAPI），不回显、不进日志。携带 headers 的连接必须使用 https://（127.0.0.1 调试除外）。

JSON 配置同样接受专家字段：`connectTimeoutSeconds`、`mutatingTools`、`readOnlyTools`、`trustReadOnly` 与 `oauthDisabled`，含义与上表一致。

## 连接与重载

Daemon 会先建立 IPC 监听，再在后台串行初始化 MCP。工作区切换、配置保存和 `mcp.reload` 会先注销旧工具与 Prompt、关闭旧连接，再加载新配置，避免残留重复注册。单个 Server 连接失败只会把该行标记为未连接并附上原因，不会中断其余 Server 的加载。远程连接超时默认 10 秒（可在高级选项中调整），初始化最长等待 30 秒；远程 Server 返回 401 时自动进入 OAuth 授权流程（见上文）。

保存配置或切换启用状态会立即返回，连接在后台进行：受影响的 Server 先标记为「连接中」，连接结束后 Daemon 向所有已连接客户端广播 `mcp.updated` 事件，Desktop 据此刷新状态，因此启用一个无法访问的 Server 也不会让界面卡住。

CLI 可用于检查：

```bash
haoyue mcp list
haoyue mcp test
```

`mcp test` 连接所有启用的 Server，并显示连接状态与工具数量。Desktop 提供等价的可视化状态与重载入口。
