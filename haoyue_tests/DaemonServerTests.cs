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
        var response = await connection.ReadAsync();

        Assert.Equal(4, response["id"]!.GetValue<long>());
        Assert.Equal("error", response["event"]!.GetValue<string>());
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

        var response = await connection.ReadAsync();
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
        Assert.Equal("AQID", messages[0]!["images"]![0]!["data"]!.GetValue<string>());
        Assert.Equal("screen-1", messages[1]!["viewedImages"]![0]!["id"]!.GetValue<string>());
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
        Assert.Equal("secret-test-key", provider["apiKey"]!.GetValue<string>());

        // The desktop receives the directly stored key for editing. Older clients that submit
        // an empty value still preserve it unless they explicitly request clearApiKey.
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
        Assert.Equal("secret-test-key", provider["apiKey"]!.GetValue<string>());
        var reloadedStore = new ConfigStore(
            Path.Combine(_tempDir, "config.json"),
            Path.Combine(_tempDir, "state.json"));
        Assert.Equal("secret-test-key", reloadedStore.Config.FindProvider("local")!.ApiKey);

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
        Assert.Null(provider["apiKey"]);

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
        Assert.Equal("done", (await connection.ReadAsync())["event"]!.GetValue<string>());
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
                && item["content"]!.GetValue<string>().Contains("项目名\tHaoyue\t")
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
        Assert.Equal("first", journal[0]!["payload"]!["Message"]!.GetValue<string>());
        Assert.Equal("WarningEvent", journal[1]!["type"]!.GetValue<string>());
        Assert.Equal("second", journal[1]!["payload"]!["Message"]!.GetValue<string>());
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
            globalWorkspace, null, journalFile);
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
        var server = new DaemonServer(_runtime, runTurn, globalWorkspace, handshakeToken);
        _asyncDisposables.Add(server);

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
    public void Contract_DispatchCases_AllRegistered()
    {
        // 契约治理：DaemonServer 分发 switch 的每个 case 都必须登记进 DaemonContract，
        // 否则方法会绕过契约（无 TS 类型、无文档导出、快照测试也检测不到）。
        var serverPath = Path.Combine(FindRepoRoot(), "haoyue_runtime", "Daemon", "DaemonServer.cs");
        var source = File.ReadAllText(serverPath);
        var cases = System.Text.RegularExpressions.Regex.Matches(source, @"case\s+""([a-z][A-Za-z0-9.]*)""\s*:")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        Assert.True(cases.Count > 50, $"分发 case 解析异常，仅匹配到 {cases.Count} 个，疑似源码结构变化");

        var registered = DaemonContract.Methods.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var unregistered = cases.Where(name => !registered.Contains(name)).ToList();
        Assert.True(unregistered.Count == 0,
            "以下分发 case 未登记进 DaemonContract（契约漂移）：\n" + string.Join("\n", unregistered));
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
