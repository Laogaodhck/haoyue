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

        var images = CollectImages(request);
        // mmproj 始终参与解析与加载签名（即使本轮纯文本），避免图片轮/文本轮交替时
        // 签名漂移导致权重反复重载。
        var mmprojPath = LocalModels.ResolveMmprojPath(request.Provider, request.Model);

        using var lease = await cache.AcquireAsync(
            path,
            BuildLoadParams(path, request.Provider),
            LoadSignature(request.Provider) + "|" + (mmprojPath ?? "nommproj"),
            mmprojPath,
            ct).ConfigureAwait(false);
        var weights = lease.Weights;

        var (contextParams, signature) = BuildContextParams(path, request.Model, request.Provider, weights);
        string? marker = lease.Mtmd is null ? null : NativeApi.MtmdDefaultMarker();
        var (prompt, startsInThinking) = BuildPrompt(weights, request, marker);

        IAsyncEnumerable<LlmStreamEvent> stream;
        if (images.Count > 0)
        {
            // llama.cpp 的 CUDA 后端在 gemma4 混合架构（fused Gated DeltaNet）的非因果
            // 图像注意力上存在病态慢路径：实测 266 个图像 token 在 3070 Ti 上需 26 分钟，
            // 同一请求全 CPU 只要 23 秒。带图请求一律强制全 CPU context 保证可用性；
            // 纯文本轮不受影响，继续享受 GPU 卸载加速。
            var (cpuParams, _) = BuildContextParams(path, request.Model, request.Provider, weights, forceCpu: true);
            stream = StreamMultimodalAsync(weights, lease.Mtmd, cpuParams, prompt, startsInThinking, images, request, ct);
        }
        else
        {
            stream = request.Provider.LocalPrefixReuse
                ? StreamReusableAsync(cache, weights, contextParams, signature, prompt, startsInThinking, request, ct)
                : StreamStatelessAsync(weights, contextParams, prompt, startsInThinking, request, ct);
        }

        await foreach (var evt in stream.ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <summary>
    /// Collects image attachments across the conversation in message order (user turns and
    /// tool results both carry Images). Order matters: mtmd consumes queued media bitmaps
    /// at the markers in the same sequence.
    /// </summary>
    internal static IReadOnlyList<ChatImageAttachment> CollectImages(LlmRequest request) => request.Messages
        .SelectMany(message => message.Images ?? (IReadOnlyList<ChatImageAttachment>)[])
        .ToList();

    /// <summary>
    /// Multimodal inference: media bitmaps are queued in conversation order, the prompt
    /// carries an image marker per attachment, and mtmd tokenizes text+image into chunks
    /// evaluated straight into the KV cache. Image turns do not participate in KV prefix
    /// reuse (image chunks occupy non-token positions, so token-prefix bookkeeping would
    /// be unsound); the context is discarded after the turn like the stateless path.
    /// </summary>
    private async IAsyncEnumerable<LlmStreamEvent> StreamMultimodalAsync(
        LLamaWeights weights, MtmdWeights? mtmd, ModelParams contextParams, string prompt, bool startsInThinking,
        IReadOnlyList<ChatImageAttachment> images, LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        if (mtmd is null)
        {
            throw new LlmException(
                $"{request.Model.Id} received {images.Count} image(s), but no vision projector (mmproj) is " +
                $"configured. Put a *mmproj*.gguf matching this model into the provider's models directory " +
                $"or set model.mmprojPath; this local model cannot see images without it.",
                retryable: false);
        }

        var context = weights.CreateContext(contextParams, NullLogger.Instance);
        try
        {
            foreach (var image in images)
            {
                var bytes = Convert.FromBase64String(image.Data);
                mtmd.LoadMedia(bytes);
            }

            // Tokenize 把 prompt 按 marker 切成 text/image 混合块；媒体位图按入队顺序
            // 被 marker 位置消费。返回值为错误码（0 = 成功）。
            var tokenizeResult = mtmd.Tokenize(prompt, true, true, out var chunks);
            if (tokenizeResult != 0 || chunks.Size == 0)
                throw new LlmException(
                    $"Multimodal tokenization failed for {request.Model.Id} (code {tokenizeResult}). " +
                    "The mmproj may not match this model file.",
                    retryable: false);
            var promptPositions = chunks.CountPositions();
            var batch = new LLamaBatch();
            var decoder = new StreamingTokenDecoder(context);
            var inferenceParams = BuildInferenceParams(request);
            inferenceParams.SamplingPipeline.Reset();
            var antiprocessor = new AntipromptProcessor(inferenceParams.AntiPrompts);

            // mtmd 块直接评估进 KV cache（image 块内部展开为视觉嵌入位置）。
            // 签名: (chunks, ctx, ref new_n_past, seqId, nBatch, logitsLast)；n_batch 必须 >0。
            var nPast = 0;
            var evalResult = mtmd.EvaluateChunks(chunks, context.NativeHandle, ref nPast, 0, 256, true);
            if (evalResult != 0)
                throw new LlmException(
                    $"Multimodal prefill failed for {request.Model.Id} (code {evalResult}).",
                    retryable: false);

            var parser = new ThinkTagParser(startsInThinking);
            var maxTokens = inferenceParams.MaxTokens < 0 ? int.MaxValue : inferenceParams.MaxTokens;
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

                if (nPast + 1 >= context.ContextSize)
                {
                    // 图片轮上下文用完直接收尾，不做 KV 平移——多模态位置与 token 档案
                    // 不对应，平移后再采样结果不可信。
                    stopped = false;
                    break;
                }

                batch.Clear();
                batch.Add(id, nPast++, LLamaSeqId.Zero, true);
                var code = await context.DecodeAsync(batch, ct).ConfigureAwait(false);
                if (code != DecodeResult.Ok) throw new LLamaDecodeError(code);
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
                Usage = new TokenUsage(promptPositions, generated),
                FinishReason = stopped || ct.IsCancellationRequested ? "stop" : "length",
            });
        }
        finally
        {
            mtmd.ClearMedia();
            context.Dispose();
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
        string path, ModelConfig model, ProviderConfig provider, LLamaWeights weights, bool forceCpu = false)
    {
        // Registering a model with a context window larger than it was trained for would
        // silently extrapolate positions, so the effective context is clamped to the model.
        // It is also clamped to a practical CPU ceiling: the KV cache of a 128k window alone
        // costs multiple gigabytes of RAM, and an entry that was written by hand inherits
        // the remote default (128k) unless the registration set a real value.
        var trained = weights.ContextSize > 0 ? weights.ContextSize : 4096;
        var configured = model.ContextWindow > 0 ? model.ContextWindow : trained;
        var contextSize = (uint)Math.Max(512, Math.Min(configured, Math.Min(trained, LocalModelProbe.DefaultContextWindow)));
        var gpuLayers = forceCpu ? 0 : (provider.GpuLayers ?? 0);
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

    /// <summary>
    /// Renders the conversation with the chat template embedded in the GGUF file. When
    /// <paramref name="imageMarker"/> is set, one marker per attachment is appended to the
    /// text of the message that carries it — mtmd replaces markers with image embeddings.
    /// </summary>
    private static (string Prompt, bool StartsInThinking) BuildPrompt(
        LLamaWeights weights, LlmRequest request, string? imageMarker = null)
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
                    template.Add("user", WithImageMarkers(message, imageMarker));
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
                    template.Add("user", $"[{message.ToolName ?? "tool"} result]\n{WithImageMarkers(message, imageMarker)}");
                    break;
            }
        }

        template.AddAssistant = true;
        string prompt;
        try
        {
            prompt = Encoding.UTF8.GetString(template.Apply());
        }
        catch (Exception ex) when (ex is not LlmException)
        {
            // 新模型的官方模板常超前于 llama.cpp 内置 Jinja 的能力（如 Gemma 4 的
            // namespace / raise_exception 语法），apply 失败时按架构走 canonical 兜底渲染，
            // 而不是让该模型在 runtime 中完全不可用。
            prompt = RenderCanonicalPrompt(weights, request, ex, imageMarker);
        }

        // DeepSeek-R1 family models reason before answering: either the template injects
        // the opening tag into the generation prompt (original R1), or the template itself
        // references the ASCII "</think>" delimiter the model emits on its own (R1-0528).
        var trimmed = prompt.TrimEnd('\n', '\r', ' ');
        var startsInThinking = trimmed.EndsWith(" thinking", StringComparison.Ordinal)
            || (TryGetChatTemplate(weights)?.Contains("</think>", StringComparison.Ordinal) ?? false);
        return (prompt, startsInThinking);
    }

    /// <summary>Message text followed by one image marker per attached image.</summary>
    internal static string WithImageMarkers(ChatMessage message, string? imageMarker)
    {
        if (imageMarker is null || message.Images is not { Count: > 0 })
            return message.Text;
        return message.Text + string.Concat(Enumerable.Repeat(imageMarker, message.Images.Count));
    }

    /// <summary>
    /// Chat template 无法被 llama.cpp 应用时的架构化兜底。仅对已验证格式的架构生效；
    /// 其余架构如实抛错（带原始模板错误），绝不静默用错误格式降级模型输出质量。
    /// </summary>
    internal static string RenderCanonicalPrompt(
        LLamaWeights weights, LlmRequest request, Exception templateError, string? imageMarker = null)
    {
        var architecture = TryGetMetadata(weights, "general.architecture");
        if (architecture is "gemma4")
            return RenderGemmaTurnPrompt(request.Messages, request.System, request.EnableThinking, imageMarker);

        throw new LlmException(
            $"The chat template of {request.Model.Id} (architecture: {architecture ?? "unknown"}) " +
            $"could not be applied by the embedded llama.cpp: {templateError.Message}. " +
            "Update LLamaSharp for template support, or convert the GGUF with a simplified template.",
            retryable: false,
            inner: templateError);
    }

    /// <summary>
    /// Gemma 4 canonical 兜底渲染（对照模型内嵌官方模板的无工具/无多模态路径）：
    /// 每个角色渲染为 "&lt;|turn&gt;role\n内容&lt;turn|&gt;\n"，系统消息仅在有内容或开启思考时出现，
    /// 生成提示以 "&lt;|turn&gt;model\n" 结尾（开启思考时追加 "&lt;|think|&gt;\n"）。
    /// 不含 &lt;bos&gt;——BOS 由 Tokenize(addBos: true) 统一注入，避免重复。
    /// </summary>
    internal static string RenderGemmaTurnPrompt(
        IReadOnlyList<ChatMessage> messages, string? system, bool enableThinking, string? imageMarker = null)
    {
        var sb = new StringBuilder();
        if (enableThinking || !string.IsNullOrWhiteSpace(system))
        {
            sb.Append("<|turn>system\n");
            if (enableThinking) sb.Append("<|think|>\n");
            if (!string.IsNullOrWhiteSpace(system))
                sb.Append(system.Trim());
            sb.Append("<turn|>\n");
        }
        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case ChatRole.User:
                    sb.Append("<|turn>user\n").Append(WithImageMarkers(message, imageMarker).Trim()).Append("<turn|>\n");
                    break;
                case ChatRole.System:
                    if (!string.IsNullOrWhiteSpace(message.Text))
                        sb.Append("<|turn>system\n").Append(message.Text.Trim()).Append("<turn|>\n");
                    break;
                case ChatRole.Assistant:
                    if (message.Text.Length > 0)
                        sb.Append("<|turn>model\n").Append(message.Text).Append("<turn|>\n");
                    break;
                case ChatRole.Tool:
                    // 工具结果回放为用户轮，信息不丢失（与主模板行为一致）。
                    sb.Append("<|turn>user\n[").Append(message.ToolName ?? "tool").Append(" result]\n")
                      .Append(WithImageMarkers(message, imageMarker)).Append("<turn|>\n");
                    break;
            }
        }
        sb.Append("<|turn>model\n");
        return sb.ToString();
    }

    private static string? TryGetMetadata(LLamaWeights weights, string key)
    {
        try
        {
            foreach (var (k, value) in weights.Metadata)
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                    return value?.ToString();
        }
        catch (Exception)
        {
            // 元数据读取失败不影响主流程。
        }
        return null;
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
