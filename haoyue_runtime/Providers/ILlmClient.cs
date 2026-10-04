namespace Haoyue.Runtime.Providers;

/// <summary>Embedding vectors for one batch of inputs, in input order.</summary>
public sealed record EmbeddingResult(IReadOnlyList<float[]> Vectors, string Model);

/// <summary>Streams a chat completion for one provider protocol (openai / anthropic).</summary>
public interface ILlmClient
{
    string Kind { get; }
    IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request, CancellationToken ct);

    /// <summary>
    /// Embeds a batch of inputs. Returns null when this provider kind has no
    /// embeddings capability, so callers degrade gracefully (e.g. fall back to
    /// keyword search) instead of failing the request.
    /// </summary>
    Task<EmbeddingResult?> EmbedAsync(
        Configuration.ProviderConfig provider, IReadOnlyList<string> inputs, string? model = null, CancellationToken ct = default);
}

public interface ILlmClientFactory
{
    ILlmClient GetClient(string kind);
}

public sealed class LlmClientFactory(IEnumerable<ILlmClient> clients) : ILlmClientFactory
{
    private readonly Dictionary<string, ILlmClient> _clients =
        clients.ToDictionary(c => c.Kind, StringComparer.OrdinalIgnoreCase);

    public ILlmClient GetClient(string kind) =>
        _clients.TryGetValue(kind, out var client)
            ? client
            : throw new LlmException($"No LLM client registered for provider kind '{kind}'.", retryable: false);
}
