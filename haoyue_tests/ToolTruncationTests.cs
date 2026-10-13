using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>
/// H-B：工具结果 head+tail 截断。构建/测试日志的失败摘要、exit code、统计几乎总在
/// 输出尾部，头截断会系统性丢弃它们——模型看到腰斩输出后误判并盲目重试。
/// </summary>
public class ToolTruncationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-truncate-" + Guid.NewGuid().ToString("N"));

    public ToolTruncationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static ToolContext NewContext(string root, int maxOutputChars = 30_000) => new()
    {
        Workspace = new WorkspaceInfo { Root = root, ProjectKinds = [] },
        Events = new EventBus(),
        Agent = new AgentConfig(),
        MaxOutputChars = maxOutputChars,
    };

    [Fact]
    public void TruncateTailPrioritized_KeepsHeadAndTail_AndMarksOmission()
    {
        var context = NewContext(_dir, maxOutputChars: 1000);
        var text = string.Concat(Enumerable.Range(0, 5000).Select(i => $"L{i:0000}\n"));

        var result = context.TruncateTailPrioritized(text, "test output", tailShare: 0.7);

        Assert.True(result.Length <= 1000, $"result was {result.Length} chars, budget is 1000");
        Assert.StartsWith("L0000", result);
        Assert.Contains("L4999", result); // 尾部行存活
        Assert.Contains("characters omitted from the middle", result);
    }

    [Fact]
    public void TruncateTailPrioritized_ShortText_ReturnsUnchanged()
    {
        var context = NewContext(_dir, maxOutputChars: 1000);
        const string text = "short enough";
        Assert.Equal(text, context.TruncateTailPrioritized(text));
    }

    [Fact]
    public void TruncateTailPrioritized_TailShareIsClamped()
    {
        var context = NewContext(_dir, maxOutputChars: 1000);
        var text = string.Concat(Enumerable.Range(0, 5000).Select(i => $"L{i:0000}\n"));

        // 极端 tailShare 也必须产出合法结果（不抛、不超预算、首尾都在）。
        foreach (var share in new[] { -5.0, 0.0, 1.0, 5.0 })
        {
            var result = context.TruncateTailPrioritized(text, "x", tailShare: share);
            Assert.True(result.Length <= 1000);
            Assert.StartsWith("L0000", result);
            Assert.Contains("L4999", result);
        }
    }

    [Fact]
    public async Task BashTool_FailedCommand_KeepsTailSummary()
    {
        var tool = new BashTool(new FilePromptProvider());
        // 6000 行输出后以退出码 3 结束：失败摘要在尾部，必须存活。
        var command = "i=0; while [ $i -lt 6000 ]; do echo \"line-$i\"; i=$((i+1)); done; exit 3";
        var result = await tool.ExecuteAsync(
            new JsonObject { ["command"] = command, ["timeout_seconds"] = 60 },
            NewContext(_dir), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Exit code 3", result.Output);
        Assert.Contains("line-5999", result.Output); // 头截断下会被丢掉的尾行
        Assert.Contains("omitted from the middle", result.Output);
    }
}
