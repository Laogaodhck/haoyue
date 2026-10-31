# ADR-20261015-巨型类行数预算与拆分策略

## 状态

已采纳（2026-10-15，随批次 E 护栏固化落地）；
2026-10-21 显式评审上调 `DaemonServer` 预算 3100 → 3150（「观测运维补线」接线成本：
daemon 自身文件日志的事件桥接与启停/配置记录、并发>1 关闭 KV 复用的可见警告、
config.save 委派记录，共 +42 行。接线点本身已是最薄形态，下沉无收益；
DaemonServer 拆分路径见决策 3，预算再次触顶时执行）。
2026-10-21 显式评审上调 `DaemonAdminApi` 预算 3250 → 3350（执行方案 5.6「安全配置进
桌面设置 UI」：GetAgentConfig/SetAgentConfig 扩展 toolPolicy 与 bashSandbox 的读写视图、
allow/deny 规则整组替换与逐条校验，共 +77 行。RPC 面按既有模式扩表是最薄接法，
partial 拆分（决策 3 的 providers 域拆分）留待预算再次触顶）。

2026-10-31 执行决策 3 的域拆分并修正计量口径：
- `DaemonAdminApi.cs` 3329 → 1726 行，providers/models 与 knowledge 两块（约 1600 行）
  迁入 `DaemonAdminApi.Models.cs` / `DaemonAdminApi.Knowledge.cs`（纯搬迁，无行为变化）。
- 护栏原先只读主文件，既有 `Files`/`Settings` 两个 partial 未被计入（拆分前聚合 3949 行），
  拆分可绕过预算。现改为**按整个类计行**（`DaemonAdminApi*.cs` 前缀匹配全部 partial）。
- 聚合口径下显式评审预算：`DaemonAdminApi` 3350 → **4050**（拆分后实测 3978，约 1.8% 余量）；
  `DaemonServer` 3150 → **3250**（新增 `Forwarding` partial 后聚合 3210，约 1.2% 余量，
  事件泵部分本已按决策 3 抽离）。

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
   `DaemonAdminApi ≤ 4050`（含全部 partial 文件的类级总量）、`DaemonServer ≤ 3250`（同口径；
   2026-10-21 自 3100 上调、2026-10-31 改聚合口径后上调，见状态行）、
   `SettingsDialog.vue ≤ 2700`、`App.vue ≤ 2400`。超额即测试失败——这是刻意设计：**任何继续膨胀必须先经历一次
   显式的预算上调评审（改本 ADR 状态行 + 预算数字），而不是默认允许**。
3. **拆分路径登记（触发条件式，非排期）**：
   - DaemonAdminApi：按域拆 partial class（providers / knowledge / evolution / tasks / models…），
     纯文件组织、无行为变化，风险最低；预算超限时首选。**已执行（2026-10-31）**：
     providers+models、knowledge 两域先行拆分；剩余 evolution / schedules / sessions 等域
     在下次触顶时按同一模式迁出。
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

- `DocsFactsTests.GiantClass_LineBudget_Maintained`：读四个文件行数对预算断言；
  `.cs` 聚合点按前缀汇总全部 partial（防止拆分变相扩容）。
- 本 ADR 的预算数字与测试常量互相锚定：改预算必须同步改 ADR 与测试（测试注释指向本 ADR）。
- 后续新增 admin RPC 的实现类按「新代码禁入」落在独立文件，聚合面只减不增。
