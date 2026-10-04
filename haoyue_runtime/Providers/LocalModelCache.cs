using Haoyue.Runtime.Configuration;
using LLama;
using LLama.Common;
using LLama.Native;

namespace Haoyue.Runtime.Providers;

/// <summary>
/// Keeps at most one GGUF model loaded in process. Local inference is serialized: a lease
/// holds the cache gate from load until the caller disposes it, so the weights can never be
/// evicted while a generation is still running. The cache also owns the KV-reuse context so
/// its lifetime is tied to the weights: switching models or disposing the cache releases it.
/// </summary>
public sealed class LocalModelCache : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string Path, string? LoadSignature, LLamaWeights Weights, ReuseEntry? Reuse)? _loaded;

    /// <summary>
    /// Loads (or reuses) the model file and returns an exclusive lease on it. When the
    /// caller passes a <paramref name="loadSignature"/> that differs from the one the
    /// current weights were loaded with (e.g. GPU offload changed in settings), the
    /// weights are reloaded even though the model path is unchanged.
    /// </summary>
    public async Task<LocalModelLease> AcquireAsync(
        string path, ModelParams? loadParams = null, string? loadSignature = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded is null
                || !string.Equals(_loaded.Value.Path, path, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_loaded.Value.LoadSignature, loadSignature, StringComparison.Ordinal))
            {
                _loaded?.Reuse?.Context.Dispose();
                _loaded?.Weights.Dispose();
                _loaded = null;
                var parameters = loadParams ?? new ModelParams(path)
                {
                    GpuLayerCount = 0,
                    UseMemorymap = true,
                };
                var weights = await Task.Run(() => LLamaWeights.LoadFromFile(parameters), ct).ConfigureAwait(false);
                _loaded = (path, loadSignature, weights, null);
                return new LocalModelLease(this, weights);
            }
            return new LocalModelLease(this, _loaded.Value.Weights);
        }
        catch (OperationCanceledException)
        {
            _gate.Release();
            throw;
        }
        catch (Exception ex) when (ex is not LlmException)
        {
            _gate.Release();
            throw new LlmException(
                $"Failed to load local model '{Path.GetFileName(path)}': {ex.Message}",
                retryable: false,
                inner: ex);
        }
    }

    private void Release() => _gate.Release();

    /// <summary>
    /// Returns the reusable inference state (live context + tokens currently decoded into
    /// its KV cache) when it belongs to the loaded model and was created with the same
    /// context parameters. Returns null when absent or stale; a stale entry is discarded.
    /// Only valid while a lease is held (local inference is serialized).
    /// </summary>
    public ReuseEntry? TryGetReuse(LLamaWeights weights, string signature)
    {
        if (_loaded is null || !ReferenceEquals(_loaded.Value.Weights, weights)) return null;
        var reuse = _loaded.Value.Reuse;
        if (reuse is null) return null;
        if (reuse.Signature != signature)
        {
            reuse.Context.Dispose();
            _loaded = _loaded.Value with { Reuse = null };
            return null;
        }
        return reuse;
    }

    /// <summary>Stores (or replaces) the KV-reuse state after a successful generation.</summary>
    public void StoreReuse(LLamaWeights weights, ReuseEntry entry)
    {
        if (_loaded is null || !ReferenceEquals(_loaded.Value.Weights, weights))
        {
            entry.Context.Dispose();
            return;
        }
        _loaded.Value.Reuse?.Context.Dispose();
        _loaded = _loaded.Value with { Reuse = entry };
    }

    /// <summary>Drops the KV-reuse state after a failed or interrupted generation.</summary>
    public void DropReuse(LLamaWeights weights)
    {
        if (_loaded is null || !ReferenceEquals(_loaded.Value.Weights, weights)) return;
        _loaded.Value.Reuse?.Context.Dispose();
        _loaded = _loaded.Value with { Reuse = null };
    }

    public void Dispose()
    {
        _loaded?.Reuse?.Context.Dispose();
        _loaded?.Weights.Dispose();
        _loaded = null;
        _gate.Dispose();
    }

    /// <summary>Holds the model loaded and blocks other local generations until disposed.</summary>
    public sealed class LocalModelLease : IDisposable
    {
        private LocalModelCache? _owner;

        internal LocalModelLease(LocalModelCache owner, LLamaWeights weights)
        {
            _owner = owner;
            Weights = weights;
        }

        public LLamaWeights Weights { get; }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release();
        }
    }

    /// <summary>
    /// A live LLamaContext whose KV cache holds exactly the tokens listed in
    /// <see cref="Tokens"/>, plus the context-parameter signature it was created with.
    /// </summary>
    public sealed class ReuseEntry
    {
        public required LLamaContext Context { get; init; }
        public required List<LLamaToken> Tokens { get; init; }
        public required string Signature { get; init; }
    }
}
