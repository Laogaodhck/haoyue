# 架构决策记录（ADR）

> 生效日期：2026-10-07。任何重大技术选型、流程变更或关键算法调整，必须以 ADR 形式文档化并随代码提交。

## 为什么要 ADR

重大架构更改（如 daemon 用 Named Pipe 而非 HTTP）实现之后，**「为什么选它而不是别的方案」**的推理过程如果不存在于任何可检索文本中，未来的维护者就只能靠考古 git log 或重新踩坑。ADR 把当时的背景、备选项与权衡固化下来。

## 规则

1. **命名**：`ADR-YYYYMMDD-<主题>.md`（同日多篇用主题区分）。
2. **不可变**：ADR 一经合入只追加状态标记（如「已被 ADR-XXXX 取代」），**不回改正文**——推翻旧决策 = 写新 ADR 引用旧 ADR。
3. **随代码提交**：实现该决策的 PR 必须包含对应 ADR 文件；评审人审查「方案合理性」时以 ADR 为载体。
4. **篇幅**：一个决策一页以内。写权衡，不写实现细节（实现细节属于代码与注释）。

## 模板

```markdown
# ADR-YYYYMMDD-<主题>

- 状态：已接受 | 已取代（被 ADR-… ） | 已废弃
- 日期：YYYY-MM-DD
- 关联代码：<目录/文件>

## 背景（Context）
遇到什么问题、有什么约束。

## 决策（Decision）
选了什么方案。

## 备选方案与取舍（Options & Trade-offs）
- 方案 A：为什么不用。
- 方案 B：为什么不用。
- 选定方案：为什么是它。

## 后果（Consequences）
正面收益、已知的负面代价与缓解手段。
```

## 索引

| ADR | 主题 | 状态 |
| --- | --- | --- |
| [ADR-20261003-daemon-transport.md](ADR-20261003-daemon-transport.md) | daemon 传输选型：Named Pipe/Unix Socket + JSONL 行协议 | 已接受 |
| [ADR-20261003-single-runtime.md](ADR-20261003-single-runtime.md) | 单 .NET 运行时：CLI 直接引用运行时库，无独立后端服务 | 已接受 |
| [ADR-20261003-desktop-electron.md](ADR-20261003-desktop-electron.md) | 桌面端选型：Electron + Vue3，经 daemon-client 桥接 | 已接受 |
| [ADR-20261004-config-single-writer.md](ADR-20261004-config-single-writer.md) | 配置三方合并与 daemon 单写者收敛（A4） | 已接受 |
| [ADR-20261007-evolution-signals.md](ADR-20261007-evolution-signals.md) | 进化引擎信号链：事件日志 + DefectAggregator 聚合 | 已接受 |
| [ADR-20261007-contract-first.md](ADR-20261007-contract-first.md) | Contract First：跨语言契约单源与快照生成链 | 已接受 |
| [ADR-20261007-config-schema-and-version.md](ADR-20261007-config-schema-and-version.md) | 统一配置源 Schema 管理器 + 版本契约 | 已接受 |
