# contracts/ — Daemon 跨语言契约快照

`daemon-contract.json` 是 `haoyue_runtime` 的 `DaemonContract`（唯一事实源）导出的协议快照，供 TS 侧生成类型使用。**禁止手工编辑本目录文件。**

## 同步命令

```bash
# 1. C# 契约变更后更新快照
HAOYUE_UPDATE_CONTRACT=1 dotnet test haoyue_tests --filter Contract_Export_MatchesSnapshot

# 2. 重新生成桌面端 TS 类型
cd haoyue_desktop && pnpm sync:contract
```

两项操作完成后一起提交。详见 `haoyue_doc/跨语言契约治理-2026-10-07.md`。

## 握手与别名

- 连接建立后的**首帧**必须是 `{"method":"handshake","params":{"token":...,"protocolVersion":...}}`；
  15 秒内未认证的连接会被断开。该帧不属于 `methods` 目录（发生在方法分发之前），
  结构见快照的 `handshake` 节点，成功响应与 `protocol.info` 同构。
- `methodAliases` 记录分发层保留的兼容别名（当前为 `agent/runTurn` → `agent.runTurn`），
  别名不在 `methods` 中重复登记，避免能力面重复。

## 方法消费面

- `protocol.info` / `contract.export` / `lock.list` 是内部诊断与工具接口，不要求桌面端 UI 消费；
  `session.resume` 供外部客户端断线重连。
- 钩子执行台账通过 `hooks.list` 的 `ledger` 节点返回，不再单列 RPC。
- `knowledge.search` 是公开的单笔记本检索入口（桌面端默认走 `knowledge.retrieve`，
  CLI 直接调用运行时 API），不是重复面。
