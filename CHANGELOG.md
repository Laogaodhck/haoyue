# 更新日志

记录用户可见变化与工程护栏调整。版本号由 `version.py` 统一管理，发布流程见 [RELEASING.md](RELEASING.md)。更早版本的发布说明见 GitHub Releases；本文件自 2026-10-31 起维护。

## [未发布]

### 安全

- **工作区信任门禁**：未受信工作区的 `.haoyue/config.json` 与 `mcp/servers.json` 不再参与加载，克隆来的仓库无法靠「打开一次」取得 MCP stdio / verify 的自动执行权。信任清单存于 `~/.haoyue/trusted-workspaces.json`，经 `workspace.trust` / `workspace.untrust` 显式放行或收回；清单损坏按「无受信工作区」处理（fail closed）。桌面端在未受信工作区顶部提示并可一键放行。
- **路径围栏按真实路径判定**：`PathGuard` 对符号链接 / Windows junction 逐段解析后再做边界与凭证判定；链接链无法解析时按越界拒绝，而不是信任逻辑拼写。
- **Unix socket 权限收紧**：POSIX 上 `~/.haoyue` 与 socket 目录设为 `0700`、socket 文件设为 `0600`，同机其他用户无法连接 daemon。
- **发布产物签名入口**：Windows 安装包与便携版支持 Authenticode 签名（`WINDOWS_CSC_LINK` / `WINDOWS_CSC_KEY_PASSWORD`）；未配置时保持未签名并在文档中如实标注。

### 修复

- **慢消费者不再丢流式内容**：订阅队列饱和时，文本 / 思考增量就地合并、其余事件按序补齐，回答不会在半句处被截断；待发表仍有 256 条上界，内存不会无限增长。
- **桌面 IPC 错误可判定**：`daemon:request` 返回结构化信封，`code` / `method` 不再被 Electron 压成字符串，渲染层可按错误码分支。
- **TS SDK 握手按真实终态收包**：握手等待 `result` 而非不存在的 `ready`，并补充真实 daemon 集成测试。
- **webserver 迁移可重放**：改用 EF Core 迁移替代 `EnsureCreated`，并新增 API 集成测试。

### 工程护栏

- **覆盖率门槛真正生效**：改用 `coverlet.msbuild`（行 ≥70% / 分支 ≥55%），未达标时 `dotnet test` 直接失败；删除不会执行阈值的 `coverage.runsettings`，避免双源。
- **供应链扫描阻断**：NuGet 漏洞报告经包装脚本转为非零退出；`pnpm audit` 对生产依赖 high 级别阻断。
- **CI 跨平台**：新增 Linux 作业，覆盖 Unix socket、符号链接解析、Python 连接器与 TS SDK 集成测试。
- **契约修正**：`config.save.fields`、`connectors.install.values` 建模为任意 JSON 对象，`mcp env` 改为字符串映射；新增 `workspace.trust` / `workspace.untrust` 与工作区信任字段（127 个 RPC 方法）。
- **零告警构建**：runtime 开启 `TreatWarningsAsErrors` 与 AOT / 裁剪分析器，JSON 序列化统一走源生成上下文。
- **巨型类拆分**：`DaemonAdminApi` 拆出 Files / Knowledge / Models，`DaemonServer` 拆出事件转发与用量累计；行数预算护栏继续生效。
- **测试并行污染修复**：修改进程环境变量的测试统一进入非并行集合，知识库同义词相关测试串行化。
- **依赖升级**：Electron 43 → 45；Markdig 统一为 1.3.2（CLI 与 webserver 共用同一版本）。
- **文档事实对齐**：README / 文档索引 / SDK 说明中的用例数、RPC 方法数由源码现场推导，护栏测试强制。

### 桌面

- 未受信工作区显示信任提示与放行入口；项目属性中的工作区配置写入同样以受信为前提。
- macOS 桌面端不再声明为发布目标（CLI 仍提供 macOS 自包含二进制），避免产出缺少自管 runtime 的残缺包。
