# 验收清单

- [x] 项目节点可展开/收起，展开后只列出归属该项目的未归档会话（非全量列表）
- [x] 项目行行内 `+` 可直接新建任务，新建任务默认归属该项目
- [x] 任务行显示排队/运行中/完成/失败/已取消状态与最近时间
- [x] 运行中的任务行显示取消入口；后台任务走 `task.cancel`，普通会话走 `agent.cancel`
- [x] 展开状态与当前选中任务写入既有 localStorage，刷新/重连/重启后保持一致
- [x] 空项目显示「暂无任务，点击项目行的 + 新建」，读取中显示「正在读取任务…」
- [x] 未新增顶层按钮，「会话」分区行为未改变
- [x] 数据仅复用 `project.list` / `session.list` / `task.list` / `task.cancel` / `agent.cancel`，未扩展契约
- [x] 归属过滤、状态映射、时间格式有单元测试覆盖
- [x] 前端 typecheck / test / lint / build / guard:components 通过
- [x] 引擎 `dotnet build Haoyue.slnx -c Release` 与 `haoyue_tests`、`haoyue_cli_tests` 全量通过
