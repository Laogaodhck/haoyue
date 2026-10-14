using System.Diagnostics;
using System.Text.Json.Nodes;
using Haoyue.Runtime.ComputerUse.SystemControl;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Prompts;

namespace Haoyue.Runtime.Tools.Builtin;

/// <summary>
/// 文档创作链（S3）：把 Markdown / 纯文本内容渲染为 docx / xlsx / pptx 产物。
/// 实现上复用仓库自带的 doc_toolkit（纯 Python、离线）：工具以子进程方式调用
/// <c>python -m doc_toolkit convert</c>，产物统一落 <c>.haoyue/outputs/documents/</c>。
/// Python 缺失或找不到 doc_toolkit 包时工具从模型工具视图中隐藏（见 Agent.Prompting），
/// 与 image_generate 的「不可用即不可见」策略一致。
/// </summary>
public sealed class DocRenderTool(IPromptProvider prompts, IConfigStore configStore) : BuiltinTool(prompts)
{
    public override string Name => "doc_render";
    public override string StatusLabel => "Rendering document";
    public override bool Mutating => true;

    /// <summary>doc_toolkit 支持的渲染目标（docx 由 DocxRenderer、xlsx 由 XlsxRenderer、
    /// pptx 由 PptxRenderer 承载；md/txt/pdf 由既有渲染器覆盖，但文档生成场景收窄到三件套）。</summary>
    public static readonly string[] SupportedFormats = ["docx", "xlsx", "pptx"];

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("content", ToolSchema.String("Markdown source to render. Headings become document sections / slide titles; markdown tables become real spreadsheet rows for xlsx."), false),
        ("source_path", ToolSchema.String("Existing markdown/text file to render instead of inline content (absolute or workspace-relative)."), false),
        ("format", ToolSchema.String($"Target format: {string.Join(" | ", SupportedFormats)}."), true),
        ("file_name", ToolSchema.String("Optional output file name without extension; a timestamped default is used otherwise."), false));

    /// <summary>
    /// 探测 doc_toolkit 包目录：显式配置 → 环境变量 → 可执行文件相对目录 →
    /// 当前目录。命中判定 = 目录下存在 <c>__init__.py</c>。返回包目录本身
    /// （其父目录将作为子进程工作目录，使 <c>python -m doc_toolkit</c> 可导入）。
    /// </summary>
    public static string? ResolveToolkitPath(DocRenderConfig config)
    {
        foreach (var candidate in new[]
                 {
                     config.ToolkitPath,
                     Environment.GetEnvironmentVariable("HAOYUE_DOC_TOOLKIT_PATH"),
                 })
        {
            if (IsToolkitRoot(candidate)) return candidate;
        }

        // 可执行文件相对：开发态为 bin/.../，发布态为 resources/runtime/ 的上层的 doc_toolkit 副本。
        foreach (var root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var relative = Path.Combine(root, "doc_toolkit");
            if (IsToolkitRoot(relative)) return relative;
        }
        return null;
    }

    /// <summary>工具可用性：总开关开启 + Python 可探测 + doc_toolkit 可定位。
    /// Agent.Prompting 据此把不可用的 doc_render 从工具视图移除。</summary>
    public static bool IsAvailable(DocRenderConfig config) =>
        config.Enabled
        && PythonLocator.Probe(string.IsNullOrWhiteSpace(config.PythonPath) ? null : config.PythonPath) is not null
        && ResolveToolkitPath(config) is not null;

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        // 参数校验先于可用性检查：输入错误与部署环境无关，反馈始终确定。
        var format = (GetString(arguments, "format") ?? "").Trim().ToLowerInvariant();
        if (!SupportedFormats.Contains(format))
            return ToolResult.Fail($"Unknown format '{format}'. Supported: {string.Join(", ", SupportedFormats)}.");

        var content = GetString(arguments, "content");
        var sourceArg = GetString(arguments, "source_path");
        if (string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(sourceArg))
            return ToolResult.Fail("Provide either 'content' (inline markdown) or 'source_path'.");

        var config = configStore.Config.DocRender;
        var python = PythonLocator.Probe(string.IsNullOrWhiteSpace(config.PythonPath) ? null : config.PythonPath);
        var toolkit = ResolveToolkitPath(config);
        if (python is null || toolkit is null)
            return ToolResult.Fail(
                "doc_render is unavailable: Python or the doc_toolkit package was not found. " +
                "Install Python, set agent.docRender.toolkitPath to the doc_toolkit package directory " +
                "(or set HAOYUE_DOC_TOOLKIT_PATH), and ensure python-docx / openpyxl / python-pptx are installed.");

        // 内容来源：内联 content 优先，其次 source_path。临时文件在 finally 统一清理。
        string sourcePath;
        var isTempSource = false;
        if (!string.IsNullOrWhiteSpace(content))
        {
            sourcePath = Path.Combine(Path.GetTempPath(), $"haoyue-docrender-{Guid.NewGuid():N}.md");
            await File.WriteAllTextAsync(sourcePath, content, ct).ConfigureAwait(false);
            isTempSource = true;
        }
        else
        {
            if (!context.TryResolveReadablePath(sourceArg!, out sourcePath, out var sourceError))
                return ToolResult.Fail(sourceError!);
            if (!File.Exists(sourcePath))
                return ToolResult.Fail($"Source file not found: {sourcePath}");
        }

        try
        {
            if (context.Workspace is null)
                return ToolResult.Fail("doc_render requires a workspace to write the artifact into.");

            var outputsDir = Path.Combine(context.Workspace.Root, ".haoyue", "outputs", "documents");
            Directory.CreateDirectory(outputsDir);
            var customName = GetString(arguments, "file_name");
            var output = Sanitize(customName) is { Length: > 0 } safeName
                ? Path.Combine(outputsDir, $"{safeName}.{format}")
                : Path.Combine(outputsDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.{format}");

            // python -m doc_toolkit：工作目录指向包父目录保证可导入；UTF-8 输出固定，
            // 避免 Windows 默认代码页把中文产物路径/错误消息打碎。
            var psi = new ProcessStartInfo
            {
                FileName = python.Install.Executable,
                Arguments = $"-m doc_toolkit convert \"{sourcePath}\" --to {format} --out \"{output}\"",
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(toolkit)),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONPATH"] = Path.GetDirectoryName(Path.GetFullPath(toolkit));

            using var process = Process.Start(psi);
            if (process is null)
                return ToolResult.Fail("Failed to start the Python interpreter for doc_render.");

            var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            if (process.ExitCode != 0 || !File.Exists(output))
                return ToolResult.Fail(
                    $"doc_toolkit exited with code {process.ExitCode}. {stderr.Trim()}");

            var size = new FileInfo(output).Length;
            var outputMessage =
                $"Document rendered: {output} ({size:N0} bytes). This absolute path can be opened with the system default application or attached to a reply.";
            return new ToolResult
            {
                Success = true,
                Output = outputMessage,
                Summary = $"渲染文档：{Path.GetFileName(output)}",
                FilePath = output,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Document rendering failed: {ex.Message}");
        }
        finally
        {
            // 内联内容走临时文件，渲染结束即清理；source_path 是用户文件不动。
            if (isTempSource)
            {
                try { File.Delete(sourcePath); } catch { }
            }
        }
    }

    private static bool IsToolkitRoot(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "__init__.py"));

    private static string? Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }
}
