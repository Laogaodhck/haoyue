using System.Diagnostics;
using System.Text.Json.Nodes;
using Haoyue.Runtime.ComputerUse.SystemControl;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>
/// S3 文档创作链：doc_render 工具的路径解析、可用性门控与真实渲染产物。
/// 真实渲染用例在「Python + doc_toolkit + 三件套依赖」齐备的环境才执行，
/// 其余环境静默跳过（与 CI 无 Python 的事实兼容）。
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public class DocRenderToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-docrender-" + Guid.NewGuid().ToString("N"));

    public DocRenderToolTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ResolveToolkitPath_ExplicitConfigPath_HitsPackageRoot()
    {
        var fakeToolkit = Path.Combine(_dir, "doc_toolkit");
        Directory.CreateDirectory(fakeToolkit);
        File.WriteAllText(Path.Combine(fakeToolkit, "__init__.py"), "");

        var resolved = DocRenderTool.ResolveToolkitPath(new DocRenderConfig { ToolkitPath = fakeToolkit });

        Assert.Equal(fakeToolkit, resolved);
    }

    [Fact]
    public void ResolveToolkitPath_NothingConfigured_ReturnsNull()
    {
        // 显式路径无效 + 环境变量清空 + 可执行文件相对/当前目录下无 doc_toolkit 包。
        var originalEnv = Environment.GetEnvironmentVariable("HAOYUE_DOC_TOOLKIT_PATH");
        var originalCwd = Environment.CurrentDirectory;
        try
        {
            Environment.SetEnvironmentVariable("HAOYUE_DOC_TOOLKIT_PATH", null);
            Environment.CurrentDirectory = _dir; // 空目录，无 doc_toolkit

            var resolved = DocRenderTool.ResolveToolkitPath(
                new DocRenderConfig { ToolkitPath = Path.Combine(_dir, "ghost") });

            Assert.Null(resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HAOYUE_DOC_TOOLKIT_PATH", originalEnv);
            Environment.CurrentDirectory = originalCwd;
        }
    }

    [Fact]
    public async Task Execute_WithoutToolkit_FailsWithGuidance()
    {
        var originalEnv = Environment.GetEnvironmentVariable("HAOYUE_DOC_TOOLKIT_PATH");
        var originalCwd = Environment.CurrentDirectory;
        try
        {
            Environment.SetEnvironmentVariable("HAOYUE_DOC_TOOLKIT_PATH", null);
            Environment.CurrentDirectory = _dir;

            var config = new HaoyueConfig();
            config.DocRender.ToolkitPath = Path.Combine(_dir, "ghost");
            var tool = new DocRenderTool(new FilePromptProvider(), MakeConfigStore(config));
            var result = await tool.ExecuteAsync(
                new JsonObject { ["content"] = "# t", ["format"] = "docx" }, MakeContext(), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("doc_toolkit", result.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HAOYUE_DOC_TOOLKIT_PATH", originalEnv);
            Environment.CurrentDirectory = originalCwd;
        }
    }

    [Fact]
    public async Task Execute_UnknownFormat_FailsWithSupportedList()
    {
        var config = new HaoyueConfig();
        var tool = new DocRenderTool(new FilePromptProvider(), MakeConfigStore(config));
        var result = await tool.ExecuteAsync(
            new JsonObject { ["content"] = "# t", ["format"] = "exe" }, MakeContext(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("docx", result.Output);
    }

    [Fact]
    public async Task Execute_MissingBothContentAndSource_Fails()
    {
        var tool = new DocRenderTool(new FilePromptProvider(), MakeConfigStore(new HaoyueConfig()));
        var result = await tool.ExecuteAsync(
            new JsonObject { ["format"] = "docx" }, MakeContext(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("content", result.Output);
    }

    [Fact]
    public async Task Execute_RendersDocxXlsxPptxIntoOutputs_WhenRuntimeAvailable()
    {
        var toolkit = FindRepoToolkit();
        var python = PythonLocator.Probe();
        if (toolkit is null || python is null || !await DependenciesPresentAsync(python))
            return; // 环境不具备：静默跳过，不判失败

        foreach (var format in new[] { "docx", "xlsx", "pptx" })
        {
            var config = new HaoyueConfig();
            config.DocRender.ToolkitPath = toolkit;
            var tool = new DocRenderTool(new FilePromptProvider(), MakeConfigStore(config));

            var content = format == "xlsx"
                ? "# 数据表\n\n| 指标 | 基线 | 优化后 |\n|---|---|---|\n| 解码 tok/s | 7.6 | 84.9 |"
                : "# 季度评审\n\n- 事件溯源重构完成\n\n> 全部单测通过。";
            var result = await tool.ExecuteAsync(
                new JsonObject { ["content"] = content, ["format"] = format, ["file_name"] = $"s3-test-{format}" },
                MakeContext(), CancellationToken.None);

            Assert.True(result.Success, $"{format}: {result.Output}");
            Assert.NotNull(result.FilePath);
            var outputsDir = Path.Combine(_dir, ".haoyue", "outputs", "documents");
            var artifact = Path.Combine(outputsDir, $"s3-test-{format}.{format}");
            Assert.True(File.Exists(artifact), $"{format} 产物缺失：{artifact}");
            Assert.True(new FileInfo(artifact).Length > 0, $"{format} 产物为空");
            Assert.Equal(artifact, result.FilePath);
        }
    }

    /// <summary>定位仓库内 doc_toolkit 包（从测试二进制目录向上找 Haoyue.slnx）。</summary>
    private static string? FindRepoToolkit()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (!File.Exists(Path.Combine(current.FullName, "Haoyue.slnx"))) continue;
            var candidate = Path.Combine(current.FullName, "doc_toolkit");
            return File.Exists(Path.Combine(candidate, "__init__.py")) ? candidate : null;
        }
        return null;
    }

    /// <summary>探测解释器是否已装齐三件套依赖（一次性子进程，失败即视为环境不具备）。</summary>
    private static async Task<bool> DependenciesPresentAsync(PythonProbeResult python)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = python.Install.Executable,
                Arguments = "-c \"import docx, openpyxl, pptx\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;
            await p.WaitForExitAsync(CancellationToken.None);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private ToolContext MakeContext() => new()
    {
        Workspace = new WorkspaceInfo { Root = _dir, ProjectKinds = [] },
        Events = new EventBus(),
        Agent = new AgentConfig(),
    };

    /// <summary>把配置写入临时文件，让 ConfigStore 从盘上加载（Config 属性只读）。</summary>
    private ConfigStore MakeConfigStore(HaoyueConfig config)
    {
        var file = Path.Combine(_dir, $"config-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(
            config, Haoyue.Runtime.Configuration.HaoyueJsonContext.Default.HaoyueConfig));
        return new ConfigStore(file, Path.Combine(_dir, $"state-{Guid.NewGuid():N}.json"));
    }
}
