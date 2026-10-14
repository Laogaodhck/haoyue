using System.Globalization;
using System.Text;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Providers;

namespace Haoyue.Tests;

/// <summary>
/// 本地模型加载五维优化（懒加载/共享/并发/回退/缓存）的机制测试。
/// 不加载真实 GGUF 权重（CI 无模型文件）：覆盖元数据缓存、指纹持久化、版本门、
/// 驱逐计划、回退链解析、进度插值与加载重试路径（文件缺失）。
/// 真实权重的端到端基准见 LocalModelLoadBenchmarkTests（HAOYUE_LOCAL_MODEL_BENCH=1）。
/// </summary>
public sealed class LocalModelLoadingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-load-tests", Guid.NewGuid().ToString("N"));

    public LocalModelLoadingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // ---------------------------------------------------------------- 元数据缓存

    [Fact]
    public void HeaderCache_HitsOnce_AndInvalidatesOnMtimeChange()
    {
        var cache = new LocalModelMetadataCache();
        var path = WriteGguf("m.gguf", version: 3);
        var fixedMtime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, fixedMtime);

        var first = cache.Header(path);
        var second = cache.Header(path);
        Assert.Equal(1, cache.HeaderReads); // 第二次命中缓存，零 IO
        Assert.Equal("qwen3", second.Architecture);
        Assert.Equal(3, second.Version);

        // 同大小同 mtime 重写文件：缓存语义锁定为「不重读」（替换模型必须换 mtime）。
        // 注意内容必须等长——缓存键含文件大小，长度变化本身就是失效信号。
        File.WriteAllBytes(path, CreateGgufBytes(version: 3, name: "Tiny Test ModeX"));
        File.SetLastWriteTimeUtc(path, fixedMtime);
        Assert.Equal("Tiny Test Model", cache.Header(path).Name);
        Assert.Equal(1, cache.HeaderReads);

        // mtime 变化（正常替换文件的路径）→ 缓存失效重读。
        File.WriteAllBytes(path, CreateGgufBytes(version: 3, name: "Tiny Test ModeX"));
        File.SetLastWriteTimeUtc(path, fixedMtime.AddSeconds(1));
        Assert.Equal("Tiny Test ModeX", cache.Header(path).Name);
        Assert.Equal(2, cache.HeaderReads);
    }

    [Fact]
    public void HeaderCache_MissingFile_ReturnsEmptyWithoutThrowing()
    {
        var cache = new LocalModelMetadataCache();
        var header = cache.Header(Path.Combine(_dir, "absent.gguf"));
        Assert.Null(header.Architecture);
        Assert.Null(header.Version);
        Assert.Equal(0, cache.HeaderReads); // 不存在的文件不触盘
    }

    [Fact]
    public void MmprojCache_ScansOncePerDirectoryState()
    {
        var cache = new LocalModelMetadataCache();
        var dir = Path.Combine(_dir, "models");
        Directory.CreateDirectory(dir);

        // 无投影器：解析为 null 并记录一次扫描。
        Assert.Null(cache.ResolveMmproj(dir, null));
        Assert.Equal(1, cache.DirectoryScans);
        Assert.Null(cache.ResolveMmproj(dir, null));
        Assert.Equal(1, cache.DirectoryScans); // 目录未变，命中缓存

        // 放入唯一投影器：目录 mtime 变化触发重扫。
        WriteGguf(Path.Combine(dir, "model-mmproj-F16.gguf"), version: 3);
        Assert.EndsWith("model-mmproj-F16.gguf", cache.ResolveMmproj(dir, null)!);
        Assert.Equal(2, cache.DirectoryScans);

        // 第二个投影器：歧义，重新扫描后解析为 null（与 LocalModels 既有语义一致）。
        WriteGguf(Path.Combine(dir, "other-mmproj-F16.gguf"), version: 3);
        Assert.Null(cache.ResolveMmproj(dir, null));
        Assert.Equal(3, cache.DirectoryScans);

        // 显式路径优先，且不触发目录扫描。
        var explicitPath = WriteGguf("explicit-mmproj.gguf", version: 3);
        Assert.Equal(Path.GetFullPath(explicitPath), cache.ResolveMmproj(dir, explicitPath));
        Assert.Equal(3, cache.DirectoryScans);
    }

    // ---------------------------------------------------------------- 指纹持久化

    [Fact]
    public void Fingerprints_RoundTripThroughDisk_WithMedianEta()
    {
        var file = Path.Combine(_dir, "fingerprints.json");
        var path = WriteGguf("eta.gguf", version: 3);

        var writer = new LocalModelFingerprints(file);
        writer.RecordSuccess(path, "sig-a", 1_000, "build-1", 3, "qwen3");
        writer.RecordSuccess(path, "sig-a", 3_000, "build-1", 3, "qwen3");
        writer.RecordSuccess(path, "sig-a", 2_000, "build-1", 3, "qwen3");

        var reader = new LocalModelFingerprints(file);
        Assert.Equal(2_000, reader.EstimatedLoadMs(path)); // 中位数，非均值
        Assert.Equal("sig-a", reader.LastGoodSignature(path));
        Assert.Equal("build-1", reader.BackendBuildAtLastSuccess(path));
    }

    [Fact]
    public void Fingerprints_FailureHistory_ResetsOnSuccess()
    {
        var file = Path.Combine(_dir, "fingerprints.json");
        var path = WriteGguf("fail.gguf", version: 3);
        var store = new LocalModelFingerprints(file);

        Assert.Null(store.LastFailure(path));
        store.RecordFailure(path, "boom-1");
        store.RecordFailure(path, "boom-2");
        Assert.Equal(2, store.ConsecutiveFailures(path));
        Assert.Contains("boom-2", store.LastFailure(path)!.Value.Error);

        store.RecordSuccess(path, "sig", 500, "build", 3, "qwen3");
        Assert.Equal(0, store.ConsecutiveFailures(path));
        Assert.Null(store.LastFailure(path));
    }

    [Fact]
    public void Fingerprints_CorruptFile_RebuildsInsteadOfThrowing()
    {
        var file = Path.Combine(_dir, "fingerprints.json");
        File.WriteAllText(file, "{ this is not json");
        var store = new LocalModelFingerprints(file);
        Assert.Null(store.EstimatedLoadMs("whatever.gguf"));

        // 自愈后仍可正常记录。
        store.RecordFailure("whatever.gguf", "later failure");
        Assert.Equal(1, store.ConsecutiveFailures("whatever.gguf"));
    }

    [Fact]
    public void Fingerprints_Forget_DropsEntry()
    {
        var file = Path.Combine(_dir, "fingerprints.json");
        var path = WriteGguf("forget.gguf", version: 3);
        var store = new LocalModelFingerprints(file);
        store.RecordSuccess(path, "sig", 800, "build", 3, "qwen3");
        store.Forget(path);
        Assert.Null(store.EstimatedLoadMs(path));
        Assert.Null(new LocalModelFingerprints(file).EstimatedLoadMs(path));
    }

    // ---------------------------------------------------------------- 版本兼容

    [Fact]
    public void Compatibility_RejectsFutureGgufVersion()
    {
        var (fatal, warning) = ModelCompatibility.Check(new GgufProbe.Header(Architecture: "qwen3", Version: 4));
        Assert.NotNull(fatal);
        Assert.Contains("GGUF 格式版本 4", fatal);
        Assert.Null(warning);

        // 已支持版本不拦截。
        var (fatal3, _) = ModelCompatibility.Check(new GgufProbe.Header(Architecture: "qwen3", Version: 3));
        Assert.Null(fatal3);
        var (fatal2, _) = ModelCompatibility.Check(new GgufProbe.Header(Architecture: "llama", Version: 2));
        Assert.Null(fatal2);
    }

    [Fact]
    public void Compatibility_WarnsOnUnreadableOrIncompleteHeaders()
    {
        var (fatal, warning) = ModelCompatibility.Check(new GgufProbe.Header());
        Assert.Null(fatal);
        Assert.NotNull(warning); // header 全空：提示但不拦截（原生层会给错误）

        var (fatal2, warning2) = ModelCompatibility.Check(new GgufProbe.Header(Version: 3));
        Assert.Null(fatal2);
        Assert.Contains("architecture", warning2!);
    }

    [Fact]
    public void Compatibility_DiagnosesBackendAbiDrift()
    {
        var file = Path.Combine(_dir, "fingerprints.json");
        var path = WriteGguf("abi.gguf", version: 3);

        // 与当前后端构建一致：无诊断。
        var same = new LocalModelFingerprints(file);
        same.RecordSuccess(path, "sig", 900, ModelCompatibility.BackendBuild, 3, "qwen3");
        Assert.Null(ModelCompatibility.DiagnoseFailure(same, path));

        // 不同构建：点明 ABI 漂移。
        var drift = new LocalModelFingerprints(file);
        drift.RecordSuccess(path, "sig", 900, "9.9.9-foreign-build", 3, "qwen3");
        var diagnosis = ModelCompatibility.DiagnoseFailure(drift, path);
        Assert.NotNull(diagnosis);
        Assert.Contains("9.9.9-foreign-build", diagnosis);
    }

    // ---------------------------------------------------------------- 驱逐计划

    [Fact]
    public void EvictionPlan_PicksLeastRecentlyUsed_IdleSlotsOnly()
    {
        var slots = new List<LocalModelCache.SlotSnapshot>
        {
            new("a.gguf", RefCount: 0, LastUsedTicks: 100, PendingDispose: false),
            new("b.gguf", RefCount: 0, LastUsedTicks: 300, PendingDispose: false),
            new("c.gguf", RefCount: 0, LastUsedTicks: 200, PendingDispose: false),
        };

        // 预算 2：淘汰最久未用的 a。
        var plan = LocalModelCache.PlanEviction(slots, 2);
        Assert.Equal(["a.gguf"], plan);

        // 预算 1：按 LRU 顺序淘汰 a、c，保留最新的 b。
        Assert.Equal(["a.gguf", "c.gguf"], LocalModelCache.PlanEviction(slots, 1));

        // 预算内不动。
        Assert.Empty(LocalModelCache.PlanEviction(slots, 3));
    }

    [Fact]
    public void EvictionPlan_NeverSelectsServingOrPendingDisposeSlots()
    {
        var slots = new List<LocalModelCache.SlotSnapshot>
        {
            new("serving.gguf", RefCount: 2, LastUsedTicks: 100, PendingDispose: false),
            new("unloading.gguf", RefCount: 0, LastUsedTicks: 150, PendingDispose: true),
            new("idle.gguf", RefCount: 0, LastUsedTicks: 200, PendingDispose: false),
        };
        // 全部受保护：宁可超预算也不驱逐服务中的权重。
        Assert.Empty(LocalModelCache.PlanEviction(slots, 1, protectKey: "idle.gguf"));
        Assert.Equal(["idle.gguf"], LocalModelCache.PlanEviction(slots, 1));
    }

    // ---------------------------------------------------------------- 回退链

    [Fact]
    public void FallbackChain_ResolvesWithinProvider_AndNeverChains()
    {
        var primary = new ModelConfig { Id = "main.gguf" };
        var backup = new ModelConfig { Id = "small.gguf" };
        var provider = new ProviderConfig
        {
            Id = "local", Kind = "local", Models = [primary, backup],
        };

        // 未配置回退：链长为 1。
        Assert.Single(FallbackChain.Build(provider, primary));

        // 配置且可解析：主 + 备。
        primary.FallbackModelId = "small.gguf";
        var chain = FallbackChain.Build(provider, primary);
        Assert.Equal(2, chain.Count);
        Assert.Same(primary, chain[0]);
        Assert.Same(backup, chain[1]);

        // 链深度封顶 2：Build 只做一层解析（LocalLlmClient 也只按主模型构建一次链），
        // 备用模型自身的 FallbackModelId 不会在同请求内继续展开。
        backup.FallbackModelId = "main.gguf";
        Assert.Equal(2, FallbackChain.Build(provider, primary).Count);
        Assert.Equal(2, FallbackChain.Build(provider, backup).Count);

        // 自引用 / 未知 id：退化为单元素链。
        primary.FallbackModelId = "main.gguf";
        Assert.Single(FallbackChain.Build(provider, primary));
        primary.FallbackModelId = "ghost.gguf";
        Assert.Single(FallbackChain.Build(provider, primary));
    }

    // ---------------------------------------------------------------- 进度插值

    [Theory]
    [InlineData(LocalLoadPhase.Queued, 0.0)]
    [InlineData(LocalLoadPhase.Probing, 0.05)]
    [InlineData(LocalLoadPhase.Loading, 0.55)]
    [InlineData(LocalLoadPhase.Verifying, 0.75)]
    [InlineData(LocalLoadPhase.Projector, 0.90)]
    [InlineData(LocalLoadPhase.Ready, 1.0)]
    [InlineData(LocalLoadPhase.Failed, 1.0)]
    public void Progress_WithoutEta_StaysAtPhaseBound(LocalLoadPhase phase, double expected)
    {
        Assert.Equal(expected, LocalLoadProgress.InterpolatedPercent(phase, elapsedMs: 0, remainingMs: null));
    }

    [Fact]
    public void Progress_WithEta_InterpolatesInsidePhase()
    {
        // Loading 阶段边界 0.55..0.75；已 1000ms / 共 3000ms → 1/3 处 ≈ 0.6167。
        var percent = LocalLoadProgress.InterpolatedPercent(LocalLoadPhase.Loading, 1_000, remainingMs: 2_000);
        Assert.InRange(percent, 0.61, 0.62);

        // ETA 耗尽后钉在阶段上界（下一阶段事件重新锚定）。
        var late = LocalLoadProgress.InterpolatedPercent(LocalLoadPhase.Loading, 9_000, remainingMs: 0);
        Assert.Equal(0.75, late);
    }

    // ---------------------------------------------------------------- 运行时策略

    [Fact]
    public void Configure_ClampsAndTogglesReuseSupport()
    {
        using var cache = new LocalModelCache(
            new LocalModelFingerprints(Path.Combine(_dir, "fp.json")),
            new LocalModelMetadataCache());

        Assert.Equal(1, cache.MaxConcurrency);
        Assert.Equal(2, cache.MaxResidentModels);
        Assert.True(cache.SupportsPrefixReuse); // 串行 = KV 复用可用
        Assert.False(cache.IsLoaded);
        Assert.Empty(cache.Snapshot());

        cache.Configure(5, 4, 0);
        Assert.Equal(5, cache.MaxResidentModels);
        Assert.Equal(4, cache.MaxConcurrency);
        Assert.False(cache.SupportsPrefixReuse); // 并发 = 每请求独立 context

        // 钳制：越界值拉回合法域。
        cache.Configure(0, 99, -5);
        Assert.Equal(1, cache.MaxResidentModels);
        Assert.Equal(16, cache.MaxConcurrency);
        Assert.Equal(0, cache.IdleUnloadMinutes);

        cache.Configure(2, 1, 20);
        Assert.True(cache.SupportsPrefixReuse);
    }

    [Fact]
    public async Task UnloadIdle_NoSlotsOrZeroThreshold_IsNoOp()
    {
        using var cache = new LocalModelCache(
            new LocalModelFingerprints(Path.Combine(_dir, "fp.json")),
            new LocalModelMetadataCache());
        await cache.UnloadIdleAsync(TimeSpan.FromMinutes(30));
        await cache.UnloadIdleAsync(TimeSpan.Zero); // 关闭语义：直接返回
        Assert.False(cache.IsLoaded);
    }

    // ---------------------------------------------------------------- 加载路径

    [Fact]
    public async Task PreloadAsync_FutureGgufVersion_RejectedBeforeNativeLoad()
    {
        // v4 文件：版本门在 llama.cpp 之前拒绝，不触碰原生加载器。
        var path = WriteGguf("future.gguf", version: 4);
        using var cache = new LocalModelCache(
            new LocalModelFingerprints(Path.Combine(_dir, "fp.json")),
            new LocalModelMetadataCache());

        var error = await Assert.ThrowsAsync<LlmException>(() => cache.PreloadAsync(
            path, _ => new LLama.Common.ModelParams(path), "sig", null, null, CancellationToken.None));
        Assert.Contains("GGUF 格式版本 4", error.Message);
        Assert.False(cache.IsLoaded);

        // 失败进指纹：连续失败计数可见（model.config.get 的 lastFailure 数据源）。
        Assert.Equal(1, cache.Fingerprints.ConsecutiveFailures(path));
    }

    [Fact]
    public async Task AcquireAsync_MissingFile_RetriesThenReports()
    {
        // 文件不存在：原生加载器两次尝试均失败 → LlmException（含尝试次数），
        // 且不留下任何槽位。
        var missing = Path.Combine(_dir, "absent.gguf");
        using var cache = new LocalModelCache(
            new LocalModelFingerprints(Path.Combine(_dir, "fp.json")),
            new LocalModelMetadataCache());

        var error = await Assert.ThrowsAsync<LlmException>(() => cache.AcquireAsync(
            missing, _ => new LLama.Common.ModelParams(missing), "sig", null, CancellationToken.None));
        Assert.Contains("absent.gguf", error.Message);
        Assert.Contains("2 次尝试", error.Message);
        Assert.False(cache.IsLoaded);
        Assert.Equal(1, cache.Fingerprints.ConsecutiveFailures(missing));
    }

    [Fact]
    public async Task LoadProgressObserved_FiresWithoutCallerProgress()
    {
        // 对话回合走的是 progress: null 的 AcquireAsync。旁路事件必须照常上报，
        // 否则冷加载一两分钟内界面毫无反馈，看起来就像「发消息没回复」。
        var missing = Path.Combine(_dir, "silent.gguf");
        using var cache = new LocalModelCache(
            new LocalModelFingerprints(Path.Combine(_dir, "fp.json")),
            new LocalModelMetadataCache());
        var phases = new List<LocalLoadPhase>();
        cache.LoadProgressObserved += progress => phases.Add(progress.Phase);

        await Assert.ThrowsAsync<LlmException>(() => cache.AcquireAsync(
            missing, _ => new LLama.Common.ModelParams(missing), "sig", null, CancellationToken.None));

        Assert.Contains(LocalLoadPhase.Loading, phases);
        Assert.Contains(LocalLoadPhase.Failed, phases);
    }

    [Fact]
    public async Task PreloadAsync_Progress_ReportsPhasesInOrder()
    {
        // 版本门路径同样产出进度事件（Probing → Failed）。
        var path = WriteGguf("progress.gguf", version: 4);
        using var cache = new LocalModelCache(
            new LocalModelFingerprints(Path.Combine(_dir, "fp.json")),
            new LocalModelMetadataCache());
        var phases = new List<LocalLoadPhase>();
        var progress = new Progress<LocalLoadProgress>(p => phases.Add(p.Phase));

        await Assert.ThrowsAsync<LlmException>(() => cache.PreloadAsync(
            path, _ => new LLama.Common.ModelParams(path), "sig", null, progress, CancellationToken.None));

        Assert.Contains(LocalLoadPhase.Probing, phases);
    }

    // ---------------------------------------------------------------- fixture

    private string WriteGguf(string fileName, int version = 3)
    {
        var path = Path.IsPathRooted(fileName) ? fileName : Path.Combine(_dir, fileName);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, CreateGgufBytes(version));
        return path;
    }

    private static byte[] CreateGgufBytes(int version = 3, string name = "Tiny Test Model")
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(0x46554747u); // "GGUF"
        writer.Write((uint)version);
        writer.Write(0UL); // tensor count
        writer.Write(3UL); // kv pairs
        WritePair(writer, "general.architecture", "qwen3");
        WritePair(writer, "general.name", name);
        WritePair(writer, "qwen3.context_length", 131_072u);
        writer.Flush();
        return stream.ToArray();

        static void WritePair(BinaryWriter writer, string key, object value)
        {
            WriteString(writer, key);
            switch (value)
            {
                case string text:
                    writer.Write(8u);
                    WriteString(writer, text);
                    break;
                case uint number:
                    writer.Write(4u);
                    writer.Write(number);
                    break;
            }
        }

        static void WriteString(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.Write((ulong)bytes.Length);
            writer.Write(bytes);
        }
    }
}
