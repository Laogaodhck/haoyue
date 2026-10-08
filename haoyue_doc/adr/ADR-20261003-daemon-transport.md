# ADR-20261003-daemon-transport

- 状态：已接受
- 日期：2026-10-03
- 关联代码：`haoyue_runtime/Daemon/DaemonServer.cs`

## 背景

桌面端、CLI 与运行时核心需要一个进程间通道：低延迟流式（token 级增量）、支持并发多连接、单机场景（不需要跨机器）、Windows 为主但也要求 Linux/macOS 可用。

## 决策

daemon 监听 **Windows Named Pipe / Unix Domain Socket**，应用层协议为 **JSONL 行协议**（每行一个 JSON 信封：`{id, event, data, details?, sessionId?}`），握手用 `~/.haoyue/daemon.token` 做本地认证。

## 备选方案与取舍

- **HTTP + SSE/WebSocket**：跨语言生态最成熟，但要引入端口管理（端口冲突、防火墙弹窗）、本机任意进程可访问的安全面（需额外认证层）、以及更重的连接生命周期管理。单机场景下这些成本全是负资产。
- **gRPC**：强类型好，但为每个事件类型定义 proto 并维护多语言 stub 生成链，对一个本机单用户协议过重；且流式取消语义在 gRPC 里实现繁琐。
- **选定（Named Pipe + JSONL）**：零端口占用、OS 级权限边界（管道默认仅当前用户可连）、token 握手再加固一层；JSONL 用任何语言 20 行代码即可对接，无 stub 生成链。

## 后果

- 正面：无网络面暴露；连接生命周期与进程生命周期天然一致；协议调试只需 `cat`/`nc`。
- 负面：跨机器管理（remote daemon）不在能力范围内——若未来需要，将走新增传输层适配而非改协议。
- JSONL 早期无 schema 约束，造成跨语言类型漂移，已由 [ADR-20261007-contract-first](ADR-20261007-contract-first.md) 补救。
