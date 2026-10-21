using System.Text.Json.Nodes;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;
using Haoyue.Runtime.Workspaces;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// 3.1 委派配额与用量汇入、3.2 独立超时、3.3 结构化聚合。
/// </summary>
public sealed class DelegationQuotaTests
{
    private sealed class StubDelegator(Func<string, AgentTurnResult> handler) : IAgentDelegator
    {
        public Task<AgentTurnResult> RunSubTaskAsync(WorkspaceInfo workspace, string task, CancellationToken ct) =>
            Task.FromResult(handler(task));
    }

    private static ToolContext Context(TurnExecutionScope? scope) => new()
    {
        Workspace = new WorkspaceManager().CreateGlobal(Path.Combine(Path.GetTempPath(), "haoyue-dq-" + Guid.NewGuid().ToString("N"))),
        Events = new EventBus(),
        Agent = new AgentConfig(),
        TurnScope = scope,
    };

    private static TurnExecutionScope NewScope() => new("session", Path.GetTempPath());

    // ---------------------------------------------------------------- 3.1 用量汇入

    [Fact]
    public void Scope_DelegatedUsage_AccumulatesAndResetsOnTake()
    {
        var scope = NewScope();
        scope.AddDelegatedUsage(100, 50, 0.01m);
        scope.AddDelegatedUsage(30, 20, 0.005m);

        var (input, output, cost) = scope.TakeDelegatedUsage();
        Assert.Equal(130, input);
        Assert.Equal(70, output);
        Assert.Equal(0.015m, cost);
        // 取走即清零：同笔用量不会重复计入父预算。
        var (in2, out2, cost2) = scope.TakeDelegatedUsage();
        Assert.Equal(0, in2);
        Assert.Equal(0, out2);
        Assert.Equal(0m, cost2);
    }

    [Fact]
    public async Task DelegateTool_FeedsSubtaskUsageBackIntoScope()
    {
        var scope = NewScope();
        var delegator = new StubDelegator(_ => new AgentTurnResult("done", false, null, InputTokens: 500, OutputTokens: 200, Cost: 0.02m));
        var tool = new DelegateTool(new FilePromptProvider(), delegator);
        var result = await tool.ExecuteAsync(
            new JsonObject { ["task"] = "子任务" }, Context(scope), CancellationToken.None);

        Assert.True(result.Success, result.Output);
        // 父回合主循环随后 Take 即并入预算闸门。
        var (input, output, cost) = scope.TakeDelegatedUsage();
        Assert.Equal(500, input);
        Assert.Equal(200, output);
        Assert.Equal(0.02m, cost);
    }

    [Fact]
    public async Task DelegateTool_EnforcesPerTurnQuota()
    {
        var scope = NewScope();
        var delegator = new StubDelegator(_ => new AgentTurnResult("ok", false, null));
        var tool = new DelegateTool(new FilePromptProvider(), delegator);
        var context = Context(scope);

        for (var i = 0; i < 8; i++)
        {
            var result = await tool.ExecuteAsync(new JsonObject { ["task"] = $"任务 {i}" }, context, CancellationToken.None);
            Assert.True(result.Success, result.Output);
        }
        // 第 9 次超出默认配额（agent.delegationMaxPerTurn = 8）：显式报错而非静默放行。
        var denied = await tool.ExecuteAsync(new JsonObject { ["task"] = "超额任务" }, context, CancellationToken.None);
        Assert.False(denied.Success);
        Assert.Contains("委派配额已用尽", denied.Output, StringComparison.Ordinal);
        Assert.Contains("delegationMaxPerTurn", denied.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DelegateTasksTool_RejectsWholeBatchWhenQuotaInsufficient()
    {
        // 整批校验：部分派发部分拒绝会让模型误以为全部完成——必须整批挡下并告知剩余额度。
        var scope = NewScope();
        for (var i = 0; i < 6; i++) scope.TryConsumeDelegationSlot(8);
        var delegator = new StubDelegator(_ => new AgentTurnResult("ok", false, null));
        var tool = new DelegateTasksTool(new FilePromptProvider(), delegator);
        var result = await tool.ExecuteAsync(
            new JsonObject { ["tasks"] = new JsonArray("A", "B", "C") }, Context(scope), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("委派配额不足", result.Output, StringComparison.Ordinal);
        Assert.Equal(6, scope.DelegationCount); // 未动配额
    }

    // ---------------------------------------------------------------- 3.2 独立超时

    /// <summary>
    /// 子任务立即以取消收场：父 CT 已取消 = 回合整体在收尾（异常上抛）；
    /// 父 CT 未取消 = 子任务被独立超时源终止（可诊断失败）。60 秒下限是给模型的
    /// 防呆钳制，测试直接控制取消源、不等真实计时。
    /// </summary>
    private sealed class CancelledSubtaskDelegator : IAgentDelegator
    {
        public Task<AgentTurnResult> RunSubTaskAsync(WorkspaceInfo workspace, string task, CancellationToken ct) =>
            Task.FromCanceled<AgentTurnResult>(ct.IsCancellationRequested ? ct : new CancellationToken(true));
    }

    [Fact]
    public async Task DelegateTool_TimeoutReturnsDiagnosticFailure_ParentCanContinue()
    {
        var tool = new DelegateTool(new FilePromptProvider(), new CancelledSubtaskDelegator());
        var result = await tool.ExecuteAsync(
            new JsonObject { ["task"] = "卡死任务", ["timeout_seconds"] = DelegateTool.MinTimeoutSeconds },
            Context(NewScope()), new CancellationTokenSource(50_000).Token);

        Assert.False(result.Success);
        Assert.Contains("子任务超时", result.Output, StringComparison.Ordinal);
        Assert.Contains("父回合可继续", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DelegateTool_ParentCancellationStillUnwinds()
    {
        // 父回合取消（非超时）：保持现行为——异常上抛让 agent loop 收尾。
        using var parentCts = new CancellationTokenSource();
        parentCts.Cancel();
        var tool = new DelegateTool(new FilePromptProvider(), new CancelledSubtaskDelegator());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(
            new JsonObject { ["task"] = "任意" }, Context(NewScope()), parentCts.Token));
    }

    // ---------------------------------------------------------------- 3.3 结构化聚合

    [Fact]
    public void Aggregator_ParsesCanonicalFourSectionReport()
    {
        var report = SubtaskReportAggregator.Parse(
            """
            ## 结论
            模块 A 已完成迁移。
            ## 关键发现
            - 旧接口仍有 3 处调用
            - 测试覆盖率 82%
            ## 涉及文件
            - src/a/one.cs
            - src/a/two.cs
            ## 风险与不确定性
            - 未验证生产配置
            """);

        Assert.Equal("模块 A 已完成迁移。", report.Conclusion);
        Assert.Equal(2, report.Findings.Count);
        Assert.Equal(["src/a/one.cs", "src/a/two.cs"], report.Files);
        Assert.Equal(["未验证生产配置"], report.Risks);
        Assert.True(report.HasStructure);
    }

    [Fact]
    public void Aggregator_ToleratesMissingSections_InlineContent_AndUnorderedHeadings()
    {
        // 缺失段落 / 标题行内联内容（"## 结论：xxx"）/乱序——全部容错。
        var report = SubtaskReportAggregator.Parse(
            """
            前言自由文本。
            ## 关键发现
            - 唯一要点
            ## 风险
            无
            ## 结论：一段话结论
            """);

        Assert.Equal("一段话结论", report.Conclusion);
        Assert.Equal(["唯一要点"], report.Findings);
        Assert.Empty(report.Files);
        Assert.Equal(["无"], report.Risks);
    }

    [Fact]
    public void Aggregator_FreeTextReport_HasNoStructure_FallsBackToRaw()
    {
        var parsed = SubtaskReportAggregator.Parse("就是一段没有标题的自由文本。");
        Assert.False(parsed.HasStructure);
        // Summarize 对无结构报告返回 null：调用方回退原文透传，不丢信息。
        Assert.Null(SubtaskReportAggregator.Summarize("完成", parsed));
    }

    [Fact]
    public void Aggregator_Summarize_TruncatesLongSections()
    {
        var parsed = SubtaskReportAggregator.Parse(
            """
            ## 结论
            结论一句话。
            ## 关键发现
            - 发现一
            - 发现二
            - 发现三
            - 发现四
            - 发现五
            ## 涉及文件
            无
            """);

        var digest = SubtaskReportAggregator.Summarize("完成", parsed, maxItemsPerSection: 3)!;
        Assert.Contains("结论：结论一句话", digest, StringComparison.Ordinal);
        Assert.Contains("发现一；发现二；发现三", digest, StringComparison.Ordinal);
        Assert.Contains("另 2 条见全文", digest, StringComparison.Ordinal);
        Assert.Contains("文件：无", digest, StringComparison.Ordinal);
    }
}
