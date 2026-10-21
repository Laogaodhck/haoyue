using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Haoyue.Runtime.Data;

public sealed class KnowledgeImportException(string message) : Exception(message);

/// <summary>
/// Extracts plain text from user files (txt/md/csv/code via UTF-8 with GBK fallback,
/// .docx and .xlsx via the built-in ZipArchive) and splits it into knowledge-entry
/// sized chunks. Imported entries keep a stable title derived from the file name, so
/// re-importing the same file upserts instead of duplicating.
/// </summary>
public static class KnowledgeImport
{
    public const long MaxFileBytes = 10 * 1024 * 1024;
    /// <summary>单个 chunk 硬上限（条目 8000 字符预算内）。也是超长无结构块的切片长度。</summary>
    public const int ChunkSize = 6000;
    /// <summary>目标 chunk 长度（结构感知合并的中值区间 3000-4000）——更小的块让检索
    /// 命中更聚焦，同时保持代码围栏/表格/列表组等语义单元完整。</summary>
    public const int TargetChunkSize = 3500;
    /// <summary>相邻 chunk 的重叠字符数：检索命中的片段带着上文，回答引用更完整。</summary>
    public const int ChunkOverlap = 200;
    public const int MaxChunksPerFile = 200;

    /// <summary>
    /// PDF 文本提取桥（4.2）：由 runtime 装配时注入（<c>DocToolkitBridge.ExtractText</c>，
    /// 经 doc_toolkit 子进程）。未注入（CLI 最小环境 / 测试）时 PDF 导入报明确错误——
    /// 「请先转为文本」的含糊路径已废除。
    /// </summary>
    public static Func<string, string>? PdfTextExtractor { get; set; }

    public static string ExtractText(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return extension switch
            {
                ".docx" => ReadDocx(path),
                ".xlsx" or ".xlsm" => ReadXlsx(path),
                ".pdf" => ReadPdf(path),
                _ => ReadTextFile(path),
            };
        }
        catch (KnowledgeImportException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException)
        {
            throw new KnowledgeImportException($"无法读取文件 {Path.GetFileName(path)}：{ex.Message}");
        }
    }

    private static string ReadPdf(string path)
    {
        if (PdfTextExtractor is null)
        {
            throw new KnowledgeImportException(
                $"PDF 导入不可用：{Path.GetFileName(path)} 需要文档工具链（Python + doc_toolkit，含 pypdf）。"
                + "请安装 Python 后重试，或将该文件先导出为文本/docx 格式。");
        }
        try
        {
            return PdfTextExtractor(path);
        }
        catch (KnowledgeImportException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new KnowledgeImportException($"PDF 提取失败：{ex.Message}");
        }
    }

    private static string ReadTextFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > MaxFileBytes)
            throw new KnowledgeImportException($"文件过大：{Path.GetFileName(path)} 超过 10 MB 限制");

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Not valid UTF-8 — on zh-CN machines that almost always means GBK/ANSI.
            text = GetAnsiEncoding().GetString(bytes);
        }

        text = text.TrimStart('\uFEFF');
        if (text.Contains('\0'))
            throw new KnowledgeImportException(
                $"不支持的文件格式：{Path.GetFileName(path)} 看起来是二进制文件，请先转为文本、docx 或 xlsx");
        return text;
    }

    private static Encoding GetAnsiEncoding()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
        try { return Encoding.GetEncoding(936); }
        catch { return Encoding.Latin1; }
    }

    private static string ReadDocx(string path)
    {
        using var archive = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read);
        var document = archive.GetEntry("word/document.xml")
            ?? throw new KnowledgeImportException($"无效的 docx 文件：{Path.GetFileName(path)} 缺少正文");

        var sb = new StringBuilder();
        using var reader = XmlReader.Create(document.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
                sb.Append(reader.ReadString());
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "br")
                sb.Append('\n');
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "p")
                sb.Append("\n\n");
        }
        return sb.ToString();
    }

    private static string ReadXlsx(string path)
    {
        using var archive = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read);
        var sharedStrings = ReadSharedStrings(archive);

        var sheets = archive.Entries
            .Where(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                            && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sheets.Count == 0)
            throw new KnowledgeImportException($"无效的 xlsx 文件：{Path.GetFileName(path)} 缺少工作表");

        var sb = new StringBuilder();
        foreach (var sheet in sheets)
        {
            using var reader = XmlReader.Create(sheet.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            var cell = new StringBuilder();
            var cellType = "";
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c")
                {
                    cell.Clear();
                    cellType = reader.GetAttribute("t") ?? "";
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "v")
                {
                    // ReadString stops on </v>; ReadElementContentAsString would land past it
                    // and the next Read() would skip </c>, losing the cell content.
                    var value = reader.ReadString();
                    cell.Append(cellType == "s" && int.TryParse(value, out var index) && index < sharedStrings.Count
                        ? sharedStrings[index]
                        : value);
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "is")
                {
                    // Inline string cells nest <t> directly; the generic "t" branch below reads them.
                    cell.Append(ReadInlineString(reader));
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "c")
                {
                    sb.Append(cell).Append('\t');
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row")
                {
                    sb.Append('\n');
                }
            }
            sb.Append("\n\n");
        }
        return sb.ToString();
    }

    private static string ReadInlineString(XmlReader reader)
    {
        var text = new StringBuilder();
        var depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
                text.Append(reader.ReadString());
            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "is" && reader.Depth <= depth)
                break;
        }
        return text.ToString();
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];

        var strings = new List<string>();
        using var reader = XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        var current = new StringBuilder();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
                current.Clear();
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
                current.Append(reader.ReadString());
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "si")
                strings.Add(current.ToString());
        }
        return strings;
    }

    /// <summary>
    /// 结构感知分块（4.1）：代码围栏/表格行组/列表组是完整语义单元，优先整体进块；
    /// 标题行倾向开启新块（新主题）。普通段落按 <see cref="TargetChunkSize"/> 贪心
    /// 合并，相邻块带 <see cref="ChunkOverlap"/> 重叠；只有无结构边界的超长单块才
    /// 按 <see cref="ChunkSize"/> 硬切（步进预留重叠）。仅新导入生效——既有库内
    /// chunk 不重建，重导入（upsert 同名条目）自然走新结构。
    /// </summary>
    public static IReadOnlyList<string> SplitChunks(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Trim();
        if (normalized.Length == 0) return [];

        var chunks = new List<string>();
        var current = new StringBuilder();
        string? overlapPrefix = null;

        void Flush()
        {
            if (current.Length == 0) return;
            var body = current.ToString();
            current.Clear();
            // 重叠前缀只加给「续块」：以标题开头的块是新主题，带上一块尾巴反而污染。
            // 加前缀后仍须守 ChunkSize 硬上限，超了就放弃重叠保完整性。
            if (overlapPrefix is not null && overlapPrefix.Length > 0
                && !IsHeadingStart(body)
                && overlapPrefix.Length + 1 + body.Length <= ChunkSize)
            {
                body = overlapPrefix + "\n" + body;
            }
            chunks.Add(body);
            overlapPrefix = TakeOverlap(body);
        }

        foreach (var atom in ScanAtoms(normalized))
        {
            if (chunks.Count >= MaxChunksPerFile) return [.. chunks];
            if (atom.Length > ChunkSize)
            {
                // 无结构边界的超长单块（巨型 base64、超长表格单元格堆叠等）：硬切，
                // 步进预留 ChunkOverlap 让相邻切片天然重叠。硬切重置重叠上下文。
                Flush();
                var step = ChunkSize - ChunkOverlap;
                for (var offset = 0; offset < atom.Length && chunks.Count < MaxChunksPerFile; offset += step)
                {
                    chunks.Add(atom.Substring(offset, Math.Min(ChunkSize, atom.Length - offset)));
                }
                overlapPrefix = null;
                continue;
            }

            // 目标区间驱动合并：next 加进 current 会超过目标长度时先 flush，
            // 保证常规 chunk 落在 3000-4000 区间（远小于 6000 硬上限）。
            // 标题强制开新块——标题是主题边界，被并入上一块尾部会同时污染
            // 上一块的检索粒度与本块的重叠前缀。
            if (IsHeadingStart(atom) || (current.Length > 0 && current.Length + 2 + atom.Length > TargetChunkSize))
                Flush();
            if (current.Length > 0) current.Append("\n\n");
            current.Append(atom);
        }
        Flush();
        return chunks;
    }

    /// <summary>ATX 标题行（1-6 个 # 后跟空格）。标题是主题边界：开新块、不接重叠前缀。</summary>
    private static bool IsHeadingStart(string text)
    {
        var hashes = 0;
        while (hashes < text.Length && text[hashes] == '#') hashes++;
        return hashes is >= 1 and <= 6 && hashes < text.Length && text[hashes] == ' ';
    }

    /// <summary>上一 chunk 的尾部 ≤200 字符，且在行边界对齐（不切词中间）。</summary>
    private static string TakeOverlap(string chunk)
    {
        if (chunk.Length <= ChunkOverlap) return chunk;
        var tail = chunk[^ChunkOverlap..];
        var newline = tail.IndexOf('\n');
        return newline >= 0 ? tail[(newline + 1)..] : tail;
    }

    private enum AtomKind { Paragraph, Heading, Table, List, Fence }

    /// <summary>
    /// 把文本扫描为不可断的原子单元序列：代码围栏（``` / ~~~ 配对，未闭合收到文末）、
    /// 标题行、连续表格行组（| 开头）、连续列表组（-/*/+/数字. 开头）、普通段落。
    /// 组间空行只作分隔，重组时以 "\n\n" 恢复。
    /// </summary>
    private static IReadOnlyList<string> ScanAtoms(string text)
    {
        var atoms = new List<string>();
        var buffer = new List<string>();
        var kind = AtomKind.Paragraph;
        var fenceMarker = '\0';

        void FlushBuffer()
        {
            if (buffer.Count == 0) return;
            atoms.Add(string.Join('\n', buffer));
            buffer.Clear();
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.TrimStart();

            if (fenceMarker != '\0')
            {
                // 围栏内：一切行原样收进，直到配对闭合标记（同一字符开头）。
                buffer.Add(line);
                if (trimmed.StartsWith(fenceMarker) && trimmed.TrimEnd().Length >= 3)
                    fenceMarker = '\0';
                continue;
            }

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                FlushBuffer();
                kind = AtomKind.Fence;
                buffer.Add(line);
                fenceMarker = trimmed[0];
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushBuffer();
                kind = AtomKind.Paragraph;
                continue;
            }

            if (IsHeadingStart(trimmed))
            {
                FlushBuffer();
                kind = AtomKind.Heading;
                buffer.Add(line);
                FlushBuffer();
                kind = AtomKind.Paragraph;
                continue;
            }

            var lineKind = ClassifyLine(trimmed);
            if (lineKind != kind && kind is AtomKind.Table or AtomKind.List)
            {
                FlushBuffer();
            }
            else if (lineKind != kind && kind is AtomKind.Paragraph && lineKind is AtomKind.Table or AtomKind.List)
            {
                FlushBuffer();
            }
            kind = lineKind;
            buffer.Add(line);
        }
        FlushBuffer();
        return atoms;
    }

    /// <summary>表格行（| 开头）与列表行（-/*/+/数字. 后跟空格）识别；其余算段落行。</summary>
    private static AtomKind ClassifyLine(string trimmed)
    {
        if (trimmed.StartsWith('|')) return AtomKind.Table;
        if (trimmed.Length >= 2 && (trimmed[0] is '-' or '*' or '+') && trimmed[1] == ' ') return AtomKind.List;
        // 有序列表："1. " / "1) "
        var digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits])) digits++;
        if (digits is > 0 and <= 4 && digits + 1 < trimmed.Length
            && trimmed[digits] is '.' or ')' && trimmed[digits + 1] == ' ') return AtomKind.List;
        return AtomKind.Paragraph;
    }
}
