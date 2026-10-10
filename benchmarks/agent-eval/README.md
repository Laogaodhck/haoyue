# Agent 级评测 harness

固定任务集 × 真实 runtime 链路的端到端回归评测：每个任务在独立临时工作区运行完整 agent 回合（真实提供商 + 工具执行 + 策略闸门），以**确定性文件断言**判分（不依赖 LLM 判分），产出通过率报告。改提示词、调纠偏阈值、换模型后跑一遍即可回归。

## 用法

```bash
# 前置：~/.haoyue/config.json 已配置可用提供商与模型
dotnet run --project benchmarks/agent-eval                      # 跑全部任务
dotnet run --project benchmarks/agent-eval -- --filter fix-bug  # 只跑匹配任务
dotnet run --project benchmarks/agent-eval -- --steps 30        # 覆盖步数预算
```

退出码：全部通过 0，存在失败 1（可直接接 CI）。结果写入 `results/`：

- `report-<时间戳>.md` — 通过率与失败明细
- `results-<时间戳>.json` — 结构化明细
- `workspaces/<时间戳>/<任务>/` — 每个任务的工作区快照（人工复核）

## 内置任务集（EvalTasks.cs，可扩展）

| 任务 | 针对能力 | 判分要点 |
|---|---|---|
| create-file | 指令执行 / 文件写入 | 文件存在 + 内容包含指定行，且不多写文件 |
| fix-bug | 代码修复 | bug 行消失 + 修复行出现，其他逻辑未动 |
| multi-step | 多步拆解执行 | 三个模块文件全部正确创建 |
| restraint | 克制（问答不落盘） | **负向判分**：不创建任何文件 |
| policy-guard | P0 策略闸门回归 | 注入 deny 规则后 `rm -rf` 被拦截，任务以 blocked 收尾 |
| summarize | 读取 + 汇总 | summary.md 存在且覆盖两份规格要点 |

## 设计说明

- 每个任务独立 `HaoyueRuntime.CreateIsolated`，共享配置但工作区完全隔离，互不污染。
- 判分只看文件系统事实（存在/缺失/包含/不包含），与模型无关——同一任务集可跨模型对比。
- `policy-guard` 任务同时是运行时策略闸门（ToolExecutionPolicy）的端到端回归用例。
- 任务上限步数默认 24（可用 `--steps` 覆盖），单任务硬超时 10 分钟。
