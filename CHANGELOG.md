# 更新日志

记录用户可见变化与工程护栏调整。版本号由 `version.py` 统一管理，发布流程见 [RELEASING.md](RELEASING.md)。更早版本的发布说明见 GitHub Releases；本文件自 2026-10-31 起维护。

## [未发布]

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

- **输入框工具条精简**：模型触发器只显示去掉 provider 前缀与量化后缀的短名，思考深度触发器只保留图标与档位（低 / 中 / 高 / 最大 / 极高 / 超级）；完整模型 id、档位全称与说明移到菜单和悬浮提示，窄窗口下不再挤成省略号。
- **界面重构为 IDE 工作台风格**：标题栏、侧栏与主区域改为扁平细边与紧凑行高；对话改成 Agent 转录样式——消息顶部标注角色与模型、用户输入左对齐为指令块、思考与工具日志使用等宽字体；新增底部状态栏，常驻显示 Runtime 连接与地址、当前模型、运行阶段与运行中任务数，离线时可点击重连；右栏改为编辑器式页签，设置页采用 IDE 导航与紧凑表单。
- 未受信工作区显示信任提示与放行入口；项目属性中的工作区配置写入同样以受信为前提。
- macOS 桌面端不再声明为发布目标（CLI 仍提供 macOS 自包含二进制），避免产出缺少自管 runtime 的残缺包。
- **新版本提示**：打包版启动时静默检查 GitHub Releases 最新正式版，有新版本才弹原生对话框并提供「前往下载」；「关于」窗口新增「检查更新」按钮，可显示「已是最新 / 发现新版本 / 暂时无法检查」（离线、限流、超时均静默降级，不阻塞启动）。检查只读取版本信息，不下载、不执行远端代码。
