# 侧边栏「项目任务」UI 规格

## 目标

在桌面端侧边栏的「项目」分区里，把属于该项目的任务/会话挂在项目节点下：可展开、可直接新建、显示状态与最近时间、运行中可取消；展开状态与选中项在会话切换、重连、重启后保持一致；空项目给出空态。**不新增顶层按钮，不改变「会话」分区行为，不改磁盘落点。**

## 现状（已核实）

| 事实 | 位置 |
| --- | --- |
| 项目区已按 `thread.projectId === project.id` 过滤并在项目下渲染会话 | `haoyue_desktop/src/renderer/src/components/Sidebar.vue`（`projectTaskLists` / `visibleThreads` / 模板 535–644 行） |
| 但项目子列表只在 `viewMode === 'projects'` 时渲染；时间视图下点项目无任何展开内容 | 同上，`v-if="expandedProjects.has(project.id) && viewMode === 'projects'"` |
| `expandedProjects` 是内存 `ref(new Set())`，未持久化；`watch(props.projects, {deep:true})` 每次项目对象变化都把全部项目重新展开 | 同上，`watch(() => props.projects, ..., { immediate: true, deep: true })` |
| 任务行只有 `thread.running` 旋转图标，无状态文本、无最近时间、无行内取消 | 同上，三处任务行模板 |
| 项目行右键菜单已有「在此项目中新建任务」→ `createTask(project.id)` | 同上 |
| 选中项 `activeThreadId` 不持久化；重启后由 `loadRuntimeState` 选择最近更新的未归档会话 | `App.vue`（`loadRuntimeState` 481–489 行） |
| 会话归属来自 `session.list`（`workspace` + 项目路径），`ThreadItem.projectId` 已建立 | `session-sync.ts` |
| `task.list` 返回后台任务：`id/prompt/workspaceRoot/status/sessionId/createdAt/endedAt/error/output`，`status ∈ running/completed/failed/cancelled`；`task.cancel` 可取消 | `haoyue_runtime/Scheduling/BackgroundTaskService.cs`、`DaemonServer.Tasks.cs`、`DaemonContract.cs` 1686–1700 行 |
| 会话头无状态字段（`SessionHeaderResult` 只有时间/用量等） | `DaemonContract.cs` 557–573 行 |

**结论：所需数据都能从既有 RPC 组合得到，不需要扩展契约。**

- 项目归属：`project.list`（已装入 `projects`）+ `session.list`（已装入 `threads.projectId`）。
- 后台任务状态/时间：`task.list`，按 `sessionId` 关联到会话行（后台任务本身会创建会话）。
- 运行中取消：后台任务用 `task.cancel`；普通会话回合用既有 `agent.cancel`（`cancelParams(thread)`）。

## 状态判定（纯函数，可单测）

`SidebarTaskStatus = 'queued' | 'running' | 'completed' | 'failed' | 'cancelled'`

判定顺序：

1. 若该会话匹配到后台任务（`task.sessionId === thread.sessionId`）→ 直接用任务状态（`running/completed/failed/cancelled`）。
2. `thread.running` → `running`。
3. `thread.queueDraining` 或 `thread.queuedMessages?.length > 0` → `queued`。
4. 没有 `sessionId` 且没有消息（尚未发送的新任务）→ `queued`。
5. 已加载消息时看最后一条 assistant：
   - `state === 'error'` 或 `finishReason ∈ {error, refusal, content_filter, safety}` → `failed`；
   - `interrupted` 或 `finishReason ∈ {cancelled, interrupted}` → `cancelled`；
   - 其余 → `completed`。
6. 其余（有 `sessionId`、未加载）→ `completed`（历史会话默认视为已完成）。

文案：排队 / 运行中 / 完成 / 失败 / 已取消。**后台任务并发槽排队在契约里仍报 `running`，此类行显示「运行中」**（见遗留风险）。

最近时间：优先 `max(thread.updatedAt, 后台任务 endedAt/createdAt)`，格式 `MM-DD HH:mm`（当天可只显示 `HH:mm`，实现取一种并测试）。

## 交互与持久化

- 项目节点：点击整行切换展开（保留现有 `Chevron`/`Folder` 风格）；子列表在三处渲染不再受 `viewMode` 限制；过滤只保留 `projectId` 匹配且未归档的会话。
- 项目行新增行内 `+` 图标按钮（悬停显示，沿用 `.row-menu-button` 的显隐与尺寸），`title="在「项目名」中新建任务"`，点击 `createTask(project.id)`，新建任务默认归属该项目。不新增顶层按钮。
- 每条任务行：标题、状态徽标、最近时间；运行中仍保留 spinner；状态为 `running` 时显示取消按钮（`aria-label="取消运行中的任务"`）。取消逻辑：
  - 匹配后台任务且 `status === 'running'` → `daemonRequest('task.cancel', { id })` 后刷新 `task.list`；
  - 否则 `thread.running` → `emit('cancelTask', thread)`，`App.vue` 新增 `cancelTaskTurn(thread)`，内部用既有 `cancelParams(thread)` + `agent.cancel`。
- 空态：项目已加载且无任务 → `暂无任务，点击项目行的 + 新建`；读取中 → `正在读取任务…`。
- 展开状态持久化：`localStorage` 键 `haoyue-sidebar-expanded-projects`（JSON 字符串数组），与既有 `haoyue-sidebar-view` 同一套方式。项目列表变化时只把**新出现的项目 id** 默认加入展开集合，不再因 `deep` 变更强制全展开。
- 选中项持久化：`App.vue` 新增 `localStorage` 键 `haoyue-active-thread`，`watch(activeThreadId)` 写入；`loadRuntimeState` 初始选择时先用存储值（仍存在且未归档），否则回退现有「最近更新」逻辑。重连原本就保留 `activeThreadId`，不改。
- 「会话」分区（`tasks-section`）模板、行为、`viewMode`、搜索均不改。

## 关键文件与改动

1. **新增** `haoyue_desktop/src/renderer/src/sidebar-project-tasks.ts`
   - `SidebarTaskStatus`、`SidebarBackgroundTask`；
   - `projectThreads(threads, projectId)`：按归属过滤 + 去归档；
   - `backgroundTaskFor(thread, tasks)`：按 `sessionId` 关联；
   - `sidebarTaskStatus(thread, task?)`、`sidebarTaskStatusLabel(status)`；
   - `taskRecentTime(thread, task?, now?)` / `formatTaskTime(ms, now)`。
2. **修改** `Sidebar.vue`
   - 引入上述纯函数与 `X` 图标（`@lucide/vue`）；
   - `expandedProjects` 初始化自 localStorage、只监听项目 id 列表、写入持久化；
   - 新增 `backgroundTasks` 状态：`onMounted` 调 `task.list`，订阅 `task.updated` 刷新（复用已 import 的 `daemonRequest`）；
   - 移除 `&& viewMode === 'projects'`；项目行加行内 `+`；任务行加状态徽标、时间、取消按钮；
   - 新增 emit `cancelTask: [thread]`。
3. **修改** `App.vue`
   - `cancelTaskTurn(thread)`（`cancelParams` + `agent.cancel`，仅该会话，不影响当前对话）；
   - 绑定 `@cancel-task`；
   - `activeThreadId` 持久化与启动恢复。
4. **修改** `styles.css`
   - `.thread-task-status`（含 `is-running/is-queued/is-completed/is-failed/is-cancelled` 颜色）、`.thread-task-time`、`.row-cancel-button`、`.project-add-task-button`；沿用现有变量与行高。
5. **新增** `haoyue_desktop/src/renderer/src/sidebar-project-tasks.test.ts`
   - 归属过滤（本项目/他项目/全局/归档）；
   - 五种状态判定（运行中、排队、完成、失败、已取消）+ 后台任务覆盖；
   - 时间格式与 `max(updatedAt, endedAt)`；
   - 按 `sessionId` 关联。

## 验证

- 前端：`pnpm --dir haoyue_desktop typecheck`、`pnpm --dir haoyue_desktop build`、`pnpm --dir haoyue_desktop test`（脚本名以 `haoyue_desktop/package.json` 为准）。
- 引擎：`& 'C:\Program Files\dotnet\dotnet.exe' build Haoyue.slnx -c Release`；`haoyue_tests`、`haoyue_cli_tests` 全量。
- 并行会话仍在同仓库改代码：门禁/全量失败若来自对方改动（方法数、行数预算、README 计数），在结论中注明，不替对方修；本任务新增 vitest 用例导致桌面用例数变化时按同一口径说明。

## 遗留风险

- 状态是派生值而非服务端权威状态：后台任务并发槽排队显示「运行中」；历史会话缺 `finishReason` 时默认「完成」。
- `task.list` 是 daemon 内存态，daemon 重启后后台任务消失；会话行回退到会话自身推导状态。
- 展开集合按项目 id 持久化；项目删除后残留 id 无副作用，但不会自动清理（可在后续统一治理）。
- 选中项持久化只在重启启动阶段生效；若存储的会话已被删除，回退最近更新。

## 不在本轮范围

上一轮磁盘落点 `.haoyue/tasks/<taskId>/` **保留不回退**。回退需改：`haoyue_runtime/Workspaces/TaskScopes.cs`（删除）、`WorkspaceManager.cs`（`WorkspaceInfo.TaskId/TaskDir/OutputsDir`）、`PathGuard.ArtifactDirectory`、`BackgroundTaskService`（`TaskScopes.Bind/Remove`）、`ScheduleService`（`Bind` 与 `ResolveWorkspace` 校验）、`DaemonServer.Tasks.cs` 的受理 try/catch、6 个产物工具、`TaskScopesTests.cs` 与 `BackgroundTaskServiceTests.cs`/`ScheduleTests.cs` 新增用例、README 计数。
