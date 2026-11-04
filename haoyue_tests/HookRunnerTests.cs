using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Daemon;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// 事件钩子执行器的配置诊断（HookRunner.Validate）与进程内状态复位。
/// 触发执行本身是 fire-and-forget（Task.Run），属于集成路径，
/// 由 DaemonServerTests 的 hooks.* 契约用例与手动试运行覆盖。
/// </summary>
public sealed class HookRunnerTests
{
    private static HookConfig Base(string action = "webhook") => new()
    {
        Name = "notify",
        Trigger = "task.completed",
        Action = action,
        Enabled = true,
        WebhookUrl = action == "webhook" ? "https://example.com/hook" : null,
        Command = action == "command" ? "dotnet --version" : null,
    };

    [Fact]
    public void ValidWebhook_NoProblem() =>
        Assert.Null(HookRunner.Validate(Base("webhook")));

    [Fact]
    public void ValidCommand_NoProblem() =>
        Assert.Null(HookRunner.Validate(Base("command")));

    [Fact]
    public void EmptyName_ReportsProblem()
    {
        var hook = Base();
        hook.Name = " ";
        Assert.Contains("名称", HookRunner.Validate(hook));
    }

    [Fact]
    public void UnsupportedTrigger_ReportsProblem()
    {
        var hook = Base();
        hook.Trigger = "turn.started";
        Assert.Contains("触发事件", HookRunner.Validate(hook));
    }

    [Fact]
    public void UnsupportedAction_ReportsProblem()
    {
        var hook = Base();
        hook.Action = "email";
        Assert.Contains("动作类型", HookRunner.Validate(hook));
    }

    [Fact]
    public void WebhookWithoutUrl_ReportsProblem()
    {
        var hook = Base();
        hook.WebhookUrl = null;
        Assert.Contains("目标地址", HookRunner.Validate(hook));
    }

    [Fact]
    public void WebhookWithNonHttpUrl_ReportsProblem()
    {
        var hook = Base();
        hook.WebhookUrl = "ftp://example.com/hook";
        Assert.Contains("http(s)", HookRunner.Validate(hook));
    }

    [Fact]
    public void CommandWithoutCommand_ReportsProblem()
    {
        var hook = Base("command");
        hook.Command = " ";
        Assert.Contains("命令", HookRunner.Validate(hook));
    }

    [Fact]
    public void ResetForTests_ClearsSnapshot()
    {
        HookRunner.ResetForTests();
        Assert.Empty(HookRunner.Snapshot());
    }
}
