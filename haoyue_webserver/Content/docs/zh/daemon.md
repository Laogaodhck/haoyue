# Daemon 运行时Runtime与 IPC 协议

Haoyue Daemon 通过本地 IPC 向桌面端、IDE 插件和其他客户端开放 Runtime。当前协议版本为 `2.1`，使用一行一个 JSON 对象的 JSONL 消息，不是完整的 JSON-RPC 2.0 实现。

## 连接地址

- Windows Named Pipe：`\\.\pipe\haoyue-<用户名>`（按当前用户命名，ACL 限定本人与 SYSTEM）
- Linux / macOS Unix Socket：`~/.haoyue/daemon.sock`

请求和响应都必须以换行符结尾。一次连接可以在 Agent 输出过程中继续发送控制请求。

```json
{"id":1,"method":"chat","params":{"sessionId":"20260731-120000-a1b2c3","message":"分析当前项目"}}
{"id":1,"event":"thinking","sessionId":"20260731-120000-a1b2c3","data":"正在检查项目结构"}
{"id":1,"event":"delta","sessionId":"20260731-120000-a1b2c3","data":"这是一个 .NET 项目。"}
{"id":1,"event":"done","sessionId":"20260731-120000-a1b2c3","data":"这是一个 .NET 项目。"}
```

响应保持统一事件信封：`id` 对应请求，`event` 表示事件类型，`data` 始终为字符串。`result` 中的结构化结果会编码成 JSON 字符串，由客户端再次解析。

## 协议与状态

| 方法 | 参数 | 说明 |
| --- | --- | --- |
| `ping` | 无 | 返回 `pong` |
| `protocol.info` | 无 | 返回协议版本、能力与方法列表 |
| `workspace.get` | 无 | 返回当前工作区路径、项目类型和模式 |
| `workspace.open` | `{ "path": "..." }` | 验证并切换 Runtime 工作区 |
| `workspace.init` | 无 | 初始化当前工作区的 Haoyue 目录 |
| `workspace.files` | `{ "workspace": "...", "query": "app", "limit": 30 }` | 列举工作区内文件，供输入框 `@` 引用挑选；只返回相对路径（正斜杠），跳过 `.git`/`node_modules`/`bin`/`obj`/`dist`/`.haoyue` 与 `.gitignore`、`.haoyueignore` 命中的内容，并拒绝 `.ssh`、`.aws`、`.env*` 等凭证目录与文件；`limit` 默认 30、上限 100 |
| `agent.mode.get` | 无 | 返回 `plan`、`readonly`、`edit` 或 `auto` |
| `agent.mode.switch` | `{ "mode": "edit" }` | 切换并持久化 Agent 模式 |
| `agent.steer` | `{ "sessionId": "...", "message": "..." }` | 向正在运行的 turn 添加指导，不取消当前请求 |

`workspace.open` 会清除当前连接的旧式恢复 Session。新的 `chat` 请求应携带 `sessionId`，项目任务同时携带 `workspace`；turn 启动时会捕获自己的工作区，之后的工作区、模式或模型切换不会改变已经运行的 turn。

## 执行与取消

`chat` 是兼容旧客户端的主要执行方法，`agent.runTurn` 和 `agent/runTurn` 是别名。

支持视觉的模型可以接收 `images` 数组。每张图片包含客户端生成的 `id`、文件名、MIME 类型和不带 Data URL 前缀的 Base64 数据；单次最多 10 张、单张最多 10 MB、合计最多 40 MB。支持 `image/png`、`image/jpeg`、`image/webp` 和 `image/gif`，纯图片消息可以省略 `message`。

```json
{"id":9,"method":"chat","params":{"message":"比较两张图片","images":[{"id":"a","name":"before.png","mediaType":"image/png","data":"..."},{"id":"b","name":"after.webp","mediaType":"image/webp","data":"..."}]}}
```

```json
{"id":10,"method":"chat","params":{"message":"修复测试","reasoningLevel":"high"}}
{"id":11,"method":"agent.cancel","params":{"requestId":10}}
```

正在运行的 turn 可以接收附加指导。`agent.steer` 会把消息放入当前 turn 的指导队列，当前模型请求结束后再加入上下文并继续下一步，不会取消或打断正在进行的请求：

```json
{"id":12,"method":"agent.steer","params":{"sessionId":"20260731-120000-a1b2c3","message":"也请检查边界情况"}}
{"id":12,"event":"result","sessionId":"20260731-120000-a1b2c3","data":"guidance queued"}
```

`agent.cancel` 的 `requestId` 可省略，此时取消当前连接的活动 turn。取消请求本身返回 `result`，被取消的 chat 最终返回：

```json
{"id":11,"event":"result","data":"cancellation requested for 10"}
{"id":10,"event":"cancelled","data":"取消前已生成的部分文本"}
```

内置高危守卫拦下一次工具调用时，turn 不会直接失败，而是挂起该调用并推送 `approval_request` 事件：`details` 含 `callId`、`tool`、拦截原因 `reason` 与参数摘要 `summary`。客户端用 `agent.respondApproval` 回传决定：

```json
{"id":13,"method":"agent.respondApproval","params":{"callId":"call-8f2c","approve":true}}
```

`approve: true` 只放行当前这一次调用（不是长期白名单）；`approve: false` 或在 120 秒内没有任何响应都按拒绝处理，被拦下的调用不会执行。批准与拒绝都会落到 `.haoyue/audit/tools.jsonl`，与其余工具执行记录同表可查。

`session.get` 返回的消息中，assistant 消息带 `modelRef`（`provider/model`），用于界面标注每条回答来自哪个模型。

流式事件包括 `thinking`、`delta`、`steer`、`status`、`image_view`、`tool_start`、`tool_done`、`workflow`、`warning` 和 `approval_request`。`workflow` 的 `details` 含 `step`、`kind`（start/think/tool/verify/repair/compact/done/error）、`label` 与 `detail`，用于客户端统计当前 turn 的执行步数。`steer` 表示附加指导已经进入当前 turn 的上下文；`image_view` 的 `details.imageId` 指明模型正在查看哪张上传图片。终止事件包括 `done`、`cancelled` 和 `error`。

Daemon 还会主动广播与任何请求无关的事件（`id` 为 0）：`schedule.updated`、`schedule.upcoming`、`task.updated`、`evolution.reflected`、`mcp.updated`、`turn.interrupted`、`memory.consolidated` 和 `model.load.progress`。`model.load.progress` 的 `details` 含 `model`、`phase`（Queued / Probing / Loading / Verifying / Projector / Ready / Failed）、`percent`（0..1）、`elapsedMs` 与 `estimatedRemainingMs`。本地 GGUF 权重的冷加载需要一到两分钟，客户端据此在首条消息上显示"加载本地模型 N%"，而不是让界面看起来毫无反应；进度通道覆盖对话回合里的懒加载，不只在 `model.preload` 时上报。广播会写给每一个已握手的连接，`details` 为每个连接各复制一份；某个客户端写入失败只会在 stderr 记一行，不会结束事件泵。

## 其他方法

| 方法 | 参数 | 说明 |
| --- | --- | --- |
| `session.list` | `{ "workspace": "...", "global": false, "includeArchived": true }` | 按工作区或全局范围列出 Session |
| `session.get` | `{ "id": "...", "workspace": "...", "limit": 80, "before": 120 }` | 读取 Session 及其消息；`limit` 取 1..500，`before` 是向更早翻页的游标，响应另带 `total` / `firstIndex` / `hasMore` |
| `session.update` | `{ "id": "...", "title": "...", "reasoningLevel": "high" }` | 更新标题、思考深度等 Session 元数据 |
| `session.archive` | `{ "id": "...", "archived": true }` | 归档或恢复 Session |
| `session.delete` | `{ "id": "..." }` | 永久删除 Session |
| `session.truncate` | `{ "id": "...", "keepCount": 5 }` | 保留前 N 条消息（"重新生成"用），返回剩余消息数 |
| `session.resume` | `{ "id": "...", "global": false }` | 恢复 Session |
| `session.new` | `{ "workspace": "...", "reasoningLevel": "high" }` 或 `{ "global": true }` | 创建并绑定一个新 Session |
| `session.image` | `{ "id": "...", "imageId": "..." }` | 按需读取会话中单张图片的 base64 字节；`session.get` 返回的消息里图片 `data` 恒为 null，只在真正要显示时按 id 取字节 |
| `session.search` | `{ "query": "...", "all": true, "includeArchived": true, "limit": 20 }` | 全文检索会话，返回标题、命中消息数与首个命中处的上下文片段；`all` 表示跨全部工作区检索，`limit` 取 1..50 |
| `events.recent` | `{ "limit": 100, "after": 12345 }` | 读取事件溯源台账，客户端重连后用 `after`（自己见过的最后一条台账 id）补播断线期间的终态事件 |
| `model.list` | 无 | 列出可用的 `provider/model` 引用 |
| `model.catalog` | 无 | 返回模型详情、能力和活动状态 |
| `model.switch` | `{ "model": "provider/model" }` | 切换并持久化模型 |
| `model.test` | `{ "model": "provider/model" }` | 发送最小真实请求测试模型 |
| `prompt.optimize` | `{ "text": "...", "model": "provider/model" }` | 使用指定或当前模型优化提示词，不创建 Session |
| `doctor` | 无 | 返回 Runtime 健康检查摘要 |
| `doctor.run` | 无 | 返回结构化 Runtime 与 Provider 检查 |
| `lock.list` | 无 | 返回当前文件写锁的“文件-任务”占用表快照 |
| `factory.reset` | 无 | 清空全局配置、会话与 SQLite 数据，恢复出厂设置并重建数据库 |
| `shutdown` | 无 | 取消全部活动 turn，返回 `bye` 并优雅停止 Daemon |

Session 方法可传 `workspace` 指向具体项目，也可传 `global: true` 使用不绑定目录的全局 Session 空间。`includeArchived` 控制列表是否包含已归档任务。Desktop 在第一次发送消息时才调用 `session.new`，因此新建一个空白任务不会产生无内容的 Session。

`reasoningLevel` 使用统一枚举：`none`、`low`、`medium`、`high`、`max`、`xhigh`、`ultra`。它不是 Provider API 参数；Runtime 会根据模型能力和 Provider 适配后再生成请求。`xhigh`/`ultra` 属于扩展档位，默认最高能力为 `max`，DeepSeek 会明确将二者转换为 `max`。

## Desktop 管理方法

Desktop 设置中心通过结构化方法管理与 CLI 相同的配置，不直接读取配置文件。Provider `apiKey` 以 `secret:` 加密引用落盘，`provider.list` 解密后返回，以便 Desktop 直接显示和编辑。Runtime 不会从环境变量读取 API Key；MCP 环境变量仍仅返回键名，不返回值。

| 方法 | 说明 |
| --- | --- |
| `routing.get` | 返回 `{ "failoverEnabled": true }`，读取失败转移开关 |
| `routing.set` | 参数 `{ "failoverEnabled": false }`，保存失败转移开关 |
| `agent.config.get` | 返回智能体设置（`networkEnabled`、`delegationEnabled`、`visionModel`、`autoVerify`） |
| `agent.config.set` | 部分更新智能体设置（未传字段保持原值）；`visionModel` 传模型引用（须声明视觉能力，否则拒绝）或空串恢复自动选择 |
| `schedule.list` | 列出全部计划任务（含下次运行时间与上次结果） |
| `schedule.create` / `schedule.update` | 参数 `{ "name", "prompt", "cron", "workspace"? , "enabled"? }`；`cron` 为 5 段表达式（分 时 日 月 周，本地时区），非法表达式返回错误 |
| `schedule.toggle` | 参数 `{ "id", "enabled" }`，启用/暂停任务 |
| `schedule.delete` | 参数 `{ "id" }`，删除任务 |
| `schedule.run` | 参数 `{ "id" }`，立即触发一次执行（后台运行，不等执行完成即返回 `started`） |
| `provider.list/upsert/use/remove/test` | 管理和测试 Provider |
| `mcp.list/upsert/remove/reload` | 管理、重连 MCP Server 并刷新工具注册 |
| `skill.list/toggle` | 查询和启用/禁用 Skill |
| `skill.official.list/install` | 返回内置官方技能目录（含安装与启用状态）；`install` 传 `{ "slug" }`，把技能写入 `~/.haoyue/skills` 并启用 |
| `usage.get` | 返回按模型聚合的调用、Token、成本和延迟 |

`project.upsert` 拒绝把用户主目录或 Haoyue 全局状态目录（`~/.haoyue`）注册为项目；`project.remove` 支持 `keepSessions: true`，清理无效项目行时保留数据库中的会话。

Daemon 会先建立 IPC 监听，再在后台初始化 MCP。`mcp.reload`、MCP 配置修改和工作区切换都会先注销旧工具与 Prompt，再串行连接新配置。

**计划任务**：Daemon 启动时内置调度器，每 10 秒检查一次启用中的计划任务，到点后为每个到期任务新建一个 Session 并执行一次 Agent turn（使用隔离 Runtime，与普通任务共享 HttpClient 连接池、熔断器与文件写锁），结果（成功/失败/取消与截断输出）记录回任务。手动 `schedule.run` 会立即触发一次并重新计算下次运行时间。单次执行有超时保护（`agent.scheduledTurnTimeoutSeconds`，默认 30 分钟），超时会中止并记录，避免卡死的回合永久阻塞调度器。计划任务只在 Daemon 运行时才会触发；桌面端关闭后不会自动补跑。

Daemon 对每个 `chat` 请求按 `sessionId` 创建独立的 Agent turn。每个 turn 使用隔离的 Runtime、Prompt/Skills、MCP 注册和事件订阅，因此同一连接或多个连接可以并发运行任意数量的任务；并发度由 CPU、内存、Provider 和本机 I/O 性能共同决定。管理类配置写入仍然串行化，避免配置文件互相覆盖，但不会阻塞已经启动的 Agent turn。

后台记忆固化在 Daemon 启动时以及之后每 6 小时运行一次，它本身也是一个 Agent turn，并使用与对话相同的本地模型。若触发时已有用户回合在飞，固化会推迟 2 分钟后重试（多次请求合并为一次重试）：本地模型同时驻留两份权重会耗尽显存，把 Daemon 直接压到原生层崩溃、回复因此消失。

Daemon 进程内维护一个集中式文件写锁协调器（Task Coordinator），作为所有并发 turn 的文件锁唯一信任源。`write_file` 和 `edit_file` 在修改文件前会先按工作区+文件路径申请写锁：锁空闲则授予并完成修改后释放；锁被其他任务占用时工具最多等待 30 秒，超时后返回明确的失败提示（附当前持有者），要求模型等待并重新读取最新内容后重试。`edit_file` 在拿到锁后才读取文件，因此修改总是作用于磁盘上的最新内容，避免并行任务互相覆盖或产生无效编辑。turn 结束时（含取消）协调器会释放该任务持有的全部锁。可通过 `lock.list` 查看当前占用表。

## Desktop 的 Daemon 生命周期

打包版 Desktop 启动时先连接本地端点。若没有 Daemon，它会从 `resources/runtime` 启动自包含 Runtime，并在 24 次短间隔探测内等待端点就绪。Desktop 只记录自己创建的子进程；退出时向该实例发送 `shutdown`，等待其退出，超时后才终止进程。若启动时连接的是外部 Daemon，退出 Desktop 只断开连接。

Desktop 会把自管 Runtime 的 stdout 与 stderr 落盘到 `<userData>/logs/daemon.log`（Windows 打包版即 `%APPDATA%\haoyue-desktop\logs\daemon.log`），每次启动写入一行分隔标记，进程退出时追加退出码与信号；单文件超过 8 MB 时滚动为 `daemon.prev.log`。原生层崩溃前的最后一条断言（例如显存不足）通常只出现在这两条流上，所以排查"发消息没有回复"应先看这个文件。自动重启多次失败时，通知会直接给出最后一行错误并指向该日志。
