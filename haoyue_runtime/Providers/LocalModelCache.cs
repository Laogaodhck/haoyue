using Haoyue.Runtime.Configuration;
using LLama;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace Haoyue.Runtime.Providers;

/// <summary>一次模型加载的结果报告：重试次数、是否降级 CPU、推理自检是否通过、耗时。</summary>
public sealed record LocalModelLoadReport
{
    /// <summary>实际生效的加载参数（降级后可能是 CPU-only）。</summary>
    public required string EffectiveGpuLayers { get; init; }
    /// <summary>总加载尝试次数（含重试与降级）。</summary>
    public required int Attempts { get; init; }
    /// <summary>GPU 加载失败后降级为 CPU-only 时为 true。</summary>
    public bool DegradedToCpu { get; init; }
    /// <summary>加载后小上下文推理自检通过。</summary>
    public bool Verified { get; init; }
    /// <summary>加载 + 自检总耗时（毫秒）。</summary>
    public long LoadDurationMs { get; init; }
    /// <summary>附加说明（投影器加载情况、降级原因等）。</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Keeps at most one GGUF model loaded in process. Local inference is serialized: a lease
/// holds the cache gate from load until the caller disposes it, so the weights can never be
/// evicted while a generation is still running. The cache also owns the KV-reuse context so
/// its lifetime is tied to the weights: switching models or disposing the cache releases it.
///
/// 加载增强（对应模型配置页与稳定性需求）：
/// 1) 失败自动重试 —— 加载失败先原参数重试一次（瞬时 IO/显存抖动），仍失败且配置了 GPU
///    卸载时降级为 CPU-only 再试（大模型显存不足时的可用性兜底），全部失败才向上抛错；
/// 2) 加载后自动验证 —— 用 512 窗口的小上下文做一次真实 decode 自检，权重损坏、上下文
///    创建失败（典型为显存不足以容纳 KV cache）在加载阶段即暴露，而不是等到第一次推理；
/// 3) 资源及时释放 —— 每次尝试的半初始化资源（weights / mtmd / 验证上下文）都在 finally
///    与降级间隙显式释放，失败路径不泄漏显存与内存；
/// 4) 懒加载与低峰值 —— 权重仅在实际请求到达时加载（mmap 映射按页读盘，避免整模型读入
///    常驻内存），并支持主动 <see cref="Unload"/> 在空闲时归还内存。
/// </summary>
public sealed class LocalModelCache : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string Path, string? LoadSignature, LLamaWeights Weights, MtmdWeights? Mtmd, ReuseEntry? Reuse, LocalModelLoadReport? Report)? _loaded;

    /// <summary>最近一次成功加载的报告（供 daemon model.status / 配置页展示）。</summary>
    public LocalModelLoadReport? LastLoadReport => _loaded?.Report;

    /// <summary>当前是否已有模型常驻（供配置页展示内存占用状态）。</summary>
    public bool IsLoaded => _loaded is not null;

    /// <summary>
    /// Loads (or reuses) the model file and returns an exclusive lease on it. When the
    /// caller passes a <paramref name="loadSignature"/> that differs from the one the
    /// current weights were loaded with (e.g. GPU offload changed in settings), the
    /// weights are reloaded even though the model path is unchanged. When
    /// <paramref name="mmprojPath"/> points to a valid multimodal projector, it is loaded
    /// alongside the weights and exposed on the lease; a broken projector degrades to
    /// text-only instead of failing every request.
    /// <paramref name="loadParamsFactory"/> receives the degradation flag and must return
    /// the load parameters to use (CPU-only variant when the flag is set).
    /// </summary>
    public async Task<LocalModelLease> AcquireAsync(
        string path, Func<bool, ModelParams>? loadParamsFactory = null, string? loadSignature = null,
        string? mmprojPath = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        // gate 所有权：成功返回时随 Lease 一起移交（Lease.Dispose 释放）；任何失败路径
        // 都在 finally 里恰好释放一次，绝不双重释放或泄漏。
        var gateHeld = true;
        try
        {
            if (_loaded is null
                || !string.Equals(_loaded.Value.Path, path, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_loaded.Value.LoadSignature, loadSignature, StringComparison.Ordinal))
            {
                ReleaseLoadedState();
                var (weights, mtmd, report) = await LoadWithRetryAsync(path, loadParamsFactory, mmprojPath, ct).ConfigureAwait(false);
                _loaded = (path, loadSignature, weights, mtmd, null, report);
                gateHeld = false;
                return new LocalModelLease(this, weights);
            }
            return new LocalModelLease(this, _loaded.Value.Weights);
        }
        finally
        {
            if (gateHeld) _gate.Release();
        }
    }

    /// <summary>兼容旧调用方：单一 ModelParams 版本（无降级能力时重试仍生效）。</summary>
    public Task<LocalModelLease> AcquireAsync(
        string path, ModelParams? loadParams = null, string? loadSignature = null,
        string? mmprojPath = null, CancellationToken ct = default) =>
        AcquireAsync(path, _ => loadParams ?? new ModelParams(path) { GpuLayerCount = 0, UseMemorymap = true },
            loadSignature, mmprojPath, ct);

    /// <summary>
    /// Loads with bounded retries: attempt 1 uses the requested parameters, attempt 2
    /// retries the same parameters once (transient IO/VRAM contention), attempt 3 falls
    /// back to CPU-only when GPU offload was requested. Every attempt disposes its
    /// partially-initialized resources before the next starts; the last error is
    /// surfaced as an <see cref="LlmException"/> with each attempt's message.
    /// </summary>
    private async Task<(LLamaWeights Weights, MtmdWeights? Mtmd, LocalModelLoadReport Report)> LoadWithRetryAsync(
        string path, Func<bool, ModelParams>? loadParamsFactory, string? mmprojPath, CancellationToken ct)
    {
        var factory = loadParamsFactory ?? (_ => new ModelParams(path) { GpuLayerCount = 0, UseMemorymap = true });
        var requested = factory(false);
        var wantsGpu = requested.GpuLayerCount > 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var attempts = new List<string>();
        var totalAttempts = wantsGpu ? 3 : 2;

        for (var attempt = 1; attempt <= totalAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var cpuOnly = attempt >= 3 && wantsGpu;
            var attemptParams = cpuOnly ? factory(true) : requested;
            LLamaWeights? weights = null;
            MtmdWeights? mtmd = null;
            try
            {
                weights = await Task.Run(() => LLamaWeights.LoadFromFile(attemptParams), ct).ConfigureAwait(false);

                // 加载后自检：一次真实 tokenize + decode。上下文创建失败（显存放不下 KV
                // cache）与权重损坏在这里暴露，触发重试 / 降级而不是第一次对话报错。
                VerifyInference(weights, path, attemptParams);

                mtmd = LoadProjector(mmprojPath, weights);
                stopwatch.Stop();
                return (weights, mtmd, new LocalModelLoadReport
                {
                    EffectiveGpuLayers = cpuOnly ? "0 (降级 CPU)" : attemptParams.GpuLayerCount.ToString(),
                    Attempts = attempt,
                    DegradedToCpu = cpuOnly && wantsGpu,
                    Verified = true,
                    LoadDurationMs = stopwatch.ElapsedMilliseconds,
                    Note = mtmd is not null
                        ? "多模态投影器已加载（视觉输入可用）"
                        : wantsGpu && cpuOnly
                            ? "GPU 加载失败，已降级为 CPU 推理"
                            : null,
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < totalAttempts)
            {
                attempts.Add($"第 {attempt} 次尝试{(cpuOnly ? "（CPU 降级）" : "")}: {ex.Message}");
                // 半初始化资源立刻释放，避免重试间隙累积占用内存 / 显存。
                weights?.Dispose();
                // 重试前短暂退避，给显存释放与 IO 队列一个整理窗口。
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                weights?.Dispose();
                attempts.Add($"第 {attempt} 次尝试{(cpuOnly ? "（CPU 降级）" : "")}: {ex.Message}");
                throw new LlmException(
                    $"加载本地模型 '{Path.GetFileName(path)}' 失败（共 {attempt} 次尝试，"
                    + (wantsGpu ? "含 CPU 降级重试" : "含 1 次重试") + "）：" + string.Join("；", attempts),
                    retryable: false,
                    inner: ex);
            }
        }

        throw new LlmException(
            $"加载本地模型 '{Path.GetFileName(path)}' 失败：" + string.Join("；", attempts),
            retryable: false);
    }

    /// <summary>
    /// Post-load self-check: a 512-window context with the same GPU layout runs one real
    /// tokenize + decode pass. This proves the weights can actually infer before the model
    /// is reported as loaded, without allocating a full-size KV cache.
    /// </summary>
    private static void VerifyInference(LLamaWeights weights, string path, ModelParams loadParams)
    {
        var verifyParams = new ModelParams(path)
        {
            // 自检窗口远小于业务上下文：验证权重与 GPU 布局可用，KV 开销可忽略。
            ContextSize = 512,
            GpuLayerCount = loadParams.GpuLayerCount,
            Threads = loadParams.Threads,
            FlashAttention = loadParams.FlashAttention,
            UseMemorymap = true,
        };
        LLamaContext? context = null;
        try
        {
            context = weights.CreateContext(verifyParams, NullLogger.Instance);
            var tokens = context.Tokenize("ping", true, true).ToList();
            if (tokens.Count == 0)
                throw new InvalidOperationException("模型自检失败：探测提示词被分词为 0 个 token，权重可能已损坏。");
            var batch = new LLamaBatch();
            for (var i = 0; i < tokens.Count; i++)
                batch.Add(tokens[i], i, LLamaSeqId.Zero, i == tokens.Count - 1);
            var result = context.Decode(batch);
            if (result != DecodeResult.Ok)
                throw new InvalidOperationException($"模型自检失败：decode 返回 {result}，模型无法正常推理。");
        }
        finally
        {
            context?.Dispose();
        }
    }

    /// <summary>
    /// Loads the multimodal projector when a valid mmproj file exists; a broken projector
    /// degrades to text-only (带图请求稍后会收到明确的配置错误) instead of failing the load.
    /// </summary>
    private static MtmdWeights? LoadProjector(string? mmprojPath, LLamaWeights weights)
    {
        if (string.IsNullOrWhiteSpace(mmprojPath) || !File.Exists(mmprojPath)) return null;
        try
        {
            // 视觉编码器固定跑 CPU：8GB 显存装 5.3GB 主权重后放不下 f16 编码器，
            // 而 CPU 编码单张截图只多花几秒，换取 VRAM 不溢出。
            return MtmdWeights.LoadFromFile(mmprojPath, weights, new MtmdContextParams
            {
                UseGpu = false,
                Warmup = false,
            });
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 主动卸载当前常驻权重（空闲时归还内存 / 显存，支持更大模型轮流加载）。
    /// 与 AcquireAsync 串行：lease 存续期间调用会等待当前推理结束。
    /// </summary>
    public async Task UnloadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ReleaseLoadedState();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ReleaseLoadedState()
    {
        _loaded?.Reuse?.Context.Dispose();
        _loaded?.Mtmd?.Dispose();
        _loaded?.Weights.Dispose();
        _loaded = null;
    }

    private void Release() => _gate.Release();

    /// <summary>Returns the reusable inference state (live context + tokens currently decoded into
    /// its KV cache) when it belongs to the loaded model and was created with the same
    /// context parameters. Returns null when absent or stale; a stale entry is discarded.
    /// Only valid while a lease is held (local inference is serialized).</summary>
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
            // 复用路径会把 TryGetReuse 返回的同一个 entry 存回来；此时绝不能 Dispose 它
            // 自己的 Context，否则下一个请求拿到的是已释放的 context（第二轮即崩）。
            var existing = _loaded.Value.Reuse;
            if (!ReferenceEquals(existing, entry))
                existing?.Context.Dispose();
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
        ReleaseLoadedState();
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
            // Lease 期间缓存不会被换模型（本地推理串行化），可直接引用 Mtmd。
            Mtmd = owner._loaded?.Mtmd;
            Report = owner._loaded?.Report;
        }

        public LLamaWeights Weights { get; }

        /// <summary>Multimodal projector when a valid mmproj file was loaded; null = text-only.</summary>
        public MtmdWeights? Mtmd { get; }

        /// <summary>本次加载的报告（重试 / 降级 / 自检信息）。</summary>
        public LocalModelLoadReport? Report { get; }

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
