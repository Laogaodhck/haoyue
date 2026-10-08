# ADR-20261007-evolution-signals

- 状态：已接受
- 日期：2026-10-07
- 关联代码：`haoyue_runtime/Evolution/`、`haoyue_runtime/Data/`（EventJournal）、`DaemonServer` 的 `evolution.*` RPC

## 背景

Agent 失败（工具报错、验证挣扎、用户负反馈）散落在会话记录里，无人看也就无人改进。需要一条自动化的「失败→信号→聚合→反思→人工终审」链路。

## 决策

1. 所有回合事件写入 SQLite **事件日志**（EventJournal，保留 5000 条，滚动淘汰）；
2. `DefectAggregator` 对日志做只读批查询，按 `(kind, tool, 归一化错误)` 指纹聚类成四类缺陷报告（ToolFailureCluster / VerificationStruggle / CapabilityGap / UserNegativeFeedback）；
3. `evolution.inspect` RPC 暴露聚合结果 → `evolution.reflect` 发起反思回合产出技能草稿 → 草稿进入待审区 → `evolution.decide` 人工终审（adopt/reject）。

## 备选方案与取舍

- **在线实时分析（回合结束即分析）**：增加每回合延迟，且单回合样本聚类无意义——批量离线聚合正确得多。
- **LLM 直接读原始日志**：上下文装不下 5000 条；聚合器把日志降为几十个指纹后才交给 LLM 反思。
- **选定（事件日志 + 指纹聚合）**：只读、可重放、对写路径零侵入。

## 后果

- 正面：失败模式可量化、可回归；反思产物（技能草稿）有人工闸门，不会污染生产技能库。
- 负面：事件日志是聚合器的唯一输入，日志损坏即链路失明——依赖 SQLite WAL 与现有备份机制；反思会话自身产生的失败需排除（已在 `evolution.inspect` 实现）。
