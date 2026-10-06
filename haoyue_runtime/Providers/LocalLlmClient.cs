using System.Runtime.CompilerServices;
using System.Text;
using Haoyue.Runtime.Configuration;
using LLama;
using LLama.Common;
using LLama.Exceptions;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.Logging.Abstractions;

namespace Haoyue.Runtime.Providers;

/// <summary>
/// Runs GGUF models in-process with llama.cpp (LLamaSharp). There is no server and no
/// network: the model file is loaded from the provider's local models directory on demand
/// and generations are serialized through the shared weight cache. Capability honesty:
/// local models currently expose streaming only (tool calling stays off).
/// </summary>
public sealed class LocalLlmClient(LocalModelCache cache) : ILlmClient
{
    public string Kind => "local";

    /// <summary>
    /// Local GGUF embeddings need a separate LLamaEmbedder context with its own
    /// memory cost; kept out of this batch so the client layer stays honest —
    /// callers degrade to null instead of receiving a half-working path.
    /// </summary>
    public Task<EmbeddingResult?> EmbedAsync(
        Configuration.ProviderConfig provider, IReadOnlyList<string> inputs, string? model = null, CancellationToken ct = default)
        => Task.FromResult<EmbeddingResult?>(null);

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var path = LocalModels.ResolveModelPath(request.Provider, request.Model);
        if (!File.Exists(path))
            throw new LlmException(
                $"Local model file not found: {path}. Put the GGUF file into the provider's models directory or fix the path.",
                retryable: false);

        using var lease = await cache.AcquireAsync(
            path,
            BuildLoadParams(path, request.Provider),
            LoadSignature(request.Provider),
            ct).ConfigureAwait(false);
        var weights = lease.Weights;

        var (contextParams, signature) = BuildContextParams(path, request.Model, request.Provider, weights);
        var (prompt, startsInThinking) = BuildPrompt(weights, request);

        await foreach (var evt in (request.Provider.LocalPrefixReuse
            ? StreamReusableAsync(cache, weights, contextParams, signature, prompt, startsInThinking, request, ct)
            : StreamStatelessAsync(weights, contextParams, prompt, startsInThinking, request, ct)).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <summary>One-shot inference: a fresh context per request, KV cache discarded afterwards.</summary>
    private async IAsyncEnumerable<LlmStreamEvent> StreamStatelessAsync(
        LLamaWeights weights, ModelParams contextParams, string prompt, bool startsInThinking,
        LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var executor = new StatelessExecutor(weights, contextParams, NullLogger.Instance);
        try
        {
            var parser = new ThinkTagParser(startsInThinking);

            await using var stream = executor
                .InferAsync(prompt, BuildInferenceParams(request), ct)
                .GetAsyncEnumerator(ct);
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await stream.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new LlmException(
                        $"Local inference with {request.Model.Id} failed: {ex.Message}",
                        retryable: false,
                        inner: ex);
                }
                if (!hasNext) break;

                foreach (var piece in parser.Process(stream.Current))
                {
                    if (piece.IsThinking) yield return new LlmThinkingDelta(piece.Text);
                    else yield return new LlmTextDelta(piece.Text);
                }
            }

            foreach (var piece in parser.Flush())
            {
                if (piece.IsThinking) yield return new LlmThinkingDelta(piece.Text);
                else yield return new LlmTextDelta(piece.Text);
            }

            var (answer, thinking) = FinalizeOutput(parser.Answer, parser.Thinking);
            yield return new LlmCompleted(new LlmCompletion
            {
                Text = answer,
                Thinking = thinking,
                Usage = new TokenUsage(
                    CountTokens(weights, prompt, addBos: true),
                    CountTokens(weights, answer) + CountTokens(weights, thinking)),
                FinishReason = "stop",
            });
        }
        finally
        {
            executor.Context.Dispose();
        }
    }

    /// <summary>
    /// Inference with cross-request KV prefix reuse, modeled on LLamaSharp's
    /// StatelessExecutor generation loop. The context stays alive in the model cache; on the
    /// next request the new prompt is tokenized and compared against the tokens already
    /// decoded into the KV cache. The shared prefix keeps its KV entries and only the
    /// diverging suffix is decoded, so multi-step agent turns skip repeated prefill of the
    /// (growing) system prompt and history. Any decode failure or backend without memory
    /// shifting falls back to a full re-prefill, which keeps results identical.
    /// </summary>
    private async IAsyncEnumerable<LlmStreamEvent> StreamReusableAsync(
        LocalModelCache cache, LLamaWeights weights, ModelParams contextParams, string signature,
        string prompt, bool startsInThinking, LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var reuse = cache.TryGetReuse(weights, signature)
                    ?? new LocalModelCache.ReuseEntry
                    {
                        Context = weights.CreateContext(contextParams, NullLogger.Instance),
                        Tokens = [],
                        Signature = signature,
                    };

        var inferenceParams = BuildInferenceParams(request);
        inferenceParams.SamplingPipeline.Reset();

        var context = reuse.Context;
        var history = reuse.Tokens; // tokens currently decoded into the KV cache
        var batch = new LLamaBatch();
        var decoder = new StreamingTokenDecoder(context);
        var antiprocessor = new AntipromptProcessor(inferenceParams.AntiPrompts);
        var completed = false;

        try
        {
            var tokens = context.Tokenize(prompt, true, true).ToList();
            if (tokens.Count == 0)
                throw new LlmException($"Local inference with {request.Model.Id} received an empty prompt.", retryable: false);
            var initialPromptLength = tokens.Count;

            // --- KV prefix reuse: drop only the diverging KV tail, decode only the new suffix ---
            var common = 0;
            var limit = Math.Min(tokens.Count, history.Count);
            while (common < limit && tokens[common] == history[common]) common++;
            if (common >= tokens.Count)
                common = tokens.Count - 1; // always decode ≥1 token so sampling sees fresh logits
            if (common < history.Count)
            {
                if (context.NativeHandle.MemoryCanShift)
                    context.NativeHandle.MemorySequenceRemove(LLamaSeqId.Zero, common, history.Count);
                else
                {
                    context.NativeHandle.MemoryClear(true);
                    common = 0;
                }
            }
            history.RemoveRange(common, history.Count - common);
            var nPast = common;

            if (tokens.Count > nPast)
            {
                var (result, _, past) = await context.DecodeAsync(
                    tokens.GetRange(nPast, tokens.Count - nPast), LLamaSeqId.Zero, batch, nPast, ct);
                if (result != DecodeResult.Ok) throw new LLamaDecodeError(result);
                history.AddRange(tokens.GetRange(common, tokens.Count - common));
                nPast = past;
            }

            var parser = new ThinkTagParser(startsInThinking);
            var maxTokens = inferenceParams.MaxTokens < 0 ? int.MaxValue : inferenceParams.MaxTokens;
            // 实测计数：generated 为本轮实际采样并解码的 token 数；stopped 表示模型正常收尾
            // （EOS 或反提示命中）。循环自然耗尽预算即长度截断，需如实上报为 "length"。
            var generated = 0;
            var stopped = false;
            for (var i = 0; i < maxTokens && !ct.IsCancellationRequested; i++)
            {
                var id = inferenceParams.SamplingPipeline.Sample(context.NativeHandle, batch.TokenCount - 1);
                if (id.IsEndOfGeneration(weights.Vocab)) { stopped = true; break; }

                decoder.Add(id);
                generated++;
                var decoded = decoder.Read();
                if (antiprocessor.Add(decoded)) { stopped = true; break; }
                foreach (var piece in parser.Process(decoded))
                {
                    if (piece.IsThinking) yield return new LlmThinkingDelta(piece.Text);
                    else yield return new LlmTextDelta(piece.Text);
                }

                // Same overflow strategy as LLamaSharp's StatelessExecutor: shift the KV
                // memory window when the backend supports it, otherwise re-prefill.
                if ((long)nPast + 1 >= context.ContextSize)
                {
                    var tokensKeep = inferenceParams.TokensKeep;
                    if (tokensKeep < 0 || tokensKeep > initialPromptLength)
                        tokensKeep = initialPromptLength;
                    else
                        tokensKeep += Convert.ToInt32(context.Vocab.ShouldAddBOS);
                    var nLeft = nPast - tokensKeep;
                    if (nLeft <= 0)
                        throw new InvalidOperationException(
                            "Cannot truncate context: TokensKeep exceeds the current context size.");
                    var percentage = Math.Max(0.01f, Math.Min(0.99f, inferenceParams.ContextTruncationPercentage));
                    var nDiscard = Math.Clamp((int)(nLeft * percentage), 1, nLeft);

                    if (context.NativeHandle.MemoryCanShift)
                    {
                        context.NativeHandle.MemorySequenceRemove(LLamaSeqId.Zero, tokensKeep, tokensKeep + nDiscard);
                        context.NativeHandle.MemorySequenceAdd(LLamaSeqId.Zero, tokensKeep + nDiscard, nPast, -nDiscard);
                        nPast -= nDiscard;
                        history.RemoveRange(tokensKeep, nDiscard);
                    }
                    else
                    {
                        history.RemoveRange(tokensKeep, nDiscard);
                        batch.Clear();
                        context.NativeHandle.MemoryClear(true);
                        var (reprefill, _, reprefillPast) =
                            await context.DecodeAsync(history, LLamaSeqId.Zero, batch, 0, ct);
                        if (reprefill != DecodeResult.Ok) throw new LLamaDecodeError(reprefill);
                        nPast = reprefillPast;
                    }
                }

                // The token joins the history only after its KV decode succeeded, so an
                // interrupted generation always leaves history and KV cache consistent.
                batch.Clear();
                batch.Add(id, nPast++, LLamaSeqId.Zero, true);
                var code = await context.DecodeAsync(batch, ct);
                if (code != DecodeResult.Ok) throw new LLamaDecodeError(code);
                history.Add(id);
            }

            foreach (var piece in parser.Flush())
            {
                if (piece.IsThinking) yield return new LlmThinkingDelta(piece.Text);
                else yield return new LlmTextDelta(piece.Text);
            }

            var (answer, thinking) = FinalizeOutput(parser.Answer, parser.Thinking);
            var completion = new LlmCompletion
            {
                Text = answer,
                Thinking = thinking,
                // 使用本轮真实 token 计数替代推理后的重新分词：prompt 已在预填充时分词
                // （tokens.Count 含 BOS），输出按采样循环实际计数，省掉一次全量重分词；
                // KV 前缀复用命中的 token 数计入 CachedInputTokens，与远程提供商缓存口径一致。
                Usage = new TokenUsage(tokens.Count, generated)
                {
                    CachedInputTokens = common,
                },
                FinishReason = stopped || ct.IsCancellationRequested ? "stop" : "length",
            };
            completed = true; // KV cache and history are consistent — keep the context
            yield return new LlmCompleted(completion);
        }
        finally
        {
            if (completed) cache.StoreReuse(weights, reuse);
            else cache.DropReuse(weights);
        }
    }

    /// <summary>Model-load parameters: GPU offload and thread count, no KV allocation yet.</summary>
    private static ModelParams BuildLoadParams(string path, ProviderConfig provider) => new(path)
    {
        GpuLayerCount = provider.GpuLayers ?? 0,
        Threads = provider.Threads is { } loadThreads ? Math.Max(1, loadThreads) : null,
        UseMemorymap = true,
    };

    /// <summary>
    /// Identity of the provider-level load settings that force a weights reload when they
    /// change (the model cache would otherwise reuse weights loaded with the old values).
    /// FlashAttention is a context-level parameter and lives in the context signature instead.
    /// </summary>
    internal static string LoadSignature(ProviderConfig provider) =>
        $"{provider.GpuLayers?.ToString() ?? "cpu"}|{provider.Threads?.ToString() ?? "auto"}";

    private static (ModelParams Params, string Signature) BuildContextParams(
        string path, ModelConfig model, ProviderConfig provider, LLamaWeights weights)
    {
        // Registering a model with a context window larger than it was trained for would
        // silently extrapolate positions, so the effective context is clamped to the model.
        // It is also clamped to a practical CPU ceiling: the KV cache of a 128k window alone
        // costs multiple gigabytes of RAM, and an entry that was written by hand inherits
        // the remote default (128k) unless the registration set a real value.
        var trained = weights.ContextSize > 0 ? weights.ContextSize : 4096;
        var configured = model.ContextWindow > 0 ? model.ContextWindow : trained;
        var contextSize = (uint)Math.Max(512, Math.Min(configured, Math.Min(trained, LocalModelProbe.DefaultContextWindow)));
        var gpuLayers = provider.GpuLayers ?? 0;
        var threads = provider.Threads;
        // Quantized KV caches require flash attention in llama.cpp; silently keep f16
        // when the user enabled quantization alone instead of failing every request.
        var kvQuant = provider.FlashAttention ? NormalizeKvQuant(provider.KvCacheQuantization) : "none";
        var parameters = new ModelParams(path)
        {
            ContextSize = contextSize,
            GpuLayerCount = gpuLayers,
            Threads = threads is { } threadCount ? Math.Max(1, threadCount) : null,
            FlashAttention = provider.FlashAttention,
            UseMemorymap = true,
        };
        if (kvQuant != "none")
        {
            parameters.TypeK = kvQuant == "q4_0" ? GGMLType.GGML_TYPE_Q4_0 : GGMLType.GGML_TYPE_Q8_0;
            parameters.TypeV = parameters.TypeK;
        }
        // Any change to these values invalidates the reusable context (different KV layout).
        var signature = $"{path}|{contextSize}|{gpuLayers}|{threads?.ToString() ?? "auto"}|{provider.FlashAttention}|{kvQuant}";
        return (parameters, signature);
    }

    internal static string NormalizeKvQuant(string? value) => value switch
    {
        "q8_0" => "q8_0",
        "q4_0" => "q4_0",
        _ => "none",
    };

    /// <summary>Renders the conversation with the chat template embedded in the GGUF file.</summary>
    private static (string Prompt, bool StartsInThinking) BuildPrompt(LLamaWeights weights, LlmRequest request)
    {
        LLamaTemplate template;
        try
        {
            template = new LLamaTemplate(weights, strict: false);
        }
        catch (Exception ex)
        {
            throw new LlmException(
                $"The GGUF file of {request.Model.Id} has no usable chat template: {ex.Message}",
                retryable: false,
                inner: ex);
        }

        if (!string.IsNullOrWhiteSpace(request.System))
            template.Add("system", request.System!);

        foreach (var message in request.Messages)
        {
            switch (message.Role)
            {
                case ChatRole.User:
                    template.Add("user", message.Text);
                    break;
                case ChatRole.System:
                    template.Add("system", message.Text);
                    break;
                case ChatRole.Assistant:
                    if (message.Text.Length > 0) template.Add("assistant", message.Text);
                    break;
                case ChatRole.Tool:
                    // Tool calling stays off for local models, but a session started on
                    // another provider can still carry tool results; replay them as user
                    // turns so the information is not silently dropped.
                    template.Add("user", $"[{message.ToolName ?? "tool"} result]\n{message.Text}");
                    break;
            }
        }

        template.AddAssistant = true;
        var prompt = Encoding.UTF8.GetString(template.Apply());

        // DeepSeek-R1 family models reason before answering: either the template injects
        // the opening tag into the generation prompt (original R1), or the template itself
        // references the ASCII "</think>" delimiter the model emits on its own (R1-0528).
        var trimmed = prompt.TrimEnd('\n', '\r', ' ');
        var startsInThinking = trimmed.EndsWith(" thinking", StringComparison.Ordinal)
            || (TryGetChatTemplate(weights)?.Contains("</think>", StringComparison.Ordinal) ?? false);
        return (prompt, startsInThinking);
    }

    private static string? TryGetChatTemplate(LLamaWeights weights)
    {
        try
        {
            foreach (var (key, value) in weights.Metadata)
                if (key == "tokenizer.chat_template")
                    return value?.ToString();
        }
        catch (Exception)
        {
            // Metadata layout is not critical here; fall back to prompt inspection.
        }
        return null;
    }

    /// <summary>
    /// 采样参数配置。温度沿用 DeepSeek 官方对 R1 系列的建议值 0.6、top-p 0.95；
    /// 显式设置重复惩罚是关键改进——库默认 RepeatPenalty=1（关闭），小参数量本地模型
    /// 在无惩罚时极易陷入逐词复读循环；惩罚窗口 64 token 覆盖近邻重复，且不惩罚换行，
    /// 避免长代码块与列表的格式被破坏。
    /// </summary>
    private static InferenceParams BuildInferenceParams(LlmRequest request)
    {
        var maxTokens = request.MaxTokens is { } requested && requested > 0
            ? requested
            : Math.Clamp(request.Model.MaxOutput, 256, 32_768);
        return new InferenceParams
        {
            MaxTokens = maxTokens,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = (float)(request.Temperature ?? 0.6),
                TopP = 0.95f,
                TopK = 40,
                MinP = 0.1f,
                RepeatPenalty = 1.1f,
                PenaltyCount = 64,
                PenalizeNewline = false,
            },
        };
    }

    /// <summary>
    /// 生成结束后的输出收尾（仅作用于最终 LlmCompletion，流式增量不受影响）：
    /// 1) 模型把全部预算耗在推理里、从未输出结束标记时回答为空，下游只能收到一次空
    ///    回复——此时把推理内容整体提升为回答兜底，保证下游总能拿到非空文本；
    /// 2) 回答统一去掉尾部空白，进入会话历史后不会污染下一轮模板渲染。
    /// </summary>
    internal static (string Answer, string Thinking) FinalizeOutput(string answer, string thinking)
    {
        if (answer.Length == 0 && thinking.Length > 0)
        {
            answer = thinking.TrimStart();
            thinking = "";
        }
        return (answer.TrimEnd(), thinking);
    }

    private static int CountTokens(LLamaWeights weights, string text, bool addBos = false)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        try { return weights.Tokenize(text, addBos, false, Encoding.UTF8).Count(); }
        catch { return 0; }
    }
}

/// <summary>
/// Incrementally splits model output into reasoning / answer pieces. Handles both the
/// ASCII "&lt;/think&gt;" delimiter of the DeepSeek-R1-0528 family and the special-token
/// delimiters of the original R1, with tags possibly split across stream chunks.
/// </summary>
internal sealed class ThinkTagParser(bool startsInThinking)
{
    private const string OpenTag = " thinking";
    private static readonly string[] CloseTags = ["</think>", "<｜end▁of▁thinking｜>"];

    private readonly StringBuilder _buffer = new();
    private readonly StringBuilder _thinking = new();
    private readonly StringBuilder _answer = new();
    private bool _inThinking = startsInThinking;
    private bool _emittedText;

    /// <summary>Everything classified as reasoning so far, which the answer may still grow into.</summary>
    public string Thinking => _thinking.ToString();

    /// <summary>Everything classified as the answer so far.</summary>
    public string Answer => _answer.ToString();

    public List<(bool IsThinking, string Text)> Process(string chunk)
    {
        if (!string.IsNullOrEmpty(chunk)) _buffer.Append(chunk);
        var pieces = new List<(bool IsThinking, string Text)>();
        while (true)
        {
            var buffer = _buffer.ToString();
            if (_inThinking)
            {
                if (FindCloseTag(buffer) is var (close, closeTag) && close >= 0)
                {
                    Add(pieces, true, buffer[..close]);
                    _buffer.Remove(0, close + closeTag.Length);
                    _inThinking = false;
                    continue;
                }
                var hold = LongestTagPrefixAtEnd(buffer, CloseTags);
                if (buffer.Length > hold) Add(pieces, true, buffer[..^hold]);
                KeepTail(hold);
                break;
            }

            var open = buffer.IndexOf(OpenTag, StringComparison.Ordinal);
            if (open >= 0)
            {
                Add(pieces, false, buffer[..open]);
                _buffer.Remove(0, open + OpenTag.Length);
                _inThinking = true;
                continue;
            }
            var keep = LongestTagPrefixAtEnd(buffer, [OpenTag]);
            if (buffer.Length > keep) Add(pieces, false, buffer[..(buffer.Length - keep)]);
            KeepTail(keep);
            break;
        }
        return pieces;
    }

    /// <summary>Releases whatever the stream ended while holding back.</summary>
    public List<(bool IsThinking, string Text)> Flush()
    {
        var pieces = new List<(bool IsThinking, string Text)>();
        if (_buffer.Length > 0)
        {
            Add(pieces, _inThinking, _buffer.ToString());
            _buffer.Clear();
        }
        return pieces;
    }

    private static (int Index, string Tag) FindCloseTag(string text)
    {
        var best = -1;
        var bestTag = "";
        foreach (var tag in CloseTags)
        {
            var index = text.IndexOf(tag, StringComparison.Ordinal);
            if (index >= 0 && (best < 0 || index < best))
            {
                best = index;
                bestTag = tag;
            }
        }
        return (best, bestTag);
    }

    private void Add(List<(bool IsThinking, string Text)> pieces, bool thinking, string text)
    {
        if (text.Length == 0) return;
        if (!thinking && !_emittedText)
        {
            // The answer that follows the thinking delimiter typically starts with blank lines.
            text = text.TrimStart('\r', '\n');
            if (text.Length == 0) return;
        }
        if (!thinking) _emittedText = true;
        (thinking ? _thinking : _answer).Append(text);
        pieces.Add((thinking, text));
    }

    private void KeepTail(int length)
    {
        if (length == 0) _buffer.Clear();
        else if (_buffer.Length > length) _buffer.Remove(0, _buffer.Length - length);
    }

    private static int LongestTagPrefixAtEnd(string text, string[] tags)
    {
        var longest = 0;
        foreach (var tag in tags)
        {
            var max = Math.Min(tag.Length - 1, text.Length);
            for (var length = max; length > longest; length--)
            {
                if (string.CompareOrdinal(text, text.Length - length, tag, 0, length) == 0)
                {
                    longest = length;
                    break;
                }
            }
        }
        return longest;
    }
}
