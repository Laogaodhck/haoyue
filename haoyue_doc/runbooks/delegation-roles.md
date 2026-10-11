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

## 统一汇报模板

所有角色返回固定四段结构（`AgentRole.ReportTemplate`）：`## 结论`、`## 关键发现`、`## 涉及文件`、`## 风险与不确定性`——聚合方（父代理或可视化面板）可稳定解析。

## 扩展新角色

在 `AgentRoles.Builtins` 增加一个 `AgentRole` 记录（Id/Name/Description/Prompt/AllowedTools）即可；工具 schema 描述、校验与提示词自动跟随。

## 测试

`haoyue_tests/AgentRoleTests.cs`（7 项）：角色解析、只读约束、未知角色拒绝、任务文本组装、白名单视图、delegate 工具 role 透传。
