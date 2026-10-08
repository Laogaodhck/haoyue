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
