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
    public const int ChunkSize = 6000;
    public const int MaxChunksPerFile = 200;

    public static string ExtractText(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return extension switch
            {
                ".docx" => ReadDocx(path),
                ".xlsx" or ".xlsm" => ReadXlsx(path),
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

    /// <summary>True when the file has no extension marker of a document format we can parse.</summary>
    public static bool LooksLikeTextFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".docx" or ".xlsx" or ".xlsm" or ".pdf") return extension != ".pdf";
        return true;
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
    /// Splits extracted text into chunks that fit a knowledge entry (8000 char limit,
    /// 6000 budget per chunk). Blank-line paragraph groups are merged greedily; a
    /// single paragraph longer than the budget is hard-split.
    /// </summary>
    public static IReadOnlyList<string> SplitChunks(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Trim();
        if (normalized.Length == 0) return [];

        var paragraphs = normalized.Split("\n\n", StringSplitOptions.TrimEntries)
            .Where(paragraph => paragraph.Length > 0)
            .ToList();
        if (paragraphs.Count <= 1 && normalized.Length > ChunkSize)
            paragraphs = [.. normalized.Split('\n', StringSplitOptions.TrimEntries).Where(line => line.Length > 0)];

        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Length > ChunkSize)
            {
                FlushChunk(chunks, current);
                for (var offset = 0; offset < paragraph.Length; offset += ChunkSize)
                {
                    var length = Math.Min(ChunkSize, paragraph.Length - offset);
                    chunks.Add(paragraph.Substring(offset, length));
                    if (chunks.Count >= MaxChunksPerFile) return [.. chunks];
                }
                continue;
            }

            if (current.Length > 0 && current.Length + paragraph.Length + 2 > ChunkSize)
                FlushChunk(chunks, current);
            if (current.Length > 0) current.Append("\n\n");
            current.Append(paragraph);
            if (current.Length >= ChunkSize)
            {
                FlushChunk(chunks, current);
                if (chunks.Count >= MaxChunksPerFile) return [.. chunks];
            }
        }
        FlushChunk(chunks, current);
        return chunks;
    }

    private static void FlushChunk(List<string> chunks, StringBuilder current)
    {
        if (current.Length == 0) return;
        chunks.Add(current.ToString());
        current.Clear();
    }
}
