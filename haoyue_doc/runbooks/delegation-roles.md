# 角色化子代理（S7）

在 `delegate_task` / `delegate_tasks` 之上叠加「角色预设」：同一委派链路（fan-out、文件锁、深度控制不变），注入角色纪律提示词并按预设收敛子代理的工具视图。

## 内置角色

| role | 名称 | 工具视图 | 适用 |
|---|---|---|---|
| `researcher` | 研究员 | 只读白名单：read_file / grep / glob / list_dir / bash / web_search / web_fetch / fact_save / update_plan | 调研、取证、方案对比 |
| `implementer` | 实现者 | 不限制（继承默认视图） | 动手改码/文档，须自验后交付 |
| `reviewer` | 评审员 | 只读白名单（无 update_plan） | 代码/方案评审，P0-P3 分级意见 |

## 行为

- **提示词注入**：`AgentRole.ComposeTask` 把角色纪律 + 原任务 + 统一汇报模板（结论/关键发现/涉及文件/风险与不确定性）拼成子任务文本。
- **工具白名单**：`AgentDelegator` 用 `AllowListToolRegistry` 包住子代理工具视图；白名单外的工具对子代理不存在（不是「可请求后被拒」）。
- **并行批量**：`delegate_tasks` 的 role 应用于全部子任务，仍保持并行 fan-out。
- **未知角色**：工具层直接失败（`未知角色「x」。可选：…`），不会走到委派。
- **深度递归**：子代理自身的再委派沿用既有深度规则；角色预设不改变深度语义。

## 自定义子智能体（Markdown）

内置角色之外，可直接用 Markdown 定义子智能体，无需改代码：

| 作用域 | 路径 | 优先级 |
|---|---|---|
| 项目级 | `<项目>/.haoyue/agents/*.md` | 最高（覆盖用户级与内置同名条目） |
| 用户级 | `~/.haoyue/agents/*.md` | 覆盖内置同名条目 |
| 内置 | `AgentRole.Builtins` | 最低 |

```markdown
---
name: "架构评审"
description: "只读复核架构改动，输出分级意见"
model: "openai/gpt-5"   # 可选；留空继承活动模型
tools:
  - read_file
  - grep
---

你以架构评审身份执行子任务……
```

- frontmatter 的 `tools` 同时接受 YAML 序列与逗号/分号分隔字符串；正文即角色指令，`ComposeTask` 仍会追加统一汇报模板。
- `SubAgentCatalog` 每次委派重新读盘：编辑文件或从桌面「设置 → 智能体 → 子智能体」保存后，下一次委派立即生效；项目级定义仅在非全局工作区可见。
- `model` 只作用于该子智能体：主代理继续使用活动模型；引用的模型不存在时子代理直接报错，不会静默退回活动模型。
- 对应 RPC：`agent.subagents.list` / `agent.subagents.save` / `agent.subagents.delete`（scope = `user` / `project`）。

## 统一汇报模板

所有角色返回固定四段结构（`AgentRole.ReportTemplate`）：`## 结论`、`## 关键发现`、`## 涉及文件`、`## 风险与不确定性`——聚合方（父代理或可视化面板）可稳定解析。

## 扩展新角色

在 `AgentRoles.Builtins` 增加一个 `AgentRole` 记录（Id/Name/Description/Prompt/AllowedTools）即可；工具 schema 描述、校验与提示词自动跟随。

## 测试

`haoyue_tests/AgentRoleTests.cs`（7 项）：角色解析、只读约束、未知角色拒绝、任务文本组装、白名单视图、delegate 工具 role 透传。

`haoyue_tests/SubAgentCatalogTests.cs` 覆盖 Markdown frontmatter 解析、三层优先级、保存 / 重命名 / 删除与热重载；`SubAgentRpcTests.cs` 覆盖 `agent.subagents.*` 的落盘与参数校验；`AgentDelegationTests.Delegation_ProjectSubAgent_PinsModelAndNarrowsToolView` 验证模型覆盖与工具白名单全链路。
