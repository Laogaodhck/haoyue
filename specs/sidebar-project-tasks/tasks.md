# 实施任务

## T1 纯逻辑模块（已完成）

- 新增 `haoyue_desktop/src/renderer/src/sidebar-project-tasks.ts`，导出：
  - `SidebarTaskStatus`、`SidebarBackgroundTask`
  - `projectThreads(threads, projectId)`
  - `backgroundTaskFor(thread, tasks)`
  - `sidebarTaskStatus(thread, task?)`、`sidebarTaskStatusLabel(status)`
  - `taskRecentTime(thread, task?, now?)`、`formatTaskTime(ms, now)`
- 判定顺序严格按 `spec.md`「状态判定」。
- 验收：无 Vue/IO 依赖，可直接 import 测试。

## T2 单元测试（已完成）

- 新增 `haoyue_desktop/src/renderer/src/sidebar-project-tasks.test.ts`：
  - 归属过滤：本项目保留；他项目、全局、归档剔除。
  - 状态：running / queued（queueDraining、queuedMessages、新空任务）/ completed / failed（`state==='error'`、`finishReason==='error'`）/ cancelled（`interrupted`、`finishReason==='cancelled'`）。
  - 后台任务覆盖：`sessionId` 匹配时以任务状态为准。
  - 时间：`max(updatedAt, endedAt)`，格式正确。
- 验收：`pnpm --dir haoyue_desktop test` 中该文件全绿。

## T3 Sidebar 展开与持久化（已完成）

- `Sidebar.vue`：
  - `expandedProjects` 从 `localStorage['haoyue-sidebar-expanded-projects']` 初始化（JSON 数组，容错）。
  - 监听 `props.projects.map(p => p.id)`（非 deep）：只把首次出现的项目 id 加入展开集合。
  - `watch(expandedProjects, ...)` 写回 localStorage（`try/catch` 容错）。
  - 项目子列表渲染去掉 `viewMode === 'projects'` 条件；点击项目行在两种视图下都能展开/收起。
- 验收：时间视图下展开项目能看到归属会话；手动收起后刷新/重连不被强制展开。

## T4 Sidebar 任务行：状态、时间、取消、新建入口（已完成）

- 新增 `backgroundTasks`（`task.list`）：`onMounted` 拉取一次；订阅 `task.updated` 后刷新；失败静默为空。
- 任务行（三处全局/时间/项目列表；至少项目列表必须改）：标题 + 状态徽标 + 最近时间；运行中保留 spinner；`status === 'running'` 显示取消按钮。
- 取消：匹配后台任务 → `daemonRequest('task.cancel', { id })` + 刷新；否则 `emit('cancelTask', thread)`。
- 项目行加行内 `+`（悬停显示）：`createTask(project.id)`，不新增顶层按钮。
- 空态：`正在读取任务…` / `暂无任务，点击项目行的 + 新建`。
- 新增 emit `cancelTask: [thread]`。
- 验收：新任务默认归属项目；运行中可取消；空项目有提示。

## T5 App 侧取消与选中持久化（已完成）

- `App.vue`：
  - `cancelTaskTurn(thread)`：`cancelParams(thread)` + `daemonRequest('agent.cancel', params)`；失败忽略（由回合终态兜底）。
  - Sidebar 绑定 `@cancel-task="cancelTaskTurn"`。
  - `localStorage['haoyue-active-thread']`：`watch(activeThreadId)` 写入；`loadRuntimeState` 初始选择优先恢复该 id（存在且未归档），否则保持现有最近更新逻辑。
- 验收：切换会话后刷新/重启仍选中同一任务；取消非当前会话的运行任务不影响当前对话。

## T6 样式（已完成）

- `styles.css` 增加：
  - `.thread-task-status` + `.is-running/.is-queued/.is-completed/.is-failed/.is-cancelled`
  - `.thread-task-time`
  - `.row-cancel-button`（与 `.row-menu-button` 同显隐规则）
  - `.project-add-task-button`
- 使用现有 CSS 变量，间距/字号与任务行一致；不改动「会话」分区样式。

## T7 验证（已完成）

- 前端：`pnpm --dir haoyue_desktop typecheck`、`pnpm --dir haoyue_desktop build`、`pnpm --dir haoyue_desktop test`（以 `package.json` 实际脚本名为准）。
- 引擎：`& 'C:\Program Files\dotnet\dotnet.exe' build Haoyue.slnx -c Release`；`dotnet test haoyue_tests -c Release --no-build`；`dotnet test haoyue_cli_tests -c Release --no-build`（以仓库既有测试命令为准）。
- 记录实际通过数/总数；并行会话导致的失败单独注明。
- 验收：前端类型检查与构建通过；新增测试全绿；引擎侧结果如实报告。

## T8 结论回复（已完成）

- 结论先行：侧边栏用户视角变化、关键文件与数据来源、验证结果、遗留风险；说明磁盘落点回退涉及文件但本轮不回退。
