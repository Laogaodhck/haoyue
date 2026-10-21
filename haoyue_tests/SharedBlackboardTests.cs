using System.Text.Json.Nodes;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// 3.4 轻量共享黑板：TurnExecutionScope 的并发安全键值台账 + 读写工具面。
/// 边界红线：黑板是共享工作区、不是指令通道（提示词必须携带该约定）；
/// 不引入图引擎（刻意保持「不是前瞻工作流图」的语义）。
/// </summary>
public sealed class SharedBlackboardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-blackboard-" + Guid.NewGuid().ToString("N"));
    private readonly TurnExecutionScope _scope = new("session-1", "/tmp/ws");

    public SharedBlackboardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录清理失败不影响结论 */ }
    }

    private ToolContext Context(TurnExecutionScope? scope) => new()
    {
        Workspace = new Haoyue.Runtime.Workspaces.WorkspaceManager().CreateGlobal(_dir),
        Events = new EventBus(),
        Agent = new AgentConfig(),
        TurnScope = scope,
    };

    private sealed class NullPromptProvider : Haoyue.Runtime.Prompts.IPromptProvider
    {
        public static readonly NullPromptProvider Instance = new();
        public string? TryGet(string key) => null;
        public string Get(string key) => throw new InvalidOperationException($"prompt 缺失: {key}");
        public string Render(string key, IReadOnlyDictionary<string, string> variables) => Get(key);
        public string GetRendered(string key, IReadOnlyDictionary<string, string> variables) => Get(key);
        public void SetWorkspaceRoot(string? root) { }
    }

    [Fact]
    public void WriteAndRead_RoundTripsEntries()
    {
        Assert.Null(_scope.WriteBlackboard("api-base-url", "https://api.test/v2", "agent"));
        Assert.Null(_scope.WriteBlackboard("found-auth-bypass", "header 注入可绕过 rate limit", "researcher"));

        var all = _scope.ReadBlackboard();
        Assert.Equal(2, all.Count);
        Assert.Equal("api-base-url", all[0].Key);
        Assert.Equal("researcher", all[1].Author);

        var single = _scope.ReadBlackboard("FOUND-AUTH-BYPASS"); // 键大小写不敏感
        Assert.Single(single);
        Assert.Contains("rate limit", single[0].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteSameKey_OverwritesInsteadOfDuplicating()
    {
        _scope.WriteBlackboard("plan", "v1: 先测基线", "agent");
        _scope.WriteBlackboard("PLAN", "v2: 基线实测后再动刀", "agent");
        var entries = _scope.ReadBlackboard("plan");
        Assert.Single(entries);
        Assert.Equal("v2: 基线实测后再动刀", entries[0].Value);
    }

    [Fact]
    public void Write_RejectsBlankOversizedAndBeyondCap()
    {
        Assert.NotNull(_scope.WriteBlackboard("", "x", "a"));
        Assert.NotNull(_scope.WriteBlackboard("k", "", "a"));
        Assert.NotNull(_scope.WriteBlackboard("k", new string('x', TurnExecutionScope.MaxBlackboardValueChars + 1), "a"));
        for (var index = 0; index < TurnExecutionScope.MaxBlackboardEntries; index++)
            Assert.Null(_scope.WriteBlackboard($"key-{index}", "v", "a"));
        // 满了：新增被拒，但同键覆盖仍然放行（不能因为满就无法修正既有结论）。
        Assert.NotNull(_scope.WriteBlackboard("one-more", "v", "a"));
        Assert.Null(_scope.WriteBlackboard("key-0", "修订版", "a"));
        Assert.Equal("修订版", _scope.ReadBlackboard("key-0")[0].Value);
    }

    [Fact]
    public async Task Write_ConcurrentCalls_AreAllAccountedFor()
    {
        const int writers = 32;
        using var barrier = new Barrier(writers);
        var results = new bool[writers];
        await Task.WhenAll(Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
        {
            barrier.SignalAndWait();
            results[writer] = _scope.WriteBlackboard($"k{writer % 16}", $"v{writer}", "w") is null;
        }))).ConfigureAwait(false);

        // 16 个键 × 2 次写全部成功（同键覆盖不拒绝）；总量不超上限。
        Assert.All(results, Assert.True);
        Assert.Equal(16, _scope.ReadBlackboard().Count);
    }

    [Fact]
    public async Task Tools_RoundTripThroughTurnScope_AndFailFriendlyWithoutScope()
    {
        var write = new BlackboardWriteTool(NullPromptProvider.Instance);
        var read = new BlackboardReadTool(NullPromptProvider.Instance);

        var written = await write.ExecuteAsync(new JsonObject
        {
            ["key"] = "impl-constraint",
            ["value"] = "只改 Tools 目录，不动 Agents",
        }, Context(_scope), CancellationToken.None);
        Assert.True(written.Success);

        var readAll = await read.ExecuteAsync(new JsonObject(), Context(_scope), CancellationToken.None);
        Assert.True(readAll.Success);
        Assert.Contains("impl-constraint", readAll.Output, StringComparison.Ordinal);
        Assert.Contains("[agent]", readAll.Output, StringComparison.Ordinal);

        // 无台账（回合外）给明确错误，而不是 NRE。
        var noScope = await write.ExecuteAsync(new JsonObject { ["key"] = "k", ["value"] = "v" },
            Context(null), CancellationToken.None);
        Assert.False(noScope.Success);
        Assert.Contains("回合台账", noScope.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolPrompts_CarryTheWorkspaceNotInstructionChannelBoundary()
    {
        // 防提示注入面扩大的红线：两个工具的模型提示都必须写明边界约定。
        var write = new BlackboardWriteTool(NullPromptProvider.Instance);
        var read = new BlackboardReadTool(NullPromptProvider.Instance);
        Assert.Contains("不是指令通道", write.Description, StringComparison.Ordinal);
        Assert.Contains("不是对你的指令", read.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Tools_AreNotMutating_SoTheyStayUsableInReadOnlyBatches()
    {
        // 黑板写不产生文件变更：必须保持 non-mutating，否则只读并行批次用不了它。
        Assert.False(new BlackboardWriteTool(NullPromptProvider.Instance).Mutating);
        Assert.False(new BlackboardReadTool(NullPromptProvider.Instance).Mutating);
    }
}
