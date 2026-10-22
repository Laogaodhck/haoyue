using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Providers;
using LLama.Common;

namespace Haoyue.Benchmarks.ModelOpt;

/// <summary>
/// 本地 GGUF 模型加载/推理基准：全部度量走 runtime 真实链路（LocalLlmClient /
/// LocalModelCache），配置直接读 ~/.haoyue/config.json——测的就是生产同一条路径。
///
/// 用法（NVIDIA 机器必须带 Cuda12 后端，否则权重全部走 CPU）：
///   dotnet run -c Release -p:LlamaBackend=Cuda12 -- bench   [--tag before] [--filter 子串] [--threads N]
///   dotnet run -c Release -p:LlamaBackend=Cuda12 -- quality [--tag before] [--filter 子串]
///   dotnet run -c Release -- compare quality-&lt;a&gt;.json quality-&lt;b&gt;.json
///
/// bench 每个模型输出：冷加载耗时（权重+512 自检）、加载期进程内存峰值、显存峰值
/// （nvidia-smi 采样差值）、短/中/长提示的 TTFT、prefill tok/s、decode tok/s。
/// quality 用固定题本 + 贪心解码（temperature 0 + 固定种子）输出可复现答案，
/// 供量化前后/跨量化档位对比。
/// 结果写入 benchmarks/model-opt/results/。
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var mode = args.Length > 0 ? args[0] : "bench";
        var tag = Arg(args, "--tag") ?? DateTime.Now.ToString("HHmm");
        var filter = Arg(args, "--filter");
        var threads = int.TryParse(Arg(args, "--threads"), out var t) ? t : (int?)null;

        try
        {
            return mode switch
            {
                "bench" => await BenchAsync(tag, filter, threads),
                "quality" => await QualityAsync(tag, filter),
                "compare" => Compare(args.Skip(1).ToArray()),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex}");
            return 2;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("modes: bench | quality | compare <a.json> <b.json>");
        return 1;
    }

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    // ---------------------------------------------------------------- 模型枚举

    private sealed record ModelEntry(ProviderConfig Provider, ModelConfig Model, string Path, string Label);

    private static List<ModelEntry> ResolveModels(string? filter)
    {
        var store = new ConfigStore();
        store.Reload();
        var list = new List<ModelEntry>();
        foreach (var provider in store.Config.Providers.Where(p => p.Kind == "local"))
        {
            foreach (var model in provider.Models)
            {
                var path = LocalModels.ResolveModelPath(provider, model);
                if (!File.Exists(path)) continue;
                if (filter is { } f && !path.Contains(f, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new ModelEntry(provider, model, path, $"{provider.Id}/{model.Id}"));
            }
        }
        return list;
    }

    // ---------------------------------------------------------------- bench

    private static async Task<int> BenchAsync(string tag, string? filter, int? threads)
    {
        var models = ResolveModels(filter);
        if (models.Count == 0)
        {
            Console.WriteLine($"没有匹配的本地模型（filter={filter ?? "无"}）。");
            return 1;
        }
        Directory.CreateDirectory(ResultsDir);
        var records = new List<BenchRecord>();
        foreach (var entry in models)
        {
            Console.WriteLine($"\n=== bench {entry.Label} ({new FileInfo(entry.Path).Length / (1024 * 1024)} MiB) @ {Stamp()} ===");
            BenchRecord record;
            try
            {
                record = await BenchOneAsync(entry, tag, threads);
            }
            catch (Exception ex)
            {
                // 单模型失败不中断整轮：其余模型的基线数据仍然有效。
                Console.WriteLine($"  [失败] {ex.GetType().Name}: {ex.Message}");
                record = new BenchRecord(tag, entry.Model.Id, entry.Path, entry.Provider.Id,
                    "FAILED", -1, -1, -1, -1, -1, 0, 0, 0, 0, 0, 0, 0, 0, ex.Message);
            }
            records.Add(record);
            if (record.ColdLoadMs >= 0)
                Console.WriteLine($"  冷加载 {record.ColdLoadMs} ms | 进程内存峰值 {record.PeakRamMib} MiB | "
                                  + $"显存峰值 {record.PeakVramMib} MiB | decode {record.MediumDecodeTps:F1} tok/s "
                                  + $"(中) / {record.LongDecodeTps:F1} tok/s (长上下文) | prefill {record.LongPrefillTps:F0} tok/s");
        }

        var file = Path.Combine(ResultsDir, $"bench-{tag}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(records, JsonOpts));
        Console.WriteLine($"\n[已保存] {file}");
        return 0;
    }

    private static async Task<BenchRecord> BenchOneAsync(ModelEntry entry, string tag, int? threads)
    {
        var provider = entry.Provider;
        var model = entry.Model;
        if (threads is { } overrideThreads)
        {
            provider = new ProviderConfig
            {
                Id = provider.Id, Kind = provider.Kind, GpuLayers = provider.GpuLayers,
                Threads = overrideThreads, FlashAttention = provider.FlashAttention,
                KvCacheQuantization = provider.KvCacheQuantization, ModelsDirectory = provider.ModelsDirectory,
                LocalPrefixReuse = provider.LocalPrefixReuse,
            };
        }

        using var cache = new LocalModelCache();
        // 把 llama.cpp 原生日志（Info+）透传到控制台：加载/上下文创建/decode 各阶段的
        // 原生输出是定位「挂住」的唯一直接证据（LocalModelCache 静态构造只把它转 Debug）。
        try
        {
            LLama.Native.NativeLibraryConfig.All.WithLogCallback((level, message) =>
            {
                if (level >= LLama.Native.LLamaLogLevel.Info)
                    Console.WriteLine($"    [llama:{level}] {message.TrimEnd()}");
            });
        }
        catch (Exception)
        {
            // 日志替换失败不影响基准本身。
        }
        var client = new LocalLlmClient(cache);

        // 与 StreamAsync 内部完全一致的加载签名：档位 + load 参数 + mmproj 后缀。
        // 签名不一致会让预热装载的权重被 StreamAsync 判为陈旧而重载，冷加载度量即失真。
        var tier = LocalLlmClient.TierFor(cache.Tuning, provider, model, entry.Path, cache.Metadata);
        var factory = LocalLlmClient.BuildLoadParams(entry.Path, provider, model, tier);
        var signature = LocalLlmClient.LoadSignature(provider, model, tier) + "|nommproj";

        var sampler = new ResourceSampler();
        sampler.Start();
        var loadWatch = Stopwatch.StartNew();
        Console.WriteLine($"  开始冷加载 @ {Stamp()}");
        long coldLoadMs;
        string effective;
        // 冷加载度量单独持 lease，测完立即归还推理槽：GenerateAsync 内部的
        // StreamAsync 还会再 Acquire 同一个槽（MaxConcurrency=1 时门是一把单
        // 信号量）——此处不释放就是嵌套获取同一把锁，自死锁且无任何原生日志
        // （实测挂死 15 分钟直到首输出闸门触发）。权重不会因释放而卸载，
        // 后续生成仍命中同一槽位（日志无第二次 llama_model_load 即验证）。
        using (var lease = await cache.AcquireAsync(entry.Path, factory, signature, null, CancellationToken.None))
        {
            coldLoadMs = lease.Report?.LoadDurationMs ?? loadWatch.ElapsedMilliseconds;
            effective = lease.Report is { } r
                ? $"gpuLayers={r.EffectiveGpuLayers} attempts={r.Attempts} degraded={r.DegradedToCpu}"
                : "unknown";
        }
        Console.WriteLine($"  权重就绪 @ {Stamp()}（{coldLoadMs} ms）");
        if (tier is { } t) effective += $" tier={t.Key}";

        // 预热一轮（触发真实上下文路径 + KV 缓存分配），不计入成绩。
        Console.WriteLine($"  预热生成开始 @ {Stamp()}");
        _ = await GenerateAsync(client, provider, model, Prompts.Short, 32, 0.3, CancellationToken.None);
        Console.WriteLine($"  预热完成 @ {Stamp()}");

        Console.WriteLine($"  短提示 @ {Stamp()}");
        var shortRun = await GenerateAsync(client, provider, model, Prompts.Short, 48, 0.3, CancellationToken.None);
        Console.WriteLine($"  中提示 @ {Stamp()}");
        var mediumRun = await GenerateAsync(client, provider, model, Prompts.Medium, 192, 0.3, CancellationToken.None);
        Console.WriteLine($"  长上下文 @ {Stamp()}");
        var longRun = await GenerateAsync(client, provider, model, Prompts.LongPrefill(), 96, 0.3, CancellationToken.None);

        sampler.Stop();
        cache.Dispose(); // 卸下权重再进下一个模型：每个模型的冷加载都是真冷加载。
        GC.Collect();

        return new BenchRecord(
            tag, model.Id, entry.Path, provider.Id, effective,
            coldLoadMs, loadWatch.ElapsedMilliseconds,
            sampler.PeakRamMib, sampler.LastRamMib, sampler.PeakVramMib,
            shortRun.DecodeTps, mediumRun.DecodeTps, longRun.DecodeTps,
            mediumRun.PrefillTps, longRun.PrefillTps,
            (long)mediumRun.TtftMs, (long)longRun.TtftMs,
            longRun.PromptTokens, Notes: tier?.Reason);
    }

    public sealed record BenchRecord(
        string Tag, string Model, string Path, string Provider, string EffectiveLoad,
        long ColdLoadMs, long LoadPlusFirstReqMs,
        long PeakRamMib, long SteadyRamMib, long PeakVramMib,
        double ShortDecodeTps, double MediumDecodeTps, double LongDecodeTps,
        double MediumPrefillTps, double LongPrefillTps,
        long MediumTtftMs, long LongTtftMs,
        long LongPromptTokens, string? Notes);

    // ---------------------------------------------------------------- quality

    private static async Task<int> QualityAsync(string tag, string? filter)
    {
        var models = ResolveModels(filter);
        if (models.Count == 0)
        {
            Console.WriteLine($"没有匹配的本地模型（filter={filter ?? "无"}）。");
            return 1;
        }
        Directory.CreateDirectory(QualityDir);
        foreach (var entry in models)
        {
            Console.WriteLine($"\n=== quality {entry.Label} ===");
            using var cache = new LocalModelCache();
            var client = new LocalLlmClient(cache);
            var provider = entry.Provider;
            var model = entry.Model;
            // 确定性口径：temperature=0（贪心）+ 固定种子。Seed 只进推理签名，不触发权重重载。
            model.Load ??= new LocalModelSettings();
            model.Load.Seed = 42;

            var results = new List<QualityItem>();
            foreach (var (name, prompt) in Prompts.Quality)
            {
                var run = await GenerateAsync(client, provider, model, prompt, 256, 0, CancellationToken.None);
                results.Add(new QualityItem(name, prompt, run.Answer));
                Console.WriteLine($"  [{name}] {run.OutputTokens} tok, {run.TotalMs:F0} ms -> {Truncate(run.Answer, 60)}");
            }
            cache.Dispose();

            var file = Path.Combine(QualityDir, $"quality-{tag}-{Sanitize(model.Id)}.json");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(
                new QualityReport(tag, model.Id, entry.Path, results), JsonOpts));
            Console.WriteLine($"  [已保存] {file}");
        }
        return 0;
    }

    public sealed record QualityItem(string Name, string Prompt, string Answer);
    public sealed record QualityReport(string Tag, string Model, string Path, List<QualityItem> Items);

    private static int Compare(string[] files)
    {
        if (files.Length != 2)
        {
            Console.WriteLine("compare 需要两个 quality JSON 文件。");
            return 1;
        }
        var a = JsonSerializer.Deserialize<QualityReport>(File.ReadAllText(files[0]), JsonOpts)!;
        var b = JsonSerializer.Deserialize<QualityReport>(File.ReadAllText(files[1]), JsonOpts)!;
        Console.WriteLine($"A = {a.Model} ({a.Tag})\nB = {b.Model} ({b.Tag})\n");
        var same = 0;
        foreach (var item in a.Items)
        {
            var other = b.Items.FirstOrDefault(x => x.Name == item.Name);
            if (other is null) continue;
            var identical = string.Equals(item.Answer.Trim(), other.Answer.Trim(), StringComparison.Ordinal);
            var dice = Dice(item.Answer, other.Answer);
            if (identical) same++;
            Console.WriteLine($"  {item.Name,-14} {(identical ? "完全一致" : "有差异"),-8} 相似度 {dice:P1} "
                              + $"({item.Answer.Length} vs {other.Answer.Length} 字)");
        }
        Console.WriteLine($"\n完全一致 {same}/{a.Items.Count}");
        return 0;
    }

    // ---------------------------------------------------------------- 生成与采样

    private static async Task<GenResult> GenerateAsync(
        LocalLlmClient client, ProviderConfig provider, ModelConfig model,
        string prompt, int maxTokens, double temperature, CancellationToken ct)
    {
        var request = new LlmRequest
        {
            Provider = provider,
            Model = model,
            Messages = [ChatMessage.User(prompt)],
            MaxTokens = maxTokens,
            Temperature = temperature,
            EnableThinking = false,
        };
        var ttft = TimeSpan.Zero;
        var started = Stopwatch.StartNew();
        var text = new StringBuilder();
        LlmCompletion? completion = null;
        await foreach (var evt in client.StreamAsync(request, ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case LlmTextDelta delta:
                    if (ttft == TimeSpan.Zero && delta.Text.Trim().Length > 0) ttft = started.Elapsed;
                    text.Append(delta.Text);
                    break;
                case LlmThinkingDelta think:
                    if (ttft == TimeSpan.Zero && think.Text.Trim().Length > 0) ttft = started.Elapsed;
                    break;
                case LlmCompleted done:
                    completion = done.Completion;
                    break;
            }
        }
        started.Stop();
        var answer = completion?.Text is { Length: > 0 } t ? t : text.ToString();
        if (ttft == TimeSpan.Zero) ttft = started.Elapsed;
        var promptTokens = completion?.Usage.TotalInputTokens ?? 0;
        var outputTokens = completion?.Usage.OutputTokens ?? 0;
        return new GenResult(
            answer, promptTokens, outputTokens, ttft.TotalMilliseconds, started.Elapsed.TotalMilliseconds);
    }

    private sealed record GenResult(string Answer, long PromptTokens, long OutputTokens, double TtftMs, double TotalMs)
    {
        public double DecodeTps => OutputTokens > 1 && TotalMs > TtftMs
            ? (OutputTokens - 1) / ((TotalMs - TtftMs) / 1000.0) : 0;
        public double PrefillTps => PromptTokens > 0 && TtftMs > 0
            ? PromptTokens / (TtftMs / 1000.0) : 0;
    }

    /// <summary>进程内存 + 显卡显存周期采样（显存取 nvidia-smi 与加载前基线之差）。</summary>
    private sealed class ResourceSampler
    {
        private readonly Process _process = Process.GetCurrentProcess();
        private CancellationTokenSource _cts = new();
        private Task? _loop;
        private long _baselineVram;

        public long PeakRamMib { get; private set; }
        public long LastRamMib { get; private set; }
        public long PeakVramMib { get; private set; }

        public void Start()
        {
            _baselineVram = QueryVramMib() ?? 0;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    Sample();
                    try { await Task.Delay(250, _cts.Token); } catch (OperationCanceledException) { break; }
                }
                Sample();
            });
        }

        public void Stop()
        {
            _cts.Cancel();
            try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
            Sample();
        }

        private void Sample()
        {
            _process.Refresh();
            var ram = _process.PrivateMemorySize64 / (1024 * 1024);
            LastRamMib = ram;
            PeakRamMib = Math.Max(PeakRamMib, ram);
            var vram = QueryVramMib();
            if (vram is { } used)
                PeakVramMib = Math.Max(PeakVramMib, Math.Max(0, used - _baselineVram));
        }

        private static long? QueryVramMib()
        {
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi",
                    "--query-gpu=memory.used --format=csv,noheader,nounits")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                if (p is null) return null;
                var text = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mib) ? mib : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    // ---------------------------------------------------------------- 杂项

    private static string ResultsDir => Path.Combine(RepoRoot, "benchmarks", "model-opt", "results");
    private static string QualityDir => Path.Combine(ResultsDir, "quality");

    private static string RepoRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent!)
                if (File.Exists(Path.Combine(dir.FullName, "Haoyue.slnx")))
                    return dir.FullName;
            return AppContext.BaseDirectory;
        }
    }

    private static string Stamp() => DateTime.Now.ToString("HH:mm:ss");

    private static string Truncate(string text, int max) =>
        text.Replace("\n", " ").Replace("\r", "").Length <= max
            ? text.Replace("\n", " ").Replace("\r", "")
            : text.Replace("\n", " ").Replace("\r", "")[..max] + "…";

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>字符二元组 Dice 相似度：0~1，对同主题不同措辞的回答给出连续刻度。</summary>
    private static double Dice(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        Dictionary<string, int> Bigrams(string s)
        {
            var map = new Dictionary<string, int>();
            for (var i = 0; i < s.Length - 1; i++)
            {
                var key = s.Substring(i, 2);
                map[key] = map.TryGetValue(key, out var n) ? n + 1 : 1;
            }
            return map;
        }
        var (x, y) = (Bigrams(a), Bigrams(b));
        var overlap = x.Keys.Sum(k => Math.Min(x[k], y.TryGetValue(k, out var n) ? n : 0));
        return 2.0 * overlap / (a.Length - 1 + b.Length - 1);
    }

    private static class Prompts
    {
        public const string Short = "请用一句话介绍杭州。";

        public const string Medium =
            "一个笼子里有鸡和兔共 35 只，脚共 94 只。请问鸡和兔各有多少只？"
            + "请先列方程再给出答案。";

        /// <summary>约 3 千 token 的长上下文预填充题：重复段落 + 插入事实，末句提问。</summary>
        public static string LongPrefill()
        {
            var filler = "杭州是一座历史悠久的城市，西湖风景区四季分明，春天桃花盛开，夏天荷花满塘。"
                         + "城市轨道交通网络密集，公交线路覆盖了主要城区与郊区。";
            var sb = new StringBuilder();
            sb.Append("请阅读下面的材料并回答最后的问题。\n\n");
            for (var i = 0; i < 18; i++)
            {
                sb.Append(filler);
                if (i == 9) sb.Append("据2026年统计，杭州数字经济的核心产业增加值达到了6830亿元。");
            }
            sb.Append("\n\n问题：材料中提到的杭州数字经济核心产业增加值是多少？请只回答数字。");
            return sb.ToString();
        }

        public static readonly (string Name, string Prompt)[] Quality =
        [
            ("常识", "中国的首都是哪座城市？请用一句话回答。"),
            ("数学", "小明有 3 个苹果，又买了 5 个，吃掉了 2 个，还剩几个？请列出计算过程。"),
            ("代码", "请用 Python 写一个函数 is_palindrome(s)，判断字符串是否为回文（忽略大小写与非字母数字字符）。"),
            ("翻译", "请把下面这句话翻译成英文：人工智能正在深刻地改变软件行业的开发方式。"),
            ("科学", "为什么天空在白天是蓝色的？请用两句话解释。"),
            ("病句", "下面这句话有语病，请修改并说明修改理由：通过这次活动，使我明白了团队合作的重要性。"),
        ];
    }
}
