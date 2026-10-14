# Agent 级评测 harness

固定任务集 × 真实 runtime 链路的端到端回归评测：每个任务在独立临时工作区运行完整 agent 回合（真实提供商 + 工具执行 + 策略闸门），以**确定性文件断言**判分（不依赖 LLM 判分），产出通过率报告。改提示词、调纠偏阈值、换模型后跑一遍即可回归。

## 用法

```bash
# 前置：~/.haoyue/config.json 已配置可用提供商与模型
dotnet run --project benchmarks/agent-eval                      # 跑全部任务
dotnet run --project benchmarks/agent-eval -- --filter fix-bug  # 只跑匹配任务
dotnet run --project benchmarks/agent-eval -- --steps 30        # 覆盖步数预算

# 供应商/模型覆盖（仅本次进程生效，不写回配置；两者必须成对提供）
dotnet run --project benchmarks/agent-eval -- --provider openrouter --model anthropic/claude-sonnet-4.5
```

**本地小模型跑不动时**：4B 级 GGUF 连基线任务（create-file）都可能推理中止——这是模型
能力边界，不是工具链缺陷。先经 CLI/桌面配置任意 OpenAI 兼容 / Anthropic 供应商，再用
`--provider/--model` 指向它复测；指向不存在的供应商或未登记模型会直接报错退出（码 2）。

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
| doc-render | 文档成链（起草 → 渲染） | report.md 存在 + `.haoyue/outputs/documents/季度评审.docx` 产出；doc_render 不可用时落 skipped.txt |
| memory-recall | 长期记忆跨会话召回 | 第一回合 memory_save 沉淀约定，第二回合（全新会话）只准依据记忆作答：recall.md 含门禁命令与签名工具，且不得出现「不知道」 |
| knowledge-retrieval | 知识库检索跨会话召回 | 第一回合 knowledge_save 入库，第二回合（全新会话）必须 knowledge_search 命中后写秒数，查不到即判失败 |

## 设计说明

- 每个任务独立 `HaoyueRuntime.CreateIsolated`，共享配置但工作区完全隔离，互不污染。
- 判分只看文件系统事实（存在/缺失/包含/不包含），与模型无关——同一任务集可跨模型对比。
- `policy-guard` 任务同时是运行时策略闸门（ToolExecutionPolicy）的端到端回归用例。
- 任务上限步数默认 24（可用 `--steps` 覆盖），单任务硬超时 10 分钟（每回合各自计时）。
- `FollowUps` 回合每轮重建一个**全新会话与全新运行时实例**：记忆/检索能力只有在
  「写的那一轮和用的那一轮不是同一个上下文」时才真正被度量，同会话追问等于自己递答案。
  因此这两个任务的判分串既不在提示词里重复出现，也禁止读文件与联网（写「不知道」即失败）。

## 通过率目标

供应商具备正常能力（≥ 中档云模型）时，任务集通过率目标 **≥85%**；本地 4B 级模型不计入
该目标（能力边界，见 `haoyue_doc/项目闭环审查报告-2026-10-12.md` G2 项）。
