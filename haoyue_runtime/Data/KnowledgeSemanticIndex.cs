using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Providers;
using Microsoft.Data.Sqlite;

namespace Haoyue.Runtime.Data;

/// <summary>Embeds a batch of texts into fixed-length vectors. Implementations must return
/// one vector per input in input order; failures throw — callers downgrade to lexical search.</summary>
public interface IEmbeddingClient
{
    Task<double[][]> EmbedBatchAsync(IReadOnlyList<string> inputs, CancellationToken ct);
}

/// <summary>
/// OpenAI-compatible /embeddings client (OpenAI, Ollama, LM Studio, OpenRouter,
/// SiliconFlow…). Sends { model, input: [..] } and reads data[i].embedding in index order.
/// </summary>
public sealed class OpenAiEmbeddingClient(HttpClient http, ProviderConfig provider, string model) : IEmbeddingClient
{
    public async Task<double[][]> EmbedBatchAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var url = LlmUrl.Join(provider.BaseUrl, "embeddings");
        using var content = new StringContent(
            new JsonObject
            {
                ["model"] = model,
                ["input"] = new JsonArray([.. inputs.Select(item => JsonValue.Create(item))]),
            }.ToJsonString(),
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"));

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        var key = provider.ResolveApiKey();
        if (!string.IsNullOrEmpty(key))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        if (provider.Headers is { } headers)
        {
            foreach (var (name, value) in headers)
                request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"embeddings endpoint returned {(int)response.StatusCode}: {Truncate(body)}");

        var payload = JsonNode.Parse(body) ?? throw new HttpRequestException("embeddings response is not JSON.");
        var data = payload["data"] as JsonArray
            ?? throw new HttpRequestException("embeddings response has no data array.");
        if (data.Count != inputs.Count)
            throw new HttpRequestException($"embeddings returned {data.Count} vectors for {inputs.Count} inputs.");

        var vectors = new double[inputs.Count][];
        foreach (var item in data)
        {
            var index = item?["index"]?.GetValue<int>() ?? Array.IndexOf(vectors, null);
            var array = item?["embedding"] as JsonArray
                ?? throw new HttpRequestException("embeddings item has no embedding array.");
            vectors[index] = [.. array.Select(node => node?.GetValue<double>() ?? 0d)];
        }
        return vectors;
    }

    private static string Truncate(string text) => text.Length > 200 ? text[..200] + "…" : text;
}

/// <summary>An embedding route: the client plus the cache key that namespaces its
/// vector space (switching embedding models must not mix incompatible vectors).</summary>
public sealed record EmbeddingRoute(IEmbeddingClient Client, string CacheKey);

/// <summary>
/// 一次混合检索的产出：排序结果 + 语义层的真实状态。
/// <param name="Entries">融合排序后的条目。</param>
/// <param name="EmbeddedNow">本次调用现场嵌入的条目数（此前缺向量）。</param>
/// <param name="PendingVectors">因单次嵌入上限本次没建的向量数——语义召回不完整，应显式建索引。</param>
/// <param name="EmbeddingError">语义层失效原因。此前三层空 catch 静默降级，用户无从知道自己
/// 处在语义还是纯词法，也不知道配置错了什么。</param>
/// </summary>
public sealed record HybridSearchOutcome(
    IReadOnlyList<KnowledgeEntry> Entries,
    int EmbeddedNow = 0,
    int PendingVectors = 0,
    string? EmbeddingError = null);

/// <summary>
/// Semantic layer on top of the lexical knowledge search: entries and queries are
/// embedded through a configurable OpenAI-compatible endpoint, vectors are cached
/// per (scope, entry, model) in SQLite, and results fuse lexical and cosine scores.
/// Any embedding failure (no provider, network error, dimension drift) degrades the
/// whole search to the pure lexical path — semantic search is an enhancement, never
/// a dependency. The degradation is reported on the outcome, not swallowed.
/// </summary>
public sealed class KnowledgeSemanticIndex(HaoyueDatabase database, ILlmHttpFactory http, IProviderManager? providers = null)
{
    /// <summary>
    /// 嵌入预算：条目正文上限 + 标题/标签表头预算。此前恒为 4000 字符，而导入分块按
    /// 6000 字符、条目正文允许 8000 字符——每条知识尾部 1/3 对语义层永久不可见。
    /// 预算与写入上限对齐后，正文总是完整进向量，只有超长表头会被裁。
    /// </summary>
    public const int MaxEmbedChars = KnowledgeStore.MaxEntryContentChars + EmbedHeaderBudget;

    private const int EmbedHeaderBudget = 200;

    /// <summary>
    /// Resolves the embedding route: an explicit "providerId/modelId" reference first
    /// (direct HTTP for openai-kind providers; the provider manager for local GGUF
    /// models and anything else), then the first enabled HTTP provider exposing an
    /// embedding-capable model, then the provider manager's own auto-selection
    /// (which covers local in-process GGUF embeddings). Null = semantic disabled.
    /// </summary>
    public EmbeddingRoute? CreateRoute(HaoyueConfig config)
    {
        if (!config.Knowledge.SemanticEnabled) return null;

        if (!string.IsNullOrWhiteSpace(config.Knowledge.EmbeddingModel))
        {
            var reference = config.Knowledge.EmbeddingModel;
            var separator = reference.IndexOf('/');
            if (separator > 0
                && config.FindProvider(reference[..separator]) is { } explicitProvider
                && explicitProvider.Enabled && !explicitProvider.IsLocal)
            {
                var model = reference[(separator + 1)..];
                return new EmbeddingRoute(
                    new OpenAiEmbeddingClient(http.GetClient(explicitProvider), explicitProvider, model),
                    reference);
            }
            // Local GGUF / non-HTTP references go through the provider manager, which
            // knows the model registry and the local weight cache.
            return providers is null
                ? null
                : new EmbeddingRoute(new DelegatingEmbeddingClient(providers, reference), reference);
        }

        foreach (var provider in config.Providers.Where(p => p.Enabled && !p.IsLocal))
        {
            var model = provider.Models.FirstOrDefault(m => m.Capabilities.Embedding);
            if (model is not null)
                return new EmbeddingRoute(
                    new OpenAiEmbeddingClient(http.GetClient(provider), provider, model.Id),
                    $"{provider.Id}/{model.Id}");
        }

        // No HTTP embedding provider — the provider manager auto-selects (local GGUF
        // path). Null at embed time degrades the search to lexical ordering.
        return providers is null
            ? null
            : new EmbeddingRoute(new DelegatingEmbeddingClient(providers, null), "auto");
    }

    /// <summary>Backward-compatible single-client view used by older callers/tests.</summary>
    public IEmbeddingClient? CreateClient(HaoyueConfig config) => CreateRoute(config)?.Client;

    /// <summary>
    /// Hybrid search: lexical scores come from <see cref="KnowledgeSearchRanker"/>,
    /// semantic scores from cached/embedded vectors.
    /// 融合口径（本批次修正）：
    /// · 词法侧按「理论满分」绝对归一（每查询词 3+2+2），不按候选最大值归一；
    /// · 语义侧用**绝对余弦**，不再除以候选最大值——归一后必有一项等于 1.0，名义 0.4 的
    ///   权重在任何查询下都被吃满，实际排序被语义主导，且相似度只有 0.31 的条目也会伪装成
    ///   0.4 的满权重信号；
    /// · 两侧各设绝对门槛（<see cref="KnowledgeTuning.MinSemanticSimilarity"/>、
    ///   <see cref="KnowledgeTuning.MinHybridScore"/>），替代原先的 <c>Final &gt; 0</c>——
    ///   负例查询（知识库里确实没有答案）必须返回空，而不是把最不离谱的条目当答案送回模型。
    /// </summary>
    public async Task<HybridSearchOutcome> SearchHybridAsync(
        string scope, IReadOnlyList<KnowledgeEntry> entries, string query, int limit,
        IEmbeddingClient client, string modelKey, CancellationToken ct)
    {
        var tokens = KnowledgeSearchRanker.BuildTokens(query);
        if (tokens.Count == 0) return new HybridSearchOutcome([]);

        // Lexical side — same scoring the plain search uses.
        var maxLexical = tokens.Count * (KnowledgeTuning.TitleWeight + KnowledgeTuning.ContentWeight + KnowledgeTuning.TagsWeight);
        var lexical = new Dictionary<long, double>();
        foreach (var entry in entries)
        {
            var (score, _) = KnowledgeSearchRanker.ScoreEntry(
                KnowledgeSearchRanker.Normalize(entry.Title),
                KnowledgeSearchRanker.Normalize(entry.Content),
                KnowledgeSearchRanker.Normalize(entry.Tags),
                tokens);
            lexical[entry.Id] = maxLexical > 0 ? Math.Min(1.0, score / (double)maxLexical) : 0;
        }

        // Semantic side — cached vectors first. 缺向量的条目按词法相关性优先、单次上限内
        // 现场补嵌：一次工具调用不能把整库（单来源可达 200 chunk）全量嵌入，本地通道逐条
        // 串行时会让回合卡死到「像没输出」。上限之外的条目计入 PendingVectors。
        var cached = LoadVectors(scope, modelKey);
        var missing = entries.Where(entry => !cached.ContainsKey(entry.Id))
            .OrderByDescending(entry => lexical.GetValueOrDefault(entry.Id))
            .ToList();
        var batch = missing.Take(KnowledgeTuning.MaxEmbedPerSearch).ToList();
        var pending = missing.Count - batch.Count;
        string? embeddingError = null;

        if (batch.Count > 0)
        {
            try
            {
                var vectors = await client.EmbedBatchAsync(
                    [.. batch.Select(EmbedTextFor)], ct).ConfigureAwait(false);
                var fresh = new List<(long Id, double[] Vector)>();
                for (var i = 0; i < batch.Count; i++)
                {
                    if (vectors[i].Length == 0) continue;
                    cached[batch[i].Id] = vectors[i];
                    fresh.Add((batch[i].Id, vectors[i]));
                }
                if (fresh.Count > 0) SaveVectors(scope, modelKey, fresh);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                embeddingError = Describe(ex);
            }
        }

        double[]? queryVector = null;
        try
        {
            queryVector = (await client.EmbedBatchAsync([Truncate(query, MaxEmbedChars)], ct).ConfigureAwait(false))[0];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            embeddingError ??= Describe(ex);
        }

        var semantic = new Dictionary<long, double>();
        if (queryVector is { Length: > 0 })
        {
            foreach (var entry in entries)
            {
                if (cached.TryGetValue(entry.Id, out var vector)
                    && vector.Length == queryVector.Length)
                    semantic[entry.Id] = Cosine(queryVector, vector);
            }
        }

        var scored = entries
            .Select(entry =>
            {
                var similarity = semantic.GetValueOrDefault(entry.Id);
                // 绝对相似度门槛之下的语义分直接归零：弱相关的向量相似不该换算成排名信号。
                var semanticTerm = similarity >= KnowledgeTuning.MinSemanticSimilarity ? similarity : 0d;
                return (Entry: entry, Final: lexical.GetValueOrDefault(entry.Id) * KnowledgeTuning.HybridLexicalWeight
                                                  + semanticTerm * KnowledgeTuning.HybridSemanticWeight);
            })
            .Where(pair => pair.Final >= KnowledgeTuning.MinHybridScore)
            .OrderByDescending(pair => pair.Final)
            .ThenByDescending(pair => pair.Entry.UpdatedAt, StringComparer.Ordinal)
            .Take(limit)
            .Select(pair => pair.Entry)
            .ToList();

        return new HybridSearchOutcome(scored, batch.Count, pending, embeddingError);
    }

    private static string Describe(Exception ex) =>
        ex is HttpRequestException or JsonException or InvalidOperationException or FormatException
            ? ex.Message
            : $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>
    /// 嵌入文本：标题 + 标签作表头，正文完整保留（预算与 <see cref="KnowledgeStore.MaxEntryContentChars"/>
    /// 对齐）。超长时只裁表头——裁正文会让尾部对语义层永久不可见。
    /// </summary>
    private static string EmbedTextFor(KnowledgeEntry entry)
    {
        var header = entry.Tags is { Length: > 0 } ? $"{entry.Title} {entry.Tags}" : entry.Title;
        header = Truncate(header, EmbedHeaderBudget);
        var body = Truncate(entry.Content, KnowledgeStore.MaxEntryContentChars);
        return header.Length > 0 ? header + "\n" + body : body;
    }

    private static string Truncate(string text, int limit) =>
        text.Length <= limit ? text : text[..limit];

    internal static double Cosine(double[] a, double[] b)
    {
        var dot = 0d;
        var normA = 0d;
        var normB = 0d;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return normA == 0 || normB == 0 ? 0 : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    /// <summary>Routes embeddings through the provider manager: covers local GGUF
    /// models (in-process llama.cpp) and any future provider kind with embeddings.</summary>
    private sealed class DelegatingEmbeddingClient(IProviderManager providers, string? modelRef) : IEmbeddingClient
    {
        public async Task<double[][]> EmbedBatchAsync(IReadOnlyList<string> inputs, CancellationToken ct)
        {
            var vectors = new double[inputs.Count][];
            for (var i = 0; i < inputs.Count; i++)
            {
                var result = await providers.EmbedAsync(inputs[i], modelRef, ct).ConfigureAwait(false)
                    ?? throw new HttpRequestException("no embedding-capable provider is available.");
                if (result.Vectors.Count == 0)
                    throw new HttpRequestException("embedding endpoint returned no vectors.");
                vectors[i] = [.. result.Vectors[^1].Select(value => (double)value)];
            }
            return vectors;
        }
    }

    private Dictionary<long, double[]> LoadVectors(string scope, string modelKey)
    {
        var result = new Dictionary<long, double[]>();
        try
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT entry_id, dim, vector FROM knowledge_vectors WHERE scope = $scope AND model = $model;";
            command.Parameters.AddWithValue("$scope", scope);
            command.Parameters.AddWithValue("$model", modelKey);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var dim = reader.GetInt64(1);
                var blob = (byte[])reader.GetValue(2);
                if (blob.Length != dim * sizeof(double)) continue; // stale row — re-embed
                var vector = new double[dim];
                Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);
                result[reader.GetInt64(0)] = vector;
            }
        }
        catch (SqliteException)
        {
            // Table missing (old database) — treated as an empty cache; SaveVectors
            // below will fail the same way and semantic search degrades to lexical.
        }
        return result;
    }

    private void SaveVectors(string scope, string modelKey, IReadOnlyList<(long Id, double[] Vector)> vectors)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var (id, vector) in vectors)
        {
            var blob = new byte[vector.Length * sizeof(double)];
            Buffer.BlockCopy(vector, 0, blob, 0, blob.Length);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO knowledge_vectors (scope, entry_id, model, dim, vector, updated_at)
                VALUES ($scope, $id, $model, $dim, $vector, $now)
                ON CONFLICT(scope, entry_id, model) DO UPDATE SET dim = $dim, vector = $vector, updated_at = $now;
                """;
            command.Parameters.AddWithValue("$scope", scope);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$model", modelKey);
            command.Parameters.AddWithValue("$dim", vector.Length);
            command.Parameters.AddWithValue("$vector", blob);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
