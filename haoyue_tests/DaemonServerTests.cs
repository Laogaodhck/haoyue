using System.IO.Compression;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Haoyue.Runtime;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Daemon;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Secrets;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

// 同义词 IPC 读写 KnowledgeTuning 的全局路径表；与 KnowledgeTuningTests 串行，
// 避免并行的 ConfigurePaths 重定向互相踩踏（其余测试类不受影响，保持并行）。
[Collection("KnowledgeTuningSerial")]
public sealed class DaemonServerTests : IAsyncDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "haoyue-daemon-tests", Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _serverCts = new();
    private readonly List<IAsyncDisposable> _asyncDisposables = [];
    private HaoyueRuntime? _runtime;

    public DaemonServerTests() => Directory.CreateDirectory(_tempDir);

    [Fact]
    public async Task Chat_AcknowledgesTurnWithStatusEventBeforeSlowStartup()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        await connection.SendAsync(1, "chat", new JsonObject { ["message"] = "hello" });

        // 回合受理确认：桌面端对 chat 施加 first-event 超时（任意事件即解除）。
        // 隔离 runtime 构建、MCP 连接与本地模型冷加载都可能超过该窗口，因此受理
        // status 帧必须在任何慢启动工作之前发出，且先于 stub 回合的第一个事件。
        var ack = await connection.ReadAsync();
        Assert.Equal(1, ack["id"]!.GetValue<long>());
        Assert.Equal("status", ack["event"]!.GetValue<string>());
        Assert.Equal("Preparing turn", ack["data"]!.GetValue<string>());
        Assert.NotNull(ack["sessionId"]!.GetValue<string>());

        var done = await connection.ReadUntilAsync(r =>
            r["id"]!.GetValue<long>() == 1 && r["event"]!.GetValue<string>() == "done");
        Assert.Equal("ok", done["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task Chat_TerminalFrameCarriesTurnFinishReason()
    {
        // 半截回答（连接中断/截断/超步数）过去一律以 done 收尾，界面把不完整回答显示成
        // 完整回答。终态帧必须自带 finishReason。
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("半截回答", false, null, TurnFinishReason.Interrupted)));

        await connection.SendAsync(1, "chat", new JsonObject { ["message"] = "hello" });
        var done = await connection.ReadUntilAsync(r =>
            r["id"]!.GetValue<long>() == 1 && r["event"]!.GetValue<string>() == "done");
        Assert.Equal("interrupted", done["details"]!["finishReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Chat_ForwardsWarningEventAsNamedStreamFrame()
    {
        // 执行策略拒绝、模型中断保留部分输出等告警过去只进事件日志，桌面与 CLI 的
        // 回合通道都看不到，用户只能看到「工具失败」却不知道为什么。
        var connection = await StartServerAsync((_, _, _, _) =>
        {
            _runtime!.Events.Publish(new WarningEvent("工具 bash 被执行策略拒绝（deny）"));
            return Task.FromResult(new AgentTurnResult("ok", false, null));
        });

        await connection.SendAsync(1, "chat", new JsonObject { ["message"] = "hello" });
        var warning = await connection.ReadUntilAsync(r => r["event"]!.GetValue<string>() == "warning");
        Assert.Equal("工具 bash 被执行策略拒绝（deny）", warning["data"]!.GetValue<string>());
        await connection.ReadUntilAsync(r =>
            r["id"]!.GetValue<long>() == 1 && r["event"]!.GetValue<string>() == "done");
    }

    [Fact]
    public async Task WarningAndErrorEvents_ArePersistedToDaemonFileLog()
    {
        // CLI / 独立 daemon 场景没有桌面旁路，daemon.log 是告警与错误的唯一持久证据。
        // ErrorEvent 有意不转发给回合客户端（回合终态由 error 信封承载），因此只等
        // 回合结束再轮询日志文件，不依赖事件泵与回合泵的帧序。
        var connection = await StartServerAsync((_, _, _, _) =>
        {
            _runtime!.Events.Publish(new WarningEvent("GPU 后端缺失，本地推理退化为 CPU"));
            _runtime!.Events.Publish(new ErrorEvent("MCP 连接失败", "connect ECONNREFUSED"));
            return Task.FromResult(new AgentTurnResult("ok", false, null));
        });

        await connection.SendAsync(1, "chat", new JsonObject { ["message"] = "hello" });
        await connection.ReadUntilAsync(r =>
            r["id"]!.GetValue<long>() == 1 && r["event"]!.GetValue<string>() == "done");

        var text = await WaitForDaemonLogLineAsync(
            line => line.Contains("[WARN] GPU 后端缺失，本地推理退化为 CPU", StringComparison.Ordinal));
        Assert.Contains("[ERROR] MCP 连接失败 | connect ECONNREFUSED", text, StringComparison.Ordinal);
    }

    /// <summary>轮询 daemon 日志直到断言条件成立（事件泵落盘与回合泵收尾存在竞态）。</summary>
    private async Task<string> WaitForDaemonLogLineAsync(Func<string, bool> predicate)
    {
        var path = Path.Combine(_tempDir, "daemon.log");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        string text = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                // 写入方持有 FileShare.Read 之外的写句柄：读取必须显式共享写。
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                text = await reader.ReadToEndAsync();
                if (predicate(text)) return text;
            }
            catch (IOException)
            {
                // 滚动/写入瞬间的共享冲突：下一轮重试。
            }
            await Task.Delay(50);
        }
        Assert.Fail($"daemon.log 未在超时前出现期望行。实际内容：\n{text}");
        return text;
    }

    [Fact]
    public async Task ConfigSave_DelegatedPatch_IsRecordedInDaemonFileLog()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        await connection.SendAsync(1, "config.save", new JsonObject
        {
            ["fields"] = new JsonObject
            {
                ["agent"] = new JsonObject { ["localModelConcurrency"] = 2 }
            }
        });
        var saved = await connection.ReadUntilAsync(r =>
            r["id"]!.GetValue<long>() == 1 && r["event"]!.GetValue<string>() == "result");
        Assert.Equal("saved", saved["data"]!.GetValue<string>());

        var text = await WaitForDaemonLogLineAsync(line =>
            line.Contains("config patch applied by CLI delegate: agent", StringComparison.Ordinal));
        Assert.Contains("config patch applied by CLI delegate: agent", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalModelConcurrencyAboveOne_PublishesKvReuseWarning_OncePerValue()
    {
        // 并发 > 1 自动关闭 KV 前缀复用：启动装配与配置变更都必须可见，同一值只告警一次。
        var workspaceRoot = CreateWorkspace("concurrency-warning");
        var configStore = new ConfigStore(
            Path.Combine(_tempDir, "config-conc.json"),
            Path.Combine(_tempDir, "state-conc.json"));
        _runtime = HaoyueRuntime.Create(
            workspaceRoot, configStore, Path.Combine(_tempDir, "haoyue-conc.db"),
            new OfflineHealthChecker(new HealthChecker(new LlmHttpFactory(), configStore)));
        var globalWorkspace = new WorkspaceManager().CreateGlobal(Path.Combine(_tempDir, "global-conc"));
        var server = new DaemonServer(
            _runtime, (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)),
            globalWorkspace, null, daemonLogFile: Path.Combine(_tempDir, "daemon.log"));
        _asyncDisposables.Add(server);
        using var events = _runtime.Events.Subscribe();

        // 默认并发 1：静默。
        server.ConfigureLocalModelCache();
        Assert.False(events.Reader.TryRead(out _));

        // 提到 2：告警一次；重复装配同一值不刷屏。
        configStore.Config.Agent.LocalModelConcurrency = 2;
        server.ConfigureLocalModelCache();
        var first = Assert.IsType<WarningEvent>(events.Reader.ReadAsync().AsTask().GetAwaiter().GetResult());
        Assert.Contains("KV 前缀复用", first.Message, StringComparison.Ordinal);
        server.ConfigureLocalModelCache();
        Assert.False(events.Reader.TryRead(out _));

        // 回到 1 后再变：配置确实变了，重新告警。
        configStore.Config.Agent.LocalModelConcurrency = 1;
        server.ConfigureLocalModelCache();
        Assert.False(events.Reader.TryRead(out _));
        configStore.Config.Agent.LocalModelConcurrency = 4;
        server.ConfigureLocalModelCache();
        var second = Assert.IsType<WarningEvent>(events.Reader.ReadAsync().AsTask().GetAwaiter().GetResult());
        Assert.Contains("4", second.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActiveChat_DoesNotBlockCancellation_AndEndsWithCancelledEvent()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<AgentTurnResult> SlowTurn(
            AgentSession session, WorkspaceInfo workspace, string message, CancellationToken ct)
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new AgentTurnResult("unreachable", false, null);
        }

        var connection = await StartServerAsync(SlowTurn);
        await connection.SendAsync(1, "chat", new JsonObject { ["message"] = "keep working" });
        // 先读掉回合受理 status 帧，保持 workspace.open 裸 ReadAsync 的帧序断言成立。
        var ack = await connection.ReadAsync();
        Assert.Equal(("1", "status"),
            (ack["id"]!.GetValue<long>().ToString(), ack["event"]!.GetValue<string>()));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await connection.SendAsync(3, "workspace.open", new JsonObject { ["path"] = _tempDir });
        var openedWorkspace = await connection.ReadAsync();
        Assert.Equal(3, openedWorkspace["id"]!.GetValue<long>());
        Assert.Equal("result", openedWorkspace["event"]!.GetValue<string>());

        await connection.SendAsync(2, "agent.cancel", new JsonObject { ["requestId"] = 1 });

        var responses = new List<JsonObject>();
        while (responses.Count < 2)
            responses.Add(await connection.ReadAsync());

        Assert.Contains(responses, response =>
            response["id"]!.GetValue<long>() == 2
            && response["event"]!.GetValue<string>() == "result");
        Assert.Contains(responses, response =>
            response["id"]!.GetValue<long>() == 1
            && response["event"]!.GetValue<string>() == "cancelled");
    }

    [Fact]
    public async Task TaskStart_RunsInBackground_AndBroadcastsTaskUpdated()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("后台完成", false, null)));

        await connection.SendAsync(1, "task.start", new JsonObject { ["message"] = "跑后台任务" });

        // task.updated 广播与 result 响应的先后不定（stub 回合可能先于响应完成），
        // 在同一循环里同时等两类帧，避免 ReadUntilAsync 把广播帧丢弃。
        JsonObject? start = null;
        JsonObject? broadcast = null;
        while (start is null || broadcast is null)
        {
            var frame = await connection.ReadAsync();
            if (frame["id"]?.GetValue<long>() == 1 && frame["event"]?.GetValue<string>() == "result")
                start = frame;
            else if (frame["event"]?.GetValue<string>() == "task.updated")
                broadcast = frame;
        }

        var task = JsonNode.Parse(start!["data"]!.GetValue<string>())!;
        var taskId = task["id"]!.GetValue<string>();
        Assert.StartsWith("bg-", taskId);
        Assert.Equal("completed", broadcast!["details"]!["status"]!.GetValue<string>());
        Assert.Equal(taskId, broadcast["details"]!["taskId"]!.GetValue<string>());

        await connection.SendAsync(2, "task.get", new JsonObject { ["id"] = taskId });
        var got = await connection.ReadUntilAsync(r =>
            r["id"]!.GetValue<long>() == 2 && r["event"]!.GetValue<string>() == "result");
        var info = JsonNode.Parse(got["data"]!.GetValue<string>())!;
        Assert.Equal("completed", info["status"]!.GetValue<string>());
        Assert.Equal("后台完成", info["output"]!.GetValue<string>());
        Assert.NotNull(info["sessionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Broadcast_WithDetails_ReachesEveryConnectedClient()
    {
        // 回归：JsonNode 只允许一个父节点。广播把同一个 details 引用挂给每个连接时，
        // 第二个客户端写入即抛「The node already has a parent」，事件泵异常会在
        // DisposeAsync 处重掀掉 daemon——桌面端与 CLI 同时在线时用户侧就是「发消息没回复」。
        var workspace = CreateWorkspace("broadcast");
        var configStore = new ConfigStore(
            Path.Combine(_tempDir, "config.json"),
            Path.Combine(_tempDir, "state.json"));
        _runtime = HaoyueRuntime.Create(
            workspace, configStore, Path.Combine(_tempDir, "haoyue.db"),
            new OfflineHealthChecker(new HealthChecker(new LlmHttpFactory(), configStore)));
        var server = new DaemonServer(
            _runtime,
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("后台完成", false, null)),
            new WorkspaceManager().CreateGlobal(Path.Combine(_tempDir, "global-state")),
            null,
            daemonLogFile: Path.Combine(_tempDir, "daemon.log"));
        _asyncDisposables.Add(server);
        var first = await OpenConnectionAsync(server);
        var second = await OpenConnectionAsync(server);

        // 连接注册与广播存在竞态：先用 ping 往返确认两个客户端都已进入事件泵，
        // 否则第二个连接可能在广播发生后才注册，回归就测不到它。
        await second.SendAsync(90, "ping");
        await second.ReadUntilAsync(r =>
            r["id"]!.GetValue<long>() == 90 && r["event"]!.GetValue<string>() == "pong");

        await first.SendAsync(1, "task.start", new JsonObject { ["message"] = "跑后台任务" });

        async Task<JsonObject> ReadBroadcast(TestConnection connection)
        {
            while (true)
            {
                var frame = await connection.ReadAsync();
                if (frame["event"]?.GetValue<string>() == "task.updated") return frame;
            }
        }

        foreach (var connection in new[] { first, second })
        {
            var broadcast = await ReadBroadcast(connection);
            Assert.Equal("completed", broadcast["details"]!["status"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task TaskGet_UnknownId_ReturnsError()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        await connection.SendAsync(1, "task.get", new JsonObject { ["id"] = "bg-nope" });
        var error = await connection.ReadUntilAsync(r =>
            r["id"]!.GetValue<long>() == 1 && r["event"]!.GetValue<string>() == "error");
        Assert.Contains("not found", error["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task TaskStart_EmptyPrompt_ReturnsInvalidParams()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        await connection.SendAsync(1, "task.start", new JsonObject());
        var error = await connection.ReadUntilAsync(r =>
            r["id"]!.GetValue<long>() == 1 && r["event"]!.GetValue<string>() == "error");
        Assert.Equal("invalidParams", error["details"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Chat_RejectsSecondConcurrentTurnOnSameSession()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<AgentTurnResult> SlowTurn(
            AgentSession session, WorkspaceInfo workspace, string message, CancellationToken ct)
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new AgentTurnResult("unreachable", false, null);
        }

        var connection = await StartServerAsync(SlowTurn);
        await connection.SendAsync(1, "chat", new JsonObject { ["message"] = "first" });
        // 回合受理确认帧必须先于 stub 回合启动（any-event 解除桌面 first-event 超时）。
        var ack = await connection.ReadAsync();
        Assert.Equal(1, ack["id"]!.GetValue<long>());
        Assert.Equal("status", ack["event"]!.GetValue<string>());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 同一会话的第二个并发回合必须被拒绝：并发写入会交错会话历史并破坏 tool-call 配对。
        await connection.SendAsync(2, "chat", new JsonObject { ["message"] = "second" });
        var second = await connection.ReadAsync();
        Assert.Equal(2, second["id"]!.GetValue<long>());
        Assert.Equal("error", second["event"]!.GetValue<string>());
        Assert.Contains("already active", second["data"]!.GetValue<string>());

        // 清理：空参 cancel 仍取消本连接的全部回合（向后兼容）。
        await connection.SendAsync(3, "agent.cancel", new JsonObject());
        var responses = new List<JsonObject>();
        while (responses.Count < 2)
            responses.Add(await connection.ReadAsync());
        Assert.Contains(responses, response =>
            response["id"]!.GetValue<long>() == 1
            && response["event"]!.GetValue<string>() == "cancelled");
    }

    [Fact]
    public async Task AgentCancel_WithSessionId_OnlyCancelsMatchingTurns()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<AgentTurnResult> SlowTurn(
            AgentSession session, WorkspaceInfo workspace, string message, CancellationToken ct)
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new AgentTurnResult("unreachable", false, null);
        }

        var connection = await StartServerAsync(SlowTurn);
        await connection.SendAsync(1, "chat", new JsonObject { ["message"] = "long turn" });
        // 先读掉回合受理 status 帧，保持后续裸 ReadAsync 的帧序断言成立。
        var ack = await connection.ReadAsync();
        Assert.Equal(("1", "status"),
            (ack["id"]!.GetValue<long>().ToString(), ack["event"]!.GetValue<string>()));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 不匹配的 sessionId 不得误伤本连接上的活动回合（渲染层在 requestId 未就绪时按会话取消）。
        await connection.SendAsync(2, "agent.cancel", new JsonObject { ["sessionId"] = "no-such-session" });
        var miss = await connection.ReadAsync();
        Assert.Equal(2, miss["id"]!.GetValue<long>());
        Assert.Equal("result", miss["event"]!.GetValue<string>());
        Assert.Equal("no active turn", miss["data"]!.GetValue<string>());

        await connection.SendAsync(3, "agent.cancel", new JsonObject());
        var responses = new List<JsonObject>();
        while (responses.Count < 2)
            responses.Add(await connection.ReadAsync());
        Assert.Contains(responses, response =>
            response["id"]!.GetValue<long>() == 1
            && response["event"]!.GetValue<string>() == "cancelled");
    }

    [Fact]
    public async Task Routing_GetAndSet_FailoverEnabled()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("", false, null)));

        await connection.SendAsync(1, "routing.get");
        var initial = await connection.ReadUntilAsync(
            response => response["id"]!.GetValue<long>() == 1
                        && response["event"]!.GetValue<string>() == "result");
        var initialData = JsonNode.Parse(initial["data"]!.GetValue<string>())!;
        var initialEnabled = initialData["failoverEnabled"]!.GetValue<bool>();
        Assert.True(initialEnabled); // default on
        Assert.False(initialData["deepSeekOptimizationEnabled"]!.GetValue<bool>()); // default off

        await connection.SendAsync(2, "routing.set", new JsonObject
        {
            ["failoverEnabled"] = false,
            ["deepSeekOptimizationEnabled"] = true,
        });
        var set = await connection.ReadUntilAsync(
            response => response["id"]!.GetValue<long>() == 2
                        && response["event"]!.GetValue<string>() == "result");
        var setData = JsonNode.Parse(set["data"]!.GetValue<string>())!;
        Assert.False(setData["failoverEnabled"]!.GetValue<bool>());
        Assert.True(setData["deepSeekOptimizationEnabled"]!.GetValue<bool>());

        await connection.SendAsync(3, "routing.get");
        var after = await connection.ReadUntilAsync(
            response => response["id"]!.GetValue<long>() == 3
                        && response["event"]!.GetValue<string>() == "result");
        var afterData = JsonNode.Parse(after["data"]!.GetValue<string>())!;
        Assert.False(afterData["failoverEnabled"]!.GetValue<bool>());
        Assert.True(afterData["deepSeekOptimizationEnabled"]!.GetValue<bool>());

        // 2.7：localFailoverToCloud 默认关；显式开启后可读回。
        Assert.False(afterData["localFailoverToCloud"]!.GetValue<bool>());
        await connection.SendAsync(4, "routing.set", new JsonObject
        {
            ["failoverEnabled"] = true,
            ["localFailoverToCloud"] = true,
        });
        var localSet = await connection.ReadUntilAsync(
            response => response["id"]!.GetValue<long>() == 4
                        && response["event"]!.GetValue<string>() == "result");
        var localData = JsonNode.Parse(localSet["data"]!.GetValue<string>())!;
        Assert.True(localData["localFailoverToCloud"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Advanced_GetAndSet_NetworkEnabled()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("", false, null)));

        await connection.SendAsync(1, "advanced.get");
        var initial = await connection.ReadUntilAsync(
            response => response["id"]!.GetValue<long>() == 1
                        && response["event"]!.GetValue<string>() == "result");
        var initialData = JsonNode.Parse(initial["data"]!.GetValue<string>())!;
        Assert.True(initialData["networkEnabled"]!.GetValue<bool>()); // default on
        Assert.True(initialData["failoverEnabled"]!.GetValue<bool>());

        await connection.SendAsync(2, "advanced.set", new JsonObject
        {
            ["networkEnabled"] = false,
        });
        var set = await connection.ReadUntilAsync(
            response => response["id"]!.GetValue<long>() == 2
                        && response["event"]!.GetValue<string>() == "result");
        var setData = JsonNode.Parse(set["data"]!.GetValue<string>())!;
        Assert.False(setData["networkEnabled"]!.GetValue<bool>());

        await connection.SendAsync(3, "advanced.get");
        var after = await connection.ReadUntilAsync(
            response => response["id"]!.GetValue<long>() == 3
                        && response["event"]!.GetValue<string>() == "result");
        var afterData = JsonNode.Parse(after["data"]!.GetValue<string>())!;
        Assert.False(afterData["networkEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Schedule_AdminMethods_CrudToggleRunAndList()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        await connection.SendAsync(1, "schedule.create", new JsonObject
        {
            ["name"] = "每日检查",
            ["prompt"] = "检查项目状态",
            ["cron"] = "0 9 * * *",
            ["workspace"] = _tempDir,
        });
        var created = ParseData(await connection.ReadUntilAsync(item => item["id"]?.GetValue<long>() == 1));
        var id = created["id"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(id));

        await connection.SendAsync(2, "schedule.toggle", new JsonObject { ["id"] = id, ["enabled"] = false });
        var toggled = ParseData(await connection.ReadUntilAsync(item => item["id"]?.GetValue<long>() == 2));
        Assert.False(toggled["enabled"]!.GetValue<bool>());

        await connection.SendAsync(3, "schedule.run", new JsonObject { ["id"] = id });
        var run = await connection.ReadUntilAsync(item => item["id"]?.GetValue<long>() == 3);
        Assert.Equal("result", run["event"]!.GetValue<string>());

        // schedule.run acknowledges immediately and executes in the background;
        // poll the list until the (instant) stub turn records its outcome.
        JsonObject? listed = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await connection.SendAsync(4, "schedule.list");
            var listResponse = await connection.ReadUntilAsync(item =>
                item["id"]!.GetValue<long>() == 4 && item["event"]!.GetValue<string>() == "result");
            var listNow = JsonNode.Parse(listResponse["data"]!.GetValue<string>())!.AsArray();
            listed = listNow.FirstOrDefault(item => item!["id"]!.GetValue<string>() == id)?.AsObject();
            if (listed is not null && listed["lastStatus"]!.GetValue<string>() is not null) break;
            await Task.Delay(50);
        }
        Assert.NotNull(listed);
        Assert.Equal("success", listed!["lastStatus"]!.GetValue<string>());

        await connection.SendAsync(5, "schedule.create", new JsonObject
        {
            ["name"] = "坏任务",
            ["prompt"] = "x",
            ["cron"] = "not a cron",
        });
        var invalid = await connection.ReadUntilAsync(item => item["id"]?.GetValue<long>() == 5);
        Assert.Equal("error", invalid["event"]!.GetValue<string>());
        Assert.Contains("cron", invalid["data"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        await connection.SendAsync(6, "schedule.delete", new JsonObject { ["id"] = id });
        var deleted = await connection.ReadUntilAsync(item => item["id"]?.GetValue<long>() == 6);
        Assert.Equal("result", deleted["event"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpUpsert_ReturnsBeforeTheServerConnects_ThenBroadcastsTheResult()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        await connection.SendAsync(1, "mcp.upsert", new JsonObject
        {
            ["name"] = "background",
            ["scope"] = "global",
            ["server"] = new JsonObject
            {
                ["transport"] = "sse",
                ["url"] = "http://127.0.0.1:9/mcp",
                ["enabled"] = true,
            },
        });

        // Enabling a server must not block on the connection, and the follow-up
        // broadcast can arrive before or after the response, so collect both.
        JsonObject? response = null;
        JsonObject? broadcast = null;
        while (response is null || broadcast is null)
        {
            var message = await connection.ReadAsync();
            var eventName = message["event"]?.GetValue<string>();
            if (eventName == "result" && message["id"]?.GetValue<long>() == 1) response = message;
            else if (eventName == "mcp.updated") broadcast = message;
        }

        var servers = JsonNode.Parse(response!["data"]!.GetValue<string>())!.AsArray();
        Assert.Contains(servers, server => server!["name"]!.GetValue<string>() == "background");
        Assert.Equal(0, broadcast!["id"]!.GetValue<long>());

        // The background connect finished and recorded why the server is offline.
        await connection.SendAsync(2, "mcp.list");
        var listed = JsonNode.Parse((await connection.ReadUntilAsync(item => item["id"]?.GetValue<long>() == 2))
            ["data"]!.GetValue<string>())!.AsArray();
        var entry = listed.OfType<JsonObject>().Single(server => server["name"]!.GetValue<string>() == "background");
        Assert.False(entry["connected"]!.GetValue<bool>());
        Assert.False(entry["connecting"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(entry["error"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Schedule_Upcoming_BroadcastsNotice()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));
        _runtime!.Events.Publish(new ScheduledTaskUpcomingEvent(
            "upcoming-id", "即将执行", DateTimeOffset.UtcNow.AddMinutes(1)));

        var notice = await connection.ReadUntilAsync(item =>
            item["event"]!.GetValue<string>() == "schedule.upcoming");
        Assert.Equal(0, notice["id"]!.GetValue<long>());
        var details = notice["details"]!.AsObject();
        Assert.Equal("upcoming-id", details["taskId"]!.GetValue<string>());
        Assert.Equal("即将执行", details["name"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(details["runAt"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Schedule_Completion_BroadcastsUpdatedEvent()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        await connection.SendAsync(1, "schedule.create", new JsonObject
        {
            ["name"] = "广播通知",
            ["prompt"] = "检查项目状态",
            ["cron"] = "0 9 * * *",
            ["workspace"] = _tempDir,
        });
        var created = ParseData(await connection.ReadUntilAsync(item => item["id"]?.GetValue<long>() == 1));
        var id = created["id"]!.GetValue<string>();

        await connection.SendAsync(2, "schedule.run", new JsonObject { ["id"] = id });
        var run = await connection.ReadUntilAsync(item => item["id"]?.GetValue<long>() == 2);
        Assert.Equal("result", run["event"]!.GetValue<string>());

        var updated = await connection.ReadUntilAsync(item =>
            item["event"]!.GetValue<string>() == "schedule.updated");
        Assert.Equal(0, updated["id"]!.GetValue<long>());
        var details = updated["details"]!.AsObject();
        Assert.Equal(id, details["taskId"]!.GetValue<string>());
        Assert.Equal("广播通知", details["name"]!.GetValue<string>());
        Assert.Equal("success", details["status"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(details["sessionId"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ActiveChat_AcceptsSteeringWithoutCancellingTheTurn()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<AgentTurnResult> SlowTurn(
            AgentSession session, WorkspaceInfo workspace, string message, CancellationToken ct)
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new AgentTurnResult("unreachable", false, null);
        }

        var connection = await StartServerAsync(SlowTurn);
        await connection.SendAsync(10, "session.new");
        var sessionId = (await connection.ReadAsync())["data"]!.GetValue<string>();
        await connection.SendAsync(1, "chat", new JsonObject
        {
            ["message"] = "keep working",
            ["sessionId"] = sessionId,
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await connection.SendAsync(2, "agent.steer", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["message"] = "also consider the edge cases",
        });
        var steering = await connection.ReadUntilAsync(
            response => response["id"]!.GetValue<long>() == 2
                        && response["event"]!.GetValue<string>() == "result");
        Assert.Equal("guidance queued", steering["data"]!.GetValue<string>());

        await connection.SendAsync(3, "agent.cancel", new JsonObject { ["requestId"] = 1 });
        var cancelled = await connection.ReadUntilAsync(
            response => response["id"]!.GetValue<long>() == 1
                        && response["event"]!.GetValue<string>() == "cancelled");
        Assert.Equal(sessionId, cancelled["sessionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task MultipleExplicitSessions_RunConcurrently_AndCancelIndependently()
    {
        var startedCount = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<AgentTurnResult> SlowTurn(
            AgentSession session, WorkspaceInfo workspace, string message, CancellationToken ct)
        {
            if (Interlocked.Increment(ref startedCount) == 2) bothStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new AgentTurnResult("unreachable", false, null);
        }

        var connection = await StartServerAsync(SlowTurn);
        await connection.SendAsync(10, "session.new");
        var firstSession = (await connection.ReadAsync())["data"]!.GetValue<string>();
        await connection.SendAsync(11, "session.new");
        var secondSession = (await connection.ReadAsync())["data"]!.GetValue<string>();

        await connection.SendAsync(1, "chat", new JsonObject
        {
            ["message"] = "first",
            ["sessionId"] = firstSession,
        });
        await connection.SendAsync(2, "chat", new JsonObject
        {
            ["message"] = "second",
            ["sessionId"] = secondSession,
        });
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await connection.SendAsync(3, "agent.cancel", new JsonObject { ["requestId"] = 1 });
        var firstCancelled = await connection.ReadUntilAsync(
            response => response["id"]!.GetValue<long>() == 1
                        && response["event"]!.GetValue<string>() == "cancelled");
        Assert.Equal(firstSession, firstCancelled["sessionId"]!.GetValue<string>());

        await connection.SendAsync(4, "agent.cancel", new JsonObject { ["requestId"] = 2 });
        var secondCancelled = await connection.ReadUntilAsync(
            response => response["id"]!.GetValue<long>() == 2
                        && response["event"]!.GetValue<string>() == "cancelled");
        Assert.Equal(secondSession, secondCancelled["sessionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConcurrentRequests_AreDispatchedConcurrently_WithoutHeadOfLineBlocking()
    {
        var connection = await StartServerAsync((_, _, _, _) => Task.FromResult(new AgentTurnResult("", false, null)));

        // Send 10 different concurrent requests simultaneously
        var requestIds = Enumerable.Range(100, 10).Select(i => (long)i).ToList();
        foreach (var id in requestIds)
        {
            var method = id % 2 == 0 ? "ping" : "protocol.info";
            await connection.SendAsync(id, method);
        }

        // Read all 10 responses, verifying no blocking or corruption
        var receivedIds = new HashSet<long>();
        for (var i = 0; i < requestIds.Count; i++)
        {
            var resp = await connection.ReadAsync();
            var respId = resp["id"]!.GetValue<long>();
            receivedIds.Add(respId);
        }

        Assert.Equal(requestIds.Count, receivedIds.Count);
        foreach (var id in requestIds)
        {
            Assert.Contains(id, receivedIds);
        }
    }

    [Fact]
    public async Task ChatError_ReturnsProviderDetailInsteadOfRuntimeSummary()
    {
        const string detail = "openai returned HTTP 401: Invalid API key supplied";
        var connection = await StartServerAsync((_, _, _, _) =>
        {
            _runtime!.Events.Publish(new ErrorEvent("LLM request failed", detail));
            return Task.FromResult(new AgentTurnResult("", false, detail));
        });

        await connection.SendAsync(4, "chat", new JsonObject { ["message"] = "hello" });
        // 跳过回合受理 status 帧，断言终态 error 信封携带完整 provider 详情。
        var response = await connection.ReadUntilAsync(r =>
            r["id"]!.GetValue<long>() == 4 && r["event"]!.GetValue<string>() == "error");

        Assert.Equal(detail, response["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task ImageOnlyChat_AcceptsMultipleValidatedAttachments()
    {
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = await StartServerAsync((_, _, message, _) =>
        {
            Assert.Equal("", message);
            invoked.SetResult();
            return Task.FromResult(new AgentTurnResult("ok", false, null));
        });

        await connection.SendAsync(40, "chat", new JsonObject
        {
            ["images"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "one",
                    ["name"] = "one.png",
                    ["mediaType"] = "image/png",
                    ["data"] = "AQID",
                },
                new JsonObject
                {
                    ["id"] = "two",
                    ["name"] = "two.webp",
                    ["mediaType"] = "image/webp",
                    ["data"] = "BAUG",
                })
        });

        var response = await connection.ReadUntilAsync(r =>
            r["id"]!.GetValue<long>() == 40 && r["event"]!.GetValue<string>() == "done");
        Assert.Equal("done", response["event"]!.GetValue<string>());
        await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ImageChat_RejectsUnsupportedMediaTypeBeforeStartingTurn()
    {
        var connection = await StartServerAsync((_, _, _, _) =>
            Task.FromResult(new AgentTurnResult("should not run", false, null)));

        await connection.SendAsync(41, "chat", new JsonObject
        {
            ["images"] = new JsonArray(new JsonObject
            {
                ["name"] = "vector.svg",
                ["mediaType"] = "image/svg+xml",
                ["data"] = "AQID",
            })
        });

        var response = await connection.ReadAsync();
        Assert.Equal("error", response["event"]!.GetValue<string>());
        Assert.Contains("Unsupported image type", response["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task SessionGet_ReturnsPersistedImagesAndViewedReferences()
    {
        var workspace = CreateWorkspace("image-session");
        var connection = await StartServerAsync((_, _, _, _) =>
            Task.FromResult(new AgentTurnResult("ok", false, null)), workspace);
        var session = _runtime!.Sessions.Create(_runtime.Workspace);
        _runtime.Sessions.Append(session, Haoyue.Runtime.Providers.ChatMessage.User("inspect",
        [
            new Haoyue.Runtime.Providers.ChatImageAttachment(
                "screen-1", "screen.png", "image/png", "AQID", 3),
        ]));
        _runtime.Sessions.Append(session, new Haoyue.Runtime.Providers.ChatMessage
        {
            Role = Haoyue.Runtime.Providers.ChatRole.Assistant,
            Text = "done",
            ViewedImages =
            [
                new Haoyue.Runtime.Providers.ChatImageReference("screen-1", "screen.png"),
            ],
        });

        await connection.SendAsync(42, "session.get", new JsonObject
        {
            ["id"] = session.Header.Id,
            ["workspace"] = workspace,
        });

        var restored = ParseData(await connection.ReadAsync());
        var messages = restored["messages"]!.AsArray();
        Assert.Equal("screen.png", messages[0]!["images"]![0]!["name"]!.GetValue<string>());
        // session.get 只回元数据：字节改走 session.image，响应体积不再随视觉历史膨胀。
        Assert.True(messages[0]!["images"]![0]!.AsObject()["data"] is null);
        Assert.Equal("screen-1", messages[1]!["viewedImages"]![0]!["id"]!.GetValue<string>());

        await connection.SendAsync(43, "session.image", new JsonObject
        {
            ["id"] = session.Header.Id,
            ["workspace"] = workspace,
            ["imageId"] = "screen-1",
        });
        var image = ParseData(await connection.ReadAsync());
        Assert.Equal("AQID", image["data"]!.GetValue<string>());
        Assert.Equal("screen.png", image["name"]!.GetValue<string>());

        await connection.SendAsync(44, "session.image", new JsonObject
        {
            ["id"] = session.Header.Id,
            ["workspace"] = workspace,
            ["imageId"] = "absent",
        });
        var miss = await connection.ReadAsync();
        Assert.Equal("error", miss["event"]!.GetValue<string>());
        Assert.Contains("not found", miss["data"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SessionGet_PagesWithLimitBefore_AndReportsCursor()
    {
        var workspace = CreateWorkspace("paged-session");
        var connection = await StartServerAsync((_, _, _, _) =>
            Task.FromResult(new AgentTurnResult("ok", false, null)), workspace);
        var session = _runtime!.Sessions.Create(_runtime.Workspace);
        for (var i = 0; i < 10; i++)
            _runtime.Sessions.Append(session, Haoyue.Runtime.Providers.ChatMessage.User($"第{i}条"));

        await connection.SendAsync(1, "session.get", new JsonObject
        {
            ["id"] = session.Header.Id,
            ["workspace"] = workspace,
            ["limit"] = 4,
        });
        var tail = ParseData(await connection.ReadAsync());
        Assert.Equal(10, tail["total"]!.GetValue<int>());
        Assert.Equal(6, tail["firstIndex"]!.GetValue<int>());
        Assert.True(tail["hasMore"]!.GetValue<bool>());
        Assert.Equal(
            new[] { "第6条", "第7条", "第8条", "第9条" },
            tail["messages"]!.AsArray().Select(node => node!["text"]!.GetValue<string>()));

        await connection.SendAsync(2, "session.get", new JsonObject
        {
            ["id"] = session.Header.Id,
            ["workspace"] = workspace,
            ["limit"] = 4,
            ["before"] = 6,
        });
        var older = ParseData(await connection.ReadAsync());
        Assert.Equal(2, older["firstIndex"]!.GetValue<int>());
        Assert.Equal(
            new[] { "第2条", "第3条", "第4条", "第5条" },
            older["messages"]!.AsArray().Select(node => node!["text"]!.GetValue<string>()));

        // 不带分页参数时保持旧的全量语义：一页到底、没有更早。
        await connection.SendAsync(3, "session.get", new JsonObject
        {
            ["id"] = session.Header.Id,
            ["workspace"] = workspace,
        });
        var whole = ParseData(await connection.ReadAsync());
        Assert.Equal(10, whole["messages"]!.AsArray().Count);
        Assert.False(whole["hasMore"]!.GetValue<bool>());
    }

    [Fact]
    public async Task WorkspaceModeAndProtocolMethods_ReturnRuntimeStateAndPersistChanges()
    {
        var workspaceA = CreateWorkspace("workspace-a");
        var workspaceB = CreateWorkspace("workspace-b", "{\"mode\":\"edit\"}");
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)),
            workspaceA);

        await connection.SendAsync(1, "protocol.info");
        var protocol = ParseData(await connection.ReadAsync());
        Assert.Equal(DaemonServer.ProtocolVersion, protocol["version"]!.GetValue<string>());
        Assert.Contains("agent.cancel", protocol["methods"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Contains("agent.steer", protocol["methods"]!.AsArray().Select(node => node!.GetValue<string>()));

        await connection.SendAsync(2, "workspace.open", new JsonObject { ["path"] = workspaceB });
        var opened = ParseData(await connection.ReadAsync());
        Assert.Equal(Path.GetFullPath(workspaceB), opened["path"]!.GetValue<string>());

        await connection.SendAsync(3, "agent.mode.switch", new JsonObject { ["mode"] = "readonly" });
        var modeResponse = await connection.ReadAsync();
        Assert.Equal("readonly", modeResponse["data"]!.GetValue<string>());

        await connection.SendAsync(4, "agent.mode.get");
        var currentMode = await connection.ReadAsync();
        Assert.Equal("readonly", currentMode["data"]!.GetValue<string>());

        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(workspaceB, ".haoyue", "config.json")))!.AsObject();
        Assert.Equal("readonly", persisted["mode"]!.GetValue<string>());

        await connection.SendAsync(5, "workspace.open", new JsonObject
        {
            ["path"] = Path.Combine(_tempDir, "missing"),
        });
        var missing = await connection.ReadAsync();
        Assert.Equal("error", missing["event"]!.GetValue<string>());
    }

    [Fact]
    public async Task AdministrativeMethods_ManageWorkspaceProvidersMcpSkillsAndDiagnostics()
    {
        var workspace = CreateWorkspace("admin-workspace", """
            {
              "mcp": {
                "servers": {
                  "inline-test": {
                    "transport": "stdio",
                    "command": "old-command",
                    "enabled": false
                  }
                }
              }
            }
            """);
        var skillDirectory = Path.Combine(workspace, "skills", "reviewer");
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(Path.Combine(skillDirectory, "prompt.txt"), "Review changes carefully.");
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)),
            workspace);

        await connection.SendAsync(10, "workspace.init");
        var initialized = ParseData(await connection.ReadAsync());
        Assert.Equal(Path.GetFullPath(workspace), initialized["path"]!.GetValue<string>());

        await connection.SendAsync(11, "provider.upsert", new JsonObject
        {
            ["id"] = "local",
            ["kind"] = "openai",
            ["baseUrl"] = "http://localhost:11434/v1",
            ["apiKey"] = "secret-test-key",
            ["models"] = new JsonArray("test-model"),
        });
        var provider = ParseData(await connection.ReadAsync());
        Assert.True(provider["apiKeyConfigured"]!.GetValue<bool>());
        Assert.False(provider.ContainsKey("apiKey"), "provider 载荷不应跨管道回传明文 API Key");

        // 客户端不再拿到明文，因此空值必须表示「保持不变」，只有显式 clearApiKey 才删除。
        await connection.SendAsync(111, "provider.upsert", new JsonObject
        {
            ["id"] = "local",
            ["kind"] = "openai",
            ["baseUrl"] = "http://localhost:11434/v1",
            ["apiKey"] = "",
            ["models"] = new JsonArray("test-model"),
        });
        provider = ParseData(await connection.ReadAsync());
        Assert.True(provider["apiKeyConfigured"]!.GetValue<bool>());
        Assert.False(provider.ContainsKey("apiKey"));
        var reloadedStore = new ConfigStore(
            Path.Combine(_tempDir, "config.json"),
            Path.Combine(_tempDir, "state.json"));
        var storedKey = reloadedStore.Config.FindProvider("local")!.ApiKey;
        Assert.True(SecretResolver.IsSecret(storedKey), "Provider ApiKey 应以 secret: 引用落盘而非明文");
        Assert.Equal("secret-test-key", reloadedStore.Config.FindProvider("local")!.ResolveApiKey());

        await connection.SendAsync(112, "provider.upsert", new JsonObject
        {
            ["id"] = "local",
            ["kind"] = "openai",
            ["baseUrl"] = "http://localhost:11434/v1",
            ["clearApiKey"] = true,
            ["models"] = new JsonArray("test-model"),
        });
        provider = ParseData(await connection.ReadAsync());
        Assert.False(provider["apiKeyConfigured"]!.GetValue<bool>());
        Assert.False(provider.ContainsKey("apiKey"));

        await connection.SendAsync(13, "provider.use", new JsonObject { ["id"] = "local" });
        Assert.Equal("local", (await connection.ReadAsync())["data"]!.GetValue<string>());

        await connection.SendAsync(131, "model.update", new JsonObject
        {
            ["provider"] = "local",
            ["id"] = "test-model",
            ["contextWindow"] = 64_000,
            ["maxOutput"] = 4_096,
            ["vision"] = true,
        });
        var updatedModel = ParseData(await connection.ReadAsync());
        Assert.Equal(64_000, updatedModel["contextWindow"]!.GetValue<int>());
        Assert.True(updatedModel["vision"]!.GetValue<bool>());
        await connection.SendAsync(132, "model.catalog");
        var catalog = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        Assert.Contains(catalog, item => item!["ref"]!.GetValue<string>() == "local/test-model"
            && item["contextWindow"]!.GetValue<int>() == 64_000
            && item["capabilities"]!["vision"]!.GetValue<bool>());

        await connection.SendAsync(14, "mcp.upsert", new JsonObject
        {
            ["name"] = "inline-test",
            ["scope"] = "workspace",
            ["server"] = new JsonObject
            {
                ["transport"] = "stdio",
                ["command"] = "updated-command",
                ["enabled"] = false,
            },
        });
        var mcpServers = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        Assert.Contains(mcpServers, server => server!["name"]!.GetValue<string>() == "inline-test");
        Assert.Contains("updated-command", await File.ReadAllTextAsync(
            Path.Combine(workspace, ".haoyue", "config.json")));

        await connection.SendAsync(15, "skill.list");
        var skills = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        Assert.Contains(skills, skill => skill!["name"]!.GetValue<string>() == "reviewer");
        await connection.SendAsync(16, "skill.toggle", new JsonObject
        {
            ["name"] = "reviewer",
            ["enabled"] = false,
        });
        skills = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        Assert.Contains(skills, skill =>
            skill!["name"]!.GetValue<string>() == "reviewer"
            && !skill["enabled"]!.GetValue<bool>());

        await connection.SendAsync(17, "usage.get");
        var usage = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        Assert.All(usage, item =>
        {
            Assert.NotNull(item!["model"]);
            Assert.Null(item["cost"]);
        });

        await connection.SendAsync(171, "usage.timeline", new JsonObject { ["days"] = 7 });
        var timeline = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        Assert.Equal(7, timeline.Count);
        Assert.All(timeline, point =>
        {
            Assert.NotNull(point!["date"]);
            Assert.NotNull(point!["totalTokens"]);
            Assert.NotNull(point!["calls"]);
        });

        await connection.SendAsync(18, "doctor.run");
        var checks = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        Assert.NotEmpty(checks);

        await connection.SendAsync(181, "project.upsert", new JsonObject
        {
            ["id"] = "desktop-project",
            ["path"] = workspace,
            ["name"] = "Desktop project",
        });
        var project = ParseData(await connection.ReadAsync());
        Assert.Equal("desktop-project", project["id"]!.GetValue<string>());
        await connection.SendAsync(182, "project.list");
        var projects = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        Assert.Contains(projects, item => item!["id"]!.GetValue<string>() == "desktop-project");

        await connection.SendAsync(19, "session.new", new JsonObject { ["reasoningLevel"] = "xhigh" });
        var sessionId = (await connection.ReadAsync())["data"]!.GetValue<string>();
        await connection.SendAsync(20, "session.get", new JsonObject { ["id"] = sessionId });
        var session = ParseData(await connection.ReadAsync());
        Assert.Equal(sessionId, session["id"]!.GetValue<string>());
        Assert.Equal("xhigh", session["reasoningLevel"]!.GetValue<string>());

        await connection.SendAsync(21, "session.update", new JsonObject
        {
            ["id"] = sessionId,
            ["title"] = "Desktop task",
            ["workspace"] = workspace,
            ["reasoningLevel"] = "ultra",
        });
        session = ParseData(await connection.ReadAsync());
        Assert.Equal("Desktop task", session["title"]!.GetValue<string>());
        Assert.Equal("ultra", session["reasoningLevel"]!.GetValue<string>());

        await connection.SendAsync(22, "session.archive", new JsonObject
        {
            ["id"] = sessionId,
            ["workspace"] = workspace,
        });
        session = ParseData(await connection.ReadAsync());
        Assert.True(session["archived"]!.GetValue<bool>());

        await connection.SendAsync(23, "session.list", new JsonObject
        {
            ["workspace"] = workspace,
            ["includeArchived"] = true,
        });
        var sessions = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        Assert.Contains(sessions, item => item!["id"]!.GetValue<string>() == sessionId);

        await connection.SendAsync(24, "session.delete", new JsonObject
        {
            ["id"] = sessionId,
            ["workspace"] = workspace,
        });
        Assert.Equal(sessionId, (await connection.ReadAsync())["data"]!.GetValue<string>());

        await connection.SendAsync(24_1, "session.new", new JsonObject { ["workspace"] = workspace });
        var projectSessionId = (await connection.ReadAsync())["data"]!.GetValue<string>();

        await connection.SendAsync(25, "project.remove", new JsonObject { ["id"] = "desktop-project" });
        Assert.Equal("desktop-project", (await connection.ReadAsync())["data"]!.GetValue<string>());
        await connection.SendAsync(26, "session.get", new JsonObject
        {
            ["id"] = projectSessionId,
            ["workspace"] = workspace,
        });
        Assert.Equal("error", (await connection.ReadAsync())["event"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProjectUpsert_RejectsUserProfileAndHaoyueStateDirectories()
    {
        var workspace = CreateWorkspace("forbidden-project");
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)), workspace);

        await connection.SendAsync(1, "project.upsert", new JsonObject
        {
            ["id"] = "home-project",
            ["path"] = Path.GetDirectoryName(HaoyuePaths.Home)!,
            ["name"] = "Home",
        });
        Assert.Equal("error", (await connection.ReadAsync())["event"]!.GetValue<string>());

        await connection.SendAsync(2, "project.upsert", new JsonObject
        {
            ["id"] = "state-project",
            ["path"] = HaoyuePaths.Home,
            ["name"] = "State",
        });
        Assert.Equal("error", (await connection.ReadAsync())["event"]!.GetValue<string>());

        // A normal project directory still registers fine.
        await connection.SendAsync(3, "project.upsert", new JsonObject
        {
            ["id"] = "ok-project",
            ["path"] = workspace,
            ["name"] = "OK",
        });
        Assert.Equal("result", (await connection.ReadAsync())["event"]!.GetValue<string>());
    }

    [Fact]
    public async Task RemoveProject_WithKeepSessions_PreservesSessionsInDatabase()
    {
        var workspace = CreateWorkspace("keep-sessions");
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)), workspace);

        await connection.SendAsync(1, "project.upsert", new JsonObject
        {
            ["id"] = "keep-project",
            ["path"] = workspace,
            ["name"] = "Keep",
        });
        Assert.Equal("result", (await connection.ReadAsync())["event"]!.GetValue<string>());

        await connection.SendAsync(2, "session.new", new JsonObject { ["workspace"] = workspace });
        var sessionId = (await connection.ReadAsync())["data"]!.GetValue<string>();

        await connection.SendAsync(3, "project.remove", new JsonObject
        {
            ["id"] = "keep-project",
            ["keepSessions"] = true,
        });
        Assert.Equal("keep-project", (await connection.ReadAsync())["data"]!.GetValue<string>());

        await connection.SendAsync(4, "session.get", new JsonObject
        {
            ["id"] = sessionId,
            ["workspace"] = workspace,
        });
        var response = await connection.ReadAsync();
        Assert.Equal("result", response["event"]!.GetValue<string>());
        Assert.Equal(sessionId, ParseData(response)["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task GlobalSessions_RunWithoutAProjectAndRemainSeparateFromWorkspaceSessions()
    {
        var observedGlobalContext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workspace = CreateWorkspace("project-sessions");
        var connection = await StartServerAsync((session, context, _, _) =>
        {
            observedGlobalContext.SetResult();
            Assert.True(context.IsGlobal);
            Assert.Null(session.Header.Workspace);
            return Task.FromResult(new AgentTurnResult("ok", false, null));
        }, workspace);

        await connection.SendAsync(30, "session.new", new JsonObject { ["global"] = true });
        var globalId = (await connection.ReadAsync())["data"]!.GetValue<string>();

        await connection.SendAsync(31, "session.get", new JsonObject
        {
            ["id"] = globalId,
            ["global"] = true,
        });
        var globalSession = ParseData(await connection.ReadAsync());
        Assert.Null(globalSession["workspace"]);

        await connection.SendAsync(32, "session.list", new JsonObject { ["global"] = true });
        var globalSessions = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        Assert.Contains(globalSessions, item => item!["id"]!.GetValue<string>() == globalId);

        await connection.SendAsync(33, "session.list", new JsonObject { ["workspace"] = workspace });
        var projectResponse = await connection.ReadAsync();
        Assert.True(projectResponse["event"]!.GetValue<string>() == "result", projectResponse["data"]!.GetValue<string>());
        var projectSessions = JsonNode.Parse(projectResponse["data"]!.GetValue<string>())!.AsArray();
        Assert.DoesNotContain(projectSessions, item => item!["id"]!.GetValue<string>() == globalId);

        await connection.SendAsync(34, "chat", new JsonObject
        {
            ["message"] = "hello without a directory",
            ["global"] = true,
        });
        Assert.Equal("done", (await connection.ReadUntilAsync(r =>
            r["id"]!.GetValue<long>() == 34 && r["event"]!.GetValue<string>() == "done"))["event"]!.GetValue<string>());
        await observedGlobalContext.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OfficialSkills_ListBundledCatalog_AndRejectUnknownSlug()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        await connection.SendAsync(1, "skill.official.list");
        var catalog = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        Assert.NotEmpty(catalog);
        Assert.All(catalog, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item!["slug"]!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(item["name"]!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(item["description"]!.GetValue<string>()));
            Assert.NotNull(item["installed"]);
            Assert.NotNull(item["enabled"]);
        });

        await connection.SendAsync(2, "skill.official.install", new JsonObject { ["slug"] = "definitely-missing" });
        Assert.Equal("error", (await connection.ReadAsync())["event"]!.GetValue<string>());
    }

    [Fact]
    public async Task Advanced_LocalInference_RoundTripAndClamp()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        // No local provider yet: the desktop hides the whole section.
        await connection.SendAsync(1, "advanced.get", new JsonObject());
        Assert.True(ParseData(await connection.ReadAsync())["localInference"] is null);

        await connection.SendAsync(2, "workspace.init");
        Assert.Equal("result", (await connection.ReadAsync())["event"]!.GetValue<string>());

        // Local provider registrations require the GGUF file to exist on disk.
        File.WriteAllBytes(Path.Combine(_tempDir, "test-model.gguf"), new byte[] { 1 });
        await connection.SendAsync(3, "provider.upsert", new JsonObject
        {
            ["id"] = "local",
            ["kind"] = "local",
            ["modelsDirectory"] = _tempDir,
            ["models"] = new JsonArray("test-model"),
        });
        Assert.Equal("result", (await connection.ReadAsync())["event"]!.GetValue<string>());

        // Full CUDA settings round-trip onto the local provider.
        await connection.SendAsync(4, "advanced.set", new JsonObject
        {
            ["localInference"] = new JsonObject
            {
                ["providerId"] = "local",
                ["gpuLayers"] = 33,
                ["contextLength"] = 8192,
                ["kvCacheQuantization"] = "q8_0",
                ["flashAttention"] = true,
            }
        });
        var updated = ParseData(await connection.ReadAsync())["localInference"]!.AsObject();
        Assert.Equal(33, updated["gpuLayers"]!.GetValue<int>());
        Assert.Equal(8192, updated["contextLength"]!.GetValue<int>());
        Assert.Equal("q8_0", updated["kvCacheQuantization"]!.GetValue<string>());
        Assert.True(updated["flashAttention"]!.GetValue<bool>());

        // The values persist through a config reload and the model window was updated too.
        var reloaded = new ConfigStore(
            Path.Combine(_tempDir, "config.json"),
            Path.Combine(_tempDir, "state.json"));
        var localProvider = reloaded.Config.FindProvider("local")!;
        Assert.Equal(33, localProvider.GpuLayers);
        Assert.True(localProvider.FlashAttention);
        Assert.Equal("q8_0", localProvider.KvCacheQuantization);
        Assert.Equal(8192, localProvider.Models[0].ContextWindow);

        // Out-of-range values are clamped; unknown quantization normalizes to none.
        await connection.SendAsync(5, "advanced.set", new JsonObject
        {
            ["localInference"] = new JsonObject
            {
                ["providerId"] = "local",
                ["gpuLayers"] = 5000,
                ["contextLength"] = 1_000_000,
                ["kvCacheQuantization"] = "bogus",
                ["flashAttention"] = false,
            }
        });
        var clamped = ParseData(await connection.ReadAsync())["localInference"]!.AsObject();
        Assert.Equal(999, clamped["gpuLayers"]!.GetValue<int>());
        Assert.Equal(32768, clamped["contextLength"]!.GetValue<int>());
        Assert.Equal("none", clamped["kvCacheQuantization"]!.GetValue<string>());
        Assert.False(clamped["flashAttention"]!.GetValue<bool>());

        // CUDA settings on a remote provider are rejected.
        await connection.SendAsync(6, "advanced.set", new JsonObject
        {
            ["localInference"] = new JsonObject { ["providerId"] = "missing-provider" }
        });
        Assert.Equal("error", (await connection.ReadAsync())["event"]!.GetValue<string>());
    }

    [Fact]
    public async Task Advanced_RulesAndMemoryConfig_RoundTripAndNormalize()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        // Both new switches round-trip through advanced.set / advanced.get.
        await connection.SendAsync(1, "advanced.set", new JsonObject
        {
            ["rulesEnabled"] = false,
            ["memoryMode"] = "manual"
        });
        var updated = ParseData(await connection.ReadAsync());
        Assert.False(updated["rulesEnabled"]!.GetValue<bool>());
        Assert.Equal("manual", updated["memoryMode"]!.GetValue<string>());

        // Unknown memory modes fall back to auto instead of poisoning the config.
        await connection.SendAsync(2, "advanced.set", new JsonObject { ["memoryMode"] = "bogus" });
        Assert.Equal("auto", ParseData(await connection.ReadAsync())["memoryMode"]!.GetValue<string>());

        // Rules come back on independently of the memory mode.
        await connection.SendAsync(3, "advanced.set", new JsonObject { ["rulesEnabled"] = true });
        var restored = ParseData(await connection.ReadAsync());
        Assert.True(restored["rulesEnabled"]!.GetValue<bool>());
        Assert.Equal("auto", restored["memoryMode"]!.GetValue<string>());

        // The read path agrees with what was just written.
        await connection.SendAsync(4, "advanced.get", new JsonObject());
        var fetched = ParseData(await connection.ReadAsync());
        Assert.True(fetched["rulesEnabled"]!.GetValue<bool>());
        Assert.Equal("auto", fetched["memoryMode"]!.GetValue<string>());

        // Rule and memory endpoints stay reachable with the new config in place.
        await connection.SendAsync(5, "rules.list", new JsonObject());
        Assert.Equal("result", (await connection.ReadAsync())["event"]!.GetValue<string>());
        await connection.SendAsync(6, "memory.get", new JsonObject());
        Assert.Equal("result", (await connection.ReadAsync())["event"]!.GetValue<string>());
    }

    [Fact]
    public async Task Knowledge_SaveListSearchDelete_AndScopeIsolation()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        await connection.SendAsync(1, "knowledge.save", new JsonObject
        {
            ["title"] = "构建命令",
            ["content"] = "使用 pnpm build 构建桌面端",
            ["tags"] = "build,前端"
        });
        var saved = ParseData(await connection.ReadAsync());
        Assert.Equal(1, saved["count"]!.GetValue<int>());
        Assert.Equal("构建命令", saved["entries"]![0]!["title"]!.GetValue<string>());

        // Re-saving with the same title updates instead of duplicating.
        await connection.SendAsync(2, "knowledge.save", new JsonObject
        {
            ["title"] = "构建命令",
            ["content"] = "使用 pnpm build:desktop 构建桌面端"
        });
        var updated = ParseData(await connection.ReadAsync());
        Assert.Equal(1, updated["count"]!.GetValue<int>());
        Assert.Contains("build:desktop", updated["entries"]![0]!["content"]!.GetValue<string>());

        await connection.SendAsync(3, "knowledge.search", new JsonObject { ["query"] = "构建" });
        var searched = ParseData(await connection.ReadAsync());
        Assert.Equal(1, searched["count"]!.GetValue<int>());

        await connection.SendAsync(4, "knowledge.search", new JsonObject { ["query"] = "不存在的词条" });
        Assert.Equal(0, ParseData(await connection.ReadAsync())["count"]!.GetValue<int>());

        // Global scope keeps its own entries and stays isolated from the workspace.
        await connection.SendAsync(5, "knowledge.save", new JsonObject
        {
            ["global"] = true,
            ["title"] = "全局偏好",
            ["content"] = "回复使用中文"
        });
        var globalList = ParseData(await connection.ReadAsync());
        Assert.Equal(1, globalList["count"]!.GetValue<int>());
        Assert.Equal("全局偏好", globalList["entries"]![0]!["title"]!.GetValue<string>());

        await connection.SendAsync(6, "knowledge.list");
        var workspaceList = ParseData(await connection.ReadAsync());
        Assert.Equal(1, workspaceList["count"]!.GetValue<int>());
        Assert.Equal("构建命令", workspaceList["entries"]![0]!["title"]!.GetValue<string>());

        var globalId = globalList["entries"]![0]!["id"]!.GetValue<int>();
        await connection.SendAsync(7, "knowledge.delete", new JsonObject { ["global"] = true, ["id"] = globalId });
        Assert.Equal(0, ParseData(await connection.ReadAsync())["count"]!.GetValue<int>());

        await connection.SendAsync(8, "knowledge.delete", new JsonObject { ["id"] = 424242 });
        Assert.Equal("error", (await connection.ReadAsync())["event"]!.GetValue<string>());

        var workspaceId = workspaceList["entries"]![0]!["id"]!.GetValue<int>();
        await connection.SendAsync(9, "knowledge.delete", new JsonObject { ["id"] = workspaceId });
        Assert.Equal(0, ParseData(await connection.ReadAsync())["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Knowledge_SemanticStatusBuild_AndRetrievalCarriesSemanticState()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        await connection.SendAsync(1, "knowledge.save", new JsonObject
        {
            ["title"] = "构建命令",
            ["content"] = "使用 pnpm build 构建桌面端",
        });
        await connection.ReadAsync();

        // 测试配置里没有声明向量化能力的模型：状态必须说「没配」并给出原因，
        // 而不是报「已启用但每条嵌入都失败」的假象。
        await connection.SendAsync(2, "knowledge.semantic.status");
        var status = ParseData(await connection.ReadAsync());
        Assert.False(status["enabled"]!.GetValue<bool>());
        Assert.Equal(1, status["total"]!.GetValue<int>());
        Assert.Equal(1, status["pending"]!.GetValue<int>());
        Assert.Contains("嵌入模型", status["reason"]!.GetValue<string>());

        // 建索引同样把原因回传，不留半个静默的索引。
        await connection.SendAsync(3, "knowledge.semantic.build", new JsonObject { ["batch"] = 4 });
        var built = ParseData(await connection.ReadAsync());
        Assert.Equal(0, built["embedded"]!.GetValue<int>());
        Assert.NotNull(built["error"]);

        // 检索结果附带语义状态，界面才能说明这次是语义还是纯词法。
        await connection.SendAsync(4, "knowledge.retrieve", new JsonObject { ["query"] = "构建" });
        var retrieved = ParseData(await connection.ReadAsync());
        Assert.Equal(1, retrieved["count"]!.GetValue<int>());
        Assert.False(retrieved["semantic"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Knowledge_TagsExport_TagFilter_AndSaveById()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        await connection.SendAsync(1, "knowledge.save", new JsonObject
        {
            ["title"] = "构建命令",
            ["content"] = "使用 pnpm build 构建桌面端",
            ["tags"] = "build,前端"
        });
        ParseData(await connection.ReadAsync());
        await connection.SendAsync(2, "knowledge.save", new JsonObject
        {
            ["title"] = "部署流程",
            ["content"] = "使用 docker compose up -d 发布",
            ["tags"] = "build，部署"
        });
        ParseData(await connection.ReadAsync());

        // 标签聚合：全角逗号同样拆分，按计数排序。
        await connection.SendAsync(3, "knowledge.tags");
        var tags = ParseData(await connection.ReadAsync());
        Assert.Equal(2, tags["total"]!.GetValue<int>());
        var tagList = tags["tags"]!.AsArray();
        Assert.Equal("build", tagList[0]!["tag"]!.GetValue<string>());
        Assert.Equal(2, tagList[0]!["count"]!.GetValue<int>());

        // 标签精确筛选："buildtool" 这类子串不算命中。
        await connection.SendAsync(4, "knowledge.list", new JsonObject { ["tag"] = "部署" });
        var filtered = ParseData(await connection.ReadAsync());
        Assert.Equal(1, filtered["count"]!.GetValue<int>());
        Assert.Equal("部署流程", filtered["entries"]![0]!["title"]!.GetValue<string>());

        // 编辑时带 id（含改名）：原地更新，不会分叉出重复条目。
        await connection.SendAsync(5, "knowledge.list");
        var list = ParseData(await connection.ReadAsync());
        var entryId = list["entries"]!.AsArray()
            .First(e => e!["title"]!.GetValue<string>() == "构建命令")!["id"]!.GetValue<int>();
        await connection.SendAsync(6, "knowledge.save", new JsonObject
        {
            ["id"] = entryId,
            ["title"] = "构建桌面命令",
            ["content"] = "使用 pnpm build:desktop 构建桌面端",
            ["tags"] = "build,前端"
        });
        var renamed = ParseData(await connection.ReadAsync());
        Assert.Equal(2, renamed["count"]!.GetValue<int>());
        Assert.Contains(renamed["entries"]!.AsArray(), e => e!["title"]!.GetValue<string>() == "构建桌面命令");

        await connection.SendAsync(7, "knowledge.save", new JsonObject
        {
            ["id"] = 987654,
            ["title"] = "不存在",
            ["content"] = "无"
        });
        Assert.Equal("error", (await connection.ReadAsync())["event"]!.GetValue<string>());

        // 导出：Markdown 包含标题与标签行。
        await connection.SendAsync(8, "knowledge.export");
        var exported = ParseData(await connection.ReadAsync());
        Assert.Equal(2, exported["count"]!.GetValue<int>());
        var markdown = exported["markdown"]!.GetValue<string>();
        Assert.Contains("## 构建桌面命令", markdown);
        Assert.Contains("> 标签：", markdown);

        // 同义词表 get/save：写入临时路径（本类已与 KnowledgeTuningTests 串行），结束后还原。
        var synonymsFile = Path.Combine(_tempDir, "synonyms.txt");
        KnowledgeTuning.ConfigurePaths(synonymsFile);
        try
        {
            await connection.SendAsync(9, "knowledge.synonyms.get");
            var initial = ParseData(await connection.ReadAsync());
            Assert.False(initial["exists"]!.GetValue<bool>());
            Assert.Equal(synonymsFile, initial["path"]!.GetValue<string>());

            await connection.SendAsync(10, "knowledge.synonyms.save", new JsonObject
            {
                ["content"] = "# 注释\n部署 = 发布"
            });
            var saved = ParseData(await connection.ReadAsync());
            Assert.True(saved["exists"]!.GetValue<bool>());
            Assert.Equal("# 注释\n部署 = 发布", saved["content"]!.GetValue<string>());
            Assert.True(File.Exists(synonymsFile));

            // 保存的条目立刻参与检索（热加载）。
            await connection.SendAsync(11, "knowledge.search", new JsonObject { ["query"] = "发布" });
            var searched = ParseData(await connection.ReadAsync());
            Assert.Contains(searched["entries"]!.AsArray(),
                e => e!["title"]!.GetValue<string>() == "部署流程");
        }
        finally
        {
            KnowledgeTuning.ResetForTests();
        }
    }

    [Fact]
    public async Task Knowledge_ImportTextDocxXlsx_AndRejectsBinary()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        var importDir = Path.Combine(_tempDir, "import");
        Directory.CreateDirectory(importDir);

        var notesPath = Path.Combine(importDir, "部署笔记.txt");
        await File.WriteAllTextAsync(notesPath, "部署命令是 pnpm deploy\r\n端口固定为 7800", new UTF8Encoding(false));

        // >6000 chars forces multi-chunk splitting with · 第i/n部分 titles.
        var longPath = Path.Combine(importDir, "长文.md");
        var paragraph = "这是一段足够长的知识内容，用来撑爆单条 6000 字符的分块上限。" + new string('知', 200) + "\n\n";
        await File.WriteAllTextAsync(longPath, string.Concat(Enumerable.Repeat(paragraph, 40)), new UTF8Encoding(false));

        var docxPath = Path.Combine(importDir, "会议纪要.docx");
        using (var stream = File.Create(docxPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("word/document.xml");
            await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            await writer.WriteAsync(
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                <w:body><w:p><w:r><w:t>发布流程第一步</w:t></w:r></w:p>
                <w:p><w:r><w:t>发布流程第二步</w:t></w:r></w:p></w:body></w:document>
                """);
        }

        var gbkPath = Path.Combine(importDir, "旧文档.txt");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        await File.WriteAllTextAsync(gbkPath, "旧编码的中文说明：构建使用 pnpm build", Encoding.GetEncoding(936));

        var xlsxPath = Path.Combine(importDir, "清单.xlsx");
        using (var stream = File.Create(xlsxPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var strings = archive.CreateEntry("xl/sharedStrings.xml");
            await using (var writer = new StreamWriter(strings.Open(), new UTF8Encoding(false)))
            {
                await writer.WriteAsync(
                    """<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>项目名</t></si><si><t>Haoyue</t></si></sst>""");
            }
            var sheet = archive.CreateEntry("xl/worksheets/sheet1.xml");
            await using (var writer = new StreamWriter(sheet.Open(), new UTF8Encoding(false)))
            {
                await writer.WriteAsync(
                    """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row><row r="2"><c r="A2"><v>备注列</v></c></row></sheetData></worksheet>""");
            }
        }

        var binaryPath = Path.Combine(importDir, "图片.png");
        await File.WriteAllBytesAsync(binaryPath, [0x89, 0x50, 0x4E, 0x47, 0x00, 0x0D, 0x0A, 0x1A]);

        await connection.SendAsync(1, "knowledge.import", new JsonObject
        {
            ["paths"] = new JsonArray(notesPath, docxPath, xlsxPath, gbkPath)
        });
        var imported = ParseData(await connection.ReadAsync());
        Assert.Equal(4, imported["importedFiles"]!.GetValue<int>());
        Assert.Equal(4, imported["importedEntries"]!.GetValue<int>());
        Assert.Equal(4, imported["count"]!.GetValue<int>());
        Assert.Contains(imported["entries"]!.AsArray(),
            item => item!["title"]!.GetValue<string>() == "部署笔记.txt");
        Assert.Contains(imported["entries"]!.AsArray(),
            item => item!["title"]!.GetValue<string>() == "会议纪要.docx"
                && item["content"]!.GetValue<string>().Contains("发布流程第二步"));
        Assert.Contains(imported["entries"]!.AsArray(),
            item => item!["title"]!.GetValue<string>() == "清单.xlsx"
                && item["content"]!.GetValue<string>().Contains("项目名\tHaoyue")
                && item["content"]!.GetValue<string>().Contains("备注列"));
        Assert.Contains(imported["entries"]!.AsArray(),
            item => item!["title"]!.GetValue<string>() == "旧文档.txt"
                && item["content"]!.GetValue<string>().Contains("pnpm build"));
        Assert.Contains(imported["entries"]!.AsArray(),
            item => item["tags"]!.GetValue<string>() == "导入,txt");

        // Re-import upserts by title instead of duplicating.
        await connection.SendAsync(2, "knowledge.import", new JsonObject
        {
            ["paths"] = new JsonArray(notesPath)
        });
        var reimported = ParseData(await connection.ReadAsync());
        Assert.Equal(1, reimported["importedEntries"]!.GetValue<int>());
        Assert.Equal(4, reimported["count"]!.GetValue<int>());

        await connection.SendAsync(3, "knowledge.import", new JsonObject
        {
            ["paths"] = new JsonArray(longPath)
        });
        var chunked = ParseData(await connection.ReadAsync());
        var chunkCount = chunked["importedEntries"]!.GetValue<int>();
        Assert.True(chunkCount > 1, $"long file should split into multiple chunks, got {chunkCount}");
        Assert.Contains(chunked["entries"]!.AsArray(),
            item => item!["title"]!.GetValue<string>().StartsWith("长文.md · 第1/"));
        Assert.Equal(4 + chunkCount, chunked["count"]!.GetValue<int>());

        await connection.SendAsync(4, "knowledge.import", new JsonObject
        {
            ["paths"] = new JsonArray(binaryPath)
        });
        var failed = await connection.ReadAsync();
        Assert.Equal("error", failed["event"]!.GetValue<string>());
        Assert.Contains("不支持", failed["data"]!.GetValue<string>());

        await connection.SendAsync(5, "knowledge.import", new JsonObject
        {
            ["paths"] = new JsonArray(Path.Combine(importDir, "missing.txt"))
        });
        var missing = await connection.ReadAsync();
        Assert.Equal("error", missing["event"]!.GetValue<string>());
        Assert.Contains("文件不存在", missing["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task Rules_AndMemory_GetSave_ListAndTraversalGuard()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        // Memory starts absent and round-trips through save/get.
        await connection.SendAsync(1, "memory.get", new JsonObject());
        var memory = ParseData(await connection.ReadAsync());
        Assert.False(memory["exists"]!.GetValue<bool>());
        Assert.Equal("", memory["content"]!.GetValue<string>());
        Assert.EndsWith("MEMORY.md", memory["path"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        await connection.SendAsync(2, "memory.save", new JsonObject { ["content"] = "# 项目记忆\n\n- 构建用 pnpm build" });
        var saved = ParseData(await connection.ReadAsync());
        Assert.True(File.Exists(saved["path"]!.GetValue<string>()));
        Assert.Contains("pnpm build", File.ReadAllText(saved["path"]!.GetValue<string>()));

        // Global memory lives in a different file and stays isolated.
        await connection.SendAsync(3, "memory.save", new JsonObject { ["global"] = true, ["content"] = "全局偏好：中文回复" });
        ParseData(await connection.ReadAsync());
        await connection.SendAsync(4, "memory.get", new JsonObject());
        var workspaceMemory = ParseData(await connection.ReadAsync());
        Assert.Contains("pnpm build", workspaceMemory["content"]!.GetValue<string>());
        await connection.SendAsync(5, "memory.get", new JsonObject { ["global"] = true });
        var globalMemory = ParseData(await connection.ReadAsync());
        Assert.Contains("全局偏好", globalMemory["content"]!.GetValue<string>());
        Assert.NotEqual(workspaceMemory["path"]!.GetValue<string>(), globalMemory["path"]!.GetValue<string>());

        // No rules yet — the list is empty and saving creates the root file.
        await connection.SendAsync(6, "rules.list", new JsonObject());
        Assert.Equal(0, ParseData(await connection.ReadAsync())["files"]!.AsArray().Count);

        await connection.SendAsync(7, "rules.save", new JsonObject { ["content"] = "- 构建命令：pnpm build" });
        var ruleSaved = ParseData(await connection.ReadAsync());
        Assert.Equal("AGENTS.md", ruleSaved["path"]!.GetValue<string>());

        await connection.SendAsync(8, "rules.save", new JsonObject
        {
            ["path"] = "packages/app/AGENTS.md",
            ["content"] = "- 仅适用于 packages/app 的前端规则"
        });
        ParseData(await connection.ReadAsync());

        await connection.SendAsync(9, "rules.list", new JsonObject());
        var listed = ParseData(await connection.ReadAsync());
        Assert.Equal(2, listed["files"]!.AsArray().Count);
        var rootFile = listed["files"]!.AsArray().First(item => item!["isRoot"]!.GetValue<bool>());
        Assert.Equal("AGENTS.md", rootFile["path"]!.GetValue<string>());
        Assert.Contains(listed["files"]!.AsArray(),
            item => item!["path"]!.GetValue<string>() == "packages/app/AGENTS.md");

        // Path traversal and non-AGENTS.md names are rejected.
        await connection.SendAsync(10, "rules.save", new JsonObject { ["path"] = "../evil/AGENTS.md", ["content"] = "evil" });
        Assert.Equal("error", (await connection.ReadAsync())["event"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(_tempDir, "evil", "AGENTS.md")));

        await connection.SendAsync(11, "rules.save", new JsonObject { ["path"] = "README.md", ["content"] = "x" });
        Assert.Equal("error", (await connection.ReadAsync())["event"]!.GetValue<string>());

        // Global sessions carry no rules.
        await connection.SendAsync(12, "rules.save", new JsonObject { ["global"] = true, ["content"] = "x" });
        Assert.Equal("error", (await connection.ReadAsync())["event"]!.GetValue<string>());

        // rules.delete removes a hierarchical file with the same validation as save.
        await connection.SendAsync(13, "rules.delete", new JsonObject { ["path"] = "packages/app/AGENTS.md" });
        var deleted = ParseData(await connection.ReadAsync());
        Assert.Equal("packages/app/AGENTS.md", deleted["path"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(_tempDir, "packages", "app", "AGENTS.md")));

        await connection.SendAsync(14, "rules.list", new JsonObject());
        Assert.Single(ParseData(await connection.ReadAsync())["files"]!.AsArray());

        await connection.SendAsync(15, "rules.delete", new JsonObject { ["path"] = "../evil/AGENTS.md" });
        Assert.Equal("error", (await connection.ReadAsync())["event"]!.GetValue<string>());
    }

    [Fact]
    public async Task EvolutionConfig_RoundTrip_PersistsAndClampsInterval()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        await connection.SendAsync(1, "evolution.config.get", new JsonObject());
        var initial = ParseData(await connection.ReadAsync());
        Assert.False(initial["autoReflect"]!.GetValue<bool>());

        await connection.SendAsync(2, "evolution.config.set", new JsonObject
        {
            ["autoReflect"] = true,
            ["intervalMinutes"] = 5
        });
        var clamped = ParseData(await connection.ReadAsync());
        Assert.True(clamped["autoReflect"]!.GetValue<bool>());
        Assert.Equal(30, clamped["intervalMinutes"]!.GetValue<int>());

        await connection.SendAsync(3, "evolution.config.set", new JsonObject { ["intervalMinutes"] = 99999 });
        Assert.Equal(10080, ParseData(await connection.ReadAsync())["intervalMinutes"]!.GetValue<int>());

        // Set persists through ConfigStore, so a fresh read reflects the stored value.
        await connection.SendAsync(4, "evolution.config.get", new JsonObject());
        var persisted = ParseData(await connection.ReadAsync());
        Assert.Equal(10080, persisted["intervalMinutes"]!.GetValue<int>());
    }

    [Fact]
    public async Task EventsRecent_ReplaysJournaledEventsOldestFirst()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        _runtime!.Events.Publish(new ErrorEvent("first"));
        _runtime!.Events.Publish(new WarningEvent("second"));

        await connection.SendAsync(1, "events.recent", new JsonObject { ["limit"] = 10 });
        var journal = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        Assert.Equal(2, journal.Count);
        Assert.Equal("ErrorEvent", journal[0]!["type"]!.GetValue<string>());
        Assert.Equal("error", journal[0]!["event"]!.GetValue<string>());
        Assert.Equal("first", journal[0]!["payload"]!["message"]!.GetValue<string>());
        Assert.Equal("warning", journal[1]!["event"]!.GetValue<string>());
        Assert.Equal("second", journal[1]!["payload"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task EventsRecent_CarriesSessionId_SoReconnectsCanAttributeTerminals()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        _runtime!.Events.Publish(new TurnCompletedEvent("session-9", false, null, null, "stop"));

        await connection.SendAsync(1, "events.recent", new JsonObject { ["limit"] = 10 });
        var journal = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        var row = Assert.Single(journal);
        Assert.Equal("turn_completed", row!["event"]!.GetValue<string>());
        Assert.Equal("session-9", row["sessionId"]!.GetValue<string>());
        Assert.Equal("stop", row["payload"]!["finishReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task EventsRecent_AfterParameter_ReturnsOnlyNewerRows()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        _runtime!.Events.Publish(new ErrorEvent("seen"));
        await connection.SendAsync(1, "events.recent", new JsonObject { ["limit"] = 10 });
        var first = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        var cursor = first[^1]!["id"]!.GetValue<long>();

        // 客户端记账后再次拉取：没有新事件时返回空集，不会把同一条终态二次灌进界面。
        await connection.SendAsync(2, "events.recent", new JsonObject { ["limit"] = 10, ["after"] = cursor });
        Assert.Empty(JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray());

        _runtime!.Events.Publish(new WarningEvent("newer"));
        await connection.SendAsync(3, "events.recent", new JsonObject { ["limit"] = 10, ["after"] = cursor });
        var replayed = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        var row = Assert.Single(replayed);
        Assert.Equal("warning", row!["event"]!.GetValue<string>());
        Assert.True(row["id"]!.GetValue<long>() > cursor);
    }

    [Fact]
    public async Task Handshake_WithValidToken_ServesSubsequentRequests()
    {
        var token = new string('a', 64);
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)),
            handshakeToken: token);

        await connection.HandshakeAsync(token, id: 7);
        var authed = await connection.ReadAsync();
        Assert.Equal(7, authed["id"]!.GetValue<long>());
        Assert.Equal("result", authed["event"]!.GetValue<string>());
        var info = JsonNode.Parse(authed["data"]!.GetValue<string>())!;
        Assert.Equal(DaemonServer.ProtocolVersion, info["version"]!.GetValue<string>());

        await connection.SendAsync(1, "ping");
        Assert.Equal("pong", (await connection.ReadAsync())["event"]!.GetValue<string>());
    }

    [Fact]
    public async Task Handshake_WithWrongToken_RejectsAndClosesTheConnection()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)),
            handshakeToken: new string('a', 64));

        await connection.HandshakeAsync(new string('b', 64));
        var rejected = await connection.ReadAsync();
        Assert.Equal("error", rejected["event"]!.GetValue<string>());
        Assert.Contains("authentication", rejected["data"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        // The server closes the link right after the rejection.
        await Assert.ThrowsAnyAsync<Exception>(() => connection.ReadAsync());
    }

    [Fact]
    public async Task Handshake_WithIncompatibleMajorVersion_IsRejectedWithVersionMismatchCode()
    {
        var token = new string('a', 64);
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)),
            handshakeToken: token);

        await connection.HandshakeWithVersionAsync(token, "99.0");
        var rejected = await connection.ReadAsync();
        Assert.Equal("error", rejected["event"]!.GetValue<string>());
        var code = rejected["details"]?["code"]?.GetValue<string>();
        Assert.Equal("versionMismatch", code);
        Assert.Contains("protocol version mismatch", rejected["data"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handshake_WithClientNewerMinorVersion_AcceptsWithUpgradeWarning()
    {
        var token = new string('a', 64);
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)),
            handshakeToken: token);

        var serverMinor = int.Parse(DaemonServer.ProtocolVersion.Split('.')[1]);
        await connection.HandshakeWithVersionAsync(token, $"2.{serverMinor + 1}");
        var authed = await connection.ReadAsync();
        Assert.Equal("result", authed["event"]!.GetValue<string>());
        var info = JsonNode.Parse(authed["data"]!.GetValue<string>())!;
        Assert.NotNull(info["versionWarning"]);
        Assert.Contains("升级", info["versionWarning"]!.GetValue<string>());
    }

    [Fact]
    public async Task Handshake_WithCompatibleVersion_AcceptsWithoutWarning()
    {
        var token = new string('a', 64);
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)),
            handshakeToken: token);

        await connection.HandshakeWithVersionAsync(token, DaemonServer.ProtocolVersion);
        var authed = await connection.ReadAsync();
        Assert.Equal("result", authed["event"]!.GetValue<string>());
        var info = JsonNode.Parse(authed["data"]!.GetValue<string>())!;
        Assert.Null(info["versionWarning"]);
    }

    [Theory]
    [InlineData(410, "any detail", true)]
    [InlineData(404, "model decommissioned by provider", true)]
    [InlineData(404, "model not found in catalog", false)]
    [InlineData(500, "internal server error", false)]
    public void ApiVersionContract_ClassifiesBreakingChanges(int status, string detail, bool expectAlert)
    {
        var alert = Haoyue.Runtime.Providers.ApiVersionContract.ClassifyBreakingChange(status, detail);
        Assert.Equal(expectAlert, alert is not null);
    }

    // ---------------------------------------------------------------- 失败日志分析（进化引擎 E1）端到端

    /// <summary>
    /// 集成测试：失败信号落盘（EventJournal/SQLite）→ evolution.inspect RPC →
    /// DefectAggregator 聚合输出。覆盖 DaemonServer switch 分发 + 真实数据库往返，
    /// 补齐此前仅单元级覆盖的缺口（测试金字塔的集成层）。
    /// </summary>
    [Fact]
    public async Task EvolutionInspect_AggregatesJournaledFailures_EndToEnd()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));
        var db = _runtime!.Database;

        // 脚本化一个「失败回合」：bash 工具连续 3 次同样错误（达到 ClusterThreshold）+ 一次用户负反馈。
        const string session = "sess-e2e-1";
        db.AppendEvent(DateTimeOffset.UtcNow, nameof(Haoyue.Runtime.Events.TurnStartedEvent),
            EventJournal.Serialize(new Haoyue.Runtime.Events.TurnStartedEvent(session, "跑构建")));
        for (var i = 0; i < 3; i++)
        {
            db.AppendEvent(DateTimeOffset.UtcNow.AddSeconds(i), nameof(Haoyue.Runtime.Events.ToolCallCompletedEvent),
                EventJournal.Serialize(new Haoyue.Runtime.Events.ToolCallCompletedEvent(
                    $"call-{i}", "bash", Success: false, "Error: connection refused", TimeSpan.FromSeconds(1))));
        }
        db.AppendEvent(DateTimeOffset.UtcNow.AddSeconds(5), nameof(Haoyue.Runtime.Events.TurnCompletedEvent),
            EventJournal.Serialize(new Haoyue.Runtime.Events.TurnCompletedEvent(session, Cancelled: false, Error: null)));
        db.AppendEvent(DateTimeOffset.UtcNow.AddSeconds(6), nameof(Haoyue.Runtime.Events.UserFeedbackEvent),
            EventJournal.Serialize(new Haoyue.Runtime.Events.UserFeedbackEvent(session, "negative", "构建一直失败")));

        await connection.SendAsync(11, "evolution.inspect", new JsonObject { ["limit"] = 500 });
        var reply = await connection.ReadAsync();
        Assert.Equal("result", reply["event"]!.GetValue<string>());

        var reports = JsonNode.Parse(reply["data"]!.GetValue<string>())!["reports"]!.AsArray();
        var cluster = reports.FirstOrDefault(r => r!["kind"]!.GetValue<string>() == "ToolFailureCluster");
        Assert.NotNull(cluster);
        Assert.Equal("bash", cluster!["toolName"]!.GetValue<string>());
        Assert.Equal(3, cluster["occurrences"]!.GetValue<int>());
        Assert.Contains("connection refused", cluster["errorSummary"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        var feedback = reports.FirstOrDefault(r => r!["kind"]!.GetValue<string>() == "UserNegativeFeedback");
        Assert.NotNull(feedback);
        Assert.Null(feedback!["toolName"]); // 反馈类报告没有工具名
        Assert.NotNull(feedback["fingerprint"]);
    }

    [Fact]
    public async Task EvolutionInspect_EmptyJournal_ReturnsNoReports()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        await connection.SendAsync(12, "evolution.inspect");
        var reply = await connection.ReadAsync();
        Assert.Equal("result", reply["event"]!.GetValue<string>());
        Assert.Empty(JsonNode.Parse(reply["data"]!.GetValue<string>())!["reports"]!.AsArray());
    }

    [Fact]
    public async Task ConfigStatus_ExposesSchemaVersionAndValidationWarnings()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        await connection.SendAsync(13, "config.status");
        var reply = await connection.ReadAsync();
        Assert.Equal("result", reply["event"]!.GetValue<string>());
        var status = JsonNode.Parse(reply["data"]!.GetValue<string>())!;
        Assert.Equal(ConfigSchema.CurrentVersion, status["schemaVersion"]!.GetValue<int>());
        Assert.False(status["hasAnomaly"]!.GetValue<bool>());
        Assert.Empty(status["validationWarnings"]!.AsArray());
        // 凭据告警必须有出口：明文 Key 迁移失败时界面靠这两个字段起横幅。
        Assert.Empty(status["secretWarnings"]!.AsArray());
        Assert.Contains(status["secretStorage"]!.GetValue<string>(), new string[] { "dpapi", "keyring", "file" });
    }

    [Fact]
    public async Task Handshake_RequiredBeforeAnyOtherMethod()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)),
            handshakeToken: new string('a', 64));

        // A business method sent before the handshake is rejected with an
        // authentication error and the connection is dropped.
        await connection.SendAsync(1, "ping");
        var rejected = await connection.ReadAsync();
        Assert.Equal(1, rejected["id"]!.GetValue<long>());
        Assert.Equal("error", rejected["event"]!.GetValue<string>());
        Assert.Contains("authentication", rejected["data"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAnyAsync<Exception>(() => connection.ReadAsync());
    }

    [Fact]
    public async Task RecoverInterruptedTurns_AppendsNotice_ClearsJournal()
    {
        var workspaceRoot = CreateWorkspace("workspace");
        var configStore = new ConfigStore(
            Path.Combine(_tempDir, "config.json"),
            Path.Combine(_tempDir, "state.json"));
        var runtime = HaoyueRuntime.Create(
            workspaceRoot,
            configStore,
            Path.Combine(_tempDir, "haoyue.db"),
            new OfflineHealthChecker(new HealthChecker(new LlmHttpFactory(), configStore)));
        var workspace = new WorkspaceManager().Detect(workspaceRoot);
        var session = runtime.Sessions.Create(workspace);
        runtime.Sessions.Append(session, ChatMessage.User("帮我整理周报"));

        // Simulate a crash residue: the turn was in flight when the process died.
        var journalFile = Path.Combine(_tempDir, "active-turns.json");
        ActiveTurnJournal.Write(journalFile, new List<ActiveTurnRecord>
        {
            new(
                session.Header.Id,
                Haoyue.Runtime.Data.HaoyueDatabase.ScopeKey(workspace),
                workspaceRoot, false, DateTimeOffset.UtcNow)
        });

        var globalWorkspace = new WorkspaceManager().CreateGlobal(Path.Combine(_tempDir, "global-state"));
        var server = new DaemonServer(
            runtime, (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)),
            globalWorkspace, null, journalFile,
            daemonLogFile: Path.Combine(_tempDir, "daemon.log"));
        _asyncDisposables.Add(server);

        await server.RecoverInterruptedTurnsAsync(CancellationToken.None);

        var reloaded = runtime.Sessions.Load(workspace, session.Header.Id)!;
        Assert.Contains(">>> [turn interrupted]", reloaded.Messages[^1].Text);
        Assert.False(File.Exists(journalFile), "journal must be cleared after successful recovery");

        // Unknown session ids in the residue must not throw; the journal still clears.
        ActiveTurnJournal.Write(journalFile, new List<ActiveTurnRecord>
        {
            new("no-such-session", "workspace|X:\\gone", "X:\\gone", false, DateTimeOffset.UtcNow)
        });
        await server.RecoverInterruptedTurnsAsync(CancellationToken.None);
        Assert.False(File.Exists(journalFile));
    }

    [Fact]
    public async Task WorkspaceFiles_RanksMatchesAndHidesIgnoredAndCredentialPaths()
    {
        var workspace = CreateWorkspace("workspace-files");
        Directory.CreateDirectory(Path.Combine(workspace, "src"));
        Directory.CreateDirectory(Path.Combine(workspace, "tools", "mapping"));
        Directory.CreateDirectory(Path.Combine(workspace, "node_modules", "dep"));
        File.WriteAllText(Path.Combine(workspace, "src", "App.cs"), "class App { }\n");
        File.WriteAllText(Path.Combine(workspace, "src", "Reapply.cs"), "class Reapply { }\n");
        File.WriteAllText(Path.Combine(workspace, "tools", "mapping", "help.md"), "# help\n");
        File.WriteAllText(Path.Combine(workspace, "node_modules", "dep", "index.js"), "// dep\n");
        File.WriteAllText(Path.Combine(workspace, ".env"), "TOKEN=never-echoed\n");

        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)), workspace);

        await connection.SendAsync(1, "workspace.files", new JsonObject
        {
            ["workspace"] = workspace,
            ["query"] = "app",
        });
        var hits = JsonNode.Parse((await connection.ReadUntilAsync(item =>
            item["id"]?.GetValue<long>() == 1))["data"]!.GetValue<string>())!.AsArray();
        // 文件名前缀 > 文件名包含 > 路径包含：三档得分互不相同，断言与写盘时间无关。
        Assert.Equal(
            new[] { "src/App.cs", "src/Reapply.cs", "tools/mapping/help.md" },
            hits.Select(node => node!.GetValue<string>()));

        await connection.SendAsync(2, "workspace.files", new JsonObject
        {
            ["workspace"] = workspace,
            ["limit"] = 2,
        });
        var browsed = JsonNode.Parse((await connection.ReadUntilAsync(item =>
            item["id"]?.GetValue<long>() == 2))["data"]!.GetValue<string>())!.AsArray();
        Assert.Equal(2, browsed.Count);
        Assert.DoesNotContain(browsed, node => node!.GetValue<string>().Contains(".env", StringComparison.Ordinal));

        // 凭证目录整体拒绝：选中的相对路径会进 prompt，等于给模型指路去读密钥。
        var sshDir = Path.Combine(_tempDir, "outside-case", ".ssh");
        Directory.CreateDirectory(sshDir);
        File.WriteAllText(Path.Combine(sshDir, "id_ed25519"), "private\n");
        await connection.SendAsync(3, "workspace.files", new JsonObject { ["workspace"] = sshDir });
        var refused = await connection.ReadUntilAsync(item => item["id"]?.GetValue<long>() == 3);
        Assert.Equal("error", refused["event"]!.GetValue<string>());
        Assert.Contains("not listable", refused["data"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    private async Task<TestConnection> StartServerAsync(
        Func<AgentSession, WorkspaceInfo, string, CancellationToken, Task<AgentTurnResult>> runTurn,
        string? workspace = null,
        string? handshakeToken = null)
    {
        workspace ??= CreateWorkspace("workspace");
        var configStore = new ConfigStore(
            Path.Combine(_tempDir, "config.json"),
            Path.Combine(_tempDir, "state.json"));
        _runtime = HaoyueRuntime.Create(
            workspace,
            configStore,
            Path.Combine(_tempDir, "haoyue.db"),
            new OfflineHealthChecker(new HealthChecker(new LlmHttpFactory(), configStore)));
        var globalWorkspace = new WorkspaceManager().CreateGlobal(Path.Combine(_tempDir, "global-state"));
        var server = new DaemonServer(_runtime, runTurn, globalWorkspace, handshakeToken,
            daemonLogFile: Path.Combine(_tempDir, "daemon.log"));
        _asyncDisposables.Add(server);

        return await OpenConnectionAsync(server);
    }

    /// <summary>
    /// 在一个既有 daemon 上再开一条命名管道连接。广播类回归必须让 ≥2 个客户端同时在线，
    /// 单连接的 <see cref="StartServerAsync"/> 覆盖不到「写给第二个客户端」这条路径。
    /// </summary>
    private async Task<TestConnection> OpenConnectionAsync(DaemonServer server)
    {
        var pipeName = $"haoyue-test-{Guid.NewGuid():N}";
        var serverPipe = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var clientPipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        var serverTask = Task.Run(async () =>
        {
            await serverPipe.WaitForConnectionAsync(_serverCts.Token);
            await server.HandleConnectionAsync(serverPipe, _serverCts.Token);
        });
        await clientPipe.ConnectAsync(_serverCts.Token);

        var connection = new TestConnection(clientPipe, serverPipe, serverTask);
        _asyncDisposables.Add(connection);
        return connection;
    }

    [Fact]
    public async Task Config_Status_And_Rebuild()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        await connection.SendAsync(1, "config.status");
        var statusResp = await connection.ReadUntilAsync(
            r => r["id"]!.GetValue<long>() == 1 && r["event"]!.GetValue<string>() == "result");
        var statusData = JsonNode.Parse(statusResp["data"]!.GetValue<string>())!;
        Assert.False(statusData["hasAnomaly"]!.GetValue<bool>());

        await connection.SendAsync(2, "config.rebuild");
        var rebuildResp = await connection.ReadUntilAsync(
            r => r["id"]!.GetValue<long>() == 2 && r["event"]!.GetValue<string>() == "result");
        var rebuildData = JsonNode.Parse(rebuildResp["data"]!.GetValue<string>())!;
        Assert.True(rebuildData["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task SessionSearch_RpcRegistered_AndReturnsEmptyListWithoutMessages()
    {
        // Message-append logic itself is covered by SessionStoreTests (the injected
        // turn handler in daemon tests bypasses Agent.RunTurnAsync, which owns the
        // user-message persistence). Here we verify the RPC is wired end to end.
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        await connection.SendAsync(1, "session.search", new JsonObject { ["query"] = "麒麟踏雪" });
        var hits = JsonNode.Parse((await connection.ReadUntilAsync(
                r => r["id"]!.GetValue<long>() == 1 && r["event"]!.GetValue<string>() == "result"))["data"]!.GetValue<string>())!.AsArray();
        Assert.Empty(hits);

        // Missing query must produce a clean protocol error, not a crash.
        await connection.SendAsync(2, "session.search", new JsonObject());
        var error = await connection.ReadUntilAsync(
            r => r["id"]!.GetValue<long>() == 2 && r["event"]!.GetValue<string>() == "error");
        Assert.Contains("query", error["data"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provider_Upsert_WithModelDetails_PersistsCorrectly()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        await connection.SendAsync(1, "provider.upsert", new JsonObject
        {
            ["id"] = "test-provider",
            ["name"] = "Test Provider",
            ["kind"] = "openai",
            ["baseUrl"] = "https://api.test.com/v1",
            ["modelDetails"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "test-model-1",
                    ["alias"] = "Test Vision",
                    ["contextWindow"] = 64000,
                    ["maxOutput"] = 4096,
                    ["vision"] = true
                }
            }
        });

        var upsertResp = await connection.ReadUntilAsync(
            r => r["id"]!.GetValue<long>() == 1 && r["event"]!.GetValue<string>() == "result");
        var upsertData = JsonNode.Parse(upsertResp["data"]!.GetValue<string>())!;
        Assert.Equal("test-provider", upsertData["id"]!.GetValue<string>());
        var details = upsertData["modelDetails"]!.AsArray();
        Assert.Single(details);
        Assert.Equal("test-model-1", details[0]!["id"]!.GetValue<string>());
        Assert.Equal("Test Vision", details[0]!["alias"]!.GetValue<string>());
        Assert.Equal(64000, details[0]!["contextWindow"]!.GetValue<int>());
        Assert.True(details[0]!["vision"]!.GetValue<bool>());
    }

    [Fact]
    public async Task UnknownMethod_ErrorCarriesContractCode()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        await connection.SendAsync(1, "no.such.method", new JsonObject());
        var error = await connection.ReadUntilAsync(
            r => r["id"]!.GetValue<long>() == 1 && r["event"]!.GetValue<string>() == "error");
        Assert.Equal("unknownMethod", error["details"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task ImplementedMethodThrowing_InternalErrorCarriesMethodName()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        // params.model 传数字 → GetValue<string>() 抛 InvalidOperationException：
        // 方法存在但执行失败，必须是 internalError（而不是 unknownMethod），并带方法名。
        await connection.SendAsync(1, "model.switch", new JsonObject { ["model"] = 42 });
        var error = await connection.ReadUntilAsync(
            r => r["id"]!.GetValue<long>() == 1 && r["event"]!.GetValue<string>() == "error");
        Assert.Equal("internalError", error["details"]!["code"]!.GetValue<string>());
        Assert.Equal("model.switch", error["details"]!["method"]!.GetValue<string>());
        Assert.StartsWith("model.switch failed:", error["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task Chat_MissingParams_ErrorCarriesInvalidParamsCode()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        await connection.SendAsync(1, "chat", new JsonObject());
        var error = await connection.ReadUntilAsync(
            r => r["id"]!.GetValue<long>() == 1 && r["event"]!.GetValue<string>() == "error");
        Assert.Equal("invalidParams", error["details"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task InvalidEnvelope_ErrorCarriesInvalidRequestCode()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        // id 类型非法（字符串）→ 信封级错误，code 必须是 invalidRequest。
        await connection.SendRawAsync("{\"id\":\"not-a-number\",\"method\":\"ping\"}");
        var error = await connection.ReadUntilAsync(
            r => r["event"]!.GetValue<string>() == "error");
        Assert.Equal("invalidRequest", error["details"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void Contract_Export_MatchesSnapshot()
    {
        var repoRoot = FindRepoRoot();
        var snapshotPath = Path.Combine(repoRoot, "contracts", "daemon-contract.json");
        var exported = DaemonContract.ExportJson().ToJsonString();
        if (Environment.GetEnvironmentVariable("HAOYUE_UPDATE_CONTRACT") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            File.WriteAllText(snapshotPath, exported + "\n");
            return;
        }
        Assert.True(File.Exists(snapshotPath),
            $"契约快照缺失：设置 HAOYUE_UPDATE_CONTRACT=1 运行测试以生成 {snapshotPath}");
        var snapshot = File.ReadAllText(snapshotPath).TrimEnd();
        Assert.True(exported == snapshot,
            "DaemonContract 导出与 contracts/daemon-contract.json 不一致：" +
            "请设置 HAOYUE_UPDATE_CONTRACT=1 重新生成快照并提交，同步更新 TS 侧生成类型。");
    }

    [Fact]
    public void Contract_AllMethods_HaveParamsSchema()
    {
        // 契约治理：所有方法必须显式声明参数 schema（无参方法用空对象），禁止回退 pending。
        var pending = DaemonContract.Methods.Where(m => m.SchemaStatus != "specified").ToList();
        Assert.True(pending.Count == 0,
            "以下方法缺少 params schema: " + string.Join(", ", pending.Select(m => m.Name)));
    }

    [Fact]
    public void Contract_AllMethods_HaveResultSchemaAndErrorCodes()
    {
        // 契约治理：所有方法必须显式声明 result schema 与错误码。
        // result schema 让 TS SDK 直接类型化响应；逐方法错误码让客户端按失败模式分支，
        // 而不是对 data 文本做字符串匹配。空声明 E() 表示「无特殊失败模式」，
        // 导出时仍会并入 internalError 兜底——漏声明（null）才是护栏要拦的。
        var noResult = DaemonContract.Methods.Where(m => m.ResultSchemaStatus != "specified").ToList();
        Assert.True(noResult.Count == 0,
            "以下方法缺少 result schema: " + string.Join(", ", noResult.Select(m => m.Name)));

        var noErrors = DaemonContract.Methods.Where(m => !m.ErrorCodesDeclared).ToList();
        Assert.True(noErrors.Count == 0,
            "以下方法未声明错误码（无特殊失败模式用 errors: E() 显式空声明）: "
            + string.Join(", ", noErrors.Select(m => m.Name)));

        // internalError 兜底必须出现在每个方法的导出错误码里（daemon 对未预期异常统一回它）。
        var fallback = DaemonErrorCode.InternalError.ToWire();
        var missingFallback = DaemonContract.Methods
            .Where(m => !m.ExportedErrorCodes.Contains(fallback, StringComparer.Ordinal))
            .Select(m => m.Name)
            .ToList();
        Assert.True(missingFallback.Count == 0,
            "以下方法导出错误码缺少 internalError 兜底: " + string.Join(", ", missingFallback));

        // 声明的错误码必须是契约枚举成员（防手写 wire 值漂移）。
        var known = Enum.GetValues<DaemonErrorCode>().Select(c => c.ToWire()).ToHashSet(StringComparer.Ordinal);
        var unknown = DaemonContract.Methods
            .SelectMany(m => m.ErrorCodes ?? [])
            .Where(code => !known.Contains(code))
            .Distinct()
            .ToList();
        Assert.True(unknown.Count == 0,
            "以下错误码不在 DaemonErrorCode 枚举中: " + string.Join(", ", unknown));
    }

    [Fact]
    public void Contract_DispatchCases_AllRegistered()
    {
        // 契约治理：DaemonServer 分发 switch 的每个 case 都必须登记进 DaemonContract，
        // 否则方法会绕过契约（无 TS 类型、无文档导出、快照测试也检测不到）。
        // 字符类含 `-`：漏掉它时 evolution.pending-list 这类连字符方法名匹配不到，
        // 护栏形同虚设（2026-10-15 修）。
        var serverPath = Path.Combine(FindRepoRoot(), "haoyue_runtime", "Daemon", "DaemonServer.cs");
        var source = File.ReadAllText(serverPath);
        var cases = System.Text.RegularExpressions.Regex.Matches(source, @"case\s+""([a-z][A-Za-z0-9.-]*)""\s*:")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        Assert.True(cases.Count > 50, $"分发 case 解析异常，仅匹配到 {cases.Count} 个，疑似源码结构变化");

        var registered = DaemonContract.Methods.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var unregistered = cases.Where(name => !registered.Contains(name)).ToList();
        Assert.True(unregistered.Count == 0,
            "以下分发 case 未登记进 DaemonContract（契约漂移）：\n" + string.Join("\n", unregistered));
    }

    [Fact]
    public void Contract_StreamEvents_HandledByRenderer()
    {
        // 契约治理（P1-3 事件穷举护栏的 .NET 侧兜底）：桌面 daemon-event-handler.ts
        // 必须对每个流式事件有显式归宿——要么是 `case 'x':` 分支，要么在提前 return
        // 的名单里显式处理。TS 侧还有编译期 never 赋值护栏双保险；本测试防止有人
        // 整段删掉 handler 分支而只跑 .NET 测试时无人发现。
        var handlerPath = Path.Combine(
            FindRepoRoot(), "haoyue_desktop", "src", "renderer", "src", "daemon-event-handler.ts");
        var source = File.ReadAllText(handlerPath);
        var missing = DaemonContract.StreamEvents
            .Where(name => !source.Contains($"case '{name}':")
                           && !source.Contains($"event.event === '{name}'"))
            .ToList();
        Assert.True(missing.Count == 0,
            "以下流式事件在桌面 handler 中没有显式分支（新增事件必须显式处理或显式忽略）：\n"
            + string.Join("\n", missing));
    }

    [Fact]
    public void PipeUserSuffix_SanitizesIllegalCharacters()
    {
        Assert.Equal("john.doe", DaemonServer.SanitizePipeUser("john.doe"));
        Assert.Equal("user-name", DaemonServer.SanitizePipeUser("user name"));
        Assert.Equal("user-name", DaemonServer.SanitizePipeUser("user\\name"));
        Assert.Equal("local", DaemonServer.SanitizePipeUser(""));
        Assert.Equal("local", DaemonServer.SanitizePipeUser(null));
        Assert.Equal("local", DaemonServer.SanitizePipeUser("   "));
    }

    [Fact]
    public async Task CreatePipeServer_RestrictsAclToCurrentUser()
    {
        // 独立测试管道名：真实 PipeName 可能与正在运行的桌面 daemon 冲突。
        var pipeName = $"haoyue-acl-test-{Guid.NewGuid():N}";
        var server = DaemonServer.CreatePipeServer(pipeName);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var security = server.GetAccessControl();
                var sids = security.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
                    .Cast<System.IO.Pipes.PipeAccessRule>()
                    .Select(rule => rule.IdentityReference.Value)
                    .ToList();
                // 当前用户与 SYSTEM 允许读写；Everyone (S-1-1-0) 不得出现。
                Assert.Contains(System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, sids);
                Assert.DoesNotContain("S-1-1-0", sids);
            }

            // 行为回归：同用户客户端仍能正常连接。
            using var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            var connectTask = client.ConnectAsync(_serverCts.Token);
            await server.WaitForConnectionAsync(_serverCts.Token).WaitAsync(TimeSpan.FromSeconds(5));
            await connectTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(client.IsConnected);
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public async Task CreatePipeServer_AcceptsTwoConcurrentClients()
    {
        // 并发回归：ACL 需含 CreateNewInstance，否则已有一条活跃连接时创建第二个
        // 服务端实例抛 UnauthorizedAccessException，桌面占用期间其他客户端永远连不上。
        var pipeName = $"haoyue-concurrent-{Guid.NewGuid():N}";
        var server1 = DaemonServer.CreatePipeServer(pipeName);
        var server2 = DaemonServer.CreatePipeServer(pipeName);
        try
        {
            using var client1 = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var client2 = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            await client1.ConnectAsync(_serverCts.Token).WaitAsync(TimeSpan.FromSeconds(5));
            await server1.WaitForConnectionAsync(_serverCts.Token).WaitAsync(TimeSpan.FromSeconds(5));

            await client2.ConnectAsync(_serverCts.Token).WaitAsync(TimeSpan.FromSeconds(5));
            await server2.WaitForConnectionAsync(_serverCts.Token).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(client1.IsConnected);
            Assert.True(client2.IsConnected);
        }
        finally
        {
            server1.Dispose();
            server2.Dispose();
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Haoyue.slnx")))
            dir = dir.Parent!;
        return dir?.FullName ?? throw new InvalidOperationException("未找到仓库根目录（Haoyue.slnx）");
    }

    private string CreateWorkspace(string name, string? workspaceConfig = null)
    {
        var root = Path.Combine(_tempDir, name);
        var configDir = Path.Combine(root, ".haoyue");
        Directory.CreateDirectory(configDir);
        if (workspaceConfig is not null)
            File.WriteAllText(Path.Combine(configDir, "config.json"), workspaceConfig);
        return root;
    }

    private static JsonObject ParseData(JsonObject response) =>
        JsonNode.Parse(response["data"]!.GetValue<string>())!.AsObject();

    public async ValueTask DisposeAsync()
    {
        _serverCts.Cancel();
        foreach (var disposable in _asyncDisposables)
        {
            try { await disposable.DisposeAsync(); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
        }
        if (_runtime is not null) await _runtime.DisposeAsync();
        _serverCts.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>
    /// Keeps daemon tests hermetic: local <see cref="IHealthChecker.RunChecks"/> still run,
    /// but provider probes never touch the network. A filtered/absent localhost port could
    /// otherwise stall <c>doctor.run</c> past the test read timeout and flake the build.
    /// </summary>
    private sealed class OfflineHealthChecker(IHealthChecker inner) : IHealthChecker
    {
        public Task<HealthReport> CheckAsync(ProviderConfig provider, CancellationToken ct = default)
            => Task.FromResult(new HealthReport(provider.Id, false, 0, "offline (test stub)"));

        public IReadOnlyList<HealthCheckResult> RunChecks(WorkspaceInfo workspace)
            => inner.RunChecks(workspace);
    }

    private sealed class TestConnection(
        NamedPipeClientStream client,
        NamedPipeServerStream server,
        Task serverTask) : IAsyncDisposable
    {
        private readonly StreamReader _reader = new(client, Encoding.UTF8, leaveOpen: true);
        private readonly StreamWriter _writer = new(client, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
        };

        public Task SendAsync(long id, string method, JsonObject? parameters = null)
        {
            var request = new JsonObject
            {
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new JsonObject(),
            };
            return _writer.WriteLineAsync(request.ToJsonString());
        }

        /// <summary>Sends the mandatory handshake message the daemon expects first.</summary>
        public Task HandshakeAsync(string token, long id = 0) =>
            SendAsync(id, "handshake", new JsonObject { ["token"] = token });

        /// <summary>Handshake with an explicit client protocol version (version contract).</summary>
        public Task HandshakeWithVersionAsync(string token, string protocolVersion, long id = 0) =>
            SendAsync(id, "handshake", new JsonObject { ["token"] = token, ["protocolVersion"] = protocolVersion });

        /// <summary>Sends a raw (possibly malformed) protocol line for envelope error tests.</summary>
        public Task SendRawAsync(string line) => _writer.WriteLineAsync(line);

        public async Task<JsonObject> ReadAsync()
        {
            // Generous guard only — normal responses arrive in milliseconds, but a
            // fully parallel test run on a loaded machine occasionally starves the
            // daemon past 5 s and turned this suite intermittently red.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var line = await _reader.ReadLineAsync(timeout.Token);
            Assert.False(string.IsNullOrWhiteSpace(line));
            return JsonNode.Parse(line)!.AsObject();
        }

        public async Task<JsonObject> ReadUntilAsync(Func<JsonObject, bool> predicate)
        {
            while (true)
            {
                var response = await ReadAsync();
                if (predicate(response)) return response;
            }
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException) { }
            server.Dispose();
            _reader.Dispose();
            await _writer.DisposeAsync();
        }
    }
}
