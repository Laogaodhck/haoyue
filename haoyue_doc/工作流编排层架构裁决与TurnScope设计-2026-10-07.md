# 工作流编排层架构裁决与 TurnScope 设计

- 日期：2026-10-07
- 背景：针对「从事件驱动转向 Workflow Graph 编排，引入 Orchestrator/Graph Engine，为每个步骤绑定 Try/Catch/Finally，实现原子性与可追溯失败回滚」的提案做架构裁决，并给出可落地的替代设计。
- 结论先行：**前瞻式 Workflow Graph 引擎不引入**（提案核心前提与现状不符，且与 Agent 动态决策本质冲突）；**采纳其可取内核**——以 per-turn 的 `TurnExecutionScope`（补偿注册 + 步骤账本）实现提案承诺的三项收益：步骤级错误处理绑定、受控子集原子性、可追溯失败回滚。本设计即前一份评审报告中 T2/T3 的实现形态升级。

> **✅ 实施状态（2026-10-07 当日收口）**：第三节设计已全部落地——
> - **第一批 + 第二批**（`2d906db`）：TurnExecutionScope 结构（步骤账本 + LIFO 文件补偿栈，50 项/20MB 预算超限降级）、RunTurnAsync 收尾 finally 化（N14 语义不变）、write/edit 注册旧内容补偿、bash/MCP 诚实标记 `Compensable: false`、`agent.undo` RPC（一次性消费账本逆序恢复，撤销记录 `>>> [undo]` 落盘）、ActiveTurnRecord.Steps 崩溃对账 + 恢复提示升级、TurnCompletedEvent 携带 UndoableFiles；
> - **第三批**（`ab9ab99`）：桌面撤销横幅（回合结束 + 存在可撤销文件时展示，点击调 `agent.undo` 后刷新）；
> - **两处落地偏差**：① RPC 命名为 `agent.undo`（与既有 `agent.*` 命名族一致，非 3.4 草案的 `turn.undo`）；② 3.4 的 `agent.autoCompensate` 配置**未实施**——默认不自动补偿已满足"半自动、不静默"目标，自动执行留作按需增强。
> - 回归：.NET 371→375 / CLI 13 / 桌面 vitest 104 全绿，typecheck 0 错误。

---

## 一、事实核查：提案前提与现状的偏差

| 提案假设 | 代码事实 |
|---|---|
| 「系统依赖事件总线来协调步骤」 | 事件总线是**严格单向广播**：`IEventBus` 仅 Publish/Subscribe（Events/EventBus.cs:9-15），Channel 无界扇出，无路由/条件触发/状态机逻辑；runtime 内部（Agent/Tools/Provider）**从不 Subscribe，只 Publish**，订阅者全部是渲染与转发层（TerminalRenderer、DaemonServer.ForwardEventsAsync）。步骤协调实际由 `RunTurnAsync` 的指令式主循环完成——**不存在需要"迁移"的事件驱动协调层** |
| 「任务应定义为节点/边的状态机图」 | 核心循环是 LLM 动态决策的 `while(true)`（Agent.cs:167-392）：下一步执行什么由模型根据上一步输出决定，步骤集合与数据依赖**运行前不可知**；turn 级状态全是局部变量，无 finally |
| 「实现真正的原子性 All-or-Nothing」 | 工具副作用横跨任意外部系统（shell 命令、MCP 服务器），系统无能力为其构造补偿动作；现有「崩溃不自动重跑」设计（DaemonServer.cs:1604 注释）正是基于此 |
| 全仓有编排预留可演进 | 大小写不敏感搜索 Orchestrator/DAG/Graph Engine **零命中**；最接近的只有展示用 `WorkflowEvent(Step,Kind,Label,Detail)` 元组（RuntimeEvents.cs:73，前端画执行流程图用）与 `update_plan` 纯声明工具（PlanTool.cs，无执行语义） |

## 二、裁决：不引入前瞻式 Workflow Graph 引擎

1. **前提不成立**：没有"事件驱动协调"需要被替换；协调已是清晰的指令式单循环。引入图引擎是替换一个不存在的机制。
2. **形态错配**：预定义 DAG（workflow）与动态循环（agent）是两类编排形态。haoyue 的步骤序列由 LLM 逐条生成、节点输入依赖上一步的运行时输出（动态数据流）——静态图要表达它，必须让模型先输出 DAG 计划再叠加模板绑定层，复杂度远超收益，且与 ReAct 循环直接冲突。
3. **确定性流程无宿主需求**：现有"多步"场景（scheduled_tasks、skill workflow 模板 code-review/fix-build/refactor）本质都是"一次 prompt 交给 agent 循环"（ScheduleService.cs:177），需要的是模型智能而非确定性引擎。
4. **嵌套边界复杂化**：子代理与主 turn 共享事件流与文件锁 owner（AgentDelegation.cs:67，同一 lockScope），图引擎需重新划分 scope 归属，收益不成比例。
5. **虚假原子性风险**：对 shell/MCP 宣称 All-or-Nothing 回滚，实际只能回滚受控子集，会给用户错误的安全预期。

> 后置观察项：若未来出现真正的**确定性管道**需求（如无 LLM 参与的 CI 类任务链），可另立独立 `WorkflowRunner` 与 agent 循环并存，而不是替换它。当前无此需求，不预留。

## 三、采纳内核：TurnExecutionScope（回溯式执行账本 + 补偿注册）

提案的三项收益不依赖图引擎即可达成。核心思路：**不做事前计划图，做事后执行图**——把每个 turn 的实际执行轨迹记录为有序步骤账本，变异步骤注册补偿动作，turn 级 Try/Catch/Finally 统一绑定。

### 3.1 结构（新文件 `Agents/TurnExecutionScope.cs`）

```csharp
public sealed record TurnStepRecord(
    int Ordinal, string Kind,          // tool | mcp | shell | llm
    string Target, bool Compensable,   // 是否可补偿
    string Status, DateTimeOffset At);

public sealed class TurnExecutionScope
{
    List<TurnStepRecord> Steps;                    // 步骤账本（可追溯）
    Stack<TurnCompensation> Compensations;         // LIFO 补偿栈
    // RecordStep(...)            —— 每次工具/LLM 执行后登记
    // RegisterCompensation(desc, Func<Task> undo)  —— 变异前注册
    // CompensateAllAsync()       —— 逆序执行补偿，单项失败不中断、逐项记录
    // RenderTrace()              —— 文本时间线（中断提示/日志/UI 用）
}
```

### 3.2 Try/Catch/Finally 绑定（改造 Agent.cs:69-428）

- 方法头初始化区（78-146）创建 scope；
- 现有 catch 链（399-414）保持不变，把 catch 后的公共收尾（终态事件 420-423、steering 清理 425-426、TurnCompletedEvent 427）移入 `finally`——**N14 修复语义不回退**（终态事件仍在所有 catch 之后的公共路径发布，只是载体改为 finally）；
- finally 中：失败/取消且存在补偿时，发布 `WarningEvent` 列出可撤销变更清单（不自动执行），并标记 scope 状态供 RPC 查询。

### 3.3 补偿注册（改造 Agent.Tools.cs:17-92 + FileTools）

- `ToolContext` 增加 `TurnScope` 通道；
- `WriteFileTool`/`EditFileTool`：已有 `oldText`（FileTools.cs:182/232，目前用后即弃）在写入成功后注册补偿"写回 oldText"——**与 T1 原子写（temp+rename）叠加**：原子写保证单操作不留半截文件，补偿栈保证 turn 级可撤销；
- `bash`/MCP 工具：`RecordStep(Compensable: false)` 只记录不注册——**诚实声明不可补偿**，与全工作流 Saga 裁决一致；
- 子代理（DelegateTool）：共享主 turn scope（同一 lockScope 的既有事实保持），账本里以 `Kind: subtask` 记录，不另立边界。

### 3.4 失败回滚策略：半自动，不静默

- 默认 **不自动补偿**：turn 失败/取消时仅呈现"本 turn 修改了 N 个文件，可撤销"；
- 撤销入口：daemon 新增 RPC `turn.undo(sessionId)`，用户确认后逆序执行补偿栈，结果以普通消息落盘；
- 可选配置 `agent.autoCompensate`（默认 false），仅显式开启时自动执行。

### 3.5 崩溃对账（接管 T3）

- `ActiveTurnRecord`（ActiveTurnJournal.cs:7-12）增加 `Steps: List<TurnStepSummary>(Target, Status)` 字段，同步 `HaoyueJsonContext.cs` source-gen 声明；
- 写放大控制：**仅在变异步骤发生时**才追加重写 journal（保持 best-effort 语义不变，ActiveTurnJournal.cs:22-40 的原子写范式沿用）；
- `RecoverInterruptedTurnsAsync`（DaemonServer.cs:1607-1653）：恢复提示从中断说明升级为"中断 + 已执行步骤清单 + 变更文件路径"，只提示不自动回滚，与「不盲目重试」原则一致。

### 3.6 可追溯性出口

- 步骤账本随 `TurnCompletedEvent` 携带摘要；UI/日志可渲染真实执行时间线——这正是提案里"可追溯"的达成方式：事后 DAG，而非事前计划图。

## 四、与既有方案的归并

| 前报告编号 | 归并结果 |
|---|---|
| T1 文件工具原子写 | 保持独立第一梯队（temp+rename），是本设计的地基 |
| T2 turn 级文件变更账本撤销 | **升级为本设计的 3.2-3.4**（补偿栈替代单纯账本，撤销语义更强） |
| T3 崩溃对账清单 | **升级为本设计的 3.5**（步骤清单替代纯文件清单） |
| Saga / Workflow Graph | 裁决不做（本文第二节） |

## 五、实施顺序建议（✅ 三批均已按此完成）

1. **第一批**：✅ TurnExecutionScope 结构 + RunTurnAsync finally 化 + write/edit 补偿注册 + `turn.undo` RPC（落地命名 `agent.undo`，`2d906db`）；
2. **第二批**：✅ ActiveTurnRecord 步骤扩展 + 恢复对账提示 + TurnCompleted 摘要（`2d906db`）；
3. **第三批**：⏸ `autoCompensate` 配置（未实施，默认不自动补偿已满足目标）+ ✅ UI 撤销入口（`ab9ab99`）。

测试要点全部覆盖：补偿逆序与单项失败续行、取消路径 finally 收尾、N14 终态事件回归、journal 步骤扩展的 source-gen 序列化、崩溃恢复清单渲染（.NET 375 全绿含 TurnScopeTests 6 用例）。
