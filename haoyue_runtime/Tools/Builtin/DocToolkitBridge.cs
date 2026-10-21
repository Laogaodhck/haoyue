using System.Diagnostics;
using Haoyue.Runtime.ComputerUse.SystemControl;
using Haoyue.Runtime.Configuration;

namespace Haoyue.Runtime.Tools.Builtin;

/// <summary>
/// 知识库 PDF 导入桥（4.2）：经仓库自带的 doc_toolkit（<c>python -m doc_toolkit text</c>）
/// 子进程提取 PDF 文本。文本层 PDF 走 pypdf；扫描件自动回退 OCR（--ocr 让 doc_toolkit
/// 仅在无文本层时逐页 RapidOCR，文本层 PDF 零额外开销）。探测与子进程模式完全照搬
/// <see cref="DocRenderTool"/> 的已验证实现；Python 或 doc_toolkit 缺失时以明确错误
/// 上抛（由 KnowledgeImport 包成 KnowledgeImportException），绝不静默降级。
/// </summary>
public static class DocToolkitBridge
{
    /// <summary>桥可用性：Python 可探测 + doc_toolkit 包可定位。</summary>
    public static bool IsAvailable(DocRenderConfig config) =>
        PythonLocator.Probe(string.IsNullOrWhiteSpace(config.PythonPath) ? null : config.PythonPath) is not null
        && DocRenderTool.ResolveToolkitPath(config) is not null;

    /// <summary>
    /// 提取 PDF 文本。失败抛 <see cref="InvalidOperationException"/>（消息可直接展示给
    /// 用户：依赖缺失给安装指引，文件问题给文件级原因）。
    /// </summary>
    public static string ExtractText(string pdfPath, DocRenderConfig config)
    {
        var python = PythonLocator.Probe(string.IsNullOrWhiteSpace(config.PythonPath) ? null : config.PythonPath);
        var toolkit = DocRenderTool.ResolveToolkitPath(config);
        if (python is null || toolkit is null)
        {
            throw new InvalidOperationException(
                "PDF 导入需要 doc_toolkit 环境：请安装 Python 与 pypdf（pip install pypdf），"
                + "或将 agent.docRender.toolkitPath 指向 doc_toolkit 包目录（或设置环境变量 HAOYUE_DOC_TOOLKIT_PATH）。");
        }

        // --ocr 是「无文本层时才回退」的语义（doc_toolkit.parse 的 ocr_fallback）：
        // 文本层 PDF 快速提取，扫描件自动逐页 OCR，一次调用覆盖两类文件。
        var psi = new ProcessStartInfo
        {
            FileName = python.Install.Executable,
            Arguments = $"-m doc_toolkit text \"{pdfPath}\" --ocr",
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

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"PDF 提取进程启动失败：{ex.Message}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        // OCR 全文档可能要几分钟（逐页识别），给足窗口；调用方持有导入级的取消语义。
        process.WaitForExit(TimeSpan.FromMinutes(10));

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            var detail = stderr.Trim();
            if (detail.Length == 0) detail = stdout.Trim();
            throw new InvalidOperationException(
                $"PDF 提取失败（{Path.GetFileName(pdfPath)}）：{(detail.Length > 0 ? detail : $"退出码 {process.ExitCode}")}");
        }
        return stdout;
    }
}
