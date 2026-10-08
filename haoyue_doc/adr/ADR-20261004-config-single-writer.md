# ADR-20261004-config-single-writer

- 状态：已接受
- 日期：2026-10-04
- 关联代码：`haoyue_runtime/Configuration/ConfigStore.cs`、`haoyue_runtime/Daemon/DaemonConfigBridge.cs`

## 背景

CLI 与 daemon 是两个进程，都会改 `~/.haoyue/config.json`。最初双方各自「读—改—全量写回」，后写的进程把先写进程的并发修改整个抹掉（last-writer-wins per file）。

## 决策

两层收敛（A4 方案）：

1. **CLI 写委托**：CLI 修改配置时，daemon 在线则把「本进程脏字段补丁」（`CaptureDirtyPatch`：相对加载基线的递归 diff）经 `config.save` RPC 交给 daemon 写盘；daemon 离线才本地 `Save()`。
2. **三方合并兜底**：`Save()` 时以「加载基线 vs 磁盘现状 vs 内存值」做字段级合并——本进程没动的字段采纳磁盘值，动了才覆盖，双方并发修改收敛而非互相覆盖。

## 备选方案与取舍

- **文件锁互斥**：解决不了「我基于旧视图的整文件写回」，锁只保证串行化破坏。
- **watch 文件 + 整体重载**：丢字段问题依旧，还引入重载风暴。
- **选定（补丁委托 + 三方合并）**：委托让 daemon 成为在线时的单写者；三方合并覆盖 daemon 离线/多 CLI 并存的剩余场景。

## 后果

- 正面：并发写不再丢数据；补丁体积小，`config.save` RPC 语义清晰。
- 负面：`Save` 依赖基线快照的正确性；基线缺失（首次运行/异常）退化为全量写——已接受，因为该场景下没有并发对端。
- 遗留：默认值散落两处、无 schema 版本——由 [ADR-20261007-config-schema-and-version](ADR-20261007-config-schema-and-version.md) 解决。
