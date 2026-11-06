# 更新日志

记录用户可见变化与工程护栏调整。版本号由 `version.py` 统一管理，发布流程见 [RELEASING.md](RELEASING.md)。更早版本的发布说明见 GitHub Releases；本文件自 2026-10-31 起维护。

## [Unreleased]

### 新增

- **设置 → 编码**：集中管理 Git 与安全工作流。Git 分支前缀（haoyue 新建分支时预填此前缀，留空则不添加前缀）与始终强制推送（haoyue 推送时使用 `--force-with-lease`：改写远端分支历史，但远端已变化时拒绝覆盖）经 `advanced.get/set` 的 coding 节持久化；Worktrees 子区展示当前工作区的 Git 工作树索引（`git worktree list`，支持刷新）；安全子区提供「三层渐进式扫描与自动修复」总开关。
- **Git 工作区面板行为接入**：面板新增「新建分支」（内联输入，按配置的分支前缀预填）与「推送」入口；开启安全开关后推送前先执行密钥 / 口令 / 私钥 / API 令牌快扫（第一层，含文件数与大小上限），命中即列出高 / 中级发现并需确认后再推送。新增主进程能力 `project:git-create-branch`、`project:git-push`、`project:git-worktrees`、`project:security-scan` 及对应 IPC / preload 桥接。
- **规则与记忆可控化**：新增结构化规则能力（`rules.list/save/delete/resolve`）——全局 / 项目作用域、启停与优先级，规则每回合重读、保存后下一回合生效；全局与项目同标题规则冲突时给出提示，并可显式选择「项目胜出 / 全局胜出 / 合并到项目」，解决结果与操作者写入命中记录。事实库新增用户侧管理（`facts.list/add/update/delete/pin/restore/confirm`），置顶事实优先注入；仲裁结果（保留哪条、取代了哪些）可见，误判可一键撤销并形成反向仲裁。规则、事实与命中记录按工作区隔离。
- **事件钩子扩展**：触发事件从 2 类扩到 5 类（`session.start` / `tool.before` / `tool.after` / `task.completed` / `schedule.completed`），支持通配符匹配、1–120 秒超时与失败策略（放行并告警 / 阻断，阻断仅对 `tool.before` 生效）；`session.start` 输出可注入系统提示。新增 `hooks.runs` 查询执行台账，执行结果写入按工作区隔离的 JSONL 台账（`.haoyue/hooks-ledger.jsonl`，默认保留 2000 条，溢出按日期归档到 `.haoyue/hooks/archive/`）。
- **智能体团体**：新增 `team.list/save/delete/copy/run/cancel/runs`，按工作区管理团队与成员（角色、职责、模型、工具白名单、专家），支持顺序执行、并行执行、主管调度、评审制四种协作模式，可设轮次上限、整体超时、成员重试与失败策略（停止 / 跳过继续 / 降级汇总）。`team.run` 受理即返回 runId，进度经新增广播 `team.run.updated` 推送，任意阶段可取消并收敛为 cancelled；daemon 启动时把残留 running 运行对账为 interrupted 并清理临时产物。最终结果汇总为一条普通助手消息，不使用分组卡片。
- **工具细粒度权限**：工作区配置新增 `allowedTools`、`disabledTools` 与 `allowToolFallbackToGlobal`，按「全局硬禁用 > 工作区禁用 > 工作区白名单 > 角色预置 > 团体成员清单 > 单次调用清单」逐层收窄（deny 优先），支持 `server/tool`、`server/*`、`*/tool` 通配符。越权工具降级为「该工具不可用」并记入钩子台账，回合继续而不整体失败；角色 / 成员清单裁空时默认保持空集（fail-closed），仅在显式开启降级开关后回退工作区清单。桌面端「设置 → 工具权限」可编辑白名单与硬禁用清单（同一套模式校验，非法即拒绝保存）、校验模式格式并切换降级开关；保存后按 `config.save` 的 `applied` / `appliedReason` 回执区分「下一回合生效」「写入别的工作区」「当前工作区未受信」三种结果。磁盘上历史手工写入的非法条目在加载时单独列出并提供一键清理，不阻塞其它字段保存。
- **审批决策留痕**：每次审批（内置高危守卫、工具同意、风险分级）写入 `ApprovalDecisionEvent`——工具名、参数摘要、原因、工作区、回合 id、操作者与结果（批准 / 拒绝 / 超时 / 取消），可经 `events.recent` 回放核对；事件裁剪时审批记录单独保留最近 2000 条，不被普通事件挤出。
- **定时任务接入审批通道**：定时运行与手动回合共用交互式审批注册表，高风险工具不再被静默放行；`approval_request` 广播到已连接客户端，120 秒内无响应按拒绝处理（fail-closed），每次运行以 `schedule:<taskId>:<runId>` 标识写入审计。
- **项目任务目录**：项目内发起的后台 / 定时任务在项目根下建立独立目录 `.haoyue/tasks/<taskId>/`，产物写入其 `outputs/`；PathGuard 按真实路径（含符号链接 / junction）在创建前后校验，无法证明在项目内时拒绝受理，绝不静默写回全局目录。任务历史裁剪时对应任务目录一并清理，避免目录无限增长；无项目归属的全局任务保持原落点。
- **侧边栏项目任务**：项目节点下挂任务列表，支持展开 / 折叠（状态持久化）、状态徽标（排队 / 运行中 / 完成 / 失败 / 已取消）与最近时间、运行中一键取消（后台任务走 `task.cancel`、普通回合走 `agent.cancel`）、项目行内 `+` 新建任务；重启后恢复上次选中的会话，存储值失效时回退最近更新。
- **GitHub CLI（gh）连接器**：新增 `github-cli` 官方连接器与独立内置 MCP 服务器（`connectors/servers/haoyue_gh_server.py`），复用本机已登录的 `gh`。暴露 11 个白名单工具：8 个只读（`gh_auth_status` / `gh_repo_view` / `gh_repo_list` / `gh_issue_list` / `gh_issue_view` / `gh_pr_list` / `gh_pr_view` / `gh_pr_checks`）与 3 个显式写（`gh_issue_create` / `gh_issue_comment` / `gh_pr_create`）。参数按正则 / 枚举 / 长度收敛并拒绝 `-` 开头的选项注入，全部以参数列表交给子进程（不经 shell），不提供任意 `gh api` 透传；复用 gh 自身凭据，不读取 / 不落盘 / 不输出 token（输出统一脱敏与截断）。未安装 / 未登录 / 超时 / 404 / 速率限制均返回可执行中文提示。设置 → 扩展的连接器卡片展示 gh 安装版本、登录账号与可用性，并提供安装文档、复制 `gh auth login` 与登录文档引导。工具仍受既有权限体系约束：全局硬禁用 > 工作区 `disabledTools` > 工作区 `allowedTools` > 角色预置 > 团体成员清单 > 单次调用，逐层收窄、deny 优先；越权只返回「该工具不可用」并写入钩子台账；未新增 RPC（复用 `connectors.list` 返回体并附带 `ghCli` 快照）。
- **契约与测试计数（本批合并后）**：RPC 方法 138 → 154，新增广播 `team.run.updated`；桌面 vitest 407 用例（41 个文件），runtime 测试 1400 + CLI 27。新增覆盖测试类：审批留痕、事实用户控制、结构化规则、任务目录、团体同步 / 异步、工具权限、GitHub CLI 探测与连接器约束、侧边栏项目任务与工具权限桌面逻辑。

### 变更

- **连接器目录扩至 12 个**：`connectors.list` 返回体新增 `ghCli` 快照（安装版本、登录账号、可用性、诊断原因），既有字段保持不变；扩展面板据此在 github-cli 卡片内展示状态与引导，无需额外请求。
- **`team.run` 改为异步受理**：不再同步等待整轮完成，立即返回 runId；进度与结果经事件通道推送，`team.cancel` 在排队 / 运行 / 收尾阶段均可取消并统一收敛为 cancelled。桌面端由等待结果改为订阅进度。
- **钩子等待语义可选**：「等待完成」为可选配置，超时只记入台账并告警，不阻断主流程；默认仍为 fire-and-forget。
- **事件裁剪分层**：普通事件仍保留最近 5000 条，审批决策记录单独保留最近 2000 条。
- **`config.save` 回执 `applied` 语义修正**：此前写入「非当前驻留工作区」时也会返回 `applied: true`（与实际内存状态相反）。现在仅在真正同步进当前引擎驻留工作区的内存时为 `true`；桌面端据此区分「已热生效」与「已写入配置文件，切换工作区后生效」，不再统一宣称已同步到内存。新增 `appliedReason`（`applied` / `other-workspace` / `untrusted`），把「写入了另一个工作区」与「写的就是当前工作区、但它尚未受信（内存中没有配置）」分开提示。

### 修复

- **工具权限面板被历史非法条目锁死**：此前磁盘 `disabledTools` 里有一条手工写入的非法模式，加载后整块清单报错并挡住保存。现在加载时把非法条目（非法模式 / 非字符串）单独列出并提供「一键清理」，合法条目照常编辑，其它字段保存不受影响；新输入的模式仍逐行校验、非法即拒绝落盘。
- **后台任务排队取消不收敛**：此前取消令牌在拿到并发槽后才创建，排队中的任务取消是空操作。现取消令牌在受理时创建，排队与运行中取消都以 cancelled 为终态，RPC 立即返回正确快照；只有真正拿到并发槽才归还信号量，避免等待被取消时误释放槽位；取消后即使回合返回成功，也不会把终态覆盖为 completed。
- **审批等待悬挂**：审批注册表与回合取消令牌绑定，回合取消 / 超时时正确收敛等待者，不再遗留悬挂请求或重复响应；定时任务复用同一注册表后，无客户端响应的审批按超时拒绝而非静默放行。
- **设置 → 编码开关点击无反馈**：开关行 `.coding-toggle-row` 缺少 `input:checked` 选中态 scoped 规则（全局样式仅覆盖 provider / model 开关行），点击后状态实际已切换并持久化，视觉上轨道不变色、滑块不动；按既有面板（McpPanel）模式补齐选中态底色、滑块位移与键盘焦点轮廓，`advanced.set` 仍逐键 patch + 失败回滚，行为不变。
- **KnowledgePdfImportTests 并行竞态（间歇失败）**：`PdfTextExtractor` 是进程级静态桥（HaoyueRuntime 装配时注册），`Pdf_WithoutBridge_FailsWithActionableMessage` 将其置空后调用时，并行测试恰好重新注册会导致断言读到「桥已注册」的文案而间歇失败（此前 Release 全量构建曾 1308/1309）。测试类归入既有非并行 `process-env` 集合，消除互踩。

## [1.3.33] - 2026-11-04

### 新增

- **任务监控**：右栏新增「任务监控」页签（固定模式）或独立悬浮窗（浮动模式），实时展示当前任务的进度回顾、执行活动、产出与来源；「设置 → 任务监控」分区控制展示形式与四个内容区块（进度与上下文 / 执行活动 / 结果与来源 / 辅助入口）的开关，偏好经 `advanced.get/set` 的 taskMonitor 节持久化；聚合层单测 13 例
- **事件钩子（Hooks）**：「设置 → 钩子」分区可查看已配置钩子、配置问题逐条诊断与最近执行状态，并支持添加 / 编辑 / 启用 / 立即测试 / 删除。钩子按触发事件（回合完成 `task.completed`、定时任务完成 `schedule.completed`）自动执行 Webhook（POST JSON、可选 Bearer 密钥）或本地命令（30 秒超时、参数化启动不拼 shell）；触发为 fire-and-forget，绝不拖慢 agent 与调度主流程；密钥只回「已配置」标记、不回传明文。设置页显式提示「使用前请确认自动执行的操作及其文件、网络访问安全」。新增 RPC `hooks.list/save/delete/test`，配置持久化于 config.json 的 Hooks 节
- **契约与测试计数**：RPC 方法 134 → 138、runtime 测试 1300 → 1309（新增 HookRunnerTests 9 例：配置诊断六类 + 状态复位）、巨类预算 `DaemonAdminApi` 4200 → 4400（新增 Hooks partial，聚合实测 4318）

## [1.3.32] - 2026-11-04

### 新增

- **doc_toolkit 表格块支持**：解析器把 Markdown 管道表格统一建模为 `TableBlock`（合并表头解析、单元格归属），docx / xlsx / pptx / markdown 渲染器对应输出真实表格——docx 用原生 Word 表格、xlsx 用行列写入、pptx 用图形表格、markdown 原样保真，不再退化为等宽文本。`doc_render` 文档创作链同步收益，含单元格对齐 / 边框 / 表头样式映射。
- **豆包式多模态能力**：新增 `image_edit`（图片编辑，与文生图共用 OpenAI 兼容 `images/generations` 端点，多传参考图——本地图转 `data:image/<fmt>;base64,<...>` data URI 或 HTTP(S) 图床 URL，交互式编辑支持 `<bbox>` / `<point>`，风格迁移 / 元素替换 / 局部修改 / 消除 / 延伸构图；未配置图像模型时工具自动隐藏）、`video_generate`（视频生成，seedance 同源异步任务：`POST {baseUrl}/contents/generations/tasks` 建任务 → 轮询 `GET .../tasks/{id}` → 下载 MP4 落 `.haoyue/outputs/videos/`，超时返回任务 ID 交 `video_status` 续查；`videoGen.model` 未配置时隐藏）、`speech_to_text` / `text_to_speech`（语音转写与合成，火山 openspeech 同源：ASR 录音文件极速版 `volc.bigasr.auc_turbo`、大模型语音合成 seed-tts-2.0，`speech.apiKey` 支持 `secret:` 加密引用，未配置时两个语音工具自动隐藏；TTS 流式多 JSON 响应逐块解码拼接）。全部复用现有 OpenAI 兼容 provider 配置体系与「产物落盘 + 视觉核验回灌」链路。
- **语音与媒体 RPC**：`speech.transcribe`（audioPath / audioUrl / audioData base64 三通道 → 文本）与 `speech.synthesize`（text / speaker → 落盘音频文件），契约方法 132 → 134，TS SDK 与桌面端契约类型同步重生成。
- **桌面端语音与媒体播放**：输入框工具条新增录音按钮（MediaRecorder 录音 → 16kHz 单声道 PCM WAV → daemon 转写，文本追加进输入框）；助手消息操作区新增朗读按钮（`speech.synthesize` → 主进程读媒体 → 就地播放）；消息内容中的视频 / 音频产物（`.mp4/.webm/.mov/.m4v/.mp3/.wav/.ogg/.opus/.m4a/.flac` 绝对路径）自动渲染为内联播放器（上限 4 个，经新增 IPC `app:read-media-base64` 读入，扩展名白名单 + 体积上限 audio≤30MB / video≤150MB）。
- **设置面板**：「设置 → 模型与提供商 → 外部能力」新增「视频生成」（模型引用 / 画幅 / 时长）与「语音服务」（API Key / 音色）两组配置，读写走既有配置文档加载与 diffPatch 保存。

### 桌面

- **对话框改豆包风格**：输入框从 940px 横贯收窄为 720px 居中胶囊（26px 大圆角、细边框、双层柔和阴影），发送钮加大为 37px 圆形主题色并带光晕（hover 加深）；空会话（无任务或任务无消息且运行时在线）时输入框脱离底部悬浮垂直居中并加宽至 820px，欢迎区上移让位，有消息 / 加载中 / 加载失败 / 离线自动回落底部固定布局。语音 / 附件 / 优化 / Agent 模式 / 模型 / 推理深度 / 联网 / 停止 / 发送全部功能按钮保留原位与交互。
- **右栏工作区面板改豆包风格**：面板头部 46 → 52px、左上 14px 圆角（呼应 workspace-main 圆角语言），页签从下划线式 IDE 页签改为 32px 胶囊式（hover 为 surface-hover 胶囊、激活态为 accent-soft 软色底 + accent 文字）；刷新 / 终端 / 关闭 / 拖拽调宽（300–720px，缩至 300px 自动关闭）与工具运行中徽标全部保留，五个内容组件（更改 / 提交记录 / 工具活动 / 浏览 / 后台任务）零改动。
- **会话列表豆包式对话摘要**：侧栏会话项标题下方新增摘要行——取首条用户消息正文（压缩空白、单行截断 46 字，title 悬浮显示全文），无用户消息自动隐藏；标题 + 项目标签 + 运行中 spinner 归入标题行，按项目 / 按时间 / 项目分组三处视图同步生效。
- **修复厂商前缀模型 ID 导致活动模型解析失败**：OpenAI 兼容网关常返回 `deepseek/deepseek-flash` 这类带斜杠的模型 ID，活动模型此前会被误拆成「提供商 deepseek + 模型 deepseek-flash」，解析失败后静默回退到全部模型（典型表现是调用落到本地 GGUF 并报文件不存在）。现在先按当前提供商拼接解析，再退回原值以兼容已存的全限定引用。
- **新增提供商支持 cURL / Python 导入**：「新增模型提供商」顶部新增可折叠导入区，粘贴 cURL、Python（OpenAI SDK / requests）或 JSON 调用片段后一键解析 API 地址、Bearer / api_key 与模型 ID，并自动回填表单；识别到预设厂商域名时同步切换接口协议。导入只在本地解析、不上传代码，粘贴内容回填后立即清空；API Key 仍走原有加密引用保存。
- **新增模型提供商支持国内云厂商预设**：「接口协议」下拉新增阿里云百炼、火山方舟、腾讯云混元、百度千帆、智谱 GLM 与 DeepSeek。选中后自动预填 OpenAI 兼容 Base URL，并按厂商给出模型 ID 与 API Key 示例；手动填过的地址不会被覆盖。运行时把这些厂商 kind 统一映射到 OpenAI 兼容客户端，健康检查、模型列表拉取与失败转移行为与普通 OpenAI 提供商一致；`provider.upsert` 校验并保留厂商 kind。
- **子智能体配置（Qoder 式 Markdown）**：「设置 → 智能体」新增子智能体列表与编辑器，兼容 `~/.haoyue/agents/*.md`（用户级）与 `<项目>/.haoyue/agents/*.md`（项目级）。frontmatter 声明名称、描述、模型与工具白名单，正文即工作指令；项目级覆盖用户级、同名覆盖内置预设，保存后下一次委派立即生效。委派工具按当前工作区动态列出可用子智能体，指定模型只作用于该子智能体（留空继承活动模型）。新增 RPC `agent.subagents.list/save/delete`。
- **设置新增「专家」分区**：原「智能体」页底部的专家参数整体移到独立的「专家」分区——回合步数、思考与工具输出预算、Bash 与计划任务超时、自动修复与续写次数、委派配额、事实注入、会话保留、记忆固化、人格、主系统提示词、额外可读目录与计划任务结果回调；原「高级设置 → 专家参数（直接编辑配置文档）」的 JSON 编辑器也并入「专家」，「高级设置」只保留网络与桌面权限及出厂重置。「智能体」只保留模型选择与行为边界，计划任务页的回调提示同步指向新位置。
- **回合预算与压缩保留比例移入「推理加速」**：单回合 token / 费用 / 时长上限与「压缩时保留的最近对话比例」从「智能体」迁到「推理加速」，与本地模型加载参数、驻留与显存治理同页管理；读写仍走 `agent.config.get/set`，现有配置无需迁移。

### 优化

- **本地模型无思考模式强制收束思考段**：R1 蒸馏等本地模型在未开启思考时仍会自发输出思考链（模板含 `</think>` 分隔符、训练使然，提示词无法抑制），此前会把整个输出窗口耗在不可见的思考上——CPU 实测 256 token 思考吃满预算、回答为空、总时长 50s+（真实回合 MaxTokens 更大时思考以分钟计）。现给无思考模式的思考段加字符上限（约 512 字符 ≈ 256 token，8B CPU 约 40–60s），耗尽即注入收束标记强制进入作答，回答不再为空、总时长有界；有思考模式仍按推理等级档位预算，不受影响。Stateless / KV 复用 / 多模态三条生成路径统一生效。
- **本地模型上下文注入预算减半**：本地模型（CPU/低算力）的记忆与 AGENTS.md 注入预算从 10000 token 降为 5000，优先保住核心指令，缩短大提示 prefill 与首 token 时间。
- **Windows 发布启用 CUDA12 本地推理后端**：Windows 打包自动以 `-p:LlamaBackend=Cuda12` 发布，NVIDIA 机器上 GGUF 权重卸载到显存（3070Ti 实测 DeepSeek-R1-0528-Qwen3-8B-Q4_K_M 解码约 39 tok/s，对比 CPU 约 5.4 tok/s，提速约 7 倍；一次简单问答从约 51s 降到约 11s）。无 GPU 的机器不受影响（运行时按可用后端加载）。
- **模型切换上下文连通性**：会话中随时切换模型（本地 ↔ 云端、不同提供商），历史消息完整延续——助手消息带来源模型标识（`ModelRef`）、思考段独立存储不污染正文，新模型按自身窗口自动适配；本地各模型独立 KV 缓存互不串扰，故障转移复用同一历史。该行为经真实模型往返实测（本地 → 云端 → 本地）并固化 Agent 层回归测试 2 例。

### 修复

- **本地模型加载期「停止键无反应」**：`LLamaWeights.LoadFromFile` 是同步读盘 + tensor 解析，此前 `Task.Run(..., ct)` 的取消令牌只在任务调度前生效——权重（4GB+，CPU 上可达数十秒）一旦开始加载，按停止键只能干等到加载完成，表现为「点了完全没反应」。现改为不受回合取消约束的后台加载 + `WaitAsync(ct)`：取消立即返回（探针实测冷加载期 5ms、生成中 138ms/1ms），后台加载完成后原子挂回缓存槽（`TryPutSlot` 防并发覆盖），下次请求直接复用已完成的加载，不泄漏权重也不重载。
- **doctor 对方舟 403 误报**：HealthChecker 原用手写的 `max_tokens=1 + stream=false` payload 探测方舟，与 reasoning 模型的参数要求不兼容，绕过真实客户端请求构造导致 doctor 显示 offline · auth failed (HTTP 403)，而真实调用链（provider test / agent 回合）正常。现改为注入 `ILlmClientFactory` 走与 provider test 相同的 `LlmRequest + client.StreamAsync` 管线，doctor 与真实调用链结果一致，透出方舟真实业务错误（如账户欠费）。
- **可用提供商误报 offline（探测目标与真实使用不一致）**：HealthChecker 此前固定用提供商的第一个模型探测——tokenhub 首模型 `deepseek/deepseek-flash` 免费额度耗尽返回 402，而配置的活动模型（`deepseek-v4-flash-202605`）调用正常，doctor 面板误导。现按配置的 provider / model 精确定位活动模型（支持 `/model` 后缀匹配），找不到再回退首模型与通用 ID，doctor 与真实调用链一致。
- **Windows 无真实 bash 时的执行缺陷**：本机只有 WSL bridge bash（`System32\bash.exe`）时，bash / terminal 工具选中它，命令经 Windows→WSL 参数转换后 `$` 变量展开为空、stdout 管道整体丢失，带变量的命令静默出错（命令截断 / 超时 / 终端 cwd 注记三个沙箱类测试同源失败）。现跳过 System32 与 WindowsApps 的 WSL bridge 别名、只接受真实 bash，否则回退 pwsh；pwsh 路径改 `-EncodedCommand`（UTF-16LE Base64 原文执行）绕开命令行引号解析，terminal 的 PowerShell 会话同路径。
- **Windows Job Object 分配竞态加固**：把进程 Assign 进沙箱围栏在并发负载下偶发瞬时失败，且原实现错误码恒为 0（P/Invoke 返回后 LastWin32Error 被后续 managed 调用重置，属诊断失真）。现 Assign 失败短重试（150ms×2）吸收进程启动过渡窗口，并透出真实 Win32 错误码；computer_exec 与 bash 沙箱共用该加固。

## [1.3.31] - 2026-11-02

### 新增

- **局域网连接**：桌面端「设置 → 局域网连接」可在「本机 Runtime」与同网段另一台电脑的 Runtime 之间切换。被连接方开启局域网监听（默认关闭，端口 47615，可改绑定地址），连接方填写地址、端口与令牌即可接入，支持测试连接与令牌查看/复制。局域网握手使用一次性 nonce + HMAC-SHA256 证明，令牌不上线、跨连接不可重放；并发上限 16，认证失败延迟断开。监听为明文 TCP（仅令牌不上线），仅建议用于可信局域网。新增 RPC `daemon.lan.get` / `daemon.lan.set`（热生效，无需重启），桌面连接偏好持久化于 `<userData>/connection.json`（POSIX 0600）；切换目标后会清空旧 Runtime 的会话列表并按新目标重新加载。

### 安全

- **技能说明书的链接与图片地址过滤**：Markdown 渲染前先校验链接/图片目标，`javascript:`、`data:` 等非白名单协议统一置为 `#`（可见文字保留）；技能主页在发布时要求完整的 http(s) 地址，渲染时不合格值降级为纯文本，第三方技能包无法借说明书注入脚本。
- **初始化与注册入口收紧**：首次初始化只接受部署方生成的 setup token（或本机回环 + 未初始化状态），注册开关默认关闭，外部无法抢占首个管理员账号。
- **会话与角色撤销即时生效**：Cookie 每次请求按数据库复核用户与角色，降权、改名校验即时反映；Blazor 管理页的高权限操作执行前再查一次数据库，长连接电路不会沿用旧角色。
- **密钥与配置原子落盘**：凭据库、daemon 令牌、workspace / 全局配置文件改为「同目录临时文件 + 落盘 + 原子替换」（POSIX 0600），写入中断不再留下截断文件；损坏的凭据库自动隔离为 `*.corrupt-*.bak` 而不是覆写。
- **桌面端 IPC 校验发送方与路径**：所有 `ipcMain.handle` 通道只接受主窗口顶层 frame，`<webview>` guest 与 iframe 一律拒绝；渲染层传入的路径必须是绝对路径，相对路径不再按主进程 cwd 解析。
- **CLI 密钥默认不走命令行**：`provider add/edit` 新增 `--api-key-stdin`，`connectors install --set KEY=-` 从标准输入读值；仍用 `--api-key` / `KEY=value` 时明确提示密钥会留在 shell 历史与进程列表里。
- **工作区信任门禁**：未受信工作区的 `.haoyue/config.json` 与 `mcp/servers.json` 不再参与加载，克隆来的仓库无法靠「打开一次」取得 MCP stdio / verify 的自动执行权。信任清单存于 `~/.haoyue/trusted-workspaces.json`，经 `workspace.trust` / `workspace.untrust` 显式放行或收回；清单损坏按「无受信工作区」处理（fail closed）。桌面端在未受信工作区顶部提示并可一键放行。
- **路径围栏按真实路径判定**：`PathGuard` 对符号链接 / Windows junction 逐段解析后再做边界与凭证判定；链接链无法解析时按越界拒绝，而不是信任逻辑拼写。
- **Unix socket 权限收紧**：POSIX 上 `~/.haoyue` 与 socket 目录设为 `0700`、socket 文件设为 `0600`，同机其他用户无法连接 daemon。
- **发布产物签名入口**：Windows 安装包与便携版支持 Authenticode 签名（`WINDOWS_CSC_LINK` / `WINDOWS_CSC_KEY_PASSWORD`）；未配置时保持未签名并在文档中如实标注。
- **内置浏览器权限收口与空白名单收紧**：桌面端 webview 的空白名单不再「全部放行」——默认只允许 localhost（127.x / ::1），外网站点必须显式加入「允许的网站」；guest 会话权限只保留全屏与剪贴板写入，摄像头、麦克风、屏幕共享、定位、通知、USB / 串口 / HID 一律拒绝。`web_fetch` 的空白名单语义保持不变（视为未启用白名单）。

### 修复

- **daemon 日志落盘前脱敏**：按整行拼接后再抹密钥（stdout 分块切断的 `Authorization` / `api_key` 也能识别），日志文件本身不再含明文凭据；诊断包与 UI 出口仍保留二次脱敏。
- **断线不再挂起审批**：Runtime 断开时清空各任务的待审批状态与模型预加载，审批回传失败会提示「审批已失效」，不再出现点了没反应的弹窗；电脑操作浮层的「停止」只取消发起它的那个会话。
- **知识库网页抓取流式限流**：响应体按块读取，超过上限立即中止（含无 Content-Length 的无限流），不再把整页读进内存。
- **MEMORY.md 并发写保护**：读-改-写加进程内闸门与跨进程文件锁，多个会话同时记忆不再互相覆盖。
- **撤销账本条数上限**：最多保留 64 个回合账本（淘汰最旧），恢复文件走原子写，长时间运行内存不再单调增长。
- **交互式 CLI 退出不再丢回合**：`haoyue chat` 在回合进行中退出会先取消并等待收尾，退出码取最后一轮结果；ANSI 剥离覆盖 CSI / OSC / 字符集序列，截断不再吞掉后续文字。
- **公式与金额不再误判**：行内代码与 `~~~` 围栏里的 `$` 不参与公式渲染，`$5 到 $10` 这类金额保持原文；prompt 里的 `datetime` 固定为 invariant 格式，不再随宿主语言变化。
- **会话图片与草稿有上限**：历史图片缓存最多 48 张并按最久未用淘汰；草稿只把文字与文件写入 localStorage（base64 图片留在内存），删除任务时同步清理草稿；流式渲染的定稿块缓存上限 256 条。
- **状态根重定向后工作区识别正确**：`HAOYUE_HOME` 指向别处时，`~/.haoyue` 仍按用户级状态处理，用户主目录不会被误认成项目根。
- **慢消费者不再丢流式内容**：订阅队列饱和时，文本 / 思考增量就地合并、其余事件按序补齐，回答不会在半句处被截断；待发表仍有 256 条上界，内存不会无限增长。
- **桌面 IPC 错误可判定**：`daemon:request` 返回结构化信封，`code` / `method` 不再被 Electron 压成字符串，渲染层可按错误码分支。
- **TS SDK 握手按真实终态收包**：握手等待 `result` 而非不存在的 `ready`，并补充真实 daemon 集成测试。
- **webserver 迁移可重放**：改用 EF Core 迁移替代 `EnsureCreated`，并新增 API 集成测试。
- **PDF 导入桥参数不再被引号拆开**：doc_toolkit 提取改用 `ArgumentList` 逐项传参，路径含空格 / 引号时不会拼成另一条命令行；超过 10 分钟的超时改为终止整棵 python 进程并明确报错，不再留下占 CPU 的提取进程。
- **daemon 请求行有硬上限**：握手与常规请求统一经 `BoundedLineReader` 读取，超长行在读取阶段即被截停（上限覆盖 40MB 图像的 base64 体积），不再把整行读进内存。
- **同步阻塞点异步化**：知识库 URL 抓取、Linux 浏览器主页修复、`HaoyueRuntime.Dispose()` 不再 `sync-over-async` 阻塞线程池，避免宿主带同步上下文时死锁。

### 工程护栏

- **测试状态根隔离**：测试进程启动时把 `HAOYUE_HOME` 指向临时目录，跑测试不再往开发者的 `~/.haoyue` 写用量日志、会话与凭据；README 用例数快照同步（runtime 1262 / CLI 27 / desktop 357）。
- **覆盖率门槛真正生效**：改用 `coverlet.msbuild`（行 ≥70% / 分支 ≥55%），未达标时 `dotnet test` 直接失败；删除不会执行阈值的 `coverage.runsettings`，避免双源。
- **供应链扫描阻断**：NuGet 漏洞报告经包装脚本转为非零退出；`pnpm audit` 对生产依赖 high 级别阻断。
- **CI 跨平台**：新增 Linux 作业，覆盖 Unix socket、符号链接解析、Python 连接器与 TS SDK 集成测试。
- **契约修正**：`config.save.fields`、`connectors.install.values` 建模为任意 JSON 对象，`mcp env` 改为字符串映射；新增 `workspace.trust` / `workspace.untrust` 与工作区信任字段（129 个 RPC 方法）。
- **零告警构建**：runtime 开启 `TreatWarningsAsErrors` 与 AOT / 裁剪分析器，JSON 序列化统一走源生成上下文。
- **巨型类拆分**：`DaemonAdminApi` 拆出 Files / Knowledge / Models，`DaemonServer` 拆出事件转发与用量累计；行数预算护栏继续生效。
- **测试并行污染修复**：修改进程环境变量的测试统一进入非并行集合，知识库同义词相关测试串行化。
- **依赖升级**：Electron 43 → 45；Markdig 统一为 1.3.2（CLI 与 webserver 共用同一版本）；桌面端按审计结论收紧 `markdown-it`（14.3.3）与 `dompurify` / `undici` 下限，`pnpm audit --prod` 高危归零。
- **平台无关的失败路径测试**：不可写日志位置改用「父路径是普通文件」，不再依赖 Windows 保留设备名或非管理员账户语义。
- **文档事实对齐**：README / 文档索引 / SDK 说明中的用例数、RPC 方法数由源码现场推导，护栏测试强制。
- **测试工程同样零告警**：`haoyue_tests` 开启 `TreatWarningsAsErrors`，xUnit 分析器与可空性告警全部清零；异步测试改用 `ReadAsync` / `WaitAsync`，不再同步阻塞。
- **前端 lint 与组件预算**：桌面端接入 ESLint 9（TS + Vue）与 Prettier，`pnpm lint` 进入 CI；新增 `guard:components` 行数预算脚本，`App.vue` 与设置对话框拆分后纳入护栏。
- **webserver 安全头与静态资源清单**：响应统一附带 CSP、`X-Content-Type-Options`、`X-Frame-Options: DENY`、Referrer-Policy 与 Permissions-Policy，API 与 HTML 页面均有集成测试；`wwwroot/lib` 的前端库新增 `manifest.json`，VendoredLibTests 校验哈希、版本下限与引用覆盖。
- **覆盖率按模块可见**：在 runtime 70/55 总门槛之外，新增 CLI（15/15）与 webserver（45/15）独立门槛，低位模块不再靠总阈值掩盖。
- **CI 动作升级**：工作流统一升级到 actions/checkout@v7、setup-dotnet@v6、upload-artifact@v7 等当前主版本，Ubuntu 运行器固定为 `ubuntu-24.04`，避免隐式漂移。
- **回合主循环拆分**：`RunTurnAsync` 的回合状态与工具结果处理抽到 `Agent.Turn.cs`，主循环只保留编排；行为不变，巨型方法不再继续膨胀。

### 桌面

- **设置控件统一为下拉与拨杆**：常规页的外观、回复语言、发送快捷键，规则与记忆页的记忆管理方式，进化页的反思间隔，MCP 添加/编辑页的连接方式与身份验证方式改为下拉选择；工具执行策略、内置高危命令守卫、风险分级闸门、Bash 沙箱改为开关拨杆，控件风格与其余设置项一致。
- **输入框工具条精简**：模型触发器只显示去掉 provider 前缀与量化后缀的短名，思考深度触发器只保留图标与档位（低 / 中 / 高 / 最大 / 极高 / 超级）；完整模型 id、档位全称与说明移到菜单和悬浮提示，窄窗口下不再挤成省略号。
- **界面重构为 IDE 工作台风格**：标题栏、侧栏与主区域改为扁平细边与紧凑行高；对话改成 Agent 转录样式——消息顶部标注角色与模型、用户输入左对齐为指令块、思考与工具日志使用等宽字体；新增底部状态栏，常驻显示 Runtime 连接与地址、当前模型、运行阶段与运行中任务数，离线时可点击重连；右栏改为编辑器式页签，设置页采用 IDE 导航与紧凑表单。
- 未受信工作区显示信任提示与放行入口；项目属性中的工作区配置写入同样以受信为前提。
- macOS 桌面端不再声明为发布目标（CLI 仍提供 macOS 自包含二进制），避免产出缺少自管 runtime 的残缺包。
- **新版本提示**：打包版启动时静默检查 GitHub Releases 最新正式版，有新版本才弹原生对话框并提供「前往下载」；「关于」窗口新增「检查更新」按钮，可显示「已是最新 / 发现新版本 / 暂时无法检查」（离线、限流、超时均静默降级，不阻塞启动）。检查只读取版本信息，不下载、不执行远端代码。
