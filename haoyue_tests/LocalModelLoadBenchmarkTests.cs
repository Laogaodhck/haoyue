using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Providers;

namespace Haoyue.Tests;

/// <summary>
/// 本地模型加载五维优化的真实基准（opt-in）：
///   set HAOYUE_LOCAL_MODEL_BENCH=1
///   set HAOYUE_LOCAL_MODEL=&lt;主 GGUF 路径&gt;        （缺省取最小的非投影器 GGUF）
///   set HAOYUE_LOCAL_MODEL_ALT=&lt;第二 GGUF 路径&gt;   （缺省复制主模型，用于交替驻留 A/B）
///
/// 模型被复制到独立的基准目录（无 mmproj 干扰、签名确定）。维度与对照（同机同参数，
/// 唯一变量为被测量项）：
///   D1 启动速度：冷加载 vs 预热后首请求
///   D2 内存峰值：mmap 开 / 关（缓存策略）
///   D3 并发吞吐：串行(1 槽 16 线程) vs 并发(4 槽 每槽 4 线程)，8 个短生成
///   D4 多模型交替：MaxResident=1（旧单槽行为）vs 2（LRU 驻留）
///   D5 热路径：元数据缓存开 / 关（BuildShape / mmproj 解析 × 2000）
///
/// 结果写入 %TEMP%/haoyue-bench/（JSON + Markdown）。默认构建为 CPU 后端
/// （LLamaSharp.Backend.Cpu）；GPU 口径需 -p:LlamaBackend=Cuda12 重新构建后重跑。
/// </summary>
public sealed class LocalModelLoadBenchmarkTests
{
    private const string BenchFlag = "HAOYUE_LOCAL_MODEL_BENCH";
    private static readonly string BenchRoot = Path.Combine(Path.GetTempPath(), "haoyue-bench");

    [Fact]
    public async Task FiveDimensionBenchmark()
    {
        if (Environment.GetEnvironmentVariable(BenchFlag) != "1")
            return; // opt-in：CI 与常规测试不跑（加载真实权重以分钟计）

        var source = ResolveSourceModel();
        Assert.True(File.Exists(source), $"基准模型不存在：{source}");
        var primary = StageModel(source, "bench-primary.gguf");
        var alt = StageModel(source, "bench-alt.gguf");
        Directory.CreateDirectory(BenchRoot);

        var results = new Dictionary<string, object>
        {
            ["environment"] = new Dictionary<string, object>
            {
                ["os"] = Environment.OSVersion.ToString(),
                ["logicalCores"] = Environment.ProcessorCount,
                ["backend"] = "cpu (LLamaSharp.Backend.Cpu)",
                ["primaryMiB"] = new FileInfo(primary).Length / (1024 * 1024),
                ["altMiB"] = new FileInfo(alt).Length / (1024 * 1024),
            },
        };

        // ---- D1 启动速度 ----
        results["D1_coldLoadMs"] = await MeasureColdLoadAsync(primary);
        results["D1_preloadedFirstRequestMs"] = await MeasurePreloadThenRequestAsync(primary);

        // ---- D2 内存峰值 ----
        results["D2_mmapOn"] = await MeasureLoadMemoryAsync(primary, useMmap: true);
        results["D2_mmapOff"] = await MeasureLoadMemoryAsync(primary, useMmap: false);

        // ---- D3 并发吞吐 ----
        results["D3_serial_1x16threads"] = await MeasureConcurrencyAsync(primary, concurrency: 1, threads: 16, requests: 8);
        results["D3_concurrent_4x4threads"] = await MeasureConcurrencyAsync(primary, concurrency: 4, threads: 4, requests: 8);

        // ---- D4 多模型交替 ----
        results["D4_alternate_maxResident1_oldSingleSlot"] = await MeasureAlternationAsync(primary, alt, maxResident: 1, rounds: 6);
        results["D4_alternate_maxResident2_lru"] = await MeasureAlternationAsync(primary, alt, maxResident: 2, rounds: 6);

        // ---- D5 热路径 ----
        results["D5_metadataCache"] = MeasureMetadataCache(primary);

        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var jsonPath = Path.Combine(BenchRoot, $"local-model-bench-{stamp}.json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        var mdPath = Path.Combine(BenchRoot, $"local-model-bench-{stamp}.md");
        await File.WriteAllTextAsync(mdPath, RenderMarkdown(results));

        Console.WriteLine($"[bench] json: {jsonPath}");
        Console.WriteLine($"[bench] markdown: {mdPath}");
    }

    // ---------------------------------------------------------------- D1 启动速度

    private static async Task<long> MeasureColdLoadAsync(string path)
    {
        using var cache = NewCache();
        var watch = Stopwatch.StartNew();
        using var lease = await cache.AcquireAsync(path, LoadParams(path), BenchSignature(path), null, CancellationToken.None);
        watch.Stop();
        Assert.True(lease.Weights is not null);
        return watch.ElapsedMilliseconds;
    }

    private static async Task<long> MeasurePreloadThenRequestAsync(string path)
    {
        using var cache = NewCache();
        var provider = BenchProvider(path);
        var model = BenchModel(path);
        var client = new LocalLlmClient(cache);

        // 预热（模拟 daemon 启动后台加载）：不计入首请求耗时。签名与 StreamAsync
        // 内部计算完全一致，否则首请求会按新签名重载——测量的就是签名契约本身。
        await cache.PreloadAsync(path, LoadParams(path), BenchSignature(path), null, null, CancellationToken.None);

        var watch = Stopwatch.StartNew();
        await foreach (var _ in client.StreamAsync(BenchRequest(provider, model), CancellationToken.None))
        {
        }
        watch.Stop();
        return watch.ElapsedMilliseconds;
    }

    // ---------------------------------------------------------------- D2 内存峰值

    private static async Task<Dictionary<string, long>> MeasureLoadMemoryAsync(string path, bool useMmap)
    {
        using var cache = NewCache();
        var process = Process.GetCurrentProcess();
        long peakWorkingSet = 0;
        long peakPrivate = 0;
        var sampling = true;
        var sampler = Task.Run(async () =>
        {
            while (Volatile.Read(ref sampling))
            {
                process.Refresh();
                peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
                await Task.Delay(50);
            }
        });

        var watch = Stopwatch.StartNew();
        using (var lease = await cache.AcquireAsync(
            path, LoadParams(path, useMmap), BenchSignature(path) + "|mmap=" + useMmap, null, CancellationToken.None))
        {
            watch.Stop();
            await Task.Delay(500); // 权重驻留期间持续采样
        }
        Volatile.Write(ref sampling, false);
        await sampler;

        return new Dictionary<string, long>
        {
            ["loadMs"] = watch.ElapsedMilliseconds,
            ["peakWorkingSetMiB"] = peakWorkingSet / (1024 * 1024),
            ["peakPrivateMiB"] = peakPrivate / (1024 * 1024),
        };
    }

    // ---------------------------------------------------------------- D3 并发吞吐

    private static async Task<Dictionary<string, object>> MeasureConcurrencyAsync(string path, int concurrency, int threads, int requests)
    {
        using var cache = NewCache();
        cache.Configure(maxResidentModels: 2, concurrency, idleUnloadMinutes: 0);
        var provider = BenchProvider(path, threads);
        var model = BenchModel(path);
        var client = new LocalLlmClient(cache);

        // 预热：并发测量只覆盖推理，不含加载。签名与 StreamAsync 一致。
        using (var warm = await cache.AcquireAsync(path, LoadParams(path), BenchSignature(path, threads), null, CancellationToken.None))
        {
            _ = warm.Weights;
        }

        var watch = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, requests)
            .Select(_ => DrainAsync(client, BenchRequest(provider, model)))
            .ToArray();
        var generated = await Task.WhenAll(tasks);
        watch.Stop();

        var totalTokens = generated.Sum();
        return new Dictionary<string, object>
        {
            ["concurrency"] = concurrency,
            ["threadsPerSlot"] = threads,
            ["requests"] = requests,
            ["wallMs"] = watch.ElapsedMilliseconds,
            ["totalGeneratedTokens"] = totalTokens,
            ["aggregateTokensPerSecond"] = Math.Round(totalTokens / Math.Max(0.001, watch.Elapsed.TotalSeconds), 1),
        };
    }

    // ---------------------------------------------------------------- D4 多模型交替

    private static async Task<Dictionary<string, object>> MeasureAlternationAsync(string a, string b, int maxResident, int rounds)
    {
        using var cache = NewCache();
        cache.Configure(maxResidentModels: maxResident, concurrency: 1, idleUnloadMinutes: 0);
        var loads = 0;
        var progress = new CountingProgress(() => Interlocked.Increment(ref loads));

        var watch = Stopwatch.StartNew();
        for (var round = 0; round < rounds; round++)
        {
            foreach (var path in new[] { a, b })
            {
                using var lease = await cache.AcquireAsync(path, LoadParams(path), BenchSignature(path), null, progress, CancellationToken.None);
                _ = lease.Weights;
            }
        }
        watch.Stop();
        await Task.Delay(200); // Progress 回调经线程池投递，等计数落定。

        return new Dictionary<string, object>
        {
            ["maxResident"] = maxResident,
            ["acquires"] = rounds * 2,
            ["weightLoads"] = Volatile.Read(ref loads),
            ["wallMs"] = watch.ElapsedMilliseconds,
            ["avgMsPerAcquire"] = Math.Round(watch.Elapsed.TotalMilliseconds / (rounds * 2), 1),
        };
    }

    private sealed class CountingProgress(Action onLoadingPhase) : IProgress<LocalLoadProgress>
    {
        public void Report(LocalLoadProgress value)
        {
            if (value.Phase == LocalLoadPhase.Loading) onLoadingPhase();
        }
    }

    // ---------------------------------------------------------------- D5 热路径

    private static Dictionary<string, object> MeasureMetadataCache(string path)
    {
        const int iterations = 2_000;
        var metadata = new LocalModelMetadataCache();
        var provider = BenchProvider(path);
        var model = BenchModel(path);

        // 开缓存：首次填充 + 2000 次命中。
        LocalLlmClient.BuildShape(path, metadata);
        var warm = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) LocalLlmClient.BuildShape(path, metadata);
        warm.Stop();

        // 关缓存（旧行为）：每次都完整读 header。
        var cold = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) LocalLlmClient.BuildShape(path, null);
        cold.Stop();

        var mmWarm = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) metadata.ResolveMmproj(provider.ModelsDirectory!, null);
        mmWarm.Stop();
        var mmCold = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) LocalModels.ResolveMmprojPath(provider, model);
        mmCold.Stop();

        return new Dictionary<string, object>
        {
            ["iterations"] = iterations,
            ["shapeCachedMs"] = warm.ElapsedMilliseconds,
            ["shapeUncachedMs"] = cold.ElapsedMilliseconds,
            ["headerReadsWithCache"] = metadata.HeaderReads,
            ["mmprojCachedMs"] = mmWarm.ElapsedMilliseconds,
            ["mmprojUncachedMs"] = mmCold.ElapsedMilliseconds,
            ["directoryScansWithCache"] = metadata.DirectoryScans,
        };
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<long> DrainAsync(LocalLlmClient client, LlmRequest request)
    {
        long generated = 0;
        await foreach (var evt in client.StreamAsync(request, CancellationToken.None))
        {
            if (evt is LlmTextDelta) generated++;
        }
        return generated;
    }

    private static LocalModelCache NewCache() => new(
        new LocalModelFingerprints(Path.Combine(BenchRoot, "fingerprints.json")),
        new LocalModelMetadataCache());

    private static Func<bool, LLama.Common.ModelParams> LoadParams(string path, bool? useMmap = null) =>
        _ => new LLama.Common.ModelParams(path)
        {
            GpuLayerCount = 0,
            UseMemorymap = useMmap ?? true,
        };

    /// <summary>与 StreamAsync 内部完全一致的加载签名（AutoOptimize=false → 无档位；基准目录无 mmproj）。</summary>
    private static string BenchSignature(string path, int? threads = null) =>
        LocalLlmClient.LoadSignature(BenchProvider(path, threads), BenchModel(path), tier: null) + "|nommproj";

    private static ProviderConfig BenchProvider(string path, int? threads = null) => new()
    {
        Id = "bench-local",
        Kind = "local",
        ModelsDirectory = Path.GetDirectoryName(path),
        Threads = threads,
    };

    private static ModelConfig BenchModel(string path) => new()
    {
        Id = Path.GetFileName(path),
        LocalPath = path,
        ContextWindow = 4096,
        MaxOutput = 256,
        // AutoOptimize=false：档位计算整体跳过（不探 GPU、不读档位），测量口径确定。
        Load = new LocalModelSettings { AutoOptimize = false },
        Capabilities = new ModelCapabilities { Streaming = true, ToolCalling = false, Thinking = false },
    };

    private static LlmRequest BenchRequest(ProviderConfig provider, ModelConfig model) => new()
    {
        Provider = provider,
        Model = model,
        Messages = [ChatMessage.User("用一句话介绍杭州。")],
        MaxTokens = 24,
        EnableThinking = false,
    };

    private static string ResolveSourceModel()
    {
        var configured = Environment.GetEnvironmentVariable("HAOYUE_LOCAL_MODEL");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        // 缺省取最小的非投影器模型：基准迭代多，小模型把总时长控制在分钟级。
        return LocalModels.ListFiles(LocalModels.ResolveDirectory(null))
            .Where(file => !LocalModels.IsProjectorFileName(file.FileName))
            .OrderBy(file => file.SizeBytes)
            .FirstOrDefault()?.Path ?? "";
    }

    private static string StageModel(string source, string name)
    {
        Directory.CreateDirectory(BenchRoot);
        var staged = Path.Combine(BenchRoot, name);
        if (!File.Exists(staged) || new FileInfo(staged).Length != new FileInfo(source).Length)
            File.Copy(source, staged, overwrite: true);
        return staged;
    }

    private static string RenderMarkdown(Dictionary<string, object> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 本地模型加载基准（原始输出）");
        sb.AppendLine();
        sb.AppendLine($"- 时间：{DateTimeOffset.Now:O}");
        sb.AppendLine();
        sb.AppendLine("| 指标 | 值 |");
        sb.AppendLine("| --- | --- |");
        foreach (var (key, value) in results)
        {
            var flat = value is Dictionary<string, object> nested
                ? string.Join("; ", nested.Select(pair => $"{pair.Key}={pair.Value}"))
                : value.ToString();
            sb.AppendLine($"| {key} | {flat} |");
        }
        return sb.ToString();
    }
}
