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
            var array = item["embedding"] as JsonArray
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
/// Semantic layer on top of the lexical knowledge search: entries and queries are
/// embedded through a configurable OpenAI-compatible endpoint, vectors are cached
/// per (scope, entry, model) in SQLite, and results fuse lexical and cosine scores.
/// Any embedding failure (no provider, network error, dimension drift) degrades the
/// whole search to the pure lexical path — semantic search is an enhancement, never
/// a dependency.
/// </summary>
public sealed class KnowledgeSemanticIndex(HaoyueDatabase database, ILlmHttpFactory http, IProviderManager? providers = null)
{
    /// <summary>Texts are truncated before embedding — knowledge entries can be long,
    /// embedding budgets are not.</summary>
    private const int MaxEmbedChars = 4000;

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
    /// semantic scores from cached/embedded vectors. Fusion is max-normalized per
    /// side (0.6 lexical + 0.4 cosine) so neither dominates by raw scale. Entries
    /// with no usable vector still compete on the lexical side.
    /// </summary>
    public async Task<IReadOnlyList<KnowledgeEntry>> SearchHybridAsync(
        string scope, IReadOnlyList<KnowledgeEntry> entries, string query, int limit,
        IEmbeddingClient client, string modelKey, CancellationToken ct)
    {
        var tokens = KnowledgeSearchRanker.BuildTokens(query);
        if (tokens.Count == 0) return [];

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

        // Semantic side — cached vectors first, missing ones embedded in batches.
        var cached = LoadVectors(scope, modelKey);
        var missing = entries.Where(entry => !cached.ContainsKey(entry.Id)).ToList();
        if (missing.Count > 0)
        {
            try
            {
                var vectors = await client.EmbedBatchAsync(
                    [.. missing.Select(entry => TruncateForEmbed(entry))], ct).ConfigureAwait(false);
                var fresh = new List<(long Id, double[] Vector)>();
                for (var i = 0; i < missing.Count; i++)
                {
                    if (vectors[i].Length == 0) continue;
                    cached[missing[i].Id] = vectors[i];
                    fresh.Add((missing[i].Id, vectors[i]));
                }
                if (fresh.Count > 0) SaveVectors(scope, modelKey, fresh);
            }
            catch (Exception) when (ct is not { IsCancellationRequested: true })
            {
                // Embedding failed — entries without cached vectors fall back to
                // lexical-only ordering below. Cached vectors stay usable.
            }
        }

        double[]? queryVector = null;
        try
        {
            queryVector = (await client.EmbedBatchAsync([Truncate(query)], ct).ConfigureAwait(false))[0];
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Query embedding failed — lexical ordering only.
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
        var maxSemantic = semantic.Count > 0 ? semantic.Values.Max() : 0;

        return entries
            .Select(entry => (
                Entry: entry,
                Final: lexical.GetValueOrDefault(entry.Id) * 0.6
                        + (maxSemantic > 0
                            ? Math.Max(0, semantic.GetValueOrDefault(entry.Id)) / maxSemantic * 0.4
                            : 0)))
            .Where(pair => pair.Final > 0)
            .OrderByDescending(pair => pair.Final)
            .ThenByDescending(pair => pair.Entry.UpdatedAt, StringComparer.Ordinal)
            .Take(limit)
            .Select(pair => pair.Entry)
            .ToList();
    }

    private static string TruncateForEmbed(KnowledgeEntry entry)
    {
        var text = string.IsNullOrEmpty(entry.Tags)
            ? entry.Title + "\n" + entry.Content
            : entry.Title + " " + entry.Tags + "\n" + entry.Content;
        return Truncate(text);
    }

    private static string Truncate(string text) =>
        text.Length <= MaxEmbedChars ? text : text[..MaxEmbedChars];

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
