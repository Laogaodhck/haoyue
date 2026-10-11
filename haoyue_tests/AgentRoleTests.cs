using System.Text.Json.Nodes;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>S7 角色化子代理护栏：角色解析、任务文本组装、未知角色拒绝、
/// 白名单工具视图。委派链路本身由 AgentDelegationTests 覆盖。</summary>
public sealed class AgentRoleTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"haoyue-roles-{Guid.NewGuid():N}");

    public AgentRoleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void Builtins_ContainThreeRoles()
    {
        Assert.Equal(3, AgentRole.Builtins.Count);
        Assert.Contains("researcher", AgentRole.Builtins.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("implementer", AgentRole.Builtins.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("reviewer", AgentRole.Builtins.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Researcher_And_Reviewer_AreReadOnly()
    {
        foreach (var roleId in new[] { "researcher", "reviewer" })
        {
            var role = AgentRole.Resolve(roleId);
            Assert.NotNull(role.AllowedTools);
            Assert.DoesNotContain("write_file", role.AllowedTools!);
            Assert.DoesNotContain("edit_file", role.AllowedTools!);
        }
        Assert.Null(AgentRole.Resolve("implementer").AllowedTools);
    }

    [Fact]
    public void Resolve_UnknownRole_Throws()
    {
        Assert.Throws<ArgumentException>(() => AgentRole.Resolve("boss"));
    }

    [Fact]
    public void ComposeTask_InjectsPromptAndTemplate()
    {
        var task = AgentRole.Resolve("researcher").ComposeTask("调研 A 与 B 的差异");
        Assert.Contains("研究员", task);
        Assert.Contains("调研 A 与 B 的差异", task);
        Assert.Contains("## 结论", task);
        Assert.Contains("## 风险与不确定性", task);
    }

    [Fact]
    public async Task DelegateTool_UnknownRole_FailsWithoutDelegating()
    {
        var delegator = new RecordingDelegator();
        var tool = new Haoyue.Runtime.Tools.Builtin.DelegateTool(
            new EmptyPromptProvider(), delegator);

        var result = await tool.ExecuteAsync(
            new JsonObject { ["task"] = "任意任务", ["role"] = "nonexistent" },
            NewContext(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, delegator.Calls.Count);
    }

    [Fact]
    public async Task DelegateTool_WithRole_ComposesTaskAndPassesRole()
    {
        var delegator = new RecordingDelegator();
        var tool = new Haoyue.Runtime.Tools.Builtin.DelegateTool(
            new EmptyPromptProvider(), delegator);

        var result = await tool.ExecuteAsync(
            new JsonObject { ["task"] = "评估方案", ["role"] = "reviewer" },
            NewContext(), CancellationToken.None);

        Assert.True(result.Success);
        var call = Assert.Single(delegator.Calls);
        Assert.Equal("reviewer", call.Role);
        Assert.Contains("评审员", call.TaskText);
        Assert.Contains("评估方案", call.TaskText);
    }

    [Fact]
    public void AllowListRegistry_HidesNonWhitelistedTools()
    {
        var registry = new CapturingToolRegistry(
            new StubTool("read_file"), new StubTool("write_file"), new StubTool("bash"));
        var view = new AllowListToolRegistry(registry, ["read_file", "bash"]);

        Assert.NotNull(view.Resolve("read_file"));
        Assert.NotNull(view.Resolve("BASH")); // 大小写不敏感
        Assert.Null(view.Resolve("write_file"));
        Assert.Null(view.Resolve("edit_file"));
        Assert.Equal(2, view.All.Count);
    }

    // ---- 测试替身 ---------------------------------------------------------

    private sealed record CallRecord(string TaskText, string? Role);

    private sealed class RecordingDelegator : IAgentDelegator
    {
        public List<CallRecord> Calls { get; } = [];

        public Task<AgentTurnResult> RunSubTaskAsync(WorkspaceInfo workspace, string task, CancellationToken ct)
        {
            Calls.Add(new CallRecord(task, null));
            return Task.FromResult(new AgentTurnResult("ok", false, null));
        }

        public Task<AgentTurnResult> RunSubTaskAsync(
            WorkspaceInfo workspace, string task, string? role, CancellationToken ct)
        {
            // 与真实 AgentDelegator 一致：组合后的文本直接进入执行路径，只记录一次。
            var composed = role is { Length: > 0 } ? AgentRole.Resolve(role).ComposeTask(task) : task;
            Calls.Add(new CallRecord(composed, role));
            return Task.FromResult(new AgentTurnResult("ok", false, null));
        }
    }

    private sealed class EmptyPromptProvider : Haoyue.Runtime.Prompts.IPromptProvider
    {
        public string? TryGet(string key) => null;
        public string Get(string key) => throw new InvalidOperationException("测试中不应取到提示词");
        public string Render(string template, IReadOnlyDictionary<string, string> variables) => template;
        public string? GetRendered(string key, IReadOnlyDictionary<string, string> variables) => null;
        public void SetWorkspaceRoot(string? root) { }
    }

    private sealed class StubTool(string name) : ITool
    {
        public string Name => name;
        public string Description => "stub";
        public JsonObject ParameterSchema => new();
        public bool Mutating => false;
        public string StatusLabel => "stub";
        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok("ok"));
    }

    private sealed class CapturingToolRegistry(params ITool[] tools) : IToolRegistry
    {
        private readonly List<ITool> _tools = [.. tools];
        public IDisposable Register(ITool tool) { _tools.Add(tool); return new DisposeStub(); }
        public ITool? Resolve(string name) => _tools.FirstOrDefault(t =>
            t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        public IReadOnlyList<ITool> All => _tools;

        private sealed class DisposeStub : IDisposable
        {
            public void Dispose() { }
        }
    }

    private ToolContext NewContext() => new()
    {
        Workspace = new WorkspaceManager().CreateGlobal(_dir),
        Events = new Haoyue.Runtime.Events.EventBus(),
        Agent = new Haoyue.Runtime.Configuration.AgentConfig(),
    };
}
