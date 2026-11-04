# 更新日志

记录用户可见变化与工程护栏调整。版本号由 `version.py` 统一管理，发布流程见 [RELEASING.md](RELEASING.md)。更早版本的发布说明见 GitHub Releases；本文件自 2026-10-31 起维护。

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
