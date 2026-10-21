# 故障排查 Runbook（incidents）

> 生效日期：2026-10-21（执行方案 7.8）。本文只写**可执行**的排障路径：每条都按
> 「现象 → 定位命令 → 恢复动作 → 升级路径」四段式组织，命令均为真实存在的 CLI /
> 路径（护栏测试 `RunbookCoverageTests.Incidents_RunbookHasRequiredSections` 校验
> 章节结构与命令词表）。工具用法类内容见 [tools.md](tools.md)，连接器见
> [connectors.md](connectors.md)，部署隔离见 [deployment-isolation.md](deployment-isolation.md)。

## daemon 崩溃或桌面端「正在准备」卡住

- **现象**：桌面端发送消息后长期停在「正在准备」；或回合中途界面失去响应、配置面板
  显示 Runtime 断连；`%APPDATA%\haoyue-desktop\logs\daemon.log` 尾部出现
  `Unhandled exception` 或 `exit code=1`。
- **定位命令**：
  - `haoyue doctor` —— 体检配置/凭据/工作区（退出码 0 = 结构完好，表格红行 = 具体缺项）。
  - `haoyue daemon` —— 前台起一个 daemon，独占观察启动日志与握手（会占住端口，先关桌面端）。
  - `haoyue model list` —— 确认当前激活模型仍存在（配置漂移会让首回合直接失败）。
  - 桌面端「帮助 → 诊断包」导出 zip：含日志尾部（脱敏）、配置快照、契约版本。
- **恢复动作**：
  1. 桌面端右上角「重新连接」；失败则退出应用后重新打开（daemon 随应用重启）。
  2. 若日志显示配置 JSON 损坏（`Config` 行标 `corrupt`），`haoyue doctor` 会给出备份
     文件路径，用备份覆盖 `~/.haoyue/config.json` 后重试。
  3. 仍失败：`haoyue init` 重建工作区骨架（不覆盖已有源码，仅补缺失的目录/模板）。
- **升级路径**：附带诊断包 + `daemon.log` 尾部 50 行提 issue；若为 `exit code=1` 且可
  复现，记录操作序列（含当时使用的模型与工具）——2026-10-14 两次真实故障均靠此定位
  （日志补齐缺失的异常栈 → 显存抢占 → 广播副本 bug）。

## 本地模型加载失败 / 回合迟迟不开始

- **现象**：选择本地模型后回合停在「正在准备」数分钟；`daemon.log` 出现
  `InvalidInputBatch`、`Failed to load model` 或 prefill 耗时异常（正常 GPU prefill
  26.6k token ≈ 7.4s，cpu 后端会劣化到分钟级）。
- **定位命令**：
  - `haoyue model test <provider/model>` —— 单次探活，返回真实失败原因（文件缺失/显存/格式）。
  - `haoyue model info <provider/model>` —— 上下文窗口、量化、后端与可见 GPU 层数。
  - 检查 `runtimes/win-x64/native/` 是否存在 `cuda12/` 目录：**缺失即 CPU 后端**，
    GPU 配置（GpuLayers）会静默失效。
- **恢复动作**：
  1. 重新以 GPU 后端构建/安装：`python build.py --platform windows --target all -p:LlamaBackend=Cuda12`；
     便携版请确认下载的是 CUDA 变体。
  2. 上下文过大导致 OOM：在模型配置里调小 ContextWindow，或换更低量化等级（Q4_K_M）。
  3. `nemotron_h` 等 Mamba2 混合架构禁用 KV 前缀复用（循环状态不支持按 token 区间删除），
     若日志出现 `InvalidInputBatch` 请在配置中关闭该模型的复用开关——该场景会走 prefill
     失败全清自愈兜底，但每轮都会重新 prefill（慢但正确）。
- **升级路径**：附 `model info` 输出 + 完整 prefill 日志；标明显卡型号与驱动版本。

## 显存不足（CUDA out of memory）

- **现象**：加载模型时报 `CUDA out of memory`，或首个回合失败但纯文本短对话正常。
- **定位命令**：
  - `haoyue model info <provider/model>` —— 查看实际 Offload 层数（GpuLayers）与上下文。
  - `haoyue usage --days 1` —— 观察近期回合的 token 规模，判断是否长上下文触发。
  - 任务管理器/`nvidia-smi` 确认无其他进程（浏览器硬件加速、其他推理进程）抢占显存。
- **恢复动作**：
  1. 降低 GpuLayers（部分层留 CPU）或缩小 ContextWindow——两者对显存的影响是线性可预期的。
  2. 关闭其他占用显存的程序后重试；显存碎片严重时重启 daemon。
  3. 换更小量化（Q4_0 / Q3_K_M）；确认模型文件大小与显存容量匹配（fp16 参数量 ≈ 2×字节）。
- **升级路径**：附 `nvidia-smi` 快照 + `model info` + 失败回合的 config 片段。

## MCP 服务器 / 连接器失效

- **现象**：连接器卡片显示「已安装」但工具不可用；`mcp.list` 某行 `connected=false`；
  日志出现 `No such file or directory`（内置本地服务器路径失效）或
  `完整性校验失败`。
- **定位命令**：
  - `haoyue mcp list` —— 每个服务器的连接状态、工具数与错误原因。
  - `haoyue mcp test` —— 逐个真实连接并列出工具（慢但最准）。
  - `haoyue connectors` —— 目录可用性、来源（official 需完整性清单登记为证）。
- **恢复动作**：
  1. **内置连接器（calendar/clipboard/email/http-api/notify/rss）路径失效**：daemon 会在
     连接时自愈 `haoyue_local_server.py` 的旧路径；若仍失败，重新安装该连接器（
     桌面「扩展 → 连接器」点安装）。
  2. **完整性校验失败**：不要强行放行——恢复原始分发内容（重装 haoyue 或
     `python tools/generate_connector_checksums.py` 后提交）；
     仅自建连接器目录可用 `HAOYUE_CONNECTORS_ALLOW_UNSIGNED=1` 显式放行。
  3. **reload 后工具消失**：自 6.2 起 reload 是两段式——失败的服务器会保留旧工具集并
     在状态里注明；若状态无此说明，升级到含该修复的版本。
- **升级路径**：附 `mcp list` 输出 + 该服务器的 args/env（脱敏）+ `connectors.list` 的
  source 字段。

## usage 数据库（haoyue.db）损坏

- **现象**：`haoyue usage` 报 SQLite 错误；桌面端用量面板空白；日志出现
  `database disk image is malformed` / `unable to open database file`。
- **定位命令**：
  - `haoyue usage --days 7` —— 复现并记录错误原文。
  - `haoyue doctor` —— 确认工作区与状态目录完整性（会列出实际使用的 db 路径）。
- **恢复动作**：
  1. 退出所有 haoyue 进程（桌面端 + 任何 `haoyue daemon`），备份 `~/.haoyue/haoyue.db*`
     （含 `-wal`/`-shm`）。
  2. 删除损坏副本后重启：数据库会在启动时重建，**会话历史与知识库条目会丢失**，
     用量统计从头开始；需要保留数据时从备份恢复或先 `haoyue knowledge export`。
  3. 桌面「设置 → 常规 → 恢复出厂设置」可一次性重建全局配置与会话数据（不删项目源码）。
- **升级路径**：附损坏文件的 `PRAGMA integrity_check` 输出与最近一次磁盘满/强杀记录。

## 日志与诊断包使用

- **现象**：需要把现场交给维护者，但不确定该给什么。
- **定位命令**：
  - 桌面：`帮助 → 诊断包`（导出 zip，日志已脱敏）。
  - CLI：`haoyue doctor` 输出可直接粘贴；`haoyue usage` 用于费用/延迟类问题。
  - 日志位置：`%APPDATA%\haoyue-desktop\logs\daemon.log`（Windows，自动轮转）。
- **恢复动作**：按问题类型附材料——崩溃附 `daemon.log` 尾部 + 退出码；模型问题附
  `model info`；工具/连接器附 `mcp list`；费用问题附 `usage --days N`。
- **升级路径**：提 issue 时标注版本（关于页可看 Runtime 协议版本）、系统、复现步骤；
  敏感内容请先自行脱敏（日志中的 API Key 已由 sink 自动遮蔽，自定义提示词不会）。
