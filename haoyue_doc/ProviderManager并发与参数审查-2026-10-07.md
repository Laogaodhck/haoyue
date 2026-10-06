# ProviderManager 并发与参数审查-2026-10-07

范围：`haoyue_runtime/Providers/ProviderManager.cs`、`CircuitBreaker.cs`，及其依赖的 `Configuration/ConfigStore.cs`、`HaoyueConfig.cs`、`ModelRegistry.cs`、`LlmHttp.cs` 与 daemon 侧写入路径 `DaemonAdminApi.cs`。本报告只做分析与建议，未改动代码。

## 一、策略参数评估

### P1【高】failover 链无整体时间预算，最坏情况可达 20+ 分钟

- 位置：`ProviderManager.cs:126-206`（候选×重试双层循环）、`HaoyueConfig.cs:148-150`（MaxAttempts=3、BaseDelay=1s、MaxDelay=20s）、`HaoyueConfig.cs:33`（TimeoutSeconds 默认 120s）。
- 现状：每个候选完整执行 3 次尝试（含指数退避 1s/2s），失败后才切下一个候选；单次尝试的响应头超时是 `Provider.TimeoutSeconds`（默认 120s，流式另有空闲超时）。最坏路径 = 4 候选 × 3 次 × (120s 超时 + 退避) ≈ 25 分钟才把 `BuildFailoverException` 抛给用户，而 turn 层没有整体 wall-clock 预算。
- 影响：用户界面长时间"假死"在 thinking，实际上是在对明知故障的候选反复重试。
- 建议：
  1. 给 `StreamAsync` 增加链级总预算（如 `Routing.FailoverBudgetSeconds`，默认 120s），用 `CancellationTokenSource.CreateLinkedTokenSource` 包住整条链，超时即抛聚合错误；
  2. 非首选候选只尝试 1 次——failover 的语义就是"换一个"，对刚失败的候选再退避重试 2 次收益极小；候选链走完一轮仍全败时再考虑第二轮。

### P2【中】重试/退避参数缺输入校验，0/负值会直接崩回合

- 位置：`ProviderManager.cs:434-439`（`BackoffDelay`）、`HaoyueConfig.cs:148-150`（无 clamp）。
- 现状：手改 config.json 把 `baseDelaySeconds` 设为 0 → 零延迟紧密重试风暴；设为负数 → `Task.Delay(TimeSpan 负值)` 抛 `ArgumentOutOfRangeException`，该异常不是 `LlmException`，Agent 层不捕 → 回合以裸异常夭折。
- 建议：`LoadConfig` 时统一 clamp：`BaseDelaySeconds ∈ [0.1, 60]`、`MaxDelaySeconds ≥ BaseDelaySeconds`、`MaxAttempts ∈ [1, 10]`、`CircuitBreakThreshold ≥ 1`、`CircuitCooldownSeconds ∈ [5, 3600]`。

### P3【中】熔断器半开语义与注释不符：冷却到期后放行所有人（惊群）

- 位置：`CircuitBreaker.cs:21-22`。注释写 "allow one probe through"，实现只是"冷却期满 `IsOpen` 返回 false"——没有探测互斥。
- 影响：冷却（默认 60s）结束的瞬间，所有并发回合同时探测故障模型；若故障未恢复，一轮并发失败立刻把断路器重新打开，且这波请求全部白付 120s 超时代价。
- 建议：增加 `Interlocked` 半开标志：冷却期满后仅第一个到达的请求作为探针放行，其余请求按 open 处理直接走 failover；探针成功则关闭，失败则重置 `OpenedAt` 重新冷却。

### P4【低】冷却时长对 429 类限流偏短

- 位置：`HaoyueConfig.cs:153`（CircuitCooldownSeconds=60）。
- LLM 服务商 429 通常按分钟级窗口计费限流，60s 冷却会导致"冷却→探测→又 429"的循环。建议区分错误类别：429/限流类失败单独计数并使用更长冷却（如 300s），网络类失败维持 60s。当前实现未区分错误类型（`RecordFailure` 只收 ref），可作为后续增强。

### P5【低】聊天请求超时无上限 clamp

- 位置：`OpenAiCompatibleClient.cs:32`、`AnthropicClient.cs:34`（`Math.Max(1, TimeoutSeconds)`，无上限）；对比 `ProviderManager.cs:296` 的模型列表接口已有 `Clamp(5, 120)`。
- 建议：与模型列表一致，加 `Math.Clamp(TimeoutSeconds, 1, 300)`。

### 值得肯定的设计

- 已提交（committed）后不再切换提供者、聚合错误 `BuildFailoverException` 保留每个候选的真实失败原因、退避抖动 0.75–1.25×、`Task.Delay(delay, ct)` 可取消、跨隔离开 turn 的进程级共享断路器——这些核心语义都是对的。

## 二、并发控制问题

### C1【严重】配置对象图无锁原地变更 vs 并发读：回合可被无关的管理操作打断

- 写方（均不在 `ConfigStore._gate` 内，只有最后的 `Save()` 序列化短暂拿锁）：
  - `DaemonAdminApi.cs:232-298`：`SetRoutingConfig`/`SetAdvancedConfig` 原地改 `Config.Routing.*`、`Config.Agent.*`；
  - `DaemonAdminApi.cs:421-521`：`UpsertProvider` 对 `config.Providers` 做 `Add`/`Remove`、整体替换 `provider.Models`；
  - `DaemonAdminApi.cs:566-578`：`RemoveProvider` 做 `config.Providers.Remove`。
- 读方（与写方并发运行，turn 跑在 `Task.Run` 里，daemon 每请求一个 dispatch 任务）：
  - `ModelRegistry.cs:24-29`：`All()` 惰性枚举 `Config.Providers` 列表后 `ToList()`；
  - `ProviderManager.cs:70-98`：`BuildCandidates` 枚举 `Routing.Fallback`；
  - Agent 循环每步读 config。
- 影响：`List<T>.Add/Remove` 发生在枚举进行中 → `InvalidOperationException("Collection was modified")`。该异常不是 `LlmException`（`Agent.cs` 的兜底 catch 只捕 OCE/LlmException），进行中的回合会以 "Server error" 夭折。另外 `ConfigStore.Save()`（ConfigStore.cs:59）在 `_gate` 内序列化 `Config` 时，写方仍可在锁外继续改同一对象图 → 保存时抛异常或把半新半旧的快照写进 config.json。
- 复现：回合流式进行中，设置界面修改 provider（增删模型/禁用）。
- 建议（按侵入度递增）：
  1. 最小改动：`UpsertProvider`/`RemoveProvider` 改为 Copy-on-Write——基于当前列表构建新 `List<ProviderConfig>`，在 `_gate` 内一次性替换 `Config.Providers` 引用；读方拿到的列表引用即稳定快照，天然无竞态；
  2. 彻底方案：`IConfigStore.Config` 语义改为"不可变快照"，所有管理操作构建新 `HaoyueConfig` 后原子替换引用（`ConfigStore.Reload()` 已经是这个模式，把 admin 写路径统一进来即可），`Save()` 序列化快照永远一致。

### C2【中】共享断路器的阈值是构造期快照，Reload 后与 ProviderManager 使用两套参数

- 位置：`DaemonServer.cs:151`、`HaoyueRuntime.cs:303-304`（构造时捕获 `Config.Routing.Retry` 对象引用）；对比 `ProviderManager.cs:55` 的 `Retry => configStore.Config.Routing.Retry` 每次读最新。
- 影响：用户编辑 config.json 后触发 `Reload()`（`Config` 被整体替换为新对象）→ 重试/退避用新参数，而断路器的 `CircuitBreakThreshold`/`CircuitCooldownSeconds` 仍用旧值，两套策略静默不一致。
- 建议：`CircuitBreaker` 不缓存 `RetryConfig`，构造参数改为 `IConfigStore`（或 `Func<RetryConfig>`），`IsOpen`/判定时现读现用；调用频率低，无性能顾虑。

### C3【中】半开无探测互斥（同 P3，并发视角）

- 冷却期满后所有并发 turn 同时探测同一故障模型，等于把熔断器的保护作用在恢复瞬间清零。修复同 P3：Interlocked 探测标志。

### C4【低】`ProviderManager.Breaker` 懒初始化竞态

- 位置：`ProviderManager.cs:58`：`_breaker ??= breaker ?? new CircuitBreaker(Retry)`。
- 并发首次访问时两个线程各自 `new` 一个断路器，其中一个实例上已记录的失败计数丢失。daemon 注入路径不受影响（只会赋值注入实例），仅独立运行时（CLI）有此窗口。
- 建议：改为构造函数内直接初始化 `private readonly CircuitBreaker _breaker = breaker ?? ...`（`Retry` 依赖 configStore，构造时可用）。

### C5【低】CircuitBreaker 读写锁不对称

- 位置：`CircuitBreaker.cs:19-22`（`IsOpen` 无锁读 `ConsecutiveFailures`/`OpenedAt`）vs `:30-35`（`RecordFailure` 有 `lock(circuit)`）。
- `DateTimeOffset` 是 16 字节 struct，理论上存在撕裂读；x64 对齐下实际风险极低，但与写侧不对称。建议 `IsOpen` 同样进 `lock(circuit)`（无竞争时 lock 开销可忽略），或把 `OpenedAt` 换成 `long` ticks + `Volatile.Read`。

### C6【低】重试循环内参数快照不一致

- 位置：`ProviderManager.cs:133`（循环上界用进入时的 `Retry.MaxAttempts`）vs `:165`（每次现读）vs `:436-438`（`BackoffDelay` 现读）。
- 配置热变更时同一回合内混用新旧参数。建议循环开始处捕获一次 `var retry = Retry;` 局部快照，整个回合内一致（与 C2 的修复方向相反但各自自洽：要么全用快照、要么全现读）。

### C7【极低】流枚举器双重 DisposeAsync

- 位置：`ProviderManager.cs:163/177`（catch 内手动 `await stream.DisposeAsync()`）+ `:201-205`（finally 再次释放）。
- 编译器生成的异步迭代器 `DisposeAsync` 幂等，当前无实际危害，但属于冗余代码；删除 catch 内的两次手动释放、统一交给 finally 即可（goto 离开 try 时 finally 会立即执行，时序无差别）。

### 无风险确认

- `UsageTracker` 的跨实例文件锁（UsageTracker.cs:21-22）正确；
- `CircuitBreaker.RecordFailure` 的 GetOrAdd + per-circuit lock 正确；
- `BuildCandidates` 的"熔断过滤但不过滤到空"（ProviderManager.cs:96-97）正确；
- 已提交流的中途放弃有 finally 兜底释放（ProviderManager.cs:142-145 注释与实现一致）。

## 三、修复优先级建议

1. **C1**（配置竞态，用户可复现的回合夭折）——Copy-on-Write 最小改动即可消除主风险；
2. **P1+C3**（链级预算 + 半开互斥）——直接决定故障期间的用户体感；
3. **C2+P2**（断路器参数过期 + 输入 clamp）——小幅改动；
4. C4~C7、P4、P5 作为日常维护顺手修复。

---

## 四、修复实施记录（2026-10-07 同日完成）

按「C1 → P1+C3 → C2+P2 → 其余」优先级全部实施，改动如下：

| 编号 | 修复内容 | 位置 |
|---|---|---|
| C1 | provider 增删改走 Copy-on-Write：新增条目在校验通过后以 `config.Providers = [..旧列表, 新条目]` 原子换引用；`RemoveProvider` 改为 Where 过滤后整体替换。并发枚举不再可能抛 `InvalidOperationException` | `DaemonAdminApi.cs` UpsertProvider / RemoveProvider |
| P1 | 新增 `RetryConfig.ChainBudgetSeconds`（默认 120s，clamp 10–600）；StreamAsync 快照链截止时间，超预算不再启动新尝试（退避延迟也会被截断到剩余预算） | `HaoyueConfig.cs`、`ProviderManager.cs` |
| P1b | 非首选候选只试 1 次（`candidateAttempts = candidateIndex == 0 ? maxAttempts : 1`），故障链最长等待大幅缩短 | `ProviderManager.cs` StreamAsync |
| C3/P3 | 熔断器半开改为单探针：`TryBeginProbe` 以 per-circuit `Interlocked` 标志放行一个探测者，其余并发回合跳过该模型；探测失败重置窗口、成功移除熔断记录 | `CircuitBreaker.cs`、StreamAsync 探测门 |
| P4 | 冷却时长按错误类别区分：429 → 300s、5xx → 120s、其余用配置值；`RecordFailure` 接受 statusCode（由 LlmException.StatusCode 传入） | `CircuitBreaker.cs` |
| C2 | 断路器改为 `Func<RetryConfig>` 活配置工厂（构造期快照重载保留供测试），daemon/HaoyueRuntime/ProviderManager 三处全部接线，config.reload 后阈值立即生效 | `CircuitBreaker.cs`、`DaemonServer.cs:151`、`HaoyueRuntime.cs`、`ProviderManager.cs` |
| P2 | `BaseDelaySeconds`/`MaxDelaySeconds` clamp 到 ≥0（杜绝 Task.Delay 负值崩溃）；聊天超时 clamp 到 1–600s（与模型列表接口一致） | `ProviderManager.cs` BackoffDelay、`OpenAiCompatibleClient.cs`、`AnthropicClient.cs` |
| C4 | 断路器懒初始化加锁（`_breakerGate`），消除 CLI 路径 `_breaker ??=` 竞态 | `ProviderManager.cs` |
| C5 | `IsOpen` 读侧进入 `lock(circuit)`，与写侧对称；阈值读入时 clamp ≥1 | `CircuitBreaker.cs` |
| C6 | StreamAsync 进入时快照 `var retry = Retry` 一份，整个链内一致使用（attempt 上界、退避、延迟判断同一来源） | `ProviderManager.cs` |
| C7 | 删除两处 catch 内手动 `DisposeAsync`，统一交给 finally（幂等冗余清理） | `ProviderManager.cs` |

**测试**：新增 3 个用例——`CircuitBreaker_HalfOpen_AdmitsSingleProbePerWindow`（单探针全语义：冷却拒止→窗口单探测→失败重启→成功清除）、`CircuitBreaker_RateLimitFailures_CoolDownLongerThanConfigured`（429 冷却覆盖配置值）、`StreamAsync_FallbackCandidatesGetSingleAttempt`（主模型 3 次重试 + 后备恰 1 次，StubLlmClient.Calls 计数验证）。ProviderTests 52 → 55 全绿。

**行为变化说明**：所有候选熔断中时，此前仍会逐个硬试（长时间挂起后失败），现在快速报「circuit open (cooling down)」，冷却期满后自动放行单探针——对单模型配置的用户是更快的明确报错而非 2 分钟无响应悬挂。

**遗留**：CLI 侧 `ProviderCommands.cs` 的 `Providers.Add/Remove` 未改 CoW（CLI 命令无并发回合，进程间靠文件交换配置，风险不变）；`ChainBudgetSeconds` 的 clamp 下限 10s 使超预算路径难以低成本单测，由单次尝试用例间接覆盖。
