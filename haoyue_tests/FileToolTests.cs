using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;
using Haoyue.Runtime.Workspaces;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// edit_file 工具的直接行为级测试：精准定位替换、唯一性约束、未匹配错误处理、
/// 参数校验，以及行尾风格容错（模型以 \n 描述编辑而文件实际为 \r\n 时不得误报未找到）。
/// </summary>
public sealed class FileToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "haoyue-filetool-tests", Guid.NewGuid().ToString("N"));
    private readonly EditFileTool _tool = new(new FilePromptProvider());

    public FileToolTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Replace_SingleMatch_UpdatesFileAndPublishesDiff()
    {
        var path = Write("sample.txt", "alpha\nbeta\ngamma\n");
        var events = new EventBus();
        using var subscription = events.Subscribe();

        var result = await ExecuteAsync(events, path, "beta", "delta");

        Assert.True(result.Success);
        Assert.Equal("alpha\ndelta\ngamma\n", await File.ReadAllTextAsync(path));
        Assert.NotNull(result.Diff);
        // 文件 diff 事件照常发布，桌面端与 CLI 的 diff 展示依赖它。
        var diffEvent = Assert.IsType<FileDiffEvent>(await subscription.Reader.ReadAsync());
        Assert.Equal(Path.GetRelativePath(_root, path), diffEvent.FilePath);
    }

    [Fact]
    public async Task Replace_NotFound_FailsAndKeepsFileIntact()
    {
        var original = "alpha\nbeta\ngamma\n";
        var path = Write("sample.txt", original);

        var result = await ExecuteAsync(new EventBus(), path, "delta", "epsilon");

        Assert.False(result.Success);
        Assert.Contains("not found", result.Output);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Replace_MultipleMatchesWithoutReplaceAll_FailsAndKeepsFileIntact()
    {
        var original = "x = 1;\nx = 2;\n";
        var path = Write("sample.txt", original);

        // "x = " 命中两处：不带 replace_all 必须失败且文件原样保留。
        var ambiguous = await ExecuteAsync(new EventBus(), path, "x = ", "y = ");
        Assert.False(ambiguous.Success);
        Assert.Contains("matches 2 locations", ambiguous.Output);
        Assert.Equal(original, await File.ReadAllTextAsync(path));

        var replaceAll = await ExecuteAsync(new EventBus(), path, "x = ", "y = ", replaceAll: true);
        Assert.True(replaceAll.Success);
        Assert.Equal("y = 1;\ny = 2;\n", await File.ReadAllTextAsync(path));
        Assert.Contains("Replaced 2 occurrences", replaceAll.Summary);
    }

    [Fact]
    public async Task Replace_RejectsEmptyOldStringAndIdenticalStrings()
    {
        var path = Write("sample.txt", "content\n");

        var emptyOld = await ExecuteAsync(new EventBus(), path, "", "new");
        Assert.False(emptyOld.Success);
        Assert.Contains("must not be empty", emptyOld.Output);

        var identical = await ExecuteAsync(new EventBus(), path, "content", "content");
        Assert.False(identical.Success);
        Assert.Contains("identical", identical.Output);
    }

    [Fact]
    public async Task Replace_MissingFile_Fails()
    {
        var result = await ExecuteAsync(new EventBus(), Path.Combine(_root, "absent.txt"), "a", "b");
        Assert.False(result.Success);
        Assert.Contains("File not found", result.Output);
    }

    [Fact]
    public async Task Replace_ToleratesLfOldStringInCrlfFile()
    {
        // 模型以 \n 描述编辑、文件实际为 \r\n：必须命中而不是误报未找到，
        // 且 new_string 归一到 CRLF，避免文件出现混合行尾。
        var path = Write("windows.txt", "first\r\nsecond\r\nthird\r\n");

        var result = await ExecuteAsync(new EventBus(), path, "second\nthird", "SECOND\nTHIRD");

        Assert.True(result.Success, result.Output);
        Assert.Equal("first\r\nSECOND\r\nTHIRD\r\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Replace_ToleratesCrlfOldStringInLfFile()
    {
        var path = Write("unix.txt", "first\nsecond\nthird\n");

        var result = await ExecuteAsync(new EventBus(), path, "second\r\nthird", "SECOND\r\nTHIRD");

        Assert.True(result.Success, result.Output);
        Assert.Equal("first\nSECOND\nTHIRD\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Replace_UniquenessIsEnforcedAfterLineEndingNormalization()
    {
        // 行尾归一化后的命中数同样要满足唯一性约束。
        var path = Write("windows.txt", "a\r\nloop\r\nb\r\nloop\r\n");

        var result = await ExecuteAsync(new EventBus(), path, "loop", "LOOP");
        Assert.False(result.Success);
        Assert.Contains("matches 2 locations", result.Output);
        Assert.Equal("a\r\nloop\r\nb\r\nloop\r\n", await File.ReadAllTextAsync(path));
    }

    private async Task<ToolResult> ExecuteAsync(
        IEventBus events, string path, string oldString, string newString, bool replaceAll = false)
    {
        var arguments = new JsonObject
        {
            ["path"] = path,
            ["old_string"] = oldString,
            ["new_string"] = newString,
        };
        if (replaceAll) arguments["replace_all"] = true;

        var context = new ToolContext
        {
            Workspace = new WorkspaceInfo { Root = _root, ProjectKinds = [] },
            Events = events,
            Agent = new AgentConfig(),
            CallId = "test-call",
        };
        return await _tool.ExecuteAsync(arguments, context, CancellationToken.None);
    }

    private string Write(string fileName, string content)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, content);
        return path;
    }
}
