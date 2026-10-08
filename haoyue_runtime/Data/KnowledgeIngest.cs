namespace Haoyue.Runtime.Data;

/// <summary>
/// Shared file → knowledge-source pipeline used by both the daemon (knowledge.import /
/// knowledge.source.add) and the CLI (haoyue knowledge import): validates the path,
/// extracts text, and stores the file as a <see cref="KnowledgeSource"/> whose chunks
/// become entries under the stable 「文件名 · 第N/M部分」 title convention — so
/// re-importing the same file refreshes the source instead of duplicating.
/// </summary>
public static class KnowledgeIngest
{
    public static (KnowledgeSource Source, int Entries) ImportFile(
        KnowledgeStore store, string scope, string rawPath, long? notebookId = null)
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
        var target = notebookId ?? store.EnsureDefaultNotebook(scope);
        var (source, _) = store.SaveSource(scope, target, "file", fileName, fullPath, text);
        store.ReplaceSourceChunks(scope, source.Id, chunks, tags);
        return (store.GetSource(scope, source.Id)!, chunks.Count);
    }
}
