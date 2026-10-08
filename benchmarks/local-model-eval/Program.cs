using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Haoyue.Runtime.Providers;

namespace Haoyue.Benchmarks.LocalModelEval;

/// <summary>
/// 本地 GGUF 模型评测入口（gemma-4-E4B-it-Q4_K_M，经 haoyue_runtime 的 LocalLlmClient 加载）。
/// 用法：dotnet run -- [fluency|speed|selfcorrect|all]（默认 all）。
/// 结果 JSON 写入 benchmarks/local-model-eval/results/。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var mode = args.Length > 0 ? args[0] : "all";
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(45));

        Directory.CreateDirectory(EvalCore.ResultsDir);
        Console.WriteLine($"=== 浩玥本地模型评测 ===");
        Console.WriteLine($"模型: {EvalCore.ModelFile}");
        Console.WriteLine($"模式: {mode}");

        using var cache = new LocalModelCache();
        var client = new LocalLlmClient(cache);

        if (mode == "probe")
        {
            // 分词行为探针：验证 Gemma 4 canonical 标记是否被当作特殊 token 正确编码，
            // 以及 addBos=true 时 BOS 是否恰好注入一次。
            using var lease = await cache.AcquireAsync(
                EvalCore.Model.LocalPath!,
                new LLama.Common.ModelParams(EvalCore.Model.LocalPath!) { UseMemorymap = true },
                null, mmprojPath: null, cts.Token);
            var weights = lease.Weights;
            var markers = new[] { "<|turn>user", "<turn|>", "<|turn>model", "<|think|>", "<|channel>thought", "<channel|>", "<bos>" };
            foreach (var marker in markers)
            {
                var ids = weights.Tokenize(marker, false, true, Encoding.UTF8).ToList();
                var rebuilt = string.Concat(ids.Select(id => weights.Vocab.LLamaTokenToString(id, true)));
                Console.WriteLine($"  marker {marker,-18} -> {ids.Count} tok: [{string.Join(", ", ids)}] roundtrip='{rebuilt}'");
            }
            var sample = "<|turn>system\n你是测试。<turn|>\n<|turn>user\n你好<turn|>\n<|turn>model\n";
            var withBos = weights.Tokenize(sample, true, true, Encoding.UTF8).ToList();
            Console.WriteLine($"  full prompt (addBos=true): {withBos.Count} tok, head ids: [{string.Join(", ", withBos.Take(6))}]");
            var headText = string.Concat(withBos.Take(6).Select(id => weights.Vocab.LLamaTokenToString(id, true)));
            Console.WriteLine($"  head roundtrip: '{headText.Replace("\n", "\\n")}'");
            return 0;
        }

        if (mode == "gputest")
        {
            // GPU 卸载验证：单轮生成，对比 CPU 基线（7.2-7.6 tok/s）确认加速生效。
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await EvalCore.GenerateAsync(
                client,
                new List<ChatMessage>
                {
                    ChatMessage.User("请用 200 字左右介绍一下艾灸的基本原理和主要功效。"),
                },
                system: null, maxTokens: 256, temperature: 0.3, ct: cts.Token);
            sw.Stop();
            Console.WriteLine($"=== GPU 加速验证 ===");
            Console.WriteLine($"  GpuLayers: {EvalCore.Provider.GpuLayers?.ToString() ?? "null(CPU)"}");
            Console.WriteLine($"  TTFT: {result.TtftMs:F0} ms (预填充 {result.PromptTokens} tok @ {result.PrefillTps:F1} tok/s)");
            Console.WriteLine($"  解码: {result.OutputTokens} tok @ {result.DecodeTps:F1} tok/s");
            Console.WriteLine($"  总耗时: {sw.Elapsed.TotalSeconds:F1}s");
            Console.WriteLine($"  回答片段: {result.Answer[..Math.Min(80, result.Answer.Length)]}");
            return 0;
        }

        if (mode == "visiontest")
        {
            // 多模态端到端验证：读入测试图片，问模型图里有什么。
            // 用法：dotnet run -- visiontest <图片路径> [问题]
            var imagePath = args.Length > 1 ? args[1] : "E:/haoyue-release/vision-test.png";
            var question = args.Length > 2 ? args[2] : "请详细描述这张图片里的内容，包括每种物体的形状和颜色。";
            var bytes = File.ReadAllBytes(imagePath);
            Console.WriteLine($"图片: {imagePath} ({bytes.Length / 1024} KB)");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await EvalCore.GenerateAsync(
                client,
                new List<ChatMessage>
                {
                    ChatMessage.User(question, [new ChatImageAttachment(
                        "vision-test", Path.GetFileName(imagePath), "image/png",
                        Convert.ToBase64String(bytes), bytes.Length)]),
                },
                system: null, maxTokens: 256, temperature: 0.3, ct: cts.Token);
            sw.Stop();
            Console.WriteLine($"=== 多模态端到端验证 ===");
            Console.WriteLine($"  预填充: {result.PromptTokens} tok, TTFT {result.TtftMs:F0} ms");
            Console.WriteLine($"  解码: {result.OutputTokens} tok @ {result.DecodeTps:F1} tok/s, 总耗时 {sw.Elapsed.TotalSeconds:F1}s");
            Console.WriteLine($"  模型回答:\n{result.Answer}");
            return 0;
        }

        Console.WriteLine("加载模型权重中（5.3GB，首次约 30-90 秒）…");
        var loadSw = System.Diagnostics.Stopwatch.StartNew();
        var warm = await EvalCore.WarmUpAsync(client, cts.Token);
        Console.WriteLine($"模型加载+预热完成，耗时 {loadSw.Elapsed.TotalSeconds:F1}s\n");

        if (mode is "all" or "fluency")
        {
            Console.WriteLine("── 任务一：对话流畅性测试 ──");
            var fluency = await FluencyEval.RunAsync(client, cts.Token);
            Save("fluency-results.json", fluency);
            foreach (var s in fluency)
            {
                Console.WriteLine($"  {s.Name}: 检查点命中 {s.CheckpointHitRate:P0}, " +
                                  $"avg distinct-2 {s.AvgDistinct2:F3}, 长度波动 {s.LengthVariation:F2}, " +
                                  $"跨轮复读 {s.TotalEchoPairs}");
            }
            Console.WriteLine();
        }

        if (mode is "all" or "speed")
        {
            Console.WriteLine("── 任务二：响应速度测试 ──");
            var lengths = await SpeedEval.RunLengthsAsync(client, cts.Token);
            var reuse = await SpeedEval.RunReuseComparisonAsync(client, cts.Token);
            Save("speed-results.json", new SpeedReport(loadSw.Elapsed.TotalSeconds, lengths, reuse));
            Console.WriteLine();
        }

        if (mode is "all" or "selfcorrect")
        {
            Console.WriteLine("── 任务三：自我修正与持续学习 ──");
            var store = new SelfCorrectionEval.LessonStore(
                Path.Combine(EvalCore.ResultsDir, "lessons.json"));
            var cases = await SelfCorrectionEval.RunAllCasesAsync(client, store, cts.Token);
            Save("selfcorrection-results.json", cases);
            foreach (var c in cases)
                Console.WriteLine($"  {c.CaseName}: 修正轮数 {c.AttemptsUsed}, ground truth {(c.GroundTruthPassed ? "PASS" : "FAIL")}");
            Console.WriteLine($"  知识积累: {store.All.Count} 条 lesson 持久化于 lessons.json");
            Console.WriteLine();
        }

        Console.WriteLine("评测完成。结果与原文见 benchmarks/local-model-eval/results/");
        return 0;
    }

    public sealed record SpeedReport(
        double LoadWarmupSeconds,
        IReadOnlyList<SpeedEval.SpeedRecord> LengthCases,
        IReadOnlyList<SpeedEval.SpeedRecord> ReuseComparison);

    private static void Save<T>(string fileName, T data)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        File.WriteAllText(
            Path.Combine(EvalCore.ResultsDir, fileName),
            JsonSerializer.Serialize(data, options),
            new UTF8Encoding(false));
        Console.WriteLine($"  [已保存] results/{fileName}");
    }
}
