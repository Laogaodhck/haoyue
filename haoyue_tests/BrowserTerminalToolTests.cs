using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;
using Haoyue.Runtime.Tools.HeadlessBrowser;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>terminal 工具：命令包裹、cwd 回读解析、会话生命周期与集成执行。</summary>
public class TerminalToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-terminal-" + Guid.NewGuid().ToString("N"));

    public TerminalToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private ToolContext Context() => new()
    {
        Workspace = new WorkspaceInfo { Root = _dir, ProjectKinds = [] },
        Events = new EventBus(),
        Agent = new AgentConfig(),
    };

    // ---------------------------------------------------------------- 包裹与解析

    [Fact]
    public void WrapCommand_Bash_AppendsRcAndCwdMarker()
    {
        var wrapped = TerminalTool.WrapCommand("echo hi", "ab12cd34", ShellRunner.ShellKind.Bash);
        Assert.StartsWith("echo hi", wrapped);
        Assert.Contains("__haoyue_rc=$?", wrapped);
        // cygpath -w 把 MSYS 挂载路径（/tmp/...）转回 Win32；无 cygpath 时回退 pwd。
        Assert.Contains("__HAOYUE_CWD_ab12cd34__$(cygpath -w \"$(pwd)\" 2>/dev/null || pwd)", wrapped);
        Assert.Contains("exit $__haoyue_rc", wrapped);
    }

    [Fact]
    public void WrapCommand_PowerShell_PropagatesLastExitCode()
    {
        var wrapped = TerminalTool.WrapCommand("echo hi", "ab12cd34", ShellRunner.ShellKind.PowerShell);
        Assert.Contains("$LASTEXITCODE", wrapped);
        Assert.Contains("__HAOYUE_CWD_ab12cd34__$((Get-Location).Path)", wrapped);
    }

    [Fact]
    public void WrapCommand_Cmd_UsesDelayedExpansion()
    {
        var wrapped = TerminalTool.WrapCommand("cd sub", "ab12cd34", ShellRunner.ShellKind.Cmd);
        Assert.Contains("!CD!", wrapped);
        Assert.Contains("exit /b !ERRORLEVEL!", wrapped);
    }

    [Fact]
    public void ExtractCwd_LastMarkerWins_KeepsOtherLines()
    {
        var output = "line1\n__HAOYUE_CWD_ab12cd34__C:\\first\nnoise\n__HAOYUE_CWD_ab12cd34__C:\\last\r\nline2\n";
        var (cwd, text) = TerminalTool.ExtractCwd(output, "ab12cd34");
        Assert.Equal("C:\\last", cwd);
        Assert.DoesNotContain("__HAOYUE_CWD_", text);
        Assert.Contains("line1", text);
        Assert.Contains("noise", text);
        Assert.Contains("line2", text);
    }

    [Fact]
    public void ExtractCwd_MissingMarker_ReturnsNullAndTrims()
    {
        var (cwd, text) = TerminalTool.ExtractCwd("plain output\n\n\n", "ab12cd34");
        Assert.Null(cwd);
        Assert.Equal("plain output", text);
    }

    // ---------------------------------------------------------------- 集成（真实 shell）

    [Fact]
    public async Task Terminal_Exec_PersistsCwdAcrossCommands()
    {
        var sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(sub);

        var tool = new TerminalTool(new FilePromptProvider());
        var session = "t-" + Guid.NewGuid().ToString("N")[..8];

        var first = await tool.ExecuteAsync(
            new JsonObject { ["command"] = "echo terminal-ok", ["session_id"] = session },
            Context(), CancellationToken.None);
        Assert.True(first.Success, first.Output);
        Assert.Contains("terminal-ok", first.Output);

        // cd 之后，后续命令应继承会话工作目录：在 sub 下落一个文件做实证。
        var change = await tool.ExecuteAsync(
            new JsonObject { ["command"] = "cd sub", ["session_id"] = session },
            Context(), CancellationToken.None);
        Assert.True(change.Success, change.Output);
        Assert.Contains($"(terminal cwd: {sub})", change.Output);

        var write = await tool.ExecuteAsync(
            new JsonObject { ["command"] = "echo still-here > cwd-proof.txt", ["session_id"] = session },
            Context(), CancellationToken.None);
        Assert.True(write.Success, write.Output);
        Assert.True(File.Exists(Path.Combine(sub, "cwd-proof.txt")),
            $"session cwd was not persisted; files in sub: {string.Join(",", Directory.Exists(sub) ? Directory.GetFiles(sub) : [])}");
        Assert.DoesNotContain("(terminal cwd:", write.Output); // 目录未再变化，不再注记

        await tool.ExecuteAsync(
            new JsonObject { ["action"] = "close", ["session_id"] = session },
            Context(), CancellationToken.None);
    }

    [Fact]
    public async Task Terminal_ListAndClose_ManageSessionLifecycle()
    {
        var tool = new TerminalTool(new FilePromptProvider());
        var session = "t-" + Guid.NewGuid().ToString("N")[..8];

        var exec = await tool.ExecuteAsync(
            new JsonObject { ["command"] = "echo x", ["session_id"] = session },
            Context(), CancellationToken.None);
        Assert.True(exec.Success, exec.Output);

        var list = await tool.ExecuteAsync(
            new JsonObject { ["action"] = "list" }, Context(), CancellationToken.None);
        Assert.True(list.Success);
        Assert.Contains(session, list.Output);

        var close = await tool.ExecuteAsync(
            new JsonObject { ["action"] = "close", ["session_id"] = session },
            Context(), CancellationToken.None);
        Assert.True(close.Success);

        var closeAgain = await tool.ExecuteAsync(
            new JsonObject { ["action"] = "close", ["session_id"] = session },
            Context(), CancellationToken.None);
        Assert.False(closeAgain.Success);
    }

    [Fact]
    public async Task Terminal_InvalidInput_FailsFast()
    {
        var tool = new TerminalTool(new FilePromptProvider());

        var unknown = await tool.ExecuteAsync(
            new JsonObject { ["action"] = "teleport" }, Context(), CancellationToken.None);
        Assert.False(unknown.Success);

        var noCommand = await tool.ExecuteAsync(
            new JsonObject { ["action"] = "exec" }, Context(), CancellationToken.None);
        Assert.False(noCommand.Success);

        var badCwd = await tool.ExecuteAsync(
            new JsonObject { ["command"] = "echo x", ["cwd"] = "no/such/dir" }, Context(), CancellationToken.None);
        Assert.False(badCwd.Success);
    }
}

/// <summary>browser 工具：定位器优先级、CDP 结果解析与（有浏览器时的）端到端冒烟。</summary>
public class BrowserToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-browser-test-" + Guid.NewGuid().ToString("N"));

    public BrowserToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Locate_PrefersEdgeOverChrome()
    {
        var env = new Dictionary<string, string?> { ["ProgramFiles"] = @"C:\pf", ["ProgramFiles(x86)"] = null, ["LocalAppData"] = null };
        var result = BrowserLocator.Locate(
            path => path.EndsWith("msedge.exe", StringComparison.OrdinalIgnoreCase),
            name => env.GetValueOrDefault(name), windows: true, pathValue: null);
        Assert.Equal(@"C:\pf\Microsoft\Edge\Application\msedge.exe", result);
    }

    [Fact]
    public void Locate_FallsBackToChromeWhenEdgeMissing()
    {
        var env = new Dictionary<string, string?> { ["ProgramFiles"] = @"C:\pf", ["ProgramFiles(x86)"] = null, ["LocalAppData"] = null };
        var result = BrowserLocator.Locate(
            path => path.EndsWith("chrome.exe", StringComparison.OrdinalIgnoreCase),
            name => env.GetValueOrDefault(name), windows: true, pathValue: null);
        Assert.Equal(@"C:\pf\Google\Chrome\Application\chrome.exe", result);
    }

    [Fact]
    public void Locate_ProbesPathDirectoriesBeforeGivingUp()
    {
        var result = BrowserLocator.Locate(
            path => path == Path.Combine("C:\\tools", "chromium.exe"),
            _ => null, windows: true, pathValue: "C:\\tools");
        Assert.Equal(Path.Combine("C:\\tools", "chromium.exe"), result);
    }

    [Fact]
    public void Locate_UnixChecksUsrBin()
    {
        var result = BrowserLocator.Locate(
            path => path == "/usr/bin/google-chrome",
            _ => null, windows: false, pathValue: null);
        Assert.Equal("/usr/bin/google-chrome", result);
    }

    [Fact]
    public void ResultValue_ExtractsPrimitiveValues()
    {
        Assert.Equal("hello", HeadlessBrowserSession.ResultValue(
            new JsonObject { ["result"] = new JsonObject { ["value"] = "hello" } }));
        Assert.Equal("42", HeadlessBrowserSession.ResultValue(
            new JsonObject { ["result"] = new JsonObject { ["value"] = 42 } }));
        Assert.Null(HeadlessBrowserSession.ResultValue(
            new JsonObject { ["result"] = new JsonObject { ["type"] = "undefined" } }));
    }

    [Fact]
    public async Task Browser_OpenEvalText_EndToEnd_WhenBrowserAvailable()
    {
        if (BrowserLocator.Locate() is null) return; // 无 Edge/Chrome 的环境跳过

        var tool = new BrowserTool(new FilePromptProvider(),
            new ConfigStore(Path.Combine(_dir, "config.json"), Path.Combine(_dir, "state.json")));
        var context = new ToolContext
        {
            Workspace = new WorkspaceInfo { Root = Path.GetTempPath(), IsGlobal = true, ProjectKinds = [] },
            Events = new EventBus(),
            Agent = new AgentConfig(),
        };

        var page = "data:text/html,<title>t1</title><body><p id=p>browser-smoke</p></body>";
        var open = await tool.ExecuteAsync(
            new JsonObject { ["action"] = "open", ["url"] = page, ["timeout_seconds"] = 60 },
            context, CancellationToken.None);
        Assert.True(open.Success, open.Output);
        Assert.Contains("browser-smoke", open.Output);

        var eval = await tool.ExecuteAsync(
            new JsonObject { ["action"] = "eval", ["script"] = "1 + 41", ["timeout_seconds"] = 60 },
            context, CancellationToken.None);
        Assert.True(eval.Success, eval.Output);
        Assert.Contains("42", eval.Output);

        await tool.ExecuteAsync(new JsonObject { ["action"] = "close" }, context, CancellationToken.None);
    }
}
