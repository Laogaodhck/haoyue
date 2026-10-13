using System.Text.Json.Nodes;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>
/// 2026-10-12 审查整改的加固行为测试：工作区写边界（H1）、事实层工具闭环（H2）、
/// 计划状态归一化（M1）、失败签名区分（M2）、委派超量拒绝（M4）、窗口校准器（M7）、
/// 二进制文件读取防护（M6）、web_fetch 内容类型分支辅助判断。
/// </summary>
public sealed class AgentHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-tests", "hardening-" + Guid.NewGuid().ToString("N"));

    public AgentHardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private WorkspaceInfo Workspace() => new() { Root = _dir, ProjectKinds = [] };

    private ToolContext Context(WorkspaceInfo? workspace = null) => new()
    {
        Workspace = workspace ?? Workspace(),
        Events = new EventBus(),
        Agent = new AgentConfig(),
    };

    // ------------------------------------------------- H1: writable path boundary

    [Fact]
    public void TryResolveWritablePath_AcceptsPathsInsideWorkspace()
    {
        var context = Context();
        Assert.True(context.TryResolveWritablePath("src/a.cs", out var resolved, out var error));
        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, "src", "a.cs")), resolved);

        // 工作区内绝对路径同样放行。
        var inside = Path.Combine(_dir, "nested", "file.md");
        Assert.True(context.TryResolveWritablePath(inside, out var resolved2, out error));
        Assert.Null(error);
        Assert.Equal(inside, resolved2);
    }

    [Fact]
    public void TryResolveWritablePath_RejectsPathsOutsideWorkspace()
    {
        var context = Context();

        // 工作区外绝对路径（父目录下的另一个目录）必须拒绝。
        var sibling = Path.Combine(Path.GetDirectoryName(_dir)!, "elsewhere", "evil.cs");
        Assert.False(context.TryResolveWritablePath(sibling, out _, out var error));
        Assert.NotNull(error);
        Assert.Contains("workspace", error, StringComparison.OrdinalIgnoreCase);

        // ../ 穿越同样拦截。
        Assert.False(context.TryResolveWritablePath("../../evil.cs", out _, out error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TryResolveWritablePath_RejectsSensitiveTargetsInsideWorkspace()
    {
        var context = Context();

        Assert.False(context.TryResolveWritablePath(".git/hooks/pre-commit", out _, out var gitError));
        Assert.Contains(".git", gitError);

        Assert.False(context.TryResolveWritablePath(".env", out _, out var envError));
        Assert.Contains(".env", envError);

        Assert.False(context.TryResolveWritablePath(".env.local", out _, out var envLocalError));
        Assert.Contains(".env", envLocalError);

        Assert.False(context.TryResolveWritablePath(".haoyue/audit/bash.jsonl", out _, out var auditError));
        Assert.Contains("audit", auditError);

        // 普通的 .haoyue 内文件（非 audit）不受影响。
        Assert.True(context.TryResolveWritablePath(".haoyue/notes.md", out _, out var haoyueError));
        Assert.Null(haoyueError);
    }

    [Fact]
    public async Task WriteFileTool_RejectsEscapingPath()
    {
        var tool = new WriteFileTool(new FilePromptProvider());
        var outside = Path.Combine(Path.GetTempPath(), "haoyue-escape-test.cs");
        var result = await tool.ExecuteAsync(
            new JsonObject { ["path"] = outside, ["content"] = "x" },
            Context(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("workspace", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(outside), "越界写入必须被拒绝，文件不应存在。");
    }

    // ------------------------------------------------- H2: fact_search / fact_forget

    [Fact]
    public async Task FactSearch_And_Forget_RoundTrip()
    {
        var store = new FactsStore();
        var entry = store.Add(_dir, "构建命令是 dotnet build", "build", confidence: 1.0);
        store.Add(_dir, "部署使用 docker compose", "deploy", confidence: 1.0);

        var workspace = Workspace();
        var search = new FactSearchTool(new FilePromptProvider());
        var searchResult = await search.ExecuteAsync(
            new JsonObject { ["query"] = "构建 build" }, Context(workspace), CancellationToken.None);

        Assert.True(searchResult.Success, searchResult.Output);
        Assert.Contains(entry.Id, searchResult.Output);
        Assert.Contains("fact_forget", searchResult.Output);

        var forget = new FactForgetTool(new FilePromptProvider());
        var forgetResult = await forget.ExecuteAsync(
            new JsonObject { ["id"] = entry.Id }, Context(workspace), CancellationToken.None);

        Assert.True(forgetResult.Success, forgetResult.Output);
        // 只删除了目标事实，其余事实保持完好。
        var remaining = store.ListActiveForRoot(_dir);
        Assert.Single(remaining);
        Assert.Equal("部署使用 docker compose", remaining[0].Content);

        var miss = await forget.ExecuteAsync(
            new JsonObject { ["id"] = "fact-nonexistent" }, Context(workspace), CancellationToken.None);
        Assert.False(miss.Success);
        Assert.Contains("fact_search", miss.Output);
    }

    // ------------------------------------------------- M1: plan status normalization

    [Fact]
    public void NormalizeStatus_MapsCommonSpellings_AndRejectsUnknown()
    {
        Assert.Equal("pending", PlanTool.NormalizeStatus("pending"));
        Assert.Equal("pending", PlanTool.NormalizeStatus("TODO"));
        Assert.Equal("in_progress", PlanTool.NormalizeStatus("in-progress"));
        Assert.Equal("in_progress", PlanTool.NormalizeStatus("进行中"));
        Assert.Equal("completed", PlanTool.NormalizeStatus("done ✅"));
        Assert.Equal("completed", PlanTool.NormalizeStatus("已完成。"));
        Assert.Equal("completed", PlanTool.NormalizeStatus("COMPLETED"));
        Assert.Null(PlanTool.NormalizeStatus("done✅-with-suffix"));
        Assert.Null(PlanTool.NormalizeStatus("fini"));
    }

    [Fact]
    public async Task PlanTool_RejectsUnknownStatus()
    {
        var tool = new PlanTool(new FilePromptProvider());
        var result = await tool.ExecuteAsync(
            new JsonObject
            {
                ["steps"] = new JsonArray(new JsonObject
                {
                    ["title"] = "第一步",
                    ["status"] = "fini", // 无法归一化的未知状态
                }),
            },
            Context(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("pending, in_progress, completed", result.Output);
    }

    [Fact]
    public async Task PlanTool_AcceptsLocalizedDone()
    {
        var tool = new PlanTool(new FilePromptProvider());
        var tracker = new TurnPlan();
        var context = new ToolContext
        {
            Workspace = Workspace(),
            Events = new EventBus(),
            Agent = new AgentConfig(),
            PlanTracker = tracker,
        };
        var result = await tool.ExecuteAsync(
            new JsonObject
            {
                ["steps"] = new JsonArray(new JsonObject
                {
                    ["title"] = "第一步",
                    ["status"] = "已完成",
                }),
            },
            context, CancellationToken.None);

        Assert.True(result.Success, result.Output);
        Assert.True(tracker.Snapshot().Steps.Single().Completed);
    }

    // ------------------------------------------------- M2: failure signature distinctness

    [Fact]
    public void Signature_DistinguishesDifferentMalformedArguments()
    {
        // 两次不同的畸形参数不允许归并到同一个 "{}" 签名。
        var a = ToolArguments.Signature("{\"cmd\": \"buil");
        var b = ToolArguments.Signature("{\"cmd\": \"test --f");
        Assert.NotEqual(a, b);

        // 可修复参数归一到修复后的 JSON。
        var ok = ToolArguments.Signature("{\"a\": 1}");
        Assert.Equal("{\"a\": 1}", ok);

        // Sanitize 保持 provider 回放语义：不可恢复参数仍是 "{}"。
        Assert.Equal("{}", ToolArguments.Sanitize("not json"));
        Assert.Equal("{}", ToolArguments.Sanitize(""));
    }

    [Fact]
    public void ParseDetailed_FlagsRepairedArguments()
    {
        var (_, _, clean) = ToolArguments.ParseDetailed("{\"a\": 1}");
        Assert.False(clean);

        var (obj, _, repaired) = ToolArguments.ParseDetailed("{\"path\": \"a.txt\", \"content\": \"hel");
        Assert.True(repaired);
        Assert.NotNull(obj);
        Assert.Equal("hel", obj!["content"]?.GetValue<string>());
    }

    // ------------------------------------------------- M4: delegate_tasks over-limit

    [Fact]
    public async Task DelegateTasksTool_RejectsMoreThanSixTasksExplicitly()
    {
        var tool = new DelegateTasksTool(new FilePromptProvider(), new UnusedDelegator());
        var tasks = Enumerable.Range(1, 7).Select(i => $"任务 {i}").ToArray();
        var result = await tool.ExecuteAsync(
            new JsonObject
            {
                ["tasks"] = new JsonArray([.. tasks.Select(t => JsonValue.Create(t))]),
            },
            Context(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("最多 6 个", result.Output);
    }

    // ------------------------------------------------- M7: context window calibrator

    [Fact]
    public void Calibrator_RecordsOverflow_AndAppliesCeiling()
    {
        ContextWindowCalibrator.Reset();
        Assert.Null(ContextWindowCalibrator.GetCeiling("prov", "model"));

        Assert.True(ContextWindowCalibrator.IsContextOverflow("\"error\": context_length_exceeded"));
        Assert.True(ContextWindowCalibrator.IsContextOverflow("prompt is too long: 200000 tokens > 128000"));
        Assert.False(ContextWindowCalibrator.IsContextOverflow("401 unauthorized"));
        Assert.False(ContextWindowCalibrator.IsContextOverflow(null));

        ContextWindowCalibrator.RecordOverflow("prov", "model", 100_000);
        var ceiling = ContextWindowCalibrator.GetCeiling("prov", "model");
        Assert.NotNull(ceiling);
        Assert.Equal(90_000, ceiling); // 预留 10% 余量

        // 更小的失败值才会继续收紧；更大的观测不放大上限。
        ContextWindowCalibrator.RecordOverflow("prov", "model", 150_000);
        Assert.Equal(90_000, ContextWindowCalibrator.GetCeiling("prov", "model"));
        ContextWindowCalibrator.RecordOverflow("prov", "model", 50_000);
        Assert.Equal(45_000, ContextWindowCalibrator.GetCeiling("prov", "model"));
        ContextWindowCalibrator.Reset();
        Assert.Null(ContextWindowCalibrator.GetCeiling("prov", "model"));
    }

    [Fact]
    public void WindowBudget_CeilingShrinksBudget_OnlyWhenBelowDeclaredWindow()
    {
        var model = new ModelConfig { Id = "m", ContextWindow = 128_000, MaxOutput = 8_192 };

        var baseline = ContextPlanner.WindowBudget(model, "sys");
        var calibrated = ContextPlanner.WindowBudget(model, "sys", 32_000);
        Assert.True(calibrated < baseline);
        Assert.Equal(32_000 - 8_192 - ContextPlanner.EstimateTokens("sys") - 1_500, calibrated);

        // 高于声明窗口的观测不影响预算。
        Assert.Equal(baseline, ContextPlanner.WindowBudget(model, "sys", 500_000));
        Assert.Equal(baseline, ContextPlanner.WindowBudget(model, "sys", null));
    }

    // ------------------------------------------------- M6: read_file binary guard

    [Fact]
    public async Task ReadFileTool_RejectsBinaryFiles()
    {
        var binaryPath = Path.Combine(_dir, "blob.bin");
        await File.WriteAllBytesAsync(binaryPath, [0x50, 0x4B, 0x00, 0x01, 0x02]);
        var tool = new ReadFileTool(new FilePromptProvider());
        var result = await tool.ExecuteAsync(
            new JsonObject { ["path"] = binaryPath },
            Context(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("binary", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadFileTool_StillReadsTextFiles()
    {
        var textPath = Path.Combine(_dir, "plain.txt");
        await File.WriteAllTextAsync(textPath, "hello\nworld\n");
        var tool = new ReadFileTool(new FilePromptProvider());
        var result = await tool.ExecuteAsync(
            new JsonObject { ["path"] = textPath },
            Context(), CancellationToken.None);

        Assert.True(result.Success, result.Output);
        Assert.Contains("hello", result.Output);
    }

    // ------------------------------------------------- L3: uniform tool audit trail

    [Fact]
    public void ToolAudit_Resolve_CoversMcpAndFileWrites_Only()
    {
        // MCP 工具无论 mutating 与否都进审计（外部系统调用本身值得留痕）。
        Assert.Equal(("mcp", "mcp__srv__send_email"),
            ToolAuditLog.Resolve(new StubTool("mcp__srv__send_email", mutating: false), new JsonObject()));

        // 文件写入按类别+目标落审计；目标取请求里的 path 参数。
        Assert.Equal(("file-write", "src/a.cs"),
            ToolAuditLog.Resolve(new WriteFileTool(new FilePromptProvider()),
                new JsonObject { ["path"] = "src/a.cs" }));
        Assert.Equal(("file-edit", "src/a.cs"),
            ToolAuditLog.Resolve(new EditFileTool(new FilePromptProvider()),
                new JsonObject { ["path"] = "src/a.cs" }));

        // 只读内建工具不落审计；bash 自有风险分级日志，不重复记录。
        Assert.Null(ToolAuditLog.Resolve(new StubTool("read_file", mutating: false), new JsonObject()));
        Assert.Null(ToolAuditLog.Resolve(new StubTool("bash", mutating: true), new JsonObject()));
        Assert.Null(ToolAuditLog.Resolve(
            new StubTool("delegate_task", mutating: true), new JsonObject()));
    }

    [Fact]
    public async Task ToolAudit_Append_WritesJsonlLine_PerCall()
    {
        var tool = new WriteFileTool(new FilePromptProvider());
        var arguments = new JsonObject { ["path"] = "notes.md", ["content"] = "hi" };
        var audited = ToolAuditLog.Resolve(tool, arguments);
        Assert.NotNull(audited);
        var (category, target) = audited.Value;

        ToolAuditLog.Append(Workspace(), new ToolAuditRecord(
            DateTime.UtcNow.ToString("o"), tool.Name, category, target, "ok", "写入完成", 12));

        var file = ToolAuditLog.AuditFile(Workspace());
        Assert.True(File.Exists(file));
        var line = File.ReadAllLines(file).Single();
        Assert.Contains("\"Tool\":\"write_file\"", line);
        Assert.Contains("\"Category\":\"file-write\"", line);
        Assert.Contains("\"Target\":\"notes.md\"", line);
        Assert.Contains("\"Outcome\":\"ok\"", line);

        // 失败的调用同样留痕（outcome=fail），两次调用两条记录。
        ToolAuditLog.Append(Workspace(), new ToolAuditRecord(
            DateTime.UtcNow.ToString("o"), "edit_file", "file-edit", "notes.md", "fail", "not found", 3));
        Assert.Equal(2, File.ReadAllLines(file).Length);
    }

    [Fact]
    public async Task ToolAudit_Append_FailureNeverBreaksExecution()
    {
        // 工作区根不可写时审计必须静默失败（真实场景：目录被删/权限收紧）。
        var ro = new WorkspaceInfo { Root = Path.Combine(_dir, "vanished"), ProjectKinds = [] };
        ToolAuditLog.Append(ro, new ToolAuditRecord(
            DateTime.UtcNow.ToString("o"), "write_file", "file-write", "a.txt", "ok", null, 1));
        // 不抛异常即通过。
        Assert.True(true);
        await Task.CompletedTask;
    }

    // ------------------------------------------------- helpers

    private sealed class StubTool(string name, bool mutating) : ITool
    {
        public string Name => name;
        public string Description => "stub";
        public JsonObject ParameterSchema => new();
        public bool Mutating => mutating;
        public string StatusLabel => name;

        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok("stub"));
    }

    private sealed class UnusedDelegator : IAgentDelegator
    {
        public Task<AgentTurnResult> RunSubTaskAsync(WorkspaceInfo workspace, string task, CancellationToken ct) =>
            throw new InvalidOperationException("guard should fire before delegation");

        public Task<IReadOnlyList<AgentTurnResult>> RunSubTasksAsync(
            WorkspaceInfo workspace, IReadOnlyList<string> tasks, CancellationToken ct) =>
            throw new InvalidOperationException("guard should fire before delegation");
    }
}
