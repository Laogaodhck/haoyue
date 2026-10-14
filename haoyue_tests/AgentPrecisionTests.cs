using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Haoyue.Runtime;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>
/// Agent 输出精度与命中率保障：空回答兜底、重复失败策略纠偏、计划纠偏、步数预算
/// 收尾与回合级指标报告。全部通过脚本化 LLM 客户端离线驱动，不依赖真实模型。
/// </summary>
public sealed class AgentPrecisionTests
{
    [Fact]
    public async Task EmptyAnswer_NudgeOnce_ThenRecover()
    {
        using var harness = await AgentHarness.CreateAsync(
            ScriptedSteps(new LlmCompletion(), new LlmCompletion { Text = "最终回答：任务已完成" }));
        var result = await harness.RunAsync("做个总结");

        Assert.Equal("最终回答：任务已完成", result.Text);
        var nudge = harness.Session.Messages.SingleOrDefault(m =>
            m.Role == ChatRole.User && m.Text.Contains("[empty answer]"));
        Assert.NotNull(nudge);
    }

    [Fact]
    public async Task SecondEmptyAnswer_EndsTurnWithEmptyFinishReason()
    {
        // 连续两次空回答＝没答上。过去它和正常收尾一样报 done，界面留下一个空白气泡，
        // 用户不知道发生了什么、该不该重试。
        using var harness = await AgentHarness.CreateAsync(ScriptedSteps(new LlmCompletion(), new LlmCompletion()));
        var result = await harness.RunAsync("做个总结");

        Assert.Equal("", result.Text);
        Assert.NotNull(result.Error);
        Assert.Equal(TurnFinishReason.Empty, result.FinishReason);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task Refusal_TerminatesWithRefusalReason_AndStaysOnTheMessage()
    {
        using var harness = await AgentHarness.CreateAsync(ScriptedSteps(
            new LlmCompletion { Text = "我无法协助这个请求。", FinishReason = "content_filter" }));
        var result = await harness.RunAsync("越界请求");

        Assert.Equal(TurnFinishReason.Refusal, result.FinishReason);
        Assert.Null(result.Error);
        Assert.Equal("我无法协助这个请求。", result.Text);
        var assistant = harness.Session.Messages.Last(m => m.Role == ChatRole.Assistant);
        Assert.Equal(TurnFinishReason.Refusal, assistant.FinishReason);
    }

    [Fact]
    public async Task InterruptedStream_WithoutContinuationBudget_ReportsInterrupted()
    {
        // 连接在半途断掉：文本仍要保留（用户能看到已生成的部分），但终态必须写清
        // 「这是中断前的部分输出」，不能显示成完整回答。
        using var harness = await AgentHarness.CreateAsync(
            ScriptedSteps(new LlmCompletion { Text = "最终回答" }),
            throwLlmAtRequest: 1, throwWithPartialText: true);
        harness.Store.Config.Agent.MaxOutputContinuations = 0;

        var result = await harness.RunAsync("讲个长故事");

        Assert.Equal("部分输出", result.Text);
        Assert.Equal(TurnFinishReason.Interrupted, result.FinishReason);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task InterruptedStream_ContinuesWhenBudgetAllows()
    {
        using var harness = await AgentHarness.CreateAsync(
            ScriptedSteps(new LlmCompletion { Text = "部分输出" }, new LlmCompletion { Text = "最终回答", FinishReason = "stop" }),
            throwLlmAtRequest: 1, throwWithPartialText: true);

        var result = await harness.RunAsync("讲个长故事");

        Assert.Equal("最终回答", result.Text);
        Assert.Equal(TurnFinishReason.Stop, result.FinishReason);
    }

    [Fact]
    public void TurnFinishReason_NormalizesProviderVariants_AndFlagsIncomplete()
    {
        Assert.Equal(TurnFinishReason.Stop, TurnFinishReason.Normalize(null));
        Assert.Equal(TurnFinishReason.Stop, TurnFinishReason.Normalize("end_turn"));
        Assert.Equal(TurnFinishReason.Length, TurnFinishReason.Normalize("max_tokens"));
        Assert.Equal(TurnFinishReason.Refusal, TurnFinishReason.Normalize("safety"));
        Assert.Equal(TurnFinishReason.ToolCalls, TurnFinishReason.Normalize("tool_calls"));
        // 未知原值原样保留，仍能被识别为「没答完」。
        Assert.Equal("audio_len", TurnFinishReason.Normalize("audio_len"));
        Assert.True(TurnFinishReason.IsIncomplete("audio_len"));
        Assert.False(TurnFinishReason.IsIncomplete(TurnFinishReason.Stop));
        Assert.False(TurnFinishReason.IsIncomplete(TurnFinishReason.ToolCalls));
    }

    [Fact]
    public async Task RepeatedIdenticalFailures_InjectStrategyGuidanceOnce()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"haoyue-missing-{Guid.NewGuid():N}.txt");
        var args = $$"""{"path":"{{missing.Replace("\\", "\\\\")}}"}""";
        using var harness = await AgentHarness.CreateAsync(ScriptedSteps(
            ToolCall("c1", "read_file", args),
            ToolCall("c2", "read_file", args),
            ToolCall("c3", "read_file", args),
            new LlmCompletion { Text = "换了一种方法后完成" }));

        var result = await harness.RunAsync("读取一个不存在的文件");

        Assert.Equal("换了一种方法后完成", result.Text);
        var nudges = harness.Session.Messages
            .Where(m => m.Role == ChatRole.User && m.Text.Contains("[repeated tool failure]"))
            .ToList();
        Assert.Single(nudges);
        Assert.Contains("read_file", nudges[0].Text);

        var report = AgentTurnReport.Capture(harness.Session.Messages, 0, result.Text);
        Assert.Equal(4, report.AssistantSteps);
        Assert.Equal(3, report.ToolCalls);
        Assert.Equal(3, report.ToolFailures);
        Assert.Equal(3, report.MaxConsecutiveSameToolFailures);
        Assert.False(report.FinalAnswerEmpty);
    }

    [Fact]
    public async Task StepBudgetExhausted_GrantsWrapUpStepAndSummarizes()
    {
        var existing = Path.Combine(Path.GetTempPath(), $"haoyue-existing-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(existing, "content");
        var args = $$"""{"path":"{{existing.Replace("\\", "\\\\")}}"}""";
        using var harness = await AgentHarness.CreateAsync(ScriptedSteps(
            ToolCall("c1", "read_file", args),
            new LlmCompletion { Text = "收尾总结：已确认文件内容" }));
        harness.Store.Config.Agent.MaxSteps = 1;
        harness.Store.Config.Agent.MaxRepairAttempts = 0;

        var result = await harness.RunAsync("多读几次文件");

        Assert.Equal("收尾总结：已确认文件内容", result.Text);
        var budget = harness.Session.Messages.Single(m =>
            m.Role == ChatRole.User && m.Text.Contains("[step budget]"));
        Assert.Contains("执行台账", budget.Text);
        Assert.Contains("1 个步骤", budget.Text);
    }

    [Fact]
    public async Task TurnTokenBudget_WrapsUpThenMarksTurnBudgetTerminated()
    {
        var existing = Path.Combine(Path.GetTempPath(), $"haoyue-budget-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(existing, "content");
        var args = $$"""{"path":"{{existing.Replace("\\", "\\\\")}}"}""";
        using var harness = await AgentHarness.CreateAsync(ScriptedSteps(
            new LlmCompletion
            {
                Usage = new TokenUsage(80, 40),
                ToolCalls = [new ToolCallRequest("c1", "read_file", args)],
            },
            new LlmCompletion { Text = "预算收尾总结" },
            new LlmCompletion { Text = "不应到达" }));
        harness.Store.Config.Agent.TurnTokenBudget = 100;

        var result = await harness.RunAsync("读取文件后总结");

        // 越界后必须真的给一步收尾（拿到总结），而不是把提示注入完就散场。
        Assert.Equal("预算收尾总结", result.Text);
        Assert.Null(result.Error);
        Assert.Equal(TurnFinishReason.Budget, result.FinishReason);
        var notice = Assert.Single(harness.Session.Messages,
            m => m.Role == ChatRole.User && m.Text.Contains("[turn budget]"));
        Assert.Contains("token 用量 120", notice.Text);
        Assert.Equal(2, harness.Session.Messages.Count(m => m.Role == ChatRole.Assistant));
    }

    [Fact]
    public async Task TurnCostBudget_UsesModelPrices_BeforeEndingTheTurn()
    {
        var existing = Path.Combine(Path.GetTempPath(), $"haoyue-cost-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(existing, "content");
        var args = $$"""{"path":"{{existing.Replace("\\", "\\\\")}}"}""";
        using var harness = await AgentHarness.CreateAsync(ScriptedSteps(
            new LlmCompletion
            {
                Usage = new TokenUsage(80, 40),
                ToolCalls = [new ToolCallRequest("c1", "read_file", args)],
            },
            new LlmCompletion { Text = "费用到顶的总结" },
            new LlmCompletion { Text = "不应到达" }));
        var model = harness.Store.Config.Providers[0].Models[0];
        model.InputPricePerMTok = 1_000m;
        model.OutputPricePerMTok = 1_000m;
        // 120 token × 1000/1M = 0.12，超过 0.1 的上限；token 闸门保持关闭。
        harness.Store.Config.Agent.TurnCostBudget = 0.1;

        var result = await harness.RunAsync("读取文件后总结");

        Assert.Equal("费用到顶的总结", result.Text);
        Assert.Equal(TurnFinishReason.Budget, result.FinishReason);
        var notice = Assert.Single(harness.Session.Messages,
            m => m.Role == ChatRole.User && m.Text.Contains("[turn budget]"));
        Assert.Contains("费用", notice.Text);
    }

    [Fact]
    public void TurnBudgetBreach_ChecksTokenCostAndWallClock_AndTreatsZeroAsUnlimited()
    {
        var config = new AgentConfig { TurnTokenBudget = 1000, TurnCostBudget = 1, TurnWallClockSeconds = 60 };
        Assert.Null(Agent.TurnBudgetBreach(config, 999, 0.9m, TimeSpan.FromSeconds(1)));
        Assert.Contains("token", Agent.TurnBudgetBreach(config, 1_000, 0m, TimeSpan.FromSeconds(1)));
        Assert.Contains("费用", Agent.TurnBudgetBreach(config, 100, 1m, TimeSpan.FromSeconds(1)));
        Assert.Contains("耗时", Agent.TurnBudgetBreach(config, 100, 0m, TimeSpan.FromSeconds(60)));

        var unlimited = new AgentConfig();
        Assert.Null(Agent.TurnBudgetBreach(unlimited, long.MaxValue, 999_999m, TimeSpan.FromDays(1)));
    }

    [Fact]
    public async Task PlanFinished_StillToolCalling_InjectsNudgeOnce()
    {
        var existing = Path.Combine(Path.GetTempPath(), $"haoyue-existing-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(existing, "content");
        var args = $$"""{"path":"{{existing.Replace("\\", "\\\\")}}"}""";
        var planArgs = $$"""{"steps":[{"title":"读取目标文件","status":"completed"}],"explanation":"任务收尾"}""";
        using var harness = await AgentHarness.CreateAsync(ScriptedSteps(
            ToolCall("p1", "update_plan", planArgs),
            ToolCall("c1", "read_file", args),
            new LlmCompletion { Text = "计划已完成，直接收尾" }));

        var result = await harness.RunAsync("按计划读取文件");

        Assert.Equal("计划已完成，直接收尾", result.Text);
        var nudges = harness.Session.Messages
            .Where(m => m.Role == ChatRole.User && m.Text.Contains("[plan finished]"))
            .ToList();
        Assert.Single(nudges);
        Assert.Contains("update_plan", nudges[0].Text);

        var planResult = harness.Session.Messages.Single(m => m.Role == ChatRole.Tool && m.ToolCallId == "p1");
        Assert.Contains("进度：1/1 已完成", planResult.Text);
    }

    [Fact]
    public async Task PlanFinished_ConcludingAfterPlanUpdate_DoesNotNudge()
    {
        var planArgs = $$"""{"steps":[{"title":"总结任务","status":"completed"}]}""";
        using var harness = await AgentHarness.CreateAsync(ScriptedSteps(
            ToolCall("p1", "update_plan", planArgs),
            new LlmCompletion { Text = "全部完成" }));

        var result = await harness.RunAsync("完成任务");

        Assert.Equal("全部完成", result.Text);
        Assert.DoesNotContain(harness.Session.Messages, m => m.Text.Contains("[plan finished]"));
    }

    [Fact]
    public async Task PlanStale_NoStatusUpdates_InjectsNudgeOnce()
    {
        var existing = Path.Combine(Path.GetTempPath(), $"haoyue-existing-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(existing, "content");
        var args = $$"""{"path":"{{existing.Replace("\\", "\\\\")}}"}""";
        var planArgs = $$"""{"steps":[{"title":"步骤一","status":"in_progress"},{"title":"步骤二","status":"pending"}]}""";
        using var harness = await AgentHarness.CreateAsync(ScriptedSteps(
            ToolCall("p1", "update_plan", planArgs),
            ToolCall("c1", "read_file", args),
            ToolCall("c2", "read_file", args),
            ToolCall("c3", "read_file", args),
            ToolCall("c4", "read_file", args),
            new LlmCompletion { Text = "补充状态后完成" }));

        var result = await harness.RunAsync("按计划执行多步读取");

        Assert.Equal("补充状态后完成", result.Text);
        var nudges = harness.Session.Messages
            .Where(m => m.Role == ChatRole.User && m.Text.Contains("[plan stale]"))
            .ToList();
        Assert.Single(nudges);
        Assert.Contains("4 次", nudges[0].Text);
    }

    [Fact]
    public async Task PlanStale_StatusUpdateResetsStalenessCounter()
    {
        var existing = Path.Combine(Path.GetTempPath(), $"haoyue-existing-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(existing, "content");
        var args = $$"""{"path":"{{existing.Replace("\\", "\\\\")}}"}""";
        var planArgs = $$"""{"steps":[{"title":"步骤一","status":"in_progress"},{"title":"步骤二","status":"pending"}]}""";
        var refreshedArgs = $$"""{"steps":[{"title":"步骤一","status":"completed"},{"title":"步骤二","status":"in_progress"}]}""";
        using var harness = await AgentHarness.CreateAsync(ScriptedSteps(
            ToolCall("p1", "update_plan", planArgs),
            ToolCall("c1", "read_file", args),
            ToolCall("c2", "read_file", args),
            ToolCall("c3", "read_file", args),
            ToolCall("p2", "update_plan", refreshedArgs),
            ToolCall("c4", "read_file", args),
            ToolCall("c5", "read_file", args),
            ToolCall("c6", "read_file", args),
            new LlmCompletion { Text = "完成" }));

        var result = await harness.RunAsync("按计划执行并同步状态");

        Assert.Equal("完成", result.Text);
        Assert.DoesNotContain(harness.Session.Messages, m => m.Text.Contains("[plan stale]"));
    }

    [Fact]
    public async Task StrategyPivot_AfterFourConsecutiveSameFailures_InjectsOnce()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"haoyue-missing-{Guid.NewGuid():N}.txt");
        var args = $$"""{"path":"{{missing.Replace("\\", "\\\\")}}"}""";
        using var harness = await AgentHarness.CreateAsync(ScriptedSteps(
            ToolCall("c1", "read_file", args),
            ToolCall("c2", "read_file", args),
            ToolCall("c3", "read_file", args),
            ToolCall("c4", "read_file", args),
            new LlmCompletion { Text = "换了一种思路后完成" }));

        var result = await harness.RunAsync("反复读取同一个不存在的文件");

        Assert.Equal("换了一种思路后完成", result.Text);
        var repeated = harness.Session.Messages
            .Where(m => m.Role == ChatRole.User && m.Text.Contains("[repeated tool failure]")).ToList();
        Assert.Single(repeated);
        var pivots = harness.Session.Messages
            .Where(m => m.Role == ChatRole.User && m.Text.Contains("[strategy pivot]")).ToList();
        Assert.Single(pivots);
        Assert.Contains("read_file", pivots[0].Text);
        Assert.Contains("4 次", pivots[0].Text);
    }

    [Fact]
    public async Task NoProgress_EditModeWithoutPlanOrChanges_InjectsOnce()
    {
        var existing = Path.Combine(Path.GetTempPath(), $"haoyue-existing-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(existing, "content");
        var args = $$"""{"path":"{{existing.Replace("\\", "\\\\")}}"}""";
        var pair = new LlmCompletion
        {
            ToolCalls =
            [
                new ToolCallRequest("a", "read_file", args),
                new ToolCallRequest("b", "read_file", args),
            ],
        };
        using var harness = await AgentHarness.CreateAsync(ScriptedSteps(
            pair, pair, pair, pair,
            new LlmCompletion { Text = "调研结束，直接总结" }));

        var result = await harness.RunAsync("反复确认文件内容");

        Assert.Equal("调研结束，直接总结", result.Text);
        var nudges = harness.Session.Messages
            .Where(m => m.Role == ChatRole.User && m.Text.Contains("[no progress]")).ToList();
        Assert.Single(nudges);
        Assert.Contains("8 次", nudges[0].Text);
        Assert.Contains("update_plan", nudges[0].Text);
    }

    [Fact]
    public async Task NoProgress_ReadOnlyMode_SkipsNudge()
    {
        var existing = Path.Combine(Path.GetTempPath(), $"haoyue-existing-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(existing, "content");
        var args = $$"""{"path":"{{existing.Replace("\\", "\\\\")}}"}""";
        var pair = new LlmCompletion
        {
            ToolCalls =
            [
                new ToolCallRequest("a", "read_file", args),
                new ToolCallRequest("b", "read_file", args),
            ],
        };
        using var harness = await AgentHarness.CreateAsync(ScriptedSteps(
            pair, pair, pair, pair,
            new LlmCompletion { Text = "只读调研完成" }));
        harness.Store.Config.Agent.Mode = "readonly";

        var result = await harness.RunAsync("只读模式下反复调研");

        Assert.Equal("只读调研完成", result.Text);
        Assert.DoesNotContain(harness.Session.Messages, m => m.Text.Contains("[no progress]"));
    }

    [Fact]
    public async Task UnknownTool_SuggestsClosestRegisteredName()
    {
        using var harness = await AgentHarness.CreateAsync(ScriptedSteps(
            ToolCall("c1", "read_fille", "{}"),
            new LlmCompletion { Text = "修正工具名后完成" }));

        var result = await harness.RunAsync("调用一个拼错的工具");

        Assert.Equal("修正工具名后完成", result.Text);
        var toolMessage = harness.Session.Messages.Single(m => m.Role == ChatRole.Tool && m.ToolCallId == "c1");
        Assert.Contains("Unknown tool: read_fille", toolMessage.Text);
        Assert.Contains("read_file", toolMessage.Text);
    }

    [Fact]
    public async Task AnswerCompleteness_UnmentionedChangedFiles_InjectsOnce()
    {
        // 写入目标必须在 harness 工作区内（2026-10-12 起 write_file 强制工作区边界）；
        // 用非全局工作区使边界即工作区根。
        var dir = Path.Combine(Path.GetTempPath(), "haoyue-precision-test", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(dir, "ws", "hello-haoyue.txt");
        var args = $$"""{"path":"{{target.Replace("\\", "\\\\")}}","content":"hello haoyue"}""";
        using var harness = await AgentHarness.CreateAsync(ScriptedSteps(
            ToolCall("w1", "write_file", args),
            new LlmCompletion { Text = "任务完成" },
            new LlmCompletion { Text = $"已完成 {Path.GetFileName(target)} 的写入" }), dir: dir, globalWorkspace: false);

        var result = await harness.RunAsync("写一个测试文件");

        Assert.Contains(Path.GetFileName(target), result.Text);
        var nudges = harness.Session.Messages
            .Where(m => m.Role == ChatRole.User && m.Text.Contains("[answer completeness]")).ToList();
        Assert.Single(nudges);
        Assert.Contains("1 个文件", nudges[0].Text);
    }

    [Fact]
    public async Task AnswerCompleteness_AnswerNamesChangedFile_SkipsNudge()
    {
        var dir = Path.Combine(Path.GetTempPath(), "haoyue-precision-test", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(dir, "ws", "named-change.txt");
        var args = $$"""{"path":"{{target.Replace("\\", "\\\\")}}","content":"hello haoyue"}""";
        using var harness = await AgentHarness.CreateAsync(ScriptedSteps(
            ToolCall("w1", "write_file", args),
            new LlmCompletion { Text = $"已写入 {Path.GetFileName(target)}" }), dir: dir, globalWorkspace: false);

        var result = await harness.RunAsync("写一个测试文件");

        Assert.Contains(Path.GetFileName(target), result.Text);
        Assert.DoesNotContain(harness.Session.Messages, m => m.Text.Contains("[answer completeness]"));
    }

    [Fact]
    public async Task RedundantSuccessfulCalls_InjectsOnce()
    {
        var existing = Path.Combine(Path.GetTempPath(), $"haoyue-existing-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(existing, "content");
        var args = $$"""{"path":"{{existing.Replace("\\", "\\\\")}}"}""";
        using var harness = await AgentHarness.CreateAsync(ScriptedSteps(
            ToolCall("c1", "read_file", args),
            ToolCall("c2", "read_file", args),
            ToolCall("c3", "read_file", args),
            new LlmCompletion { Text = "确认无误，完成" }));

        var result = await harness.RunAsync("反复确认同一个文件");

        Assert.Equal("确认无误，完成", result.Text);
        var nudges = harness.Session.Messages
            .Where(m => m.Role == ChatRole.User && m.Text.Contains("[redundant call]")).ToList();
        Assert.Single(nudges);
        Assert.Contains("3 次", nudges[0].Text);
    }

    [Fact]
    public async Task PlanningHint_ComplexInput_InjectsOnce()
    {
        var complex = "先检查 preview.py 的执行与打包逻辑，然后修复 build.py 在 Windows 环境下的编码异常，最后运行测试验证构建流程是否通过";
        using var harness = await AgentHarness.CreateAsync(ScriptedSteps(new LlmCompletion { Text = "已了解任务" }));

        var result = await harness.RunAsync(complex);

        Assert.Equal("已了解任务", result.Text);
        Assert.Single(harness.Session.Messages
            .Where(m => m.Role == ChatRole.User && m.Text.Contains("[planning hint]")).ToList());
    }

    [Fact]
    public async Task PlanningHint_SimpleInput_Skips()
    {
        using var harness = await AgentHarness.CreateAsync(ScriptedSteps(new LlmCompletion { Text = "好的" }));

        var result = await harness.RunAsync("修复这个 bug");

        Assert.Equal("好的", result.Text);
        Assert.DoesNotContain(harness.Session.Messages, m => m.Text.Contains("[planning hint]"));
    }

    [Fact]
    public async Task MemorySave_AppendsDatedLine_AndDedupes()
    {
        var content = "用户在本项目偏好使用 pnpm 管理依赖";
        var args = $$"""{"content":"{{content}}","topic":"convention"}""";
        using var harness = await AgentHarness.CreateAsync(ScriptedSteps(
            ToolCall("m1", "memory_save", args),
            ToolCall("m2", "memory_save", args),
            new LlmCompletion { Text = "已记住" }));

        var result = await harness.RunAsync("记住一个项目偏好");

        Assert.Equal("已记住", result.Text);
        Assert.True(File.Exists(harness.Workspace.MemoryFile), "memory file should exist");
        var text = File.ReadAllText(harness.Workspace.MemoryFile);
        Assert.Contains(content, text);
        Assert.Equal(1, text.Split('\n').Count(l => l.Contains(content, StringComparison.Ordinal)));
        var dedupe = harness.Session.Messages.Single(m => m.Role == ChatRole.Tool && m.ToolCallId == "m2");
        Assert.Contains("已存在", dedupe.Text);
    }

    [Fact]
    public async Task TurnInterrupted_AppendsExecutedTraceForContinuity()
    {
        var existing = Path.Combine(Path.GetTempPath(), $"haoyue-existing-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(existing, "content");
        var args = $$"""{"path":"{{existing.Replace("\\", "\\\\")}}"}""";
        using var harness = await AgentHarness.CreateAsync(
            ScriptedSteps(ToolCall("c1", "read_file", args), new LlmCompletion { Text = "不可达" }),
            throwLlmAtRequest: 2);

        var result = await harness.RunAsync("读取后继续任务");

        Assert.NotNull(result.Error);
        var trace = harness.Session.Messages.Single(m =>
            m.Role == ChatRole.User && m.Text.Contains("[turn interrupted]"));
        Assert.Contains("read_file", trace.Text);
    }

    [Fact]
    public void AgentTurnReport_ComputesMetricsFromMessages()
    {
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = ChatRole.Assistant,
                ToolCalls =
                [
                    new ToolCallRequest("a", "read_file", "{}"),
                    new ToolCallRequest("b", "read_file", "{}"),
                ],
            },
            new() { Role = ChatRole.Tool, ToolCallId = "a", ToolName = "read_file", ToolSuccess = false },
            new() { Role = ChatRole.Tool, ToolCallId = "b", ToolName = "read_file", ToolSuccess = true },
            new() { Role = ChatRole.Assistant, ToolCalls = [new ToolCallRequest("c", "edit_file", "{}")] },
            new() { Role = ChatRole.Tool, ToolCallId = "c", ToolName = "edit_file", ToolSuccess = false },
            ChatMessage.Assistant("结论"),
        };

        var report = AgentTurnReport.Capture(messages, finalText: "结论");

        Assert.Equal(3, report.AssistantSteps);
        Assert.Equal(3, report.ToolCalls);
        Assert.Equal(2, report.ToolFailures);
        Assert.Equal(1, report.MaxConsecutiveSameToolFailures);
        Assert.Equal(2.0 / 3, report.ToolFailureRate, precision: 4);
        Assert.False(report.FinalAnswerEmpty);
        Assert.Contains("失败率 66.7", report.RenderSummary());
    }

    [Fact]
    public void AgentTurnReport_EmptyFinalAnswerFlagsMiss()
    {
        var report = AgentTurnReport.Capture([ChatMessage.Assistant("")], finalText: "");
        Assert.True(report.FinalAnswerEmpty);
        Assert.True(report.TurnEndedWithoutAnswer);
    }

    // ---------------------------------------------------------------- harness

    private static List<LlmCompletion> ScriptedSteps(params LlmCompletion[] steps) => [.. steps];

    private static LlmCompletion ToolCall(string id, string name, string args) => new()
    {
        ToolCalls = [new ToolCallRequest(id, name, args)],
    };

    private sealed class ScriptedClientFactory : ILlmClientFactory
    {
        private readonly List<LlmCompletion> _steps;
        public ScriptedClientFactory(List<LlmCompletion> steps) => _steps = steps;
        public List<LlmRequest> Requests { get; } = [];
        /// <summary>1-based request index that fails with an LlmException; 0 = never throw.</summary>
        public int ThrowAtRequest { get; set; }
        /// <summary>当 ThrowAtRequest 命中时先吐出一段文本再断开，复现「流到一半连接中断」。</summary>
        public bool ThrowWithPartialText { get; set; }
        public ILlmClient GetClient(string kind) => new ScriptedClient(this);

        private sealed class ScriptedClient(ScriptedClientFactory owner) : ILlmClient
        {
            public string Kind => "openai";

            public Task<EmbeddingResult?> EmbedAsync(
                ProviderConfig provider, IReadOnlyList<string> inputs, string? model = null, CancellationToken ct = default)
                => Task.FromResult<EmbeddingResult?>(null);

            public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
                LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
            {
                owner.Requests.Add(request);
                if (owner.ThrowAtRequest > 0 && owner.Requests.Count == owner.ThrowAtRequest)
                {
                    if (owner.ThrowWithPartialText)
                    {
                        yield return new LlmTextDelta("部分输出");
                        throw new LlmException("connection dropped mid-stream", retryable: false);
                    }
                    throw new LlmException("scripted failure", retryable: false);
                }
                await Task.Yield();
                var index = Math.Min(owner.Requests.Count - 1, owner._steps.Count - 1);
                yield return new LlmCompleted(owner._steps[index]);
            }
        }
    }

    private sealed class AgentHarness(
        ConfigStore store, HaoyueRuntime runtime, AgentSession session, WorkspaceInfo workspace,
        ScriptedClientFactory factory) : IDisposable
    {
        public ConfigStore Store { get; } = store;
        public AgentSession Session { get; } = session;
        public WorkspaceInfo Workspace { get; } = workspace;

        public async Task<AgentTurnResult> RunAsync(string input) =>
            await runtime.Agent.RunTurnAsync(Session, workspace, input, CancellationToken.None);

        public void Dispose()
        {
            runtime.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(workspace.Root)) Directory.Delete(workspace.Root, true);
        }

        public static async Task<AgentHarness> CreateAsync(
            List<LlmCompletion> steps, int throwLlmAtRequest = 0, string? dir = null,
            bool globalWorkspace = true, bool throwWithPartialText = false)
        {
            dir ??= Path.Combine(Path.GetTempPath(), "haoyue-precision-test", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var store = new ConfigStore(Path.Combine(dir, "config.json"), Path.Combine(dir, "state.json"));
            store.Config.Providers.Clear();
            store.Config.Providers.Add(new ProviderConfig
            {
                Id = "openai",
                Kind = "openai",
                BaseUrl = "https://test.local/v1",
                Models = [new ModelConfig { Id = "gpt-test", ContextWindow = 128_000 }],
            });
            var capture = new ScriptedClientFactory(steps) { ThrowAtRequest = throwLlmAtRequest, ThrowWithPartialText = throwWithPartialText };
            var workspace = globalWorkspace
                ? new WorkspaceManager().CreateGlobal(Path.Combine(dir, "global"))
                : new WorkspaceInfo { Root = Path.Combine(dir, "ws"), ProjectKinds = [] };

            var runtime = HaoyueRuntime.CreateIsolated(workspace, configureServices: services =>
            {
                services.AddSingleton<IConfigStore>(store);
                services.AddSingleton(new HaoyueDatabase(Path.Combine(dir, "state.db")));
                services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
                services.AddSingleton<ILlmClientFactory>(capture);
                services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
            });
            var session = runtime.Sessions.Create(workspace, networkEnabled: false);
            await Task.CompletedTask;
            return new AgentHarness(store, runtime, session, workspace, capture);
        }
    }
}
