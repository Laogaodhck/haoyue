# Runbook（操作手册）强制化规范

> 生效日期：2026-10-07。本规范是「知识沉淀机制」的强制约束，配套护栏测试 `haoyue_tests/RunbookCoverageTests` 保证不回潮。

## 强制要求

1. **新增内置工具**（`haoyue_runtime/Tools/Builtin/` 或 Skills 目录下实现 `ITool`）：
   - 必须在 `haoyue_cli/prompts/tool/<name>.txt` 提交模型侧描述；
   - **必须**在 [`tools.md`](tools.md) 增加对应的 `## <tool-name>` 章节（how-to-use：用途、参数、何时用/何时不用、失败表现）。
2. **新增官方技能**（`haoyue_runtime/Skills/OfficialSkillCatalog.cs`）：
   - **必须**在 [`skills.md`](skills.md) 增加对应 `## <slug>` 章节（how-to-use：适用场景、触发方式、边界）。
3. **新增 RPC 方法**（`DaemonContract`）：在 `haoyue_doc/` 提交设计说明或在对应 ADR 中引用（ADR 规范见 [`adr/`](../adr/README.md)）。

## Code Review 约定

以上文档属于**代码变更的一部分**：PR 中新增工具/技能而未携带 runbook 更新时，评审人应直接打回；护栏测试会先行在 CI 拦截。

## 为什么

核心业务逻辑与工具行为属于「隐式知识」：只读代码或调试才能理解，人员/时间一换就丢失。Runbook 把每次工具引入时的决策与用法固化为可检索文本，是防止知识流失的最低成本手段。
