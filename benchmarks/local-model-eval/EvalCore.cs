using System.Diagnostics;
using System.Text;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Providers;

namespace Haoyue.Benchmarks.LocalModelEval;

/// <summary>被测模型与共享生成通道：走 runtime 真实的 LocalLlmClient 加载与推理链路。</summary>
public static class EvalCore
{
    public const string ModelsDirectory = @"E:\GitHub\haoyue\models";
    public const string ModelFile = "gemma-4-E4B-it-Q4_K_M.gguf";

    /// <summary>仓库根目录（含 Haoyue.slnx），结果写入 benchmarks/local-model-eval/results。</summary>
    private static readonly string RepoRoot = FindRepoRoot();
    public static readonly string ResultsDir =
        Path.Combine(RepoRoot, "benchmarks", "local-model-eval", "results");

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent!)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Haoyue.slnx")))
                return dir.FullName;
        }
        return AppContext.BaseDirectory;
    }

    public static ProviderConfig Provider { get; } = new()
    {
        Id = "local-eval",
        Kind = "local",
        ModelsDirectory = ModelsDirectory,
        // 线程交给 llama.cpp 自动选择（全部逻辑核），与生产默认一致。
        Threads = null,
        // GPU 卸载层数：环境变量 HAOYUE_GPU_LAYERS 控制（未设 = 纯 CPU）。
        GpuLayers = int.TryParse(Environment.GetEnvironmentVariable("HAOYUE_GPU_LAYERS"), out var gpuLayers) ? gpuLayers : null,
    };

    public static ModelConfig Model { get; } = new()
    {
        Id = ModelFile,
        LocalPath = Path.Combine(ModelsDirectory, ModelFile),
        ContextWindow = 32_768,
        MaxOutput = 8_192,
    };

    /// <summary>单次生成的完整观测数据。</summary>
    public sealed record GenResult(
        string Answer,
        string Thinking,
        long PromptTokens,
        long OutputTokens,
        long CachedTokens,
        double TtftMs,
        double TotalMs,
        string FinishReason)
    {
        /// <summary>解码吞吐（tokens/s）：TTFT 之后的生成阶段。</summary>
        public double DecodeTps => OutputTokens > 1 && TotalMs > TtftMs
            ? (OutputTokens - 1) / ((TotalMs - TtftMs) / 1000.0)
            : 0;
        /// <summary>预填充速度（tokens/s）：TTFT 近似等于 prompt 预填充耗时。</summary>
        public double PrefillTps => PromptTokens > 0 && TtftMs > 0
            ? PromptTokens / (TtftMs / 1000.0)
            : 0;
    }

    /// <summary>标准流式生成：记录 TTFT、总耗时与真实 token 计数。</summary>
    public static async Task<GenResult> GenerateAsync(
        LocalLlmClient client,
        IReadOnlyList<ChatMessage> messages,
        string? system = null,
        int? maxTokens = null,
        double? temperature = null,
        CancellationToken ct = default)
    {
        var request = new LlmRequest
        {
            Provider = Provider,
            Model = Model,
            Messages = messages,
            System = system,
            MaxTokens = maxTokens,
            Temperature = temperature,
        };

        var ttft = TimeSpan.Zero;
        var started = Stopwatch.StartNew();
        var text = new StringBuilder();
        var thinking = new StringBuilder();
        LlmCompletion? completion = null;

        await foreach (var evt in client.StreamAsync(request, ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case LlmTextDelta delta:
                    if (ttft == TimeSpan.Zero && delta.Text.Trim().Length > 0)
                        ttft = started.Elapsed;
                    text.Append(delta.Text);
                    break;
                case LlmThinkingDelta think:
                    if (ttft == TimeSpan.Zero && think.Text.Trim().Length > 0)
                        ttft = started.Elapsed;
                    thinking.Append(think.Text);
                    break;
                case LlmCompleted done:
                    completion = done.Completion;
                    break;
            }
        }
        started.Stop();

        var answer = completion?.Text is { Length: > 0 } t ? t : text.ToString();
        if (ttft == TimeSpan.Zero) ttft = started.Elapsed;
        return new GenResult(
            answer,
            thinking.ToString(),
            completion?.Usage.TotalInputTokens ?? 0,
            completion?.Usage.OutputTokens ?? 0,
            completion?.Usage.CachedInputTokens ?? 0,
            ttft.TotalMilliseconds,
            started.Elapsed.TotalMilliseconds,
            completion?.FinishReason ?? "");
    }

    /// <summary>首轮加载权重的耗时单独报告。</summary>
    public static async Task<TimeSpan> WarmUpAsync(LocalLlmClient client, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await GenerateAsync(
            client,
            [ChatMessage.User("请只回答两个字：好的")],
            system: null,
            maxTokens: 16,
            temperature: 0.1,
            ct: ct).ConfigureAwait(false);
        return sw.Elapsed;
    }

    public static string ResultPath(string name) =>
        Path.Combine(ResultsDir, name);
}
