using Haoyue.Runtime;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Evolution;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>
/// H-C 记忆固化引擎：近期会话 → 隔离回合 → FactsStore 的自动化沉淀链路。
/// 覆盖提炼入库、空窗跳过、空结论/解析失败、anti-self-feeding、单航班、
/// 按来源工作区分别落盘。
/// </summary>
public sealed class ConsolidationRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-tests", "consolidation-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly HaoyueRuntime _runtime;

    public ConsolidationRunnerTests()
    {
        Directory.CreateDirectory(_dir);
        _runtime = HaoyueRuntime.Create(
            _dir,
            new ConfigStore(Path.Combine(_dir, "config.json"), Path.Combine(_dir, "state.json")),
            Path.Combine(_dir, "runtime.db"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private ConsolidationRunner NewRunner(ConsolidationTurnRunner turnRunner) => new(
        _runtime, new FileLockCoordinator(), new LlmHttpFactory(),
        new CircuitBreaker(new RetryConfig()), turnRunner);

    /// <summary>在 runtime 当前工作区造一条有内容的近期会话。</summary>
    private void SeedSession(string userText = "这个项目以后都用 pnpm，不要用 npm", string assistantText = "好的，后续统一使用 pnpm。")
    {
        var session = _runtime.Sessions.Create(_runtime.Workspace);
        _runtime.Sessions.Append(session, ChatMessage.User(userText));
        _runtime.Sessions.Append(session, ChatMessage.Assistant(assistantText));
    }

    private static ConsolidationTurnRunner Scripted(string conclusion, Action<string>? onPrompt = null) =>
        (_, _, prompt, _) =>
        {
            onPrompt?.Invoke(prompt);
            return Task.FromResult(new AgentTurnResult(conclusion, false, null));
        };

    [Fact]
    public async Task RunAsync_DistillsFacts_FromRecentSessions()
    {
        SeedSession();
        string? capturedPrompt = null;
        var runner = NewRunner(Scripted(
            "分析完成。\n```json\n{\"facts\":[{\"entry\":1,\"content\":\"构建统一使用 pnpm\",\"topic\":\"build\",\"confidence\":0.9}]}\n```",
            onPrompt: p => capturedPrompt = p));

        var result = await runner.RunAsync("test", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(1, result.SessionsScanned);
        Assert.Equal(1, result.FactsAdded);
        Assert.Equal(0, result.FactsSuperseded);
        Assert.NotNull(result.SessionId);

        // 提示词带上了会话摘要（诉求/结论），编号即 entry。
        Assert.NotNull(capturedPrompt);
        Assert.Contains("这个项目以后都用 pnpm", capturedPrompt);
        Assert.Contains("entry", capturedPrompt);

        var facts = new FactsStore().ListActiveForRoot(_runtime.Workspace.Root);
        var fact = Assert.Single(facts);
        Assert.Equal("构建统一使用 pnpm", fact.Content);
        Assert.Equal("build", fact.Topic);
        Assert.Equal(0.9, fact.Confidence);
        Assert.Equal("agent", fact.Source);
    }

    [Fact]
    public async Task RunAsync_NoRecentSessions_SkipsTurn()
    {
        var invoked = false;
        var runner = NewRunner((_, _, _, _) =>
        {
            invoked = true;
            return Task.FromResult(new AgentTurnResult("", false, null));
        });

        var result = await runner.RunAsync("test", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(0, result.SessionsScanned);
        Assert.False(invoked, "没有可消化的会话时不应启动固化回合");
    }

    [Fact]
    public async Task RunAsync_EmptyFacts_IsNoOp()
    {
        SeedSession();
        var runner = NewRunner(Scripted("无可沉淀。\n```json\n{\"facts\":[]}\n```"));

        var result = await runner.RunAsync("test", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(1, result.SessionsScanned);
        Assert.Equal(0, result.FactsAdded);
        Assert.False(File.Exists(FactsStore.FactsFileForRoot(_runtime.Workspace.Root)));
    }

    [Fact]
    public async Task RunAsync_ParseFailure_ReturnsError_AndWritesNothing()
    {
        SeedSession();
        var runner = NewRunner(Scripted("我觉得没什么好沉淀的。（没有 JSON 结论块）"));

        var result = await runner.RunAsync("test", CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Contains("结论块", result.Error);
        Assert.Equal(0, result.FactsAdded);
        Assert.False(File.Exists(FactsStore.FactsFileForRoot(_runtime.Workspace.Root)));
    }

    [Fact]
    public async Task RunAsync_ExcludesOwnConsolidationSessions_AndStaysIdempotent()
    {
        SeedSession();
        var runner = NewRunner(Scripted(
            "```json\n{\"facts\":[{\"entry\":1,\"content\":\"构建统一使用 pnpm\",\"topic\":\"build\",\"confidence\":0.9}]}\n```"));
        var first = await runner.RunAsync("test", CancellationToken.None);
        Assert.Equal(1, first.FactsAdded);

        // 第二轮：固化会话本身（标题带标记）不得再被扫描；同一事实幂等不重复入库。
        string? secondPrompt = null;
        var second = await NewRunner(Scripted("```json\n{\"facts\":[]}\n```", p => secondPrompt = p))
            .RunAsync("test", CancellationToken.None);

        Assert.Equal(1, second.SessionsScanned); // 只有原始会话，固化会话被排除
        Assert.NotNull(secondPrompt);
        // 固化会话的标题不得回喂进下一轮提示词（anti-self-feeding）。
        Assert.DoesNotContain("记忆固化（后台）", secondPrompt);
        var facts = new FactsStore().ListActiveForRoot(_runtime.Workspace.Root);
        Assert.Single(facts);
    }

    [Fact]
    public async Task RunAsync_SingleFlight_RejectsConcurrentPass()
    {
        SeedSession();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = NewRunner((_, _, _, _) =>
        {
            entered.SetResult();
            return release.Task.ContinueWith(_ => new AgentTurnResult("```json\n{\"facts\":[]}\n```", false, null));
        });

        var firstRun = runner.RunAsync("test", CancellationToken.None);
        await entered.Task;
        var rejected = await runner.RunAsync("test", CancellationToken.None);
        Assert.NotNull(rejected.Error);
        Assert.Contains("已在进行中", rejected.Error);

        release.SetResult();
        var first = await firstRun;
        Assert.Null(first.Error);
    }

    [Fact]
    public async Task RunAsync_WritesFactsToSourceWorkspaceRoot()
    {
        // 项目工作区注册进 ProjectStore，会话建在项目里：事实必须落项目根，
        // 不混进 runtime 当前（全局）工作区。
        var projectRoot = Path.Combine(_dir, "proj");
        Directory.CreateDirectory(projectRoot);
        _runtime.Projects.Upsert(null, projectRoot, "proj");
        var projectWorkspace = new WorkspaceManager().Detect(projectRoot);
        var session = _runtime.Sessions.Create(projectWorkspace);
        _runtime.Sessions.Append(session, ChatMessage.User("部署走 CI，别手动上线"));
        _runtime.Sessions.Append(session, ChatMessage.Assistant("明白，部署统一走 CI。"));

        var runner = NewRunner(Scripted(
            "```json\n{\"facts\":[{\"entry\":1,\"content\":\"部署统一走 CI，禁止手动上线\",\"topic\":\"deploy\",\"confidence\":1.0}]}\n```"));

        var result = await runner.RunAsync("test", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(1, result.SessionsScanned);
        Assert.Equal(1, result.FactsAdded);
        Assert.True(File.Exists(Path.Combine(projectRoot, ".haoyue", "facts.json")));
        Assert.False(File.Exists(FactsStore.FactsFileForRoot(_runtime.Workspace.Root)),
            "项目事实不应写入 runtime 当前工作区");
    }

    [Fact]
    public async Task RunAsync_PublishesCompletionEvent()
    {
        SeedSession();
        var runner = NewRunner(Scripted("```json\n{\"facts\":[]}\n```"));

        using var subscription = _runtime.Events.Subscribe();
        await runner.RunAsync("test", CancellationToken.None);

        var evt = await subscription.Reader.ReadAsync();
        var completed = Assert.IsType<MemoryConsolidationCompletedEvent>(evt);
        Assert.Equal(1, completed.SessionsScanned);
        Assert.Equal(0, completed.FactsAdded);
        Assert.Null(completed.Error);
    }

    // ---------------------------------------------------------------- MEMORY.md 晋升

    [Fact]
    public async Task RunAsync_DurableFact_PromotesIntoWorkspaceMemory()
    {
        SeedSession("以后所有回复都用中文");
        var runner = NewRunner(Scripted(
            "```json\n{\"facts\":[{\"entry\":1,\"content\":\"回复统一使用中文\",\"topic\":\"preference\","
            + "\"confidence\":1.0,\"durable\":true}]}\n```"));

        var result = await runner.RunAsync("test", CancellationToken.None);

        Assert.Equal(1, result.MemoryPromoted);
        Assert.Null(result.MemoryNote);
        var memory = await File.ReadAllTextAsync(_runtime.Workspace.MemoryFile);
        Assert.Contains("回复统一使用中文", memory);
        Assert.Contains("[preference]", memory);
        // 事实层与记忆层双写：晋升不代替事实入库。
        Assert.Contains(new FactsStore().ListActiveForRoot(_runtime.Workspace.Root),
            f => f.Content == "回复统一使用中文");
    }

    [Fact]
    public async Task RunAsync_DurableFact_StillSupersededByManualMemoryMode()
    {
        // 手动管理模式下 MEMORY.md 归用户，后台写手不得插手——但要说明为什么没写。
        SeedSession();
        _runtime.ConfigStore.Config.Agent.MemoryMode = MemoryMode.Manual;
        var runner = NewRunner(Scripted(
            "```json\n{\"facts\":[{\"entry\":1,\"content\":\"回复统一使用中文\",\"confidence\":1.0,\"durable\":true}]}\n```"));

        var result = await runner.RunAsync("test", CancellationToken.None);

        Assert.Equal(0, result.MemoryPromoted);
        Assert.Contains("手动", result.MemoryNote);
        Assert.Equal(1, result.FactsAdded);
        Assert.False(File.Exists(_runtime.Workspace.MemoryFile));
    }

    [Fact]
    public async Task RunAsync_LowConfidenceDurable_IsKeptOutOfMemory()
    {
        SeedSession();
        var runner = NewRunner(Scripted(
            "```json\n{\"facts\":[{\"entry\":1,\"content\":\"大概是用 pnpm 吧\",\"confidence\":0.6,\"durable\":true}]}\n```"));

        var result = await runner.RunAsync("test", CancellationToken.None);

        Assert.Equal(0, result.MemoryPromoted);
        Assert.Contains("低置信", result.MemoryNote);
        Assert.False(File.Exists(_runtime.Workspace.MemoryFile));
    }

    [Fact]
    public async Task RunAsync_DurablePromotion_IsCappedPerPass()
    {
        SeedSession();
        var facts = string.Join(",", Enumerable.Range(1, 7).Select(i =>
            $"{{\"entry\":1,\"content\":\"约定 {i}\",\"confidence\":1.0,\"durable\":true}}"));
        var runner = NewRunner(Scripted($"```json\n{{\"facts\":[{facts}]}}\n```"));

        var result = await runner.RunAsync("test", CancellationToken.None);

        Assert.Equal(7, result.FactsAdded);
        Assert.Equal(5, result.MemoryPromoted);
        Assert.Contains("上限", result.MemoryNote);
        Assert.Equal(5, (await File.ReadAllTextAsync(_runtime.Workspace.MemoryFile))
            .Split('\n').Count(l => l.Contains("约定 ")));
    }

    [Fact]
    public async Task RunAsync_RepeatedDurableFact_DoesNotDuplicateMemoryLine()
    {
        SeedSession();
        var conclusion = "```json\n{\"facts\":[{\"entry\":1,\"content\":\"回复统一使用中文\",\"confidence\":1.0,\"durable\":true}]}\n```";
        Assert.Equal(1, (await NewRunner(Scripted(conclusion)).RunAsync("test", CancellationToken.None)).MemoryPromoted);

        SeedSession("再确认一次语言偏好");
        var second = await NewRunner(Scripted(conclusion)).RunAsync("test", CancellationToken.None);

        Assert.Equal(0, second.MemoryPromoted);
        Assert.Contains("已存在", second.MemoryNote);
        Assert.Single((await File.ReadAllTextAsync(_runtime.Workspace.MemoryFile))
            .Split('\n').Where(l => l.Contains("回复统一使用中文")));
    }

    [Fact]
    public void BuildPrompt_AdvertisesDurablePromotionContract()
    {
        var prompt = NewRunner((_, _, _, _) => Task.FromResult(new AgentTurnResult("```json\n{\"facts\":[]}\n```", false, null)))
            .BuildPrompt([new ConsolidationRunner.SessionDigest(_runtime.Workspace, "诉求：以后都用中文")]);

        Assert.Contains("durable", prompt);
        Assert.Contains("MEMORY.md", prompt);
    }
}
