namespace Haoyue.Runtime.Data;

/// <summary>
/// Shared file → knowledge-entry pipeline used by both the daemon (knowledge.import)
/// and the CLI (haoyue knowledge import): validates the path, extracts text, splits
/// chunks and saves them under the stable 「文件名 · 第N/M部分」 title convention with
/// 导入,&lt;ext&gt; tags — so re-importing the same file upserts instead of duplicating.
/// </summary>
public static class KnowledgeIngest
{
    public static (string FileName, int Entries) ImportFile(KnowledgeStore store, string scope, string rawPath)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(rawPath.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new KnowledgeImportException($"无效的文件路径：{ex.Message}");
        }
        if (!File.Exists(fullPath))
            throw new KnowledgeImportException($"文件不存在：{fullPath}");
        if (new FileInfo(fullPath).Length > KnowledgeImport.MaxFileBytes)
            throw new KnowledgeImportException($"文件过大：{Path.GetFileName(fullPath)} 超过 10 MB 限制");

        var text = KnowledgeImport.ExtractText(fullPath);
        var chunks = KnowledgeImport.SplitChunks(text);
        if (chunks.Count == 0)
            throw new KnowledgeImportException($"文件内容为空：{Path.GetFileName(fullPath)}");

        var fileName = Path.GetFileName(fullPath);
        var tags = $"导入,{Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant()}";
        for (var index = 0; index < chunks.Count; index++)
        {
            var title = chunks.Count == 1 ? fileName : $"{fileName} · 第{index + 1}/{chunks.Count}部分";
            store.Save(scope, title, chunks[index], tags);
        }
        return (fileName, chunks.Count);
    }
}
