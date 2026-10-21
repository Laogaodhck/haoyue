using System.Text;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Tools.Builtin;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// 知识库 PDF 导入桥（4.2）：桥未注册 → 明确报错；桥可用时文本 PDF 提取成功、
/// 损坏 PDF 明确报错。扫描件 OCR 路径依赖 RapidOCR 模型，不在自动化范围
///（doc_toolkit 的 --ocr 语义已在 verify_ocr.py 覆盖）。
/// </summary>
public sealed class KnowledgePdfImportTests
{
    [Fact]
    public void Pdf_WithoutBridge_FailsWithActionableMessage()
    {
        // 桥未注册（CLI 最小环境 / 未装配 runtime）：不再有「请先转为文本」的含糊路径。
        var original = KnowledgeImport.PdfTextExtractor;
        KnowledgeImport.PdfTextExtractor = null;
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"haoyue-pdf-{Guid.NewGuid():N}.pdf");
            File.WriteAllBytes(path, [0x25, 0x50, 0x44, 0x46]); // "%PDF"
            try
            {
                var ex = Assert.Throws<KnowledgeImportException>(() => KnowledgeImport.ExtractText(path));
                Assert.Contains("PDF 导入不可用", ex.Message, StringComparison.Ordinal);
                Assert.Contains("doc_toolkit", ex.Message, StringComparison.Ordinal);
            }
            finally
            {
                File.Delete(path);
            }
        }
        finally
        {
            KnowledgeImport.PdfTextExtractor = original;
        }
    }

    [Fact]
    public void Pdf_TextLayer_ExtractsViaDocToolkit()
    {
        if (!DocToolkitBridge.IsAvailable(new DocRenderConfig()))
            return; // 本机无 Python/pypdf/doc_toolkit：桥由其他用例锁定语义

        var pdf = Path.Combine(Path.GetTempPath(), $"haoyue-pdf-ok-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(pdf, MinimalPdf("Hello Haoyue"));
        var original = KnowledgeImport.PdfTextExtractor;
        KnowledgeImport.PdfTextExtractor = path => DocToolkitBridge.ExtractText(path, new DocRenderConfig());
        try
        {
            var text = KnowledgeImport.ExtractText(pdf);
            Assert.Contains("Hello Haoyue", text, StringComparison.Ordinal);
        }
        catch (KnowledgeImportException ex) when (ex.Message.Contains("pypdf", StringComparison.Ordinal))
        {
            // 运行环境 Python 在但缺 pypdf：依赖指引已明确，语义等价于「桥不可用」。
            return;
        }
        finally
        {
            KnowledgeImport.PdfTextExtractor = original;
            File.Delete(pdf);
        }
    }

    [Fact]
    public void Pdf_CorruptedFile_FailsWithExplicitMessage()
    {
        if (!DocToolkitBridge.IsAvailable(new DocRenderConfig()))
            return;

        var pdf = Path.Combine(Path.GetTempPath(), $"haoyue-pdf-bad-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(pdf, "%PDF-1.4\nthis is not a real pdf body\n"u8.ToArray());
        var original = KnowledgeImport.PdfTextExtractor;
        KnowledgeImport.PdfTextExtractor = path => DocToolkitBridge.ExtractText(path, new DocRenderConfig());
        try
        {
            var ex = Assert.Throws<KnowledgeImportException>(() => KnowledgeImport.ExtractText(pdf));
            Assert.Contains("PDF 提取失败", ex.Message, StringComparison.Ordinal);
            // 错误消息必须带文件名与 doc_toolkit 的原因（不是空泛的失败）。
            Assert.Contains(Path.GetFileName(pdf), ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            KnowledgeImport.PdfTextExtractor = original;
            File.Delete(pdf);
        }
    }

    /// <summary>
    /// 生成一个含单页文本层的最小合法 PDF（xref 偏移动态计算）。
    /// </summary>
    private static byte[] MinimalPdf(string text)
    {
        var content = $"BT /F1 24 Tf 72 700 Td ({text}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var buffer = new StringBuilder();
        buffer.Append("%PDF-1.4\n");
        var offsets = new int[objects.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = Encoding.ASCII.GetByteCount(buffer.ToString());
            buffer.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xrefOffset = Encoding.ASCII.GetByteCount(buffer.ToString());
        buffer.Append($"xref\n0 {objects.Length + 1}\n");
        buffer.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
            buffer.Append($"{offset:D10} 00000 n \n");
        buffer.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(buffer.ToString());
    }
}
