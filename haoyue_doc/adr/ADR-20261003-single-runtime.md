# ADR-20261003-single-runtime

- 状态：已接受
- 日期：2026-10-03
- 关联代码：`haoyue_runtime/`、`haoyue_cli/haoyue_cli.csproj`

## 背景

需要决定 CLI、桌面端与「核心 Agent 能力」三者的进程与代码组织方式：是拆成独立后端服务，还是共享一个运行时库。

## 决策

**单一 .NET 运行时库（haoyue_runtime）承载全部核心能力**；CLI（haoyue_cli）以 `ProjectReference` 直接引用运行时，与 daemon 进程共享同一套类（`ConfigStore`、`ToolRegistry`、会话存储）。桌面端不直接引用 .NET 代码，经 daemon 通道交互。

## 备选方案与取舍

- **独立后端服务（CLI 经 HTTP 调用）**：边界清晰，但 CLI 离线场景（daemon 未启动）也要能用，得维护两套入口与两套错误语义。
- **选定（共享运行时库）**：默认值、校验、合并逻辑天然单源（不存在 CLI/服务端各写一份的漂移）；CLI 可在 daemon 离线时直连运行时降级工作。
- 风险与缓解：CLI 与 daemon 同时读写配置文件会互相覆盖——通过 [ADR-20261004-config-single-writer](ADR-20261004-config-single-writer.md) 的三方合并 + 委托写入解决。

## 后果

- 正面：零接口层即可复用全部能力；发布物是一个自包含单文件 exe。
- 负面：CLI 与运行时编译耦合，运行时内部重构可能牵连 CLI；用「daemon 在线时 CLI 业务写操作委托 daemon」的纪律缓解写路径分叉。
