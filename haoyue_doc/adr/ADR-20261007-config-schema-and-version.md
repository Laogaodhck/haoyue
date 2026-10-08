# ADR-20261007-config-schema-and-version

- 状态：已接受
- 日期：2026-10-07
- 关联代码：`haoyue_runtime/Configuration/ConfigSchema.cs`、`haoyue_runtime/Providers/ApiVersionContract.cs`、`DaemonServer` 握手

## 背景

两个版本/一致性问题：

1. 配置默认值语义上存在两处（`HaoyueConfig` 属性初始化器 + `DefaultHaoyueConfig` 种子），`config.status` 只能查损坏不能查字段级越界；配置文件无版本号，无法追踪结构演进。
2. daemon 协议版本只是握手时的「告知性」字段，客户端不校验；上游 LLM API 变更/退役时调用链裸崩溃（如 410 Gone 只会得到一条普通 HTTP 错误）。

## 决策

1. **统一配置源 Schema 管理器（ConfigSchema）**：全部受管字段的类型/默认值/范围/枚举集中登记；`ConfigStore.LoadConfig` 接入「规范化（默认回填、越界钳制、枚举回退）→ schemaVersion 逐级迁移 → 反序列化」管道；告警经 `config.status` 暴露；漂移护栏测试断言登记表与 `DefaultHaoyueConfig` 序列化输出一致（默认值从此单源）。CLI Profile 视图与 Runtime 详细视图是同一登记表的字段分组（`View` 标记），不是两套配置。
2. **版本契约（ApiVersionContract + 握手校验）**：协议版本、Anthropic API 版本、契约格式版本集中声明；握手携带 `protocolVersion`，主版本不一致拒绝（新错误码 `versionMismatch`），客户端次版本更高时接受但回 `versionWarning`；上游 API 失败经 `ClassifyBreakingChange`（410 一律、404+退役关键词）转为明确的 Breaking Change 告警文案。

## 备选方案与取舍

- **配置迁移用独立迁移文件目录（如 Flyway 式）**：配置只有几十个字段，逐级迁移表内嵌在 `ConfigSchema.Migrations` 足够，独立目录是仪式感。
- **上游 API 变更靠 try/catch 全局兜底**：无法区分「配错了」与「API 退役了」，用户拿到的还是不可行动的错误文本。
- **版本不兼容时静默降级**：正是本次要消灭的「隐式失败」；明确报错（versionMismatch）才能驱动升级。

## 后果

- 正面：默认值漂移有 CI 护栏；配置文件演进可追踪；版本断裂从「神秘崩溃」变为「可行动的告警」。
- 负面：新增配置字段必须登记 ConfigSchema（未登记字段不受校验保护）；Breaking Change 判定基于状态码+关键词启发式，可能漏报非典型文案——410 硬规则覆盖了最明确的退役信号。
