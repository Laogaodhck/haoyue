using Haoyue.Runtime.Configuration;
using LLama;
using LLama.Common;

namespace Haoyue.Runtime.Providers;

/// <summary>
/// Keeps at most one GGUF model loaded in process. Local inference is serialized: a lease
/// holds the cache gate from load until the caller disposes it, so the weights can never be
/// evicted while a generation is still running.
/// </summary>
public sealed class LocalModelCache : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string Path, LLamaWeights Weights)? _loaded;

    /// <summary>Loads (or reuses) the model file and returns an exclusive lease on it.</summary>
    public async Task<LocalModelLease> AcquireAsync(string path, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded is null || !string.Equals(_loaded.Value.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                _loaded?.Weights.Dispose();
                _loaded = null;
                var parameters = new ModelParams(path)
                {
                    GpuLayerCount = 0,
                    UseMemorymap = true,
                };
                var weights = await Task.Run(() => LLamaWeights.LoadFromFile(parameters), ct).ConfigureAwait(false);
                _loaded = (path, weights);
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

    public void Dispose()
    {
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
}
