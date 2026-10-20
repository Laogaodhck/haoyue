using System.Collections.Concurrent;
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
    /// <summary>附加说明（投影器加载情况、降级原因、兼容性提示等）。</summary>
    public string? Note { get; init; }
}

/// <summary>常驻模型槽的一次状态快照（model.config.get / 管理面板展示）。</summary>
public sealed record ModelResidency(
    string Path,
    string? LoadSignature,
    long WeightBytes,
    int RefCount,
    DateTimeOffset LastUsedUtc,
    LocalModelLoadReport? Report);

/// <summary>
/// 进程内 GGUF 权重库（懒加载 / 权重共享 / 并发支持 / 错误恢复 / 缓存策略五维优化的载体）。
///
/// 与「单模型 + 全局串行门」旧结构的关键差异：
/// 1) **多模型 LRU 驻留**：最多 <see cref="MaxResidentModels"/> 个模型同时驻留
///    （默认 2）。聊天模型与嵌入模型交替请求不再互相卸载重载；超出预算时按
///    「最久未用 + 无活跃 lease」驱逐，绝不驱逐正在服务的权重。加载签名变化
///    （调档 / 改配置）走同一套槽位机制。
/// 2) **加载与推理解耦**：<c>_loadGate</c> 只包住加载/驱逐（秒~分钟级）；
///    <c>_inferenceGate</c> 是 <see cref="MaxConcurrency"/> 个并发生成槽（默认 1 =
///    与旧行为一致的严格串行）。并发 &gt; 1 时每请求独立 context，KV 前缀复用自动
///    关闭（<see cref="SupportsPrefixReuse"/>）。
/// 3) **进度与 ETA**：加载过程按阶段回调 <see cref="LocalLoadProgress"/>（含基于
///    历史耗时指纹的剩余时间估计），daemon 转发为 model.load.progress 广播。
/// 4) **错误恢复**：原参数重试 → CPU 降级重试（既有），叠加指纹诊断（后端 ABI
///    变化 / 连续失败历史）与版本门（GGUF v&gt;3 早期拒绝）。
/// 5) **空闲回收**：<see cref="UnloadIdleAsync"/> 按配置归还长时间空闲的权重。
///
/// 既有增强全部保留：失败重试 + GPU→CPU 降级、加载后 512 窗口真实 decode 自检、
/// 半初始化资源即时释放、mmap 懒读盘、KV 复用上下文与权重同生命周期。
///
/// 锁序（防死锁）：_loadGate → _slotGate；快速路径与空闲回收只取 _slotGate；
/// inference 槽等待发生在 _loadGate 之外。lease 先登记（_inferenceLeases++）
/// 再等待推理槽，因此并发数变更永远看得到在途等待者，不会丢失唤醒。
/// </summary>
public sealed class LocalModelCache : IDisposable
{
    // llama.cpp 的原生日志默认直写 stdout/stderr：CLI 一次性对话时 sched_reserve /
    // llama_context 之类的调试行会混进用户可见输出，daemon 下则灌满 daemon.log。
    // 装载一次日志回调，只把 Warning 及以上转进 Debug 通道（调试时仍可见，正常使用不再污染）。
    static LocalModelCache()
    {
        try
        {
            NativeLibraryConfig.All.WithLogCallback((level, message) =>
            {
                if (level is LLamaLogLevel.Warning or LLamaLogLevel.Error)
                    System.Diagnostics.Debug.WriteLine($"[llama] {message}");
            });
        }
        catch
        {
            // 日志路由失败绝不能影响模型加载本身。
        }
    }

    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly Lock _slotGate = new();
    private readonly LocalModelMetadataCache _metadata;
    private readonly LocalModelFingerprints _fingerprints;
    private readonly SemaphoreSlim _preloadGate = new(1, 1);

    // 并发生成槽。并发数变更时在无活跃 lease 的前提下整体替换；替换发生在
    // _slotGate 内，且仅当 _inferenceLeases == 0（无持有者也无等待者——
    // 等待者在 WaitAsync 返回前就已登记为 lease）。
    private SemaphoreSlim _inferenceGate;
    private int _inferenceLeases;
    private int _pendingConcurrency;

    private LocalModelLoadReport? _lastReport;
    private bool _disposed;

    public LocalModelCache(LocalModelFingerprints? fingerprints = null, LocalModelMetadataCache? metadata = null)
    {
        _fingerprints = fingerprints ?? new LocalModelFingerprints();
        _metadata = metadata ?? new LocalModelMetadataCache();
        _inferenceGate = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
    }

    /// <summary>自适应推理调优器（P3/P4/P5）：随缓存单例存活，语义与旧版一致。</summary>
    public AdaptiveInferenceTuner Tuning { get; } = new();

    /// <summary>元数据缓存（header / mmproj 解析）。LocalLlmClient 热路径共用。</summary>
    public LocalModelMetadataCache Metadata => _metadata;

    /// <summary>加载指纹（ETA / 版本诊断 / 失败历史）。</summary>
    public LocalModelFingerprints Fingerprints => _fingerprints;

    /// <summary>同时驻留的模型数上限（LRU，1..16）。默认 2 = 聊天 + 嵌入互不重载。</summary>
    public int MaxResidentModels { get; private set; } = 2;

    /// <summary>并发生成槽数（1..16）。默认 1 = 本地推理严格串行（旧行为）。</summary>
    public int MaxConcurrency { get; private set; } = 1;

    /// <summary>空闲回收阈值（分钟）。0 = 不回收。由 daemon 维护周期读取后传入 UnloadIdleAsync。</summary>
    public int IdleUnloadMinutes { get; private set; } = 20;

    /// <summary>并发 &gt; 1 时每请求独立 context，KV 前缀复用不可用（调用方据此改走无状态路径）。</summary>
    public bool SupportsPrefixReuse => MaxConcurrency <= 1;

    /// <summary>最近一次成功加载的报告（供 daemon model.status / 配置页展示）。</summary>
    public LocalModelLoadReport? LastLoadReport => _lastReport;

    /// <summary>当前是否有模型常驻（供配置页展示内存占用状态）。</summary>
    public bool IsLoaded => !_slots.IsEmpty;

    /// <summary>常驻模型数。</summary>
    public int ResidentCount => _slots.Count;

    /// <summary>
    /// 运行时策略配置（daemon 启动 / 配置变更时调用）。并发数变更有活跃 lease 时
    /// 延后到最后一个 lease 释放后生效——绝不抢夺正在服务的生成槽。
    /// </summary>
    public void Configure(int maxResidentModels, int concurrency, int idleUnloadMinutes)
    {
        MaxResidentModels = Math.Clamp(maxResidentModels, 1, 16);
        IdleUnloadMinutes = Math.Clamp(idleUnloadMinutes, 0, 24 * 60);
        var slots = Math.Clamp(concurrency, 1, 16);
        if (slots == MaxConcurrency) return;
        lock (_slotGate)
        {
            if (_inferenceLeases == 0)
                SwapInferenceGate(slots);
            else
                _pendingConcurrency = slots;
        }
    }

    /// <summary>
    /// 加载进度旁路：任何一次权重加载（含对话回合的懒加载）都会上报，不依赖调用方
    /// 是否传入 IProgress。daemon 订阅它把进度广播给客户端——冷加载要两分钟以上，
    /// 没有反馈时界面看起来就像「发消息没反应」。
    /// </summary>
    public event Action<LocalLoadProgress>? LoadProgressObserved;

    /// <summary>
    /// 加载（或复用）模型文件并返回其独占 lease。加载签名与当前权重不一致时重新
    /// 加载；不一致的旧权重不立即卸载，交给 LRU / 空闲回收——嵌入与聊天模型交替
    /// 时双方都保持驻留。mmproj 有效时随权重加载并在 lease 上暴露；投影器损坏
    /// 降级为纯文本而不是让每次请求失败。
    /// </summary>
    public Task<LocalModelLease> AcquireAsync(
        string path, Func<bool, ModelParams>? loadParamsFactory = null, string? loadSignature = null,
        string? mmprojPath = null, CancellationToken ct = default) =>
        AcquireAsync(path, loadParamsFactory, loadSignature, mmprojPath, progress: null, ct);

    /// <summary>带加载进度的获取入口（预热 / daemon model.preload 用）。</summary>
    public async Task<LocalModelLease> AcquireAsync(
        string path, Func<bool, ModelParams>? loadParamsFactory, string? loadSignature,
        string? mmprojPath, IProgress<LocalLoadProgress>? progress, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var slot = await ResolveSlotAsync(path, loadParamsFactory, loadSignature, mmprojPath, progress, ct)
            .ConfigureAwait(false);
        // lease 先登记再等待推理槽：Configure 的并发数替换因此永远看得到在途等待者。
        var lease = new LocalModelLease(this, slot);
        try
        {
            await lease.WaitForSlotAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            lease.Abandon();
            throw;
        }
        return lease;
    }

    /// <summary>兼容旧调用方：单一 ModelParams 版本（无降级能力时重试仍生效）。</summary>
    public Task<LocalModelLease> AcquireAsync(
        string path, ModelParams? loadParams, string? loadSignature,
        string? mmprojPath, CancellationToken ct) =>
        AcquireAsync(path, _ => loadParams ?? new ModelParams(path) { GpuLayerCount = 0, UseMemorymap = true },
            loadSignature, mmprojPath, null, ct);

    /// <summary>
    /// 预热：把权重装进槽位但不持有 lease、不占用生成槽。单航班——进行中的加载
    /// 后续调用直接等待同一结果。已驻留（签名一致）时零成本返回。失败抛
    /// LlmException，调用方（daemon 启动路径）自行决定吞掉或广播。
    /// </summary>
    public async Task<LocalModelLoadReport> PreloadAsync(
        string path, Func<bool, ModelParams>? loadParamsFactory, string? loadSignature,
        string? mmprojPath = null, IProgress<LocalLoadProgress>? progress = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _preloadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var key = SlotKey(path, loadSignature);
            if (_slots.TryGetValue(key, out var existing) && !existing.PendingDispose)
                return existing.Report;
            await _loadGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_slots.TryGetValue(key, out var raced) && !raced.PendingDispose)
                    return raced.Report;
                var (weights, mtmd, report) = await LoadWithRetryAsync(
                    path, loadParamsFactory, loadSignature, mmprojPath, progress, ct).ConfigureAwait(false);
                PutSlot(key, path, loadSignature, weights, mtmd, report);
                EvictUnderBudget(key);
                return report;
            }
            finally
            {
                _loadGate.Release();
            }
        }
        finally
        {
            _preloadGate.Release();
        }
    }

    /// <summary>
    /// 主动卸载全部常驻权重（配置变更 / 管理员手动回收）。有活跃 lease 的槽标记
    /// 待销毁，由最后一个 lease 释放时真正释放原生资源——不死等推理结束，也不
    /// 在推理中途抽走权重。
    /// </summary>
    public async Task UnloadAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<Slot> toDispose;
            lock (_slotGate)
            {
                toDispose = [];
                foreach (var pair in _slots)
                {
                    if (!_slots.TryRemove(pair.Key, out var slot)) continue;
                    if (Volatile.Read(ref slot.RefCount) == 0)
                        toDispose.Add(slot);
                    else
                        slot.PendingDispose = true; // 最后一个 lease 释放时收尾
                }
            }
            foreach (var slot in toDispose) DisposeSlot(slot);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    /// 归还空闲超过 <paramref name="idle"/> 的权重（daemon 维护周期调用）。
    /// idle 为非正值时直接返回（关闭语义）。只清理无活跃 lease 的槽。
    /// </summary>
    public Task UnloadIdleAsync(TimeSpan idle, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (idle <= TimeSpan.Zero) return Task.CompletedTask;
        var cutoff = DateTimeOffset.UtcNow - idle;
        List<Slot> victims;
        lock (_slotGate)
        {
            victims = [];
            foreach (var slot in _slots.Values)
            {
                if (slot.PendingDispose || Volatile.Read(ref slot.RefCount) > 0) continue;
                if (new DateTimeOffset(Volatile.Read(ref slot.LastUsedTicks), TimeSpan.Zero) >= cutoff) continue;
                if (_slots.TryRemove(slot.Key, out _)) victims.Add(slot);
            }
        }
        foreach (var slot in victims) DisposeSlot(slot);
        return Task.CompletedTask;
    }

    /// <summary>常驻快照（管理面板 / model.config.get）。</summary>
    public IReadOnlyList<ModelResidency> Snapshot()
    {
        lock (_slotGate)
        {
            return _slots.Values
                .Select(slot => new ModelResidency(
                    slot.Path, slot.LoadSignature, slot.WeightBytes,
                    Volatile.Read(ref slot.RefCount),
                    new DateTimeOffset(Volatile.Read(ref slot.LastUsedTicks), TimeSpan.Zero),
                    slot.Report))
                .OrderBy(residency => residency.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    // ---------------------------------------------------------------- slot 解析

    private async Task<Slot> ResolveSlotAsync(
        string path, Func<bool, ModelParams>? loadParamsFactory, string? loadSignature,
        string? mmprojPath, IProgress<LocalLoadProgress>? progress, CancellationToken ct)
    {
        var key = SlotKey(path, loadSignature);

        // 快速路径：已驻留且签名一致——只取 _slotGate 做引用计数，不碰加载门。
        var hit = TryAcquireSlotRef(key);
        if (hit is not null) return hit;

        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 双检：等待加载门期间可能已被并行请求装入。
            hit = TryAcquireSlotRef(key);
            if (hit is not null) return hit;

            var (weights, mtmd, report) = await LoadWithRetryAsync(
                path, loadParamsFactory, loadSignature, mmprojPath, progress, ct).ConfigureAwait(false);
            var slot = PutSlot(key, path, loadSignature, weights, mtmd, report);
            EvictUnderBudget(key);
            return AcquireRef(slot);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private Slot? TryAcquireSlotRef(string key)
    {
        lock (_slotGate)
        {
            if (!_slots.TryGetValue(key, out var slot) || slot.PendingDispose) return null;
            Interlocked.Increment(ref slot.RefCount);
            Volatile.Write(ref slot.LastUsedTicks, DateTimeOffset.UtcNow.Ticks);
            return slot;
        }
    }

    private Slot AcquireRef(Slot slot)
    {
        lock (_slotGate)
        {
            Interlocked.Increment(ref slot.RefCount);
            Volatile.Write(ref slot.LastUsedTicks, DateTimeOffset.UtcNow.Ticks);
            return slot;
        }
    }

    private Slot PutSlot(string key, string path, string? loadSignature, LLamaWeights weights, MtmdWeights? mtmd, LocalModelLoadReport report)
    {
        long weightBytes = 0;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists) weightBytes = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 文件大小仅用于展示与预算，取不到不影响加载。
        }
        var slot = new Slot
        {
            Key = key,
            Path = path,
            LoadSignature = loadSignature,
            Weights = weights,
            Mtmd = mtmd,
            Report = report,
            WeightBytes = weightBytes,
            LastUsedTicks = DateTimeOffset.UtcNow.Ticks,
        };
        lock (_slotGate) _slots[key] = slot;
        _lastReport = report;
        return slot;
    }

    /// <summary>
    /// LRU 驱逐：仅选「无活跃 lease + 未标记待销毁」的槽，按最久未用排序，
    /// 直到驻留数回到预算内。无合格受害者时允许临时超预算——绝不驱逐正在服务的
    /// 权重，也绝不因内存预算让请求失败。
    /// </summary>
    private void EvictUnderBudget(string protectKey)
    {
        List<Slot> victims;
        lock (_slotGate)
        {
            if (_slots.Count <= MaxResidentModels)
                return;
            var snapshots = _slots.Values.Select(ToSnapshot).ToList();
            var condemned = PlanEviction(snapshots, MaxResidentModels, protectKey);
            victims = condemned
                .Select(key => _slots.TryGetValue(key, out var slot) ? slot : null)
                .Where(slot => slot is not null)
                .Select(slot => slot!)
                .ToList();
            foreach (var victim in victims) _slots.TryRemove(victim.Key, out _);
        }
        foreach (var victim in victims) DisposeSlot(victim);
    }

    private static SlotSnapshot ToSnapshot(Slot slot) => new(
        slot.Key, Volatile.Read(ref slot.RefCount), Volatile.Read(ref slot.LastUsedTicks), slot.PendingDispose);

    /// <summary>
    /// 驱逐计划（纯函数，测试直连）：按 LRU 选出可安全驱逐的槽位键。选择条件：
    /// 无活跃 lease、未标记待销毁、非受保护键（通常是刚加载完成的键）。
    /// </summary>
    internal static List<string> PlanEviction(IReadOnlyList<SlotSnapshot> slots, int maxResident, string? protectKey = null)
    {
        var overflow = slots.Count - maxResident;
        if (overflow <= 0) return [];
        return slots
            .Where(slot => slot.RefCount == 0 && !slot.PendingDispose
                && (protectKey is null || !string.Equals(slot.Key, protectKey, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(slot => slot.LastUsedTicks)
            .Take(overflow)
            .Select(slot => slot.Key)
            .ToList();
    }

    /// <summary>驱逐计划视图（与 <see cref="Slot"/> 解耦，便于测试与推理）。</summary>
    internal readonly record struct SlotSnapshot(string Key, int RefCount, long LastUsedTicks, bool PendingDispose);

    // ---------------------------------------------------------------- lease 记账

    private void OnLeaseReleased(Slot slot)
    {
        Slot? orphan = null;
        lock (_slotGate)
        {
            _inferenceLeases--;
            if (Interlocked.Decrement(ref slot.RefCount) == 0 && slot.PendingDispose)
                orphan = slot;
            if (_inferenceLeases == 0 && _pendingConcurrency > 0)
            {
                SwapInferenceGate(_pendingConcurrency);
                _pendingConcurrency = 0;
            }
        }
        if (orphan is not null) DisposeSlot(orphan);
    }

    private void OnLeaseAbandoned(Slot slot)
    {
        // 等待推理槽期间被取消：释放槽位引用，不动推理槽（从未获得）。
        Slot? orphan = null;
        lock (_slotGate)
        {
            _inferenceLeases--;
            if (Interlocked.Decrement(ref slot.RefCount) == 0 && slot.PendingDispose)
                orphan = slot;
        }
        if (orphan is not null) DisposeSlot(orphan);
    }

    private void SwapInferenceGate(int slots)
    {
        var old = _inferenceGate;
        _inferenceGate = new SemaphoreSlim(slots, slots);
        MaxConcurrency = slots;
        old.Dispose();
    }

    private void DisposeSlot(Slot slot)
    {
        try
        {
            slot.Reuse?.Context.Dispose();
            slot.Mtmd?.Dispose();
            slot.Weights.Dispose();
        }
        catch (Exception)
        {
            // 原生资源释放失败（进程退出竞态等）不向上抛：槽已被移出字典，
            // 不会再次被访问；GC 终结器是最后兜底。
        }
    }

    private static string SlotKey(string path, string? loadSignature)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { full = path; }
        return full.ToLowerInvariant() + "\n" + (loadSignature ?? "");
    }

    // ---------------------------------------------------------------- KV 复用

    /// <summary>Returns the reusable inference state when it belongs to the loaded model and was created with the same context parameters. Returns null when absent or stale; a stale entry is discarded. Only valid while a lease is held (local inference is serialized).</summary>
    public ReuseEntry? TryGetReuse(LLamaWeights weights, string signature)
    {
        var slot = FindSlot(weights);
        if (slot is null) return null;
        var reuse = slot.Reuse;
        if (reuse is null) return null;
        if (reuse.Signature != signature)
        {
            reuse.Context.Dispose();
            slot.Reuse = null;
            return null;
        }
        return reuse;
    }

    /// <summary>Stores (or replaces) the KV-reuse state after a successful generation.</summary>
    public void StoreReuse(LLamaWeights weights, ReuseEntry entry)
    {
        var slot = FindSlot(weights);
        if (slot is null)
        {
            entry.Context.Dispose();
            return;
        }
        // 复用路径会把 TryGetReuse 返回的同一个 entry 存回来；此时绝不能 Dispose 它
        // 自己的 Context，否则下一个请求拿到的是已释放的 context（第二轮即崩）。
        var existing = slot.Reuse;
        if (!ReferenceEquals(existing, entry))
            existing?.Context.Dispose();
        slot.Reuse = entry;
    }

    /// <summary>Drops the KV-reuse state after a failed or interrupted generation.</summary>
    public void DropReuse(LLamaWeights weights)
    {
        var slot = FindSlot(weights);
        if (slot is null) return;
        slot.Reuse?.Context.Dispose();
        slot.Reuse = null;
    }

    private Slot? FindSlot(LLamaWeights weights)
    {
        foreach (var slot in _slots.Values)
            if (ReferenceEquals(slot.Weights, weights)) return slot;
        return null;
    }

    // ---------------------------------------------------------------- 加载实现

    /// <summary>
    /// Loads with bounded retries: attempt 1 uses the requested parameters, attempt 2
    /// retries the same parameters once (transient IO/VRAM contention), attempt 3 falls
    /// back to CPU-only when GPU offload was requested. Every attempt disposes its
    /// partially-initialized resources before the next starts; the last error is
    /// surfaced as an <see cref="LlmException"/> with each attempt's message, the
    /// fingerprint diagnosis (backend ABI drift) and the recent failure count.
    /// </summary>
    private async Task<(LLamaWeights Weights, MtmdWeights? Mtmd, LocalModelLoadReport Report)> LoadWithRetryAsync(
        string path, Func<bool, ModelParams>? loadParamsFactory, string? loadSignature, string? mmprojPath,
        IProgress<LocalLoadProgress>? progress, CancellationToken ct)
    {
        var factory = loadParamsFactory ?? (_ => new ModelParams(path) { GpuLayerCount = 0, UseMemorymap = true });
        var requested = factory(false);
        var wantsGpu = requested.GpuLayerCount > 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var attempts = new List<string>();
        var totalAttempts = wantsGpu ? 3 : 2;
        var eta = _fingerprints.EstimatedLoadMs(path);
        var fileName = Path.GetFileName(path);

        void Report(LocalLoadPhase phase, int attempt, bool degraded, string? message = null)
        {
            var elapsed = stopwatch.ElapsedMilliseconds;
            var remaining = eta is { } estimate ? Math.Max(0, estimate - elapsed) : (long?)null;
            var snapshot = new LocalLoadProgress
            {
                ModelPath = path,
                Phase = phase,
                Attempt = attempt,
                Degraded = degraded,
                ElapsedMs = elapsed,
                EstimatedRemainingMs = remaining,
                Message = message,
                Percent = LocalLoadProgress.InterpolatedPercent(phase, elapsed, remaining),
            };
            try
            {
                progress?.Report(snapshot);
                LoadProgressObserved?.Invoke(snapshot);
            }
            catch (Exception)
            {
                // 进度回调异常绝不影响加载本身（IProgress 默认同步调用）。
            }
        }

        // 兼容性门：GGUF 版本早期拒绝 + header 健康提示。
        var header = _metadata.Header(path);
        var (fatal, warning) = ModelCompatibility.Check(header);
        Report(LocalLoadPhase.Probing, 1, false, warning);
        if (fatal is not null)
        {
            _fingerprints.RecordFailure(path, fatal);
            throw new LlmException($"加载本地模型 '{fileName}' 被拒绝：{fatal}", retryable: false);
        }

        for (var attempt = 1; attempt <= totalAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var cpuOnly = attempt >= 3 && wantsGpu;
            var attemptParams = cpuOnly ? factory(true) : requested;
            LLamaWeights? weights = null;
            MtmdWeights? mtmd = null;
            try
            {
                Report(LocalLoadPhase.Loading, attempt, cpuOnly);
                weights = await Task.Run(() => LLamaWeights.LoadFromFile(attemptParams), ct).ConfigureAwait(false);

                // 加载后自检：一次真实 tokenize + decode。上下文创建失败（显存放不下 KV
                // cache）与权重损坏在这里暴露，触发重试 / 降级而不是第一次对话报错。
                Report(LocalLoadPhase.Verifying, attempt, cpuOnly);
                VerifyInference(weights, path, attemptParams);

                Report(LocalLoadPhase.Projector, attempt, cpuOnly);
                mtmd = LoadProjector(mmprojPath, weights);
                stopwatch.Stop();

                var note = warning;
                if (mtmd is not null)
                    note = string.IsNullOrEmpty(note) ? "多模态投影器已加载（视觉输入可用）" : note + "；多模态投影器已加载";
                else if (wantsGpu && cpuOnly)
                    note = string.IsNullOrEmpty(note) ? "GPU 加载失败，已降级为 CPU 推理" : note + "；GPU 加载失败，已降级为 CPU 推理";

                var report = new LocalModelLoadReport
                {
                    EffectiveGpuLayers = cpuOnly ? "0 (降级 CPU)" : attemptParams.GpuLayerCount.ToString(),
                    Attempts = attempt,
                    DegradedToCpu = cpuOnly && wantsGpu,
                    Verified = true,
                    LoadDurationMs = stopwatch.ElapsedMilliseconds,
                    Note = note,
                };
                _fingerprints.RecordSuccess(
                    path, loadSignature, report.LoadDurationMs,
                    ModelCompatibility.BackendBuild, header.Version, header.Architecture);
                Report(LocalLoadPhase.Ready, attempt, cpuOnly);
                return (weights, mtmd, report);
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
                _fingerprints.RecordFailure(path, ex.Message);
                var diagnosis = ModelCompatibility.DiagnoseFailure(_fingerprints, path);
                var history = _fingerprints.ConsecutiveFailures(path);
                // 终态必须上报：否则客户端的加载进度会永远停在最后一个阶段上。
                Report(LocalLoadPhase.Failed, attempt, cpuOnly, ex.Message);
                throw new LlmException(
                    $"加载本地模型 '{fileName}' 失败（共 {attempt} 次尝试，"
                    + (wantsGpu ? "含 CPU 降级重试" : "含 1 次重试") + "）：" + string.Join("；", attempts)
                    + (diagnosis is null ? "" : "。" + diagnosis)
                    + (history > 1 ? $"。（该文件近期已连续失败 {history} 次）" : ""),
                    retryable: false,
                    inner: ex);
            }
        }

        var failure = $"加载本地模型 '{fileName}' 失败：" + string.Join("；", attempts);
        _fingerprints.RecordFailure(path, failure);
        throw new LlmException(failure, retryable: false);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<Slot> all;
        lock (_slotGate)
        {
            all = _slots.Values.ToList();
            _slots.Clear();
        }
        foreach (var slot in all) DisposeSlot(slot);
        _preloadGate.Dispose();
        _loadGate.Dispose();
        lock (_slotGate) _inferenceGate.Dispose();
    }

    /// <summary>Holds the model loaded and blocks other local generations until disposed.</summary>
    public sealed class LocalModelLease : IDisposable
    {
        private readonly LocalModelCache _owner;
        private readonly Slot _slot;
        private readonly SemaphoreSlim _gate;
        private bool _slotAcquired;
        private bool _settled;

        internal LocalModelLease(LocalModelCache owner, Slot slot)
        {
            _owner = owner;
            _slot = slot;
            lock (owner._slotGate)
            {
                owner._inferenceLeases++;
                _gate = owner._inferenceGate;
            }
        }

        public LLamaWeights Weights => _slot.Weights;

        /// <summary>Multimodal projector when a valid mmproj file was loaded; null = text-only.</summary>
        public MtmdWeights? Mtmd => _slot.Mtmd;

        /// <summary>本次加载的报告（重试 / 降级 / 自检信息）。</summary>
        public LocalModelLoadReport? Report => _slot.Report;

        /// <summary>等待并持有一个并发生成槽（ AcquireAsync 内部调用；取消时不泄漏槽位引用）。</summary>
        internal Task WaitForSlotAsync(CancellationToken ct)
        {
            _slotAcquired = true;
            return _gate.WaitAsync(ct);
        }

        /// <summary>等待推理槽被取消：只撤槽位引用与登记，从未获得推理槽故不释放。</summary>
        internal void Abandon()
        {
            if (_settled) return;
            _settled = true;
            _slotAcquired = false;
            _owner.OnLeaseAbandoned(_slot);
        }

        public void Dispose()
        {
            if (_settled) return;
            _settled = true;
            if (_slotAcquired)
            {
                try { _gate.Release(); }
                catch (SemaphoreFullException) { }
                catch (ObjectDisposedException) { }
                // 缓存整体 Dispose 后 lease 才释放：信号量已销毁，无需归还。
            }
            _owner.OnLeaseReleased(_slot);
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

    internal sealed class Slot
    {
        public required string Key { get; init; }
        public required string Path { get; init; }
        public required LLamaWeights Weights { get; init; }
        public MtmdWeights? Mtmd { get; init; }
        public required LocalModelLoadReport Report { get; init; }
        public string? LoadSignature { get; init; }
        public long WeightBytes { get; init; }
        public int RefCount;
        public long LastUsedTicks;
        public volatile bool PendingDispose;
        public ReuseEntry? Reuse;
    }
}
