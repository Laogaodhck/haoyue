# ADR-20261015-巨型类行数预算与拆分策略

## 状态

已采纳（2026-10-15，随批次 E 护栏固化落地）

## 背景

评估文档（haoyue_doc/Agent能力与工程缺口评估-2026-10-14.md）指出四个巨型文件已成为
改动热点与认知负担：

| 文件 | 行数（2026-10-15 基线） | 角色 |
|---|---|---|
| `haoyue_runtime/Daemon/DaemonAdminApi.cs` | 3160 | 88 个 admin RPC 的实现聚合 |
| `haoyue_runtime/Daemon/DaemonServer.cs` | 2996 | daemon 消息分发 + 会话/事件泵 |
| `haoyue_desktop/src/renderer/src/components/SettingsDialog.vue` | 2590 | 全部设置面板 |
| `haoyue_desktop/src/renderer/src/App.vue` | 2331 | 桌面主视图编排 |

需要决定：大规模拆分，还是冻结增长。

## 决策

1. **现在不做大规模拆分**。四个文件都是「契约/调度聚合点」：DaemonAdminApi/DaemonServer
   被 Contract First 契约治理测试（快照一致、分发 case 全登记、renderer 事件穷举）与
   Prompt 前缀缓存稳定性契约锚定；App.vue/SettingsDialog.vue 与桌面 typecheck 紧耦合。
   搬迁式重构的风险（契约漂移、缓存契约破坏、Electron 验证盲区）大于当前收益。
2. **冻结增长：行数预算护栏**。四个文件各设行数上限，由
   `haoyue_tests/DocsFactsTests.cs` 的 `GiantClass_LineBudget_Maintained` 强制：
   `DaemonAdminApi ≤ 3250`、`DaemonServer ≤ 3100`、`SettingsDialog.vue ≤ 2700`、
   `App.vue ≤ 2400`。超额即测试失败——这是刻意设计：**任何继续膨胀必须先经历一次
   显式的预算上调评审（改本 ADR 状态行 + 预算数字），而不是默认允许**。
3. **拆分路径登记（触发条件式，非排期）**：
   - DaemonAdminApi：按域拆 partial class（providers / knowledge / evolution / tasks / models…），
     纯文件组织、无行为变化，风险最低；预算超限时首选。
   - DaemonServer：先抽 switch 分发到 `per-method` 注册表，再按域 partial。
   - SettingsDialog.vue / App.vue：按面板抽 SFC 子组件；需在拆分前补齐交互回归
     （Electron GUI 冒烟本机不可信，依赖 vitest + typecheck 保障）。
4. **新代码禁入**：新增 RPC 实现优先放独立文件的新类（如已存在的
   ConnectorDaemonApi/OfficialSkillDaemonApi 模式），不再向 DaemonAdminApi 堆叠；
   新增设置面板放独立组件。这一条与预算护栏共同构成「结构不再恶化」的双重闸门。

## 权衡

- **立即拆分 vs 冻结增长**：拆分的可测收益（可读性、合并冲突减少）真实但非紧迫；
  在覆盖率分支门槛刚建立、契约护栏刚固化的时点，大搬迁会放大回归面。预算护栏把
  「何时必须拆」变成可度量的客观条件。
- **partial class vs 语义拆分**：partial 只改善物理组织不降低耦合，但配合「新代码禁入」
  可以让边界自然浮现，比一次性人为划域更稳。
- **预算余量（~3%）**：留出小修小补空间，避免护栏高频误伤；超额即触发评审而非
  直接禁止合并。

## 影响

- `DocsFactsTests.GiantClass_LineBudget_Maintained`：读四个文件行数对预算断言。
- 本 ADR 的预算数字与测试常量互相锚定：改预算必须同步改 ADR 与测试（测试注释指向本 ADR）。
- 后续新增 admin RPC 的实现类按「新代码禁入」落在独立文件，聚合面只减不增。
