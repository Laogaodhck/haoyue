using System.Text.Json;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Daemon;

/// <summary>
/// Knowledge surface: entries, notebooks, sources, synonyms, semantic index, and
/// memory/rules storage. Split out of DaemonAdminApi.cs by domain (ADR-20261015);
/// the line-budget guard measures the whole class across its partial files.
/// </summary>
internal sealed partial class DaemonAdminApi
{
    /// <summary>
    /// Knowledge entries for one scope with notebook/source attribution; params
    /// accept the standard global/workspace selectors plus optional notebookId
    /// and tag filters.
    /// </summary>
    public string ListKnowledge(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var limit = Math.Clamp(parameters["limit"]?.GetValue<int?>() ?? 500, 1, 2000);
        var tag = OptionalString(parameters, "tag");
        var notebookId = OptionalId(parameters, "notebookId");
        return KnowledgeInfoPayload(workspace,
            runtime.Knowledge.ListInfo(HaoyueDatabase.ScopeKey(workspace), limit, notebookId, tag));
    }

    public async Task<string> SearchKnowledge(JsonObject parameters, CancellationToken ct)
    {
        var workspace = SessionWorkspace(parameters);
        var query = RequiredString(parameters, "query");
        var limit = Math.Clamp(parameters["limit"]?.GetValue<int?>() ?? 50, 1, 100);
        var notebookId = OptionalId(parameters, "notebookId");
        var (infos, semantic) = await RetrieveHybridAsync(
            workspace, query, notebookId is long nb ? [nb] : null, null,
            OptionalString(parameters, "tag"), limit, ct).ConfigureAwait(false);
        return KnowledgeInfoPayload(workspace, infos, semantic);
    }
    /// <summary>Distinct tags with occurrence counts plus the total entry count of the scope (or one notebook).</summary>
    public string KnowledgeTags(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var scope = HaoyueDatabase.ScopeKey(workspace);
        var tags = new JsonArray();
        int total;
        if (OptionalId(parameters, "notebookId") is long notebookId)
        {
            var infos = runtime.Knowledge.ListInfo(scope, 2000, notebookId);
            total = infos.Count;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var info in infos)
            {
                foreach (var rawTag in (info.Entry.Tags ?? "").Split([',', '，', ';', '；'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (rawTag.Length == 0) continue;
                    counts[rawTag] = counts.GetValueOrDefault(rawTag) + 1;
                }
            }
            foreach (var pair in counts.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                tags.Add((JsonNode)new JsonObject { ["tag"] = pair.Key, ["count"] = pair.Value });
        }
        else
        {
            total = runtime.Knowledge.Count(scope);
            foreach (var (tag, count) in runtime.Knowledge.TagCounts(scope))
            {
                tags.Add((JsonNode)new JsonObject { ["tag"] = tag, ["count"] = count });
            }
        }
        return new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["isGlobal"] = workspace.IsGlobal,
            ["total"] = total,
            ["tags"] = tags,
        }.ToJsonString();
    }

    /// <summary>
    /// Renders the whole scope as one Markdown document for backup/sharing. The
    /// desktop saves it through its own save dialog; the daemon never writes to
    /// user-chosen paths by itself.
    /// </summary>
    public string ExportKnowledge(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var scope = HaoyueDatabase.ScopeKey(workspace);
        var entries = runtime.Knowledge.List(scope, 2000);

        return new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["isGlobal"] = workspace.IsGlobal,
            ["count"] = entries.Count,
            ["markdown"] = KnowledgeExport.ToMarkdown(workspace.IsGlobal ? "全局" : workspace.Root, entries),
        }.ToJsonString();
    }

    // ───────────────────── knowledge center: notebooks ─────────────────────

    public string ListKnowledgeNotebooks(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        return KnowledgeNotebooksPayload(workspace);
    }

    public string SaveKnowledgeNotebook(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var name = RequiredString(parameters, "name");
        var id = OptionalId(parameters, "id");
        try
        {
            runtime.Knowledge.SaveNotebook(
                HaoyueDatabase.ScopeKey(workspace), name, OptionalString(parameters, "description"), id);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            throw new DaemonRequestException(ex.Message);
        }
        return KnowledgeNotebooksPayload(workspace);
    }

    public string DeleteKnowledgeNotebook(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var id = RequiredId(parameters, "id");
        try
        {
            runtime.Knowledge.DeleteNotebook(HaoyueDatabase.ScopeKey(workspace), id);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            throw new DaemonRequestException(ex.Message);
        }
        return KnowledgeNotebooksPayload(workspace);
    }

    // ────────────────────── knowledge center: sources ──────────────────────

    /// <summary>
    /// 知识库 URL 导入/刷新共用的链接校验器：现读「允许的网站」白名单（桌面端增删
    /// 立即生效，无缓存），叠加私网/链路本地硬拦截。配置为空 = 白名单未启用，
    /// 仅保留 scheme 与公网主机两道底线。
    /// </summary>
    private Func<Uri, (bool Ok, string? Reason)> KnowledgeUrlValidator() =>
        u => KnowledgeUrlPolicy.Check(
            u, WebSiteAllowlist.Parse(runtime.ConfigStore.Config.Web?.AllowedSites ?? []));

    /// <summary>
    /// Adds one ingest source: kind "text" (title+content), "url" (page fetched and
    /// reduced to text) or "file" (paths[] reused from knowledge.import). Long text
    /// is chunked into 「标题 · 第N/M部分」 entries; re-adding the same url/file
    /// refreshes instead of duplicating.
    /// </summary>
    public string AddKnowledgeSource(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var scope = HaoyueDatabase.ScopeKey(workspace);
        var kind = RequiredString(parameters, "kind").Trim().ToLowerInvariant();
        if (kind is not ("text" or "url" or "file"))
            throw new DaemonRequestException("params.kind must be text, url or file");
        var notebookId = OptionalId(parameters, "notebookId") ?? runtime.Knowledge.EnsureDefaultNotebook(scope);

        if (kind == "text")
        {
            var content = RequiredString(parameters, "content");
            if (content.Length > KnowledgeWeb.MaxTextChars)
                throw new DaemonRequestException($"文本过长（{content.Length} 字符），上限 {KnowledgeWeb.MaxTextChars}。");
            var chunks = KnowledgeImport.SplitChunks(content);
            if (chunks.Count == 0)
                throw new DaemonRequestException("文本内容为空，无法收录。");
            var (source, _) = runtime.Knowledge.SaveSource(
                scope, notebookId, "text", RequiredString(parameters, "title"), null, content);
            var count = runtime.Knowledge.ReplaceSourceChunks(scope, source.Id, chunks, OptionalString(parameters, "tags"));
            return SourceActionPayload(workspace, notebookId, SourceNode(runtime.Knowledge.GetSource(scope, source.Id)!, true), count);
        }

        if (kind == "url")
        {
            var raw = RequiredString(parameters, "url");
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new DaemonRequestException("params.url 必须是 http(s) 绝对链接。");
            string title, text;
            // 与 web_fetch 同一套白名单，另叠加私网/链路本地硬拦截（白名单关闭时也生效），
            // 重定向逐跳校验——否则「允许的网站」对知识库导入形同虚设。
            try { (title, text) = KnowledgeWeb.Fetch(uri, KnowledgeUrlValidator()); }
            catch (KnowledgeWebException ex) { throw new DaemonRequestException(ex.Message); }
            var chunks = KnowledgeImport.SplitChunks(text);
            if (chunks.Count == 0)
                throw new DaemonRequestException("网页内容为空，无法收录。");
            var (source, _) = runtime.Knowledge.SaveSource(
                scope, notebookId, "url", title, uri.ToString(), text);
            var count = runtime.Knowledge.ReplaceSourceChunks(
                scope, source.Id, chunks, OptionalString(parameters, "tags") ?? $"网页,{uri.Host}");
            return SourceActionPayload(workspace, notebookId, SourceNode(runtime.Knowledge.GetSource(scope, source.Id)!, true), count);
        }

        var paths = parameters["paths"] as JsonArray
            ?? throw new DaemonRequestException("params.paths is required");
        if (paths.Count == 0)
            throw new DaemonRequestException("params.paths must contain at least one file path");
        var added = new List<KnowledgeSource>();
        var files = new JsonArray();
        var totalEntries = 0;
        foreach (var node in paths)
        {
            var raw = node?.GetValue<string>() ?? throw new DaemonRequestException("params.paths must contain file paths");
            KnowledgeSource source;
            int count;
            try
            {
                (source, count) = KnowledgeIngest.ImportFile(runtime.Knowledge, scope, raw, notebookId);
            }
            catch (KnowledgeImportException ex)
            {
                throw new DaemonRequestException(ex.Message);
            }
            added.Add(source);
            files.Add((JsonNode)new JsonObject { ["file"] = source.Title, ["entries"] = count });
            totalEntries += count;
            try { KnowledgeSourceWatcher.EnsureStarted(runtime.Knowledge, scope); }
            catch { /* watcher 是 best-effort 增强能力，启动失败不影响导入结果 */ }
        }
        var sources = new JsonArray();
        foreach (var source in added)
            sources.Add((JsonNode)SourceNode(source, false));
        return new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["isGlobal"] = workspace.IsGlobal,
            ["notebookId"] = notebookId,
            ["sources"] = sources,
            ["entries"] = totalEntries,
            ["importedFiles"] = files.Count,
            ["importedEntries"] = totalEntries,
            ["files"] = files,
        }.ToJsonString();
    }

    public string ListKnowledgeSources(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        return KnowledgeSourcesPayload(workspace, RequiredId(parameters, "notebookId"));
    }

    /// <summary>One source's content as a windowed slice (offset + maxChars ≤ 50000).</summary>
    public string ReadKnowledgeSource(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var id = RequiredId(parameters, "id");
        var source = runtime.Knowledge.GetSource(HaoyueDatabase.ScopeKey(workspace), id)
            ?? throw new DaemonRequestException($"No knowledge source #{id} in this scope.");
        var offset = Math.Clamp(parameters["offset"]?.GetValue<int?>() ?? 0, 0, Math.Max(source.Content.Length - 1, 0));
        var maxChars = Math.Clamp(parameters["maxChars"]?.GetValue<int?>() ?? 50_000, 1, 50_000);
        var slice = source.Content[offset..Math.Min(source.Content.Length, offset + maxChars)];

        var node = SourceNode(source, false);
        node["content"] = slice;
        node["offset"] = offset;
        node["totalChars"] = source.Content.Length;
        return new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["isGlobal"] = workspace.IsGlobal,
            ["source"] = node,
        }.ToJsonString();
    }

    public string DeleteKnowledgeSource(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var scope = HaoyueDatabase.ScopeKey(workspace);
        var id = RequiredId(parameters, "id");
        var source = runtime.Knowledge.GetSource(scope, id)
            ?? throw new DaemonRequestException($"No knowledge source #{id} in this scope.");
        runtime.Knowledge.DeleteSource(scope, id);
        return KnowledgeSourcesPayload(workspace, source.NotebookId);
    }

    /// <summary>
    /// Re-extracts a source: files are re-read from disk, URLs re-fetched, text
    /// re-chunked — then the chunk entries are rebuilt in place.
    /// </summary>
    public string RefreshKnowledgeSource(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var scope = HaoyueDatabase.ScopeKey(workspace);
        var id = RequiredId(parameters, "id");
        var source = runtime.Knowledge.GetSource(scope, id)
            ?? throw new DaemonRequestException($"No knowledge source #{id} in this scope.");

        int count;
        switch (source.Kind)
        {
            case "file":
                try
                {
                    (_, count) = KnowledgeIngest.ImportFile(runtime.Knowledge, scope, source.Locator!, source.NotebookId);
                }
                catch (KnowledgeImportException ex)
                {
                    throw new DaemonRequestException(ex.Message);
                }
                break;
            case "url":
                if (!Uri.TryCreate(source.Locator, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                    throw new DaemonRequestException($"来源链接无效：{source.Locator}");
                string title, text;
                // 刷新与新增同一套校验：白名单事后收紧 / 私网拦截不能只卡首次导入。
                try { (title, text) = KnowledgeWeb.Fetch(uri, KnowledgeUrlValidator()); }
                catch (KnowledgeWebException ex) { throw new DaemonRequestException(ex.Message); }
                var urlChunks = KnowledgeImport.SplitChunks(text);
                if (urlChunks.Count == 0)
                    throw new DaemonRequestException("网页内容为空，无法刷新。");
                runtime.Knowledge.SaveSource(scope, source.NotebookId, "url", title, uri.ToString(), text);
                count = runtime.Knowledge.ReplaceSourceChunks(scope, source.Id, urlChunks, null);
                break;
            default:
                var textChunks = KnowledgeImport.SplitChunks(source.Content);
                if (textChunks.Count == 0)
                    throw new DaemonRequestException("来源内容为空，无法刷新。");
                count = runtime.Knowledge.ReplaceSourceChunks(scope, source.Id, textChunks, null);
                break;
        }

        return SourceActionPayload(workspace, source.NotebookId, SourceNode(runtime.Knowledge.GetSource(scope, id)!, true), count);
    }

    // ───────────────────── knowledge center: retrieval ─────────────────────

    /// <summary>
    /// Ranked retrieval across the whole scope or selected notebooks/sources,
    /// every hit annotated with its notebook/source origin. Hybrid when an embedding
    /// route exists — see <see cref="RetrieveHybridAsync"/>.
    /// </summary>
    public async Task<string> RetrieveKnowledge(JsonObject parameters, CancellationToken ct)
    {
        var workspace = SessionWorkspace(parameters);
        var query = RequiredString(parameters, "query");
        var limit = Math.Clamp(parameters["limit"]?.GetValue<int?>() ?? 8, 1, 50);
        var (infos, semantic) = await RetrieveHybridAsync(
            workspace, query, OptionalIdList(parameters, "notebookIds"), OptionalIdList(parameters, "sourceIds"),
            OptionalString(parameters, "tag"), limit, ct).ConfigureAwait(false);
        return KnowledgeInfoPayload(workspace, infos, semantic);
    }

    /// <summary>
    /// 检索的混合通道：候选池按笔记本/来源/标签筛出（不做词法裁剪，否则语义层看不到
    /// 用户没命中关键词的那部分），再交给 <see cref="KnowledgeSemanticIndex"/> 融合排序。
    /// 语义不可用（没配嵌入模型）或嵌入失败时退回词法顺序——检索结果不能因为语义层
    /// 坏掉而变空，但也不能假装还是语义结果，所以 <c>semantic</c> 节点把真实状态一并返回。
    /// </summary>
    private async Task<(IReadOnlyList<KnowledgeEntryInfo> Infos, JsonObject Semantic)> RetrieveHybridAsync(
        Haoyue.Runtime.Workspaces.WorkspaceInfo workspace, string query,
        IReadOnlyList<long>? notebookIds, IReadOnlyList<long>? sourceIds, string? tag, int limit, CancellationToken ct)
    {
        var scope = HaoyueDatabase.ScopeKey(workspace);
        var pool = runtime.Knowledge.CollectInfos(scope, notebookIds, sourceIds, tag, 2000);
        var entries = pool.Select(info => info.Entry).ToList();
        var byId = pool.ToDictionary(info => info.Entry.Id, info => info);
        var status = runtime.SemanticIndex.Status(scope, entries, runtime.ConfigStore.Config);

        IReadOnlyList<KnowledgeEntryInfo> Lexical() =>
            KnowledgeSearchRanker.Rank(entries, query, limit).Select(entry => byId[entry.Id]).ToList();

        if (!status.Enabled)
            return (Lexical(), SemanticNode(status, degraded: false, embeddedNow: 0, pending: status.Pending, error: null));

        var route = runtime.SemanticIndex.CreateRoute(runtime.ConfigStore.Config)!;
        try
        {
            var outcome = await runtime.SemanticIndex.SearchHybridAsync(
                scope, entries, query, limit, route.Client, route.CacheKey, ct).ConfigureAwait(false);
            if (outcome.EmbeddingError is not null)
                // 语义失效时融合分只剩词法项，且还要过绝对阈值——比纯词法排序更严，
                // 会把原本能看到的弱命中筛掉。退回完整词法排序，并把原因标在 semantic 节点上。
                return (Lexical(), SemanticNode(status, degraded: true, embeddedNow: 0, pending: status.Pending, error: outcome.EmbeddingError));

            return (outcome.Entries.Select(entry => byId[entry.Id]).ToList(),
                SemanticNode(status, degraded: false, embeddedNow: outcome.EmbeddedNow, pending: outcome.PendingVectors, error: null));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (Lexical(), SemanticNode(status, degraded: true, embeddedNow: 0, pending: status.Pending, error: ex.Message));
        }
    }

    private static JsonObject SemanticNode(SemanticIndexStatus status, bool degraded, int embeddedNow, int pending, string? error) => new()
    {
        ["enabled"] = status.Enabled,
        ["model"] = status.Model,
        ["total"] = status.Total,
        ["embedded"] = status.Embedded,
        ["pending"] = pending,
        ["embeddedNow"] = embeddedNow,
        ["degraded"] = degraded,
        ["reason"] = status.Reason,
        ["error"] = error,
    };

    /// <summary>语义索引状态（纯本地读取，不发网络请求）：桌面知识中心据此显示语义/词法与待建数量。</summary>
    public string SemanticStatus(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var scope = HaoyueDatabase.ScopeKey(workspace);
        var status = runtime.SemanticIndex.Status(scope, runtime.Knowledge.LoadScope(scope), runtime.ConfigStore.Config);
        return new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["isGlobal"] = workspace.IsGlobal,
            ["enabled"] = status.Enabled,
            ["model"] = status.Model,
            ["total"] = status.Total,
            ["embedded"] = status.Embedded,
            ["pending"] = status.Pending,
            ["reason"] = status.Reason,
        }.ToJsonString();
    }

    /// <summary>
    /// 分批建语义索引：一次只嵌 <c>batch</c> 条（默认 32，上限 128），返回本次进度与剩余量。
    /// 桌面按 <c>pending</c> 循环调用即可，单批保持在客户端 60 秒超时窗口内。
    /// </summary>
    public async Task<string> BuildSemanticIndex(JsonObject parameters, CancellationToken ct)
    {
        var workspace = SessionWorkspace(parameters);
        var scope = HaoyueDatabase.ScopeKey(workspace);
        var batch = OptionalId(parameters, "batch") is long value
            ? (int)Math.Clamp(value, 1, KnowledgeSemanticIndex.MaxBuildBatch)
            : KnowledgeSemanticIndex.DefaultBuildBatch;
        var entries = runtime.Knowledge.LoadScope(scope);
        var result = await runtime.SemanticIndex.BuildAsync(
            scope, entries, runtime.ConfigStore.Config, batch, ct).ConfigureAwait(false);
        return new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["isGlobal"] = workspace.IsGlobal,
            ["embedded"] = result.Embedded,
            ["failed"] = result.Failed,
            ["total"] = result.Total,
            ["pending"] = result.Pending,
            ["model"] = result.Model,
            ["error"] = result.Error,
        }.ToJsonString();
    }

    // ───────────────────── knowledge center: helpers ───────────────────────

    private static long? OptionalId(JsonObject parameters, string name)
    {
        if (parameters[name] is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var longId)) return longId;
        if (value.TryGetValue<double>(out var doubleId)) return (long)doubleId;
        throw new DaemonRequestException($"params.{name} must be an integer id");
    }

    private static long RequiredId(JsonObject parameters, string name) =>
        OptionalId(parameters, name) ?? throw new DaemonRequestException($"params.{name} is required");

    private static List<long>? OptionalIdList(JsonObject parameters, string name)
    {
        if (parameters[name] is not JsonArray array || array.Count == 0) return null;
        var values = new List<long>();
        foreach (var node in array)
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue<long>(out var longValue)) { values.Add(longValue); continue; }
                if (value.TryGetValue<double>(out var doubleValue)) { values.Add((long)doubleValue); continue; }
            }
            throw new DaemonRequestException($"params.{name} must contain integer ids");
        }
        return values;
    }

    private static JsonObject NotebookNode(KnowledgeNotebook notebook) => new()
    {
        ["id"] = notebook.Id,
        ["name"] = notebook.Name,
        ["description"] = notebook.Description,
        ["isDefault"] = notebook.IsDefault,
        ["entryCount"] = notebook.EntryCount,
        ["sourceCount"] = notebook.SourceCount,
        ["createdAt"] = notebook.CreatedAt,
        ["updatedAt"] = notebook.UpdatedAt,
    };

    private static JsonObject SourceNode(KnowledgeSource source, bool includeContent)
    {
        var node = new JsonObject
        {
            ["id"] = source.Id,
            ["notebookId"] = source.NotebookId,
            ["kind"] = source.Kind,
            ["title"] = source.Title,
            ["locator"] = source.Locator,
            ["chunkCount"] = source.ChunkCount,
            ["charCount"] = source.Content.Length,
            ["createdAt"] = source.CreatedAt,
            ["updatedAt"] = source.UpdatedAt,
        };
        if (includeContent) node["content"] = source.Content;
        return node;
    }

    private string KnowledgeNotebooksPayload(Haoyue.Runtime.Workspaces.WorkspaceInfo workspace)
    {
        var data = new JsonArray();
        foreach (var notebook in runtime.Knowledge.ListNotebooks(HaoyueDatabase.ScopeKey(workspace)))
            data.Add((JsonNode)NotebookNode(notebook));
        return new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["isGlobal"] = workspace.IsGlobal,
            ["count"] = data.Count,
            ["notebooks"] = data,
        }.ToJsonString();
    }

    private string KnowledgeSourcesPayload(Haoyue.Runtime.Workspaces.WorkspaceInfo workspace, long notebookId)
    {
        var data = new JsonArray();
        foreach (var source in runtime.Knowledge.ListSources(HaoyueDatabase.ScopeKey(workspace), notebookId))
            data.Add((JsonNode)SourceNode(source, false));
        return new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["isGlobal"] = workspace.IsGlobal,
            ["notebookId"] = notebookId,
            ["count"] = data.Count,
            ["sources"] = data,
        }.ToJsonString();
    }

    private static string SourceActionPayload(
        Haoyue.Runtime.Workspaces.WorkspaceInfo workspace, long notebookId, JsonObject source, int entries) => new JsonObject
    {
        ["workspace"] = workspace.Root,
        ["isGlobal"] = workspace.IsGlobal,
        ["notebookId"] = notebookId,
        ["source"] = source,
        ["entries"] = entries,
    }.ToJsonString();

    /// <summary>
    /// Read-only view of the user-editable search synonym table
    /// (~/.haoyue/knowledge/synonyms.txt, hot-reloaded by KnowledgeTuning).
    /// </summary>
    public string GetKnowledgeSynonyms()
    {
        var path = KnowledgeTuning.ActivePath;
        string? content = null;
        if (File.Exists(path))
        {
            try { content = File.ReadAllText(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new DaemonRequestException($"无法读取同义词表：{ex.Message}");
            }
        }
        return new JsonObject
        {
            ["path"] = path,
            ["exists"] = content is not null,
            ["content"] = content ?? "",
        }.ToJsonString();
    }

    public string SaveKnowledgeSynonyms(JsonObject parameters)
    {
        var content = RequiredString(parameters, "content");
        var path = KnowledgeTuning.ActivePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DaemonRequestException($"无法写入同义词表：{ex.Message}");
        }
        // 写盘与随后的检索可能落在同一个文件系统时间戳桶里，「时间 + 长度」指纹会
        // 判定未变而继续用旧表；这里显式失效，让下一次检索必然重读（不依赖时间戳精度）。
        KnowledgeTuning.Invalidate();
        return GetKnowledgeSynonyms();
    }

    /// <summary>Creates or updates one entry (same-title upsert, or in-place when params.id is given) and returns the refreshed list.</summary>
    public string SaveKnowledge(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var title = RequiredString(parameters, "title");
        var content = RequiredString(parameters, "content");
        if (content.Length > KnowledgeStore.MaxEntryContentChars)
            throw new DaemonRequestException(
                $"Knowledge content is too long ({content.Length} chars); keep entries under {KnowledgeStore.MaxEntryContentChars} characters.");
        long? id = null;
        if (parameters["id"] is JsonValue value)
        {
            if (value.TryGetValue<long>(out var longId)) id = longId;
            else if (value.TryGetValue<double>(out var doubleId)) id = (long)doubleId;
        }
        try
        {
            runtime.Knowledge.Save(HaoyueDatabase.ScopeKey(workspace), title, content,
                OptionalString(parameters, "tags"), id, OptionalId(parameters, "notebookId"));
        }
        catch (KeyNotFoundException)
        {
            throw new DaemonRequestException($"No knowledge entry #{id} in this scope.");
        }
        return KnowledgeInfoPayload(workspace, runtime.Knowledge.ListInfo(HaoyueDatabase.ScopeKey(workspace), 500));
    }

    public string DeleteKnowledge(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        long? id = null;
        if (parameters["id"] is JsonValue value)
        {
            if (value.TryGetValue<long>(out var longId)) id = longId;
            else if (value.TryGetValue<double>(out var doubleId)) id = (long)doubleId;
        }
        if (id is null)
            throw new DaemonRequestException("params.id is required");
        if (!runtime.Knowledge.Delete(HaoyueDatabase.ScopeKey(workspace), id.Value))
            throw new DaemonRequestException($"No knowledge entry #{id} in this scope.");
        return KnowledgeInfoPayload(workspace, runtime.Knowledge.ListInfo(HaoyueDatabase.ScopeKey(workspace), 500));
    }

    /// <summary>Workspace or global MEMORY.md content plus its absolute path.</summary>
    public string GetMemory(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        string? content = null;
        if (File.Exists(workspace.MemoryFile))
        {
            try { content = File.ReadAllText(workspace.MemoryFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new DaemonRequestException($"无法读取记忆文件：{ex.Message}");
            }
        }
        return new JsonObject
        {
            ["path"] = workspace.MemoryFile,
            ["isGlobal"] = workspace.IsGlobal,
            ["exists"] = content is not null,
            ["content"] = content ?? "",
            ["chars"] = content?.Length ?? 0,
            ["lines"] = content?.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length ?? 0,
            ["charLimit"] = MemoryBudget.MaxChars,
            ["lineLimit"] = MemoryBudget.MaxLines,
            ["warning"] = content is null ? null : MemoryBudget.ApproachingWarning(content),
        }.ToJsonString();
    }

    public string SaveMemory(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var content = RequiredString(parameters, "content");
        // 与 memory_save 工具同一额度：桌面编辑器整篇覆盖写入也不能绕过上限，
        // 否则上限只在模型侧生效、用户一保存就把它撑破了。
        if (MemoryBudget.Overflow(content) is { } overflow)
            throw new DaemonRequestException(overflow);
        var file = workspace.MemoryFile;
        try
        {
            Directory.CreateDirectory(workspace.MemoryDir);
            // 原子替换：MEMORY.md 无备份，崩溃或断电不能留下半截文件。
            var temp = file + ".tmp";
            File.WriteAllText(temp, content);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(file + ".tmp")) File.Delete(file + ".tmp"); } catch (Exception) { }
            throw new DaemonRequestException($"无法写入记忆文件：{ex.Message}");
        }
        return new JsonObject
        {
            ["path"] = file,
            ["isGlobal"] = workspace.IsGlobal,
            ["chars"] = content.Length,
            ["limit"] = MemoryBudget.MaxChars,
            ["warning"] = MemoryBudget.ApproachingWarning(content),
        }.ToJsonString();
    }

    /// <summary>
    /// Lists the hierarchical AGENTS.md rule files under the workspace root
    /// (root plus two directory levels). Global workspaces carry no rules.
    /// </summary>
    public string ListRules(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var files = new JsonArray();
        foreach (var rule in WorkspaceRules.List(workspace))
        {
            files.Add((JsonNode)new JsonObject
            {
                ["path"] = rule.Path,
                ["isRoot"] = rule.IsRoot,
                ["content"] = rule.Content,
            });
        }
        return new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["isGlobal"] = workspace.IsGlobal,
            ["files"] = files,
        }.ToJsonString();
    }

    public string SaveRules(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var content = RequiredString(parameters, "content");
        string savedPath;
        try
        {
            savedPath = WorkspaceRules.Save(workspace, OptionalString(parameters, "path"), content);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new DaemonRequestException(ex.Message);
        }
        catch (IOException ex)
        {
            throw new DaemonRequestException(ex.Message);
        }
        return new JsonObject
        {
            ["path"] = savedPath,
        }.ToJsonString();
    }

    /// <summary>
    /// Deletes one hierarchical rule file with the same validation as
    /// <see cref="SaveRules"/>. Missing files are not an error.
    /// </summary>
    public string DeleteRules(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        string deletedPath;
        try
        {
            deletedPath = WorkspaceRules.Delete(workspace, OptionalString(parameters, "path"));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new DaemonRequestException(ex.Message);
        }
        catch (IOException ex)
        {
            throw new DaemonRequestException(ex.Message);
        }
        return new JsonObject
        {
            ["path"] = deletedPath,
        }.ToJsonString();
    }

    /// <summary>
    /// Imports files (text formats, .docx, .xlsx) into the knowledge base. Long
    /// documents are split into entry-sized chunks with stable titles, so repeated
    /// imports of the same file upsert instead of duplicating.
    /// </summary>
    public string ImportKnowledge(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var scope = HaoyueDatabase.ScopeKey(workspace);

        var paths = parameters["paths"] as JsonArray
            ?? throw new DaemonRequestException("params.paths is required");
        if (paths.Count == 0)
            throw new DaemonRequestException("params.paths must contain at least one file path");

        var files = new JsonArray();
        var totalEntries = 0;
        foreach (var node in paths)
        {
            var raw = node?.GetValue<string>() ?? throw new DaemonRequestException("params.paths must contain file paths");
            KnowledgeSource source;
            int count;
            try
            {
                (source, count) = KnowledgeIngest.ImportFile(runtime.Knowledge, scope, raw);
            }
            catch (KnowledgeImportException ex)
            {
                throw new DaemonRequestException(ex.Message);
            }
            files.Add((JsonNode)new JsonObject { ["file"] = source.Title, ["entries"] = count });
            totalEntries += count;
        }

        var payload = JsonNode.Parse(KnowledgeInfoPayload(workspace, runtime.Knowledge.ListInfo(scope, 500)))!.AsObject();
        payload["importedFiles"] = files.Count;
        payload["importedEntries"] = totalEntries;
        payload["files"] = files;
        return payload.ToJsonString();
    }

    private static JsonObject InfoNode(KnowledgeEntryInfo info)
    {
        var node = new JsonObject
        {
            ["id"] = info.Entry.Id,
            ["title"] = info.Entry.Title,
            ["content"] = info.Entry.Content,
            ["tags"] = info.Entry.Tags,
            ["createdAt"] = info.Entry.CreatedAt,
            ["updatedAt"] = info.Entry.UpdatedAt,
            ["notebookId"] = info.NotebookId,
            ["notebookName"] = info.NotebookName,
        };
        if (info.SourceId is long sourceId)
        {
            node["sourceId"] = sourceId;
            node["sourceTitle"] = info.SourceTitle;
            node["sourceKind"] = info.SourceKind;
            node["sourceLocator"] = info.SourceLocator;
            node["chunkOrdinal"] = info.ChunkOrdinal;
        }
        return node;
    }

    private static string KnowledgeInfoPayload(
        Haoyue.Runtime.Workspaces.WorkspaceInfo workspace,
        IReadOnlyList<KnowledgeEntryInfo> infos,
        JsonObject? semantic = null)
    {
        var data = new JsonArray();
        foreach (var info in infos)
        {
            data.Add((JsonNode)InfoNode(info));
        }
        var payload = new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["isGlobal"] = workspace.IsGlobal,
            ["count"] = data.Count,
            ["entries"] = data,
        };
        if (semantic is not null) payload["semantic"] = semantic;
        return payload.ToJsonString();
    }
}
