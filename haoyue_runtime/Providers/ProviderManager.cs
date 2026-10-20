using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;

namespace Haoyue.Runtime.Providers;

public interface IProviderManager
{
    /// <summary>Resolves the model to use right now: workspace override → profile → routing strategy.</summary>
    ModelInfo ResolveActive(WorkspaceConfig? workspace = null);

    /// <summary>Ordered failover chain starting with the active model.</summary>
    IReadOnlyList<ModelInfo> BuildCandidates(WorkspaceConfig? workspace = null);

    /// <summary>Resolves a model by its <c>provider/model</c> reference; null when unknown.</summary>
    ModelInfo? ResolveModel(string reference);

    /// <summary>
    /// Streams a completion with retry (exponential backoff + jitter), circuit breaking and
    /// automatic provider failover. Once the first token arrives the stream is committed and
    /// errors propagate instead of switching providers mid-answer.
    /// </summary>
    IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        Func<ModelInfo, LlmRequest> requestFactory,
        WorkspaceConfig? workspace,
        CancellationToken ct,
        Func<ModelInfo, bool>? candidateFilter = null);

    /// <summary>Sends a minimal real completion to verify a model end-to-end.</summary>
    Task<(bool Success, string Detail, double LatencyMs)> TestModelAsync(ModelInfo model, CancellationToken ct = default);

    /// <summary>
    /// 工具调用能力探测。<see cref="ModelCapabilities.ToolCalling"/> 是纯用户声明，
    /// 本地客户端直接忽略 <c>request.Tools</c>——按声明注入全量工具 schema 会让本地
    /// 回合空谈且白烧 schema token。三态语义见 <see cref="ToolCallingSupport"/>；
    /// <see cref="ToolCallingSupport.Unknown"/> 时调用方保持旧行为（继续注入）。
    /// </summary>
    Task<ToolCallingSupport> ProbeToolCallingAsync(ModelInfo model, CancellationToken ct = default)
        => Task.FromResult(ToolCallingSupport.Supported);

    /// <summary>Fetches model identifiers from a provider's model-list endpoint.</summary>
    Task<IReadOnlyList<string>> FetchModelsAsync(ProviderConfig provider, string? url = null, CancellationToken ct = default);

    /// <summary>
    /// Embeds one input through an embedding-capable model. An explicit
    /// <paramref name="modelRef"/> wins; otherwise the first enabled model flagged
    /// <c>capabilities.embedding</c> is used. Returns null when no embedding-capable
    /// client kind exists or none is configured — callers degrade (e.g. to keyword search).
    /// </summary>
    Task<EmbeddingResult?> EmbedAsync(string input, string? modelRef = null, CancellationToken ct = default);

    /// <summary>
    /// 是否存在可用的嵌入目标（显式引用能解析，或注册表里有声明 embedding 能力的模型）。
    /// 默认实现假定存在，第三方实现不必改动；<see cref="ProviderManager"/> 给出精确答案，
    /// 语义索引状态据此区分「压根没配模型」与「配了但这次嵌入失败」——此前委派路由恒存在，
    /// 状态会声称语义可用，而每条嵌入都以 "no embedding-capable provider" 收场。
    /// </summary>
    bool HasEmbeddingTarget(string? modelRef = null) => true;

    /// <summary>
    /// Embeds a batch of inputs through one embedding-capable model and returns one vector
    /// per input in input order. <see cref="ProviderManager"/> overrides the default body
    /// with a single model call: the local GGUF path leases and loads weights per call, so
    /// the fallback below turns one index build into N weight round trips — exactly the
    /// "turn looks frozen" failure mode the semantic layer is meant to avoid.
    /// </summary>
    async Task<EmbeddingResult?> EmbedBatchAsync(
        IReadOnlyList<string> inputs, string? modelRef = null, CancellationToken ct = default)
    {
        var vectors = new List<float[]>(inputs.Count);
        var model = string.Empty;
        foreach (var input in inputs)
        {
            var result = await EmbedAsync(input, modelRef, ct).ConfigureAwait(false);
            if (result is null) return null;
            if (result.Vectors.Count == 0)
                throw new InvalidOperationException($"Embedding model '{result.Model}' returned no vectors.");
            vectors.Add(result.Vectors[^1]);
            model = result.Model;
        }
        return new EmbeddingResult(vectors, model);
    }
}

public sealed class ProviderManager(
    IConfigStore configStore,
    IModelRegistry registry,
    ILlmClientFactory clientFactory,
    ILlmHttpFactory httpFactory,
    IUsageTracker usageTracker,
    IEventBus eventBus,
    CircuitBreaker? breaker = null,
    Haoyue.Runtime.Data.ModelTokenStats? tokenStats = null) : IProviderManager
{
    private CircuitBreaker? _breaker;
    private readonly object _breakerGate = new();
    private readonly Haoyue.Runtime.Data.ModelTokenStats? _tokenStats = tokenStats;
    private RetryConfig Retry => configStore.Config.Routing.Retry;
    // A shared breaker can be injected by the daemon so circuit state survives
    // across isolated per-turn runtimes; standalone runtimes create their own.
    // The factory form keeps thresholds live across config reloads (C2).
    private CircuitBreaker Breaker
    {
        get
        {
            if (_breaker is not null) return _breaker;
            lock (_breakerGate)
            {
                _breaker ??= breaker ?? new CircuitBreaker(() => configStore.Config.Routing.Retry);
            }
            return _breaker;
        }
    }

    public ModelInfo ResolveActive(WorkspaceConfig? workspace = null)
    {
        var candidates = BuildCandidates(workspace);
        if (candidates.Count == 0)
            throw new LlmException(
                "No models configured. Run 'haoyue provider add' or edit ~/.haoyue/config.json.",
                retryable: false);
        return candidates[0];
    }

    public ModelInfo? ResolveModel(string reference) => registry.Resolve(reference);

    public IReadOnlyList<ModelInfo> BuildCandidates(WorkspaceConfig? workspace = null)
    {
        var config = configStore.Config;
        var explicitRefs = new List<string>();

        // 1. Workspace override beats everything.
        if (!string.IsNullOrWhiteSpace(workspace?.Model))
            explicitRefs.Add(Qualify(workspace!.Model!, workspace.Provider ?? config.Provider));

        // 2. Global active model selection.
        if (!string.IsNullOrWhiteSpace(config.Model))
            explicitRefs.Add(Qualify(config.Model!, config.Provider));

        // 3. Global fallback chain.
        var chain = explicitRefs
            .Concat(config.Routing.Fallback)
            .Select(registry.Resolve)
            .Where(m => m is not null && m.Provider.Enabled)
            .Select(m => m!)
            .DistinctBy(m => m.Ref, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (chain.Count == 0)
            chain = registry.All().ToList();

        // Skip open circuits, but never filter down to nothing.
        var healthy = chain.Where(m => !Breaker.IsOpen(m.Ref)).ToList();
        return healthy.Count > 0 ? healthy : chain;
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        Func<ModelInfo, LlmRequest> requestFactory,
        WorkspaceConfig? workspace,
        [EnumeratorCancellation] CancellationToken ct,
        Func<ModelInfo, bool>? candidateFilter = null)
    {
        var candidates = candidateFilter is null
            ? BuildCandidates(workspace)
            : BuildCandidates(workspace).Where(candidateFilter).ToList();
        if (candidates.Count == 0)
            throw new LlmException(
                candidateFilter is null ? "No models configured." : "No compatible models configured.",
                retryable: false);

        // Optional automatic failover: when disabled, only the active model is tried and a
        // failed request stops the turn with the real error instead of switching to another
        // provider/model (routing.failoverEnabled, default on).
        if (!configStore.Config.Routing.FailoverEnabled && candidates.Count > 1)
            candidates = candidates.Take(1).ToList();

        LlmException? lastError = null;
        // Every candidate failure is remembered so the final error can explain each model
        // that was tried (the active model first) instead of surfacing only the last
        // fallback's error -- e.g. a default cloud provider that has no API key configured.
        var failures = new List<(string Ref, string Error)>();

        // Snapshot the retry parameters once per chain so retries never mix old and new
        // values mid-flight when a config reload lands between attempts, and clamp them
        // so a bad config cannot crash the turn with a non-LlmException type.
        var retry = Retry;
        var maxAttempts = Math.Max(1, retry.MaxAttempts);
        // Wall-clock budget for the whole chain: without it the worst case (candidates ×
        // attempts × header timeout) could stretch to tens of minutes before the user
        // sees any error. Once the budget is gone, new attempts are not started. The
        // lower clamp only guards against zero/negative misconfigurations — a small
        // explicit budget is honored as-is (which also keeps the exhausted-budget path
        // unit-testable without sleeping for seconds).
        var chainDeadline = DateTimeOffset.UtcNow
            + TimeSpan.FromSeconds(Math.Clamp(retry.ChainBudgetSeconds, 0.5, 600));
        var attemptsStarted = 0;

        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var model = candidates[candidateIndex];
            if (attemptsStarted > 0 && DateTimeOffset.UtcNow >= chainDeadline)
            {
                failures.Add((model.Ref, "failover chain budget exhausted; attempt skipped"));
                continue;
            }

            if (candidateIndex > 0)
                eventBus.Publish(new ProviderSwitchedEvent(
                    candidates[candidateIndex - 1].Ref, model.Ref, lastError?.Message ?? "failover"));

            // The active model gets the full MaxAttempts; fallback candidates get a single
            // attempt by default (retrying each of them the full MaxAttempts times multiplies
            // the wait without meaningfully improving the odds), configurable via
            // retry.fallbackRetryAttempts for providers whose transient 429s need one retry.
            var candidateAttempts = candidateIndex == 0
                ? maxAttempts
                : 1 + Math.Clamp(retry.FallbackRetryAttempts, 0, 5);

            for (var attempt = 1; attempt <= candidateAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var stopwatch = Stopwatch.StartNew();
                var committed = false;
                LlmCompletion? completion = null;
                IAsyncEnumerator<LlmStreamEvent>? stream = null;

                // The finally below disposes the provider stream on every exit path,
                // including when the consumer abandons the iterator mid-stream (for
                // example Ctrl+C while a turn is still in "thinking"). Without it the
                // response body/connection would stay open until the provider times out.
                try
                {
                    // Chain-level wall-clock budget: once exhausted, no new attempt is
                    // started (an in-flight stream still runs to completion or failure).
                    if (attemptsStarted > 0 && DateTimeOffset.UtcNow >= chainDeadline)
                    {
                        failures.Add((model.Ref, "failover chain budget exhausted; attempt skipped"));
                        goto NextCandidate;
                    }
                    attemptsStarted++;

                    // Half-open single-probe gate: when the circuit is cooling down, one
                    // caller wins the probe and the rest skip the model instead of
                    // thundering-herd probing a failing provider.
                    if (!Breaker.TryBeginProbe(model.Ref))
                    {
                        failures.Add((model.Ref, "circuit open (cooling down); attempt skipped"));
                        goto NextCandidate;
                    }

                    stream = TryOpenStream(model, requestFactory, ct, out var builtRequest);

                    while (true)
                    {
                        LlmStreamEvent? evt;
                        try
                        {
                            if (!await stream.MoveNextAsync().ConfigureAwait(false)) break;
                            evt = stream.Current;
                        }
                        catch (LlmException ex) when (!committed && ex.Retryable && !ct.IsCancellationRequested)
                        {
                            lastError = ex;
                            NoteToolRejection(model, builtRequest, ex);
                            Breaker.RecordFailure(model.Ref, ex.StatusCode);
                            RecordUsage(model, null, stopwatch, success: false);

                            if (attempt < candidateAttempts)
                            {
                                var delay = BackoffDelay(attempt, retry);
                                // An explicit Retry-After hint from the provider outruns
                                // our own backoff guess.
                                if (ex.RetryAfter is { } hint && hint > delay) delay = hint;
                                // Never sleep past the chain budget: cap the backoff to the
                                // remaining wall-clock time and bail out if it is spent.
                                var remaining = chainDeadline - DateTimeOffset.UtcNow;
                                if (remaining <= TimeSpan.Zero)
                                {
                                    failures.Add((model.Ref, ex.Message));
                                    goto NextCandidate;
                                }
                                if (delay > remaining) delay = remaining;
                                eventBus.Publish(new ProviderRetryEvent(model.Ref, attempt, ex.Message, delay));
                                await Task.Delay(delay, ct).ConfigureAwait(false);
                            }
                            goto NextAttempt;
                        }
                        catch (LlmException ex)
                        {
                            NoteToolRejection(model, builtRequest, ex);
                            Breaker.RecordFailure(model.Ref, ex.StatusCode);
                            RecordUsage(model, completion, stopwatch, success: false);
                            // Once the stream has yielded tokens we can no longer switch providers
                            // mid-answer, so propagate immediately. A non-retryable failure of the
                            // active model (bad key, invalid request...) is also surfaced directly
                            // instead of being hidden by a silent failover. But a fallback candidate
                            // rejecting the request (e.g. HTTP 401 from an unconfigured default)
                            // must not abort the remaining candidates: record it and keep walking.
                            if (committed || (candidateIndex == 0 && !ex.Retryable)) throw;
                            lastError = ex;
                            failures.Add((model.Ref, ex.Message));
                            goto NextCandidate;
                        }

                        committed = true;
                        if (evt is LlmCompleted done) completion = done.Completion;
                        yield return evt;
                    }

                    Breaker.RecordSuccess(model.Ref);
                    RecordUsage(model, completion, stopwatch, success: true, builtRequest);
                    yield break;

                    NextAttempt: ;
                }
                finally
                {
                    if (stream is not null)
                        await stream.DisposeAsync().ConfigureAwait(false);
                }
            }

            // All retries for this candidate were exhausted; remember the failure too.
            if (lastError is not null && !failures.Exists(failure => failure.Ref == model.Ref))
                failures.Add((model.Ref, lastError.Message));

            NextCandidate: ;
        }

        throw BuildFailoverException(failures, lastError);
    }

    /// <summary>
    /// Builds the terminal error when every candidate model failed. A single failure keeps its
    /// original message; multiple failures are aggregated in candidate order (the active model
    /// first) so clients see the real cause instead of a confusing fallback error.
    /// </summary>
    private static LlmException BuildFailoverException(
        IReadOnlyList<(string Ref, string Error)> failures,
        LlmException? lastError)
    {
        if (failures.Count == 0)
            return lastError ?? new LlmException("All providers failed.", retryable: false);
        if (failures.Count == 1)
            return lastError ?? new LlmException(failures[0].Error, retryable: false);

        var builder = new StringBuilder();
        builder.Append($"All candidate models failed after automatic failover ({failures.Count} tried):");
        foreach (var (refName, error) in failures)
        {
            builder.Append('\n');
            builder.Append("- ");
            builder.Append(refName);
            builder.Append(": ");
            builder.Append(error);
        }
        return new LlmException(builder.ToString(), lastError?.StatusCode, retryable: false, inner: lastError);
    }

    public async Task<(bool Success, string Detail, double LatencyMs)> TestModelAsync(
        ModelInfo model, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var client = clientFactory.GetClient(model.Provider.Kind);
            var request = new LlmRequest
            {
                Provider = model.Provider,
                Model = model.Model,
                Messages = [ChatMessage.User("Reply with the single word: ok")],
                MaxTokens = 16,
            };

            var text = "";
            await foreach (var evt in client.StreamAsync(request, ct).ConfigureAwait(false))
                if (evt is LlmCompleted done)
                    text = done.Completion.Text;

            stopwatch.Stop();
            return (true, string.IsNullOrWhiteSpace(text) ? "(empty reply)" : text.Trim(), stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is LlmException or HttpRequestException or OperationCanceledException)
        {
            stopwatch.Stop();
            return (false, ex.Message, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    public async Task<IReadOnlyList<string>> FetchModelsAsync(
        ProviderConfig provider,
        string? url = null,
        CancellationToken ct = default)
    {
        // A local provider has no model endpoint — the models directory is the catalogue.
        if (provider.IsLocal)
            return LocalModels.ScanIds(provider.ModelsDirectory);

        var endpoint = string.IsNullOrWhiteSpace(url)
            ? provider.Kind.Equals("anthropic", StringComparison.OrdinalIgnoreCase)
                ? LlmUrl.JoinV1(provider.BaseUrl, "models")
                : LlmUrl.Join(provider.BaseUrl, "models")
            : url.Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            throw new LlmException($"Invalid model list URL: {endpoint}", retryable: false);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyModelListHeaders(request, provider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(provider.TimeoutSeconds, 5, 120)));

        HttpResponseMessage response;
        try
        {
            response = await httpFactory.GetClient(provider)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException($"Fetching models from {provider.Id} timed out.", retryable: false);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"Cannot fetch models from {provider.Id}: {ex.Message}", retryable: false, inner: ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = body.Length > 240 ? body[..240] : body;
                throw new LlmException(
                    $"Model list request failed for {provider.Id} (HTTP {(int)response.StatusCode}): {detail}",
                    retryable: false);
            }

            JsonNode? root;
            try { root = JsonNode.Parse(body); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                throw new LlmException($"Model list response from {provider.Id} is not valid JSON.", retryable: false, inner: ex);
            }

            var array = root as JsonArray;
            if (array is null && root is JsonObject rootObject)
                array = rootObject["data"] as JsonArray ?? rootObject["models"] as JsonArray;
            if (array is null)
                throw new LlmException($"Model list response from {provider.Id} does not contain a model array.", retryable: false);

            var ids = array
                .Select(ModelId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ids.Count == 0)
                throw new LlmException($"Model list response from {provider.Id} contains no model IDs.", retryable: false);
            return ids;
        }
    }

    /// <inheritdoc />
    public Task<EmbeddingResult?> EmbedAsync(string input, string? modelRef = null, CancellationToken ct = default)
        => EmbedBatchAsync([input], modelRef, ct);

    /// <inheritdoc />
    public bool HasEmbeddingTarget(string? modelRef = null)
    {
        try
        {
            return ResolveEmbeddingTarget(modelRef) is not null;
        }
        catch (LlmException)
        {
            // 引用写错（提供商或模型 id 不存在）同样是「没有可用嵌入目标」。
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<EmbeddingResult?> EmbedBatchAsync(
        IReadOnlyList<string> inputs, string? modelRef = null, CancellationToken ct = default)
    {
        if (inputs.Count == 0) return new EmbeddingResult([], string.Empty);

        var target = ResolveEmbeddingTarget(modelRef);
        if (target is null) return null; // nothing embedding-capable configured — caller degrades

        var client = clientFactory.GetClient(target.Provider.Kind);
        return await client.EmbedAsync(target.Provider, inputs, target.Model.Id, ct).ConfigureAwait(false);
    }

    private ModelInfo? ResolveEmbeddingTarget(string? modelRef)
    {
        if (!string.IsNullOrWhiteSpace(modelRef))
            return registry.Resolve(modelRef!)
                ?? throw new LlmException($"Unknown embedding model '{modelRef}'.", retryable: false);

        // First enabled model flagged capabilities.embedding wins (priority order from the registry).
        return registry.All().FirstOrDefault(m => m.Capabilities.Embedding);
    }

    // ---------------------------------------------------------------- helpers

    private readonly ConcurrentDictionary<string, ToolCallingSupport> _toolCallingCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 工具调用能力探测——零流量设计：本地提供方直接判 Unsupported（LocalLlmClient
    /// 不解析 tools），用户显式关闭的能力同样不探测；远程提供方不主动发探测请求，
    /// 而是在真实请求被提供方以 tools 相关错误拒绝时（<see cref="NoteToolRejection"/>）
    /// 记入缓存。Unknown 表示尚无证据，调用方保持旧行为（继续注入）。
    /// </summary>
    public Task<ToolCallingSupport> ProbeToolCallingAsync(ModelInfo model, CancellationToken ct = default)
    {
        if (_toolCallingCache.TryGetValue(model.Ref, out var cached))
            return Task.FromResult(cached);

        // 用户显式关闭的能力不需要探测——按未支持处理。
        if (!model.Model.Capabilities.ToolCalling)
            return Task.FromResult(Cache(model.Ref, ToolCallingSupport.Unsupported));

        // 本地 GGUF 通道完全忽略 request.Tools（没有原生工具协议解析），
        // 注入 schema 只会消耗上下文且让模型"空谈工具"。
        if (model.Provider.Kind.Equals("local", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Cache(model.Ref, ToolCallingSupport.Unsupported));

        return Task.FromResult(ToolCallingSupport.Unknown);
    }

    /// <summary>
    /// 远程提供方拒绝了带 tools 的请求时记录该模型不支持工具调用，后续回合不再注入
    /// 工具 schema（第一次拒绝已经付出了真实错误代价，此处只是不再重复犯错）。
    /// </summary>
    private void NoteToolRejection(ModelInfo model, LlmRequest? request, LlmException ex)
    {
        if (request?.Tools is not { Count: > 0 }) return;
        if (!LooksLikeToolRejection(ex.Message)) return;
        Cache(model.Ref, ToolCallingSupport.Unsupported);
    }

    private static bool LooksLikeToolRejection(string message)
    {
        var text = message.ToLowerInvariant();
        return text.Contains("tool")
            && (text.Contains("not support") || text.Contains("unsupported") || text.Contains("unknown")
                || text.Contains("invalid") || text.Contains("unrecognized") || text.Contains("400")
                || text.Contains("422"));
    }

    private ToolCallingSupport Cache(string ref_, ToolCallingSupport support)
    {
        _toolCallingCache[ref_] = support;
        return support;
    }

    private IAsyncEnumerator<LlmStreamEvent> TryOpenStream(
        ModelInfo model, Func<ModelInfo, LlmRequest> requestFactory, CancellationToken ct, out LlmRequest request)
    {
        var client = clientFactory.GetClient(model.Provider.Kind);
        request = requestFactory(model);
        return client.StreamAsync(request, ct).GetAsyncEnumerator(ct);
    }

    private static void ApplyModelListHeaders(HttpRequestMessage request, ProviderConfig provider)
    {
        var key = provider.ResolveApiKey();
        if (provider.Kind.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(key)) request.Headers.TryAddWithoutValidation("x-api-key", key);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else if (!string.IsNullOrWhiteSpace(key))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }
        if (!string.IsNullOrWhiteSpace(provider.Organization))
            request.Headers.TryAddWithoutValidation("OpenAI-Organization", provider.Organization);
        if (provider.Headers is not null)
            foreach (var (name, value) in provider.Headers)
                request.Headers.TryAddWithoutValidation(name, value);
    }

    private static string? ModelId(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        if (node is not JsonObject model) return null;
        foreach (var key in new[] { "id", "name", "model" })
        {
            if (model[key] is JsonValue candidate && candidate.TryGetValue<string>(out var id))
                return id;
        }
        return null;
    }

    private void RecordUsage(ModelInfo model, LlmCompletion? completion, Stopwatch stopwatch, bool success, LlmRequest? request = null)
    {
        stopwatch.Stop();
        var usage = completion?.Usage ?? new TokenUsage(0, 0);
        // 本地推理性能遥测（InferenceMeter 实测）随用量一起落账：TTFT / 加载等待 /
        // 净 prefill / decode / 缓存命中率——从此 usage.jsonl 与实时事件都是消费方。
        var perf = completion?.Performance;
        usageTracker.Record(new UsageEntry
        {
            Provider = model.Provider.Id,
            Model = model.Model.Id,
            InputTokens = usage.InputTokens,
            TotalInputTokens = usage.TotalInputTokens,
            CachedInputTokens = usage.CachedInputTokens,
            CacheCreationInputTokens = usage.CacheCreationInputTokens,
            OutputTokens = usage.OutputTokens,
            // 按模型定价折算真实成本入库：此前恒为 0，用量账本的费用列只能由下游另算，
            // 回合预算闸门（agent.turnCostBudget）也无处取数。
            Cost = ComputeCost(model.Model, usage),
            ElapsedMs = stopwatch.Elapsed.TotalMilliseconds,
            Success = success,
            TtftMs = perf is null ? null : perf.TtftMs,
            LoadWaitMs = perf is null ? null : perf.LoadWaitMs,
            PrefillTokensPerSecond = perf is null ? null : perf.PrefillTokensPerSecond,
            DecodeTokensPerSecond = perf is null ? null : perf.DecodeTokensPerSecond,
            CacheHitRate = perf is null ? null : perf.CacheHitRate,
            PrefixReuse = perf is null ? null : perf.PrefixReuse,
        });

        // H-E: token 估算校准闭环。provider 回传的真实 prompt tokens 与发送前的本地
        // 估算配对，EWMA 沉淀为 per-model 系数；ContextPlanner 下一次估算即按该系数
        // 收敛。遥测属性：任何存储失败都不允许影响请求结果。
        if (success && _tokenStats is not null && request is not null
            && completion?.Usage is { TotalInputTokens: > 0 } reported)
        {
            try
            {
                _tokenStats.Record(
                    model.Provider.Id, model.Model.Id, reported.TotalInputTokens,
                    Haoyue.Runtime.Agents.ContextPlanner.EstimateRequestTokens(request));
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
            {
            }
        }
    }

    public static decimal ComputeCost(ModelConfig model, TokenUsage usage) =>
        (model.InputPricePerMTok * usage.InputTokens + model.OutputPricePerMTok * usage.OutputTokens) / 1_000_000m;

    private static TimeSpan BackoffDelay(int attempt, RetryConfig retry)
    {
        // Clamps guarantee Task.Delay never sees a negative value from a bad config
        // (which would crash the turn with an ArgumentOutOfRangeException instead of
        // a controlled LlmException).
        var baseDelay = Math.Max(0, retry.BaseDelaySeconds) * Math.Pow(2, attempt - 1);
        var jitter = Random.Shared.NextDouble() * 0.5 + 0.75; // 0.75x – 1.25x
        return TimeSpan.FromSeconds(Math.Min(baseDelay * jitter, Math.Max(0, retry.MaxDelaySeconds)));
    }

    private static string Qualify(string modelRef, string? providerId) =>
        modelRef.Contains('/') || string.IsNullOrWhiteSpace(providerId) ? modelRef : $"{providerId}/{modelRef}";

}
