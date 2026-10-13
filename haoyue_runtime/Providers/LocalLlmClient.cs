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
    /// 自适应调优器（P3/P4/P5）：装载档位计算、反馈调档与复用命中率跟踪。实例随
    /// 权重缓存单例存活（daemon 与客户端共享同一份）。仅在「自动」语义（AutoOptimize
    /// 且提供商级未显式配 GpuLayers）下生效；本地推理严格串行，无需加锁。
    /// </summary>
    private AdaptiveInferenceTuner Tuner => cache.Tuning;

    /// <summary>一次性超长文档（约 2 万 token 以上）的字符阈值：复用命中率过低时改走无状态路径（P5）。</summary>
    private const int LongDocumentChars = 80_000;

    /// <summary>
    /// 思考开标记（单一事实来源）：部分模型家族的模板把生成提示渲染成以该字面量结尾，
    /// BuildPrompt 据此判定 startsInThinking，ThinkTagParser 在输出流中匹配同一标记。
    /// 两侧必须一致——由模板耦合单测锁定。
    /// </summary>
    internal const string ThinkingOpenMarker = " thinking";

    /// <summary>解码速率低于该值（tok/s）且开启思考时，向上层附缩短思考预算的提示（P5）。</summary>
    private const double SlowDecodeHintTokensPerSecond = 10;

    /// <summary>
    /// 计算本请求的自适应档位。仅在「自动」语义下生效（AutoOptimize 且提供商级未显式
    /// 配置 GpuLayers）；模型形状来自 GGUF header + 文件大小（经元数据缓存，重复请求
    /// 零 IO），GPU 能力来自 nvidia-smi 探测，任一不可得即返回 null（保持提供商默认
    /// 行为，绝不猜测）。
    /// </summary>
    private InferenceTier? TierFor(Configuration.ProviderConfig provider, ModelConfig? model, string path) =>
        TierFor(Tuner, provider, model, path, cache.Metadata);

    /// <summary>静态形态：daemon 预热等无客户端实例的场景复用同一档位口径。</summary>
    internal static InferenceTier? TierFor(
        AdaptiveInferenceTuner tuner, Configuration.ProviderConfig provider, ModelConfig? model, string path,
        LocalModelMetadataCache? metadata = null)
    {
        if (!(model?.Load?.AutoOptimize ?? true)) return null;      // 用户手动配置：绝不干预
        if (provider.GpuLayers is not null) return null;            // 提供商级显式卸载：尊重配置
        var shape = BuildShape(path, metadata);
        if (shape is null) return null;
        return tuner.EnsureTier(path, shape, GpuProbe.Probe());
    }

    /// <summary>
    /// 模型静态形状（KV 显存估算用）。KvDim = KV头数 × 头维度；header 缺头维度时按
    /// 隐藏维度的一半保守估算（介于 MHA 与常见 GQA 之间），完全缺失则为 null。
    /// header 读取经 <paramref name="metadata"/> 缓存（路径+大小+mtime 键），重复
    /// 请求不触盘。
    /// </summary>
    internal static ModelShape? BuildShape(string path, LocalModelMetadataCache? metadata = null)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length <= 0) return null;
            var header = metadata is null ? GgufProbe.Read(path) : metadata.Header(path);
            long? kvDim = header.HeadCountKv is { } kvHeads
                && (header.KeyLength ?? header.ValueLength) is { } headDim
                    ? kvHeads * headDim
                    : header.EmbeddingLength is { } embedding ? embedding / 2 : null;
            return new ModelShape(file.Length, header.BlockCount, kvDim);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>自适应档位估算用的上下文长度（与 BuildContextParams 的 auto 分支口径一致）。</summary>
    internal static long EstimateContextForTier(ModelConfig? model, GgufProbe.Header header)
    {
        var trained = header.ContextLength is > 0 ? header.ContextLength.Value : 4096;
        return model?.ContextWindow > 0 ? Math.Min(model.ContextWindow, trained) : Math.Min(trained, LocalModelProbe.DefaultContextWindow);
    }

    /// <summary>
    /// In-process GGUF embeddings: acquires the shared weights lease, builds a
    /// dedicated embedding context (small window — embeddings do not need chat KV)
    /// and embeds inputs sequentially. Throws on a missing/broken model file so the
    /// semantic search layer degrades to lexical instead of returning garbage.
    /// </summary>
    public async Task<EmbeddingResult?> EmbedAsync(
        Configuration.ProviderConfig provider, IReadOnlyList<string> inputs, string? model = null, CancellationToken ct = default)
    {
        if (inputs.Count == 0) return new EmbeddingResult([], model ?? "");
        var modelConfig = model is not null
            ? provider.Models.FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase))
            : provider.Models.FirstOrDefault(m => m.Capabilities.Embedding);
        if (modelConfig is null)
            throw new LlmException(
                $"Local provider '{provider.Id}' has no embedding-capable model registered"
                + (model is null ? "" : $" (requested: '{model}')")
                + ". Add the model entry with capabilities.embedding = true.",
                retryable: false);
        var path = LocalModels.ResolveModelPath(provider, modelConfig);
        if (!File.Exists(path))
            throw new LlmException(
                $"Local embedding model file not found: {path}. Put the GGUF file into the provider's models directory or fix the path.",
                retryable: false);

        // 嵌入与聊天共用同一权重与签名口径：档位参与签名，避免聊天（带档位）与嵌入
        // （无档位）交替时签名漂移导致权重反复重载。
        var tier = TierFor(provider, modelConfig, path);

        using var lease = await cache.AcquireAsync(
            path,
            BuildLoadParams(path, provider, modelConfig, tier),
            LoadSignature(provider, modelConfig, tier),
            null,
            ct).ConfigureAwait(false);
        var weights = lease.Weights;

        using var embedder = new LLamaEmbedder(weights, new ModelParams(path)
        {
            // Embedding contexts stay small: no chat history, no generation.
            ContextSize = 2048,
            GpuLayerCount = provider.GpuLayers ?? 0,
            Threads = provider.Threads is { } embedThreads ? Math.Max(1, embedThreads) : null,
            UseMemorymap = true,
        }, NullLogger.Instance);

        var vectors = new List<float[]>(inputs.Count);
        foreach (var input in inputs)
        {
            ct.ThrowIfCancellationRequested();
            var embeddings = await embedder.GetEmbeddings(input, ct).ConfigureAwait(false);
            // 均值池化模型返回单向量；无池化模型按 token 返回——取末位向量（因果嵌入的标准做法）。
            vectors.Add(embeddings[^1]);
        }
        return new EmbeddingResult(vectors, modelConfig?.Id ?? Path.GetFileNameWithoutExtension(path));
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        // 异常回退链：主模型 + （可选）同提供商备用模型。仅「尚未产出任何 token 前」
        // 的加载/预处理失败才触发回退——已开始流式输出的异常原样上抛（半截回答
        // 换模型重答比失败更糟）。备用模型自身不再回退（不形成链）。
        // 结构说明：C# 迭代器禁止在含 catch 的 try 内 yield，故用手动枚举器 +
        // 仅含 finally 的外层 try；加载失败的捕获放在不含 yield 的内层 try。
        var chain = FallbackChain.Build(request.Provider, request.Model);
        LlmException? lastFailure = null;
        for (var index = 0; index < chain.Count; index++)
        {
            var model = chain[index];
            var hasNext = index < chain.Count - 1;
            var attempt = model == request.Model ? request : request with { Model = model };
            var streamed = false;
            var failed = false;
            var enumerator = StreamOnceAsync(attempt, ct).GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (LlmException ex) when (hasNext && !streamed)
                    {
                        lastFailure = ex;
                        failed = true;
                        break;
                    }
                    if (!moved) break;
                    streamed = true;
                    yield return enumerator.Current;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            if (failed) continue;
            yield break;
        }
        throw lastFailure ?? new LlmException("本地模型推理失败。", retryable: false);
    }

    /// <summary>
    /// 单次完整推理（原 StreamAsync 主体）。前置检查（文件缺失 / 投影器误注册）
    /// 与权重加载失败抛 <see cref="LlmException"/>——调用方的回退链据此换模型。
    /// </summary>
    private async IAsyncEnumerable<LlmStreamEvent> StreamOnceAsync(
        LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var path = LocalModels.ResolveModelPath(request.Provider, request.Model);
        if (!File.Exists(path))
            throw new LlmException(
                $"Local model file not found: {path}. Put the GGUF file into the provider's models directory or fix the path.",
                retryable: false);
        if (LocalModels.IsProjectorFileName(Path.GetFileName(path)))
            throw new LlmException(
                $"{request.Model.Id} 指向的是多模态投影器文件（mmproj），不是聊天模型，无法加载。"
                + "投影器会被同目录的主模型自动识别使用；请在模型列表中移除该条目。",
                retryable: false);

        var images = CollectImages(request);
        // mmproj 始终参与解析与加载签名（即使本轮纯文本），避免图片轮/文本轮交替时
        // 签名漂移导致权重反复重载。解析经元数据缓存（目录 mtime 键），重复请求
        // 不再枚举模型目录。
        var mmprojPath = cache.Metadata.ResolveMmproj(
            LocalModels.ResolveDirectory(request.Provider.ModelsDirectory), request.Model.MmprojPath);

        // 自适应档位（P3）：自动语义下按显存预算选档；签名携带档位身份，调档即走既有重载通道。
        var tier = TierFor(request.Provider, request.Model, path);

        using var lease = await cache.AcquireAsync(
            path,
            BuildLoadParams(path, request.Provider, request.Model, tier),
            LoadSignature(request.Provider, request.Model, tier) + "|" + (mmprojPath ?? "nommproj"),
            mmprojPath,
            ct).ConfigureAwait(false);
        var weights = lease.Weights;

        var (contextParams, signature) = BuildContextParams(path, request.Model, request.Provider, weights, tier: tier);
        string? marker = lease.Mtmd is null ? null : NativeApi.MtmdDefaultMarker();
        // 思考默认值：模型加载设置可覆盖每次请求的开关（配置页「思考」分区）。
        var thinkingEnabled = request.Model.Load?.EnableThinking ?? request.EnableThinking;
        var (prompt, startsInThinking) = BuildPrompt(weights, request, marker, thinkingEnabled);

        // 路径调度（P5）：会话型请求走 KV 前缀复用；一次性超长文档（复用命中率观测过低）
        // 改走无状态路径，避免为不再复用的 prompt 保留整段 KV 常驻。并发 > 1 时每请求
        // 独立 context，复用条目无法安全共享，同样改走无状态路径。
        var useReuse = request.Provider.LocalPrefixReuse && cache.SupportsPrefixReuse;
        if (useReuse && prompt.Length > LongDocumentChars && Tuner.LastCacheHitRate < 0.05)
            useReuse = false;

        var meter = new InferenceMeter(prefixReuse: useReuse, multimodal: images.Count > 0);
        IAsyncEnumerable<LlmStreamEvent> stream;
        if (images.Count > 0)
        {
            // llama.cpp 的 CUDA 后端在 gemma4 混合架构（fused Gated DeltaNet）的非因果
            // 图像注意力上存在病态慢路径：实测 266 个图像 token 在 3070 Ti 上需 26 分钟，
            // 同一请求全 CPU 只要 23 秒。带图请求一律强制全 CPU context 保证可用性；
            // 纯文本轮不受影响，继续享受 GPU 卸载加速。
            var (cpuParams, _) = BuildContextParams(path, request.Model, request.Provider, weights, forceCpu: true, tier: tier);
            stream = StreamMultimodalAsync(weights, lease.Mtmd, cpuParams, prompt, startsInThinking, images, request, ct);
        }
        else
        {
            stream = useReuse
                ? StreamReusableAsync(cache, weights, contextParams, signature, prompt, startsInThinking, request, ct)
                : StreamStatelessAsync(weights, contextParams, prompt, startsInThinking, request, ct);
        }

        await foreach (var evt in MeasureAsync(stream, meter, request).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <summary>
    /// 遥测包装（P1）+ OOM 反馈（P4）：对内层流透传每个事件并打点，完成事件结算
    /// InferencePerformance（附慢模型思考预算提示，P5）；内层抛出 OOM 类异常时通知
    /// 调优器紧急降档后原样上抛。取消与普通异常的语义完全不变。
    /// </summary>
    private async IAsyncEnumerable<LlmStreamEvent> MeasureAsync(
        IAsyncEnumerable<LlmStreamEvent> inner, InferenceMeter meter, LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var enumerator = inner.GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                LlmStreamEvent current;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                    current = enumerator.Current;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (OomPattern.Matches(ex.Message)) Tuner.OnOom();
                    throw;
                }

                yield return meter.Observe(current);
                if (current is LlmCompleted done)
                {
                    var finished = meter.Finalize(done);
                    var performance = finished.Completion.Performance;
                    if (performance is { } perf
                        && perf.DecodeTokensPerSecond < SlowDecodeHintTokensPerSecond
                        && perf.DecodeTokensPerSecond > 0
                        && request.EnableThinking)
                    {
                        finished = finished with
                        {
                            Completion = finished.Completion with
                            {
                                Performance = perf with
                                {
                                    Hint = $"本地模型解码速度 {perf.DecodeTokensPerSecond:F1} tok/s 低于 "
                                           + $"{SlowDecodeHintTokensPerSecond:F0} tok/s，建议缩短思考预算或关闭思考模式。",
                                },
                            },
                        };
                    }
                    if (finished.Completion.Performance is { } final)
                        Tuner.OnObservation(final);
                    yield return finished;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
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
            if (tokenizeResult != 0 || chunks is null || chunks.Size == 0)
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
            // 思考预算（与 KV 复用路径同口径）：耗尽后注入结束标记强制收束。
            var thinkingBudget = ThinkingBudgetTokens(request);
            var thinkingTokens = 0;
            var budgetExhausted = false;
            for (var i = 0; i < maxTokens && !ct.IsCancellationRequested; i++)
            {
                var id = inferenceParams.SamplingPipeline.Sample(context.NativeHandle, batch.TokenCount - 1);
                if (id.IsEndOfGeneration(weights.Vocab)) { stopped = true; break; }

                decoder.Add(id);
                generated++;
                var decoded = decoder.Read();
                if (antiprocessor.Add(decoded)) { stopped = true; break; }

                if (thinkingBudget is { } mBudget && !budgetExhausted && parser.InThinking)
                {
                    thinkingTokens++;
                    if (thinkingTokens >= mBudget)
                    {
                        budgetExhausted = true;

                        foreach (var piece in parser.Process("</think>"))
                        {
                            if (piece.IsThinking) yield return new LlmThinkingDelta(piece.Text);
                            else yield return new LlmTextDelta(piece.Text);
                        }
                    }
                }

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

        // 思考预算：预算耗尽时注入结束标记强制收束进入作答（与 llama-server
        // --reasoning-budget 行为一致），避免小模型单步思考以分钟计。
        var thinkingBudget = ThinkingBudgetTokens(request);
        var thinkingTokens = 0;
        var budgetExhausted = false;

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

                // 思考预算计量：仅在思考段内逐采样 token 计数；耗尽后一次性注入
                // 结束标记，parser 随即切入作答段，后续采样继续正常进行。
                if (thinkingBudget is { } budget && !budgetExhausted && parser.InThinking)
                {
                    thinkingTokens++;
                    if (thinkingTokens >= budget)
                    {
                        budgetExhausted = true;

                        foreach (var piece in parser.Process("</think>"))
                        {
                            if (piece.IsThinking) yield return new LlmThinkingDelta(piece.Text);
                            else yield return new LlmTextDelta(piece.Text);
                        }
                    }
                }

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

    /// <summary>
    /// Model-load parameters builder. Merges the provider-level settings with the per-model
    /// load settings from the model configuration page (model.Load wins; AutoOptimize=true
    /// forces GPU offload / threads back to the provider level, i.e. "AUTO"). Returns a
    /// factory so the model cache can request a CPU-only variant (GPU offload degradation)
    /// without re-deriving every field. Batch sizes, mmap/mlock, FlashAttention, KV offload,
    /// unified KV and RoPE overrides all come straight from the configuration page.
    /// <paramref name="tier"/> 为自适应档位（P3）：仅在自动语义下由调用方传入，覆盖
    /// GPU 卸载层数、KV 卸载与物理批大小；null 表示保持原有自动/手动语义。
    /// </summary>
    internal static Func<bool, ModelParams> BuildLoadParams(
        string path, ProviderConfig provider, ModelConfig? model, InferenceTier? tier = null)
    {
        var load = model?.Load;
        // 显式覆盖仅在「load 存在且 AutoOptimize=false 且值非空」时生效；load 为 null
        // （未配置过加载页）必须走提供商级默认。属性模式对 null 恒为 false，因此这里
        // 不能写成 `load is { AutoOptimize: true } or { GpuOffload: null }`——该写法在
        // load == null 时落入 else 分支对 load! 解引用，直接 NullReferenceException。
        // 档位存在（自动语义 + 提供商级未显式配 GPU）时，档位就是「自动」的最终答案。
        var gpuLayers = tier is { } tierValue ? tierValue.GpuLayers
            : load is { AutoOptimize: false, GpuOffload: { } explicitGpu }
                ? explicitGpu
                : provider.GpuLayers ?? 0;
        var threads = load is { AutoOptimize: false, Threads: { } explicitThreads }
            ? explicitThreads
            : provider.Threads;
        var kvOffload = tier is { } kvTier ? kvTier.KvOffload : load?.OffloadKvCacheToGpu ?? true;
        var ubatch = tier is { } ubTier ? ubTier.UBatch : load?.PhysicalBatchSize ?? 512;
        var requested = new ModelParams(path)
        {
            GpuLayerCount = gpuLayers,
            Threads = threads is { } loadThreads ? Math.Max(1, loadThreads) : null,
            // mmap 按页映射权重文件：加载峰值内存远低于整模型读入，且启动更快；mlock
            // 默认关闭，避免锁页把常驻内存顶到峰值（配置页「内存」分区）。
            UseMemorymap = load?.TryMmap ?? true,
            UseMemoryLock = load?.KeepModelInMemory ?? false,
            BatchSize = (uint)Math.Clamp(load?.EvaluationBatchSize ?? 2048, 16, 8192),
            UBatchSize = (uint)Math.Clamp(ubatch, 16, 8192),
            FlashAttention = load?.FlashAttention ?? provider.FlashAttention,
            NoKqvOffload = !kvOffload,
            KVUnified = load?.UnifiedKvCache,
            RopeFrequencyBase = (float?)load?.RopeFrequencyBase,
            RopeFrequencyScale = (float?)load?.RopeFrequencyScale,
        };
        return cpuOnly => cpuOnly ? requested with { GpuLayerCount = 0 } : requested;
    }

    /// <summary>
    /// Identity of the provider- and model-level load settings that force a weights reload
    /// when they change (the model cache would otherwise reuse weights loaded with the old
    /// values). FlashAttention is a context-level parameter and lives in the context
    /// signature instead. 自适应档位（非 null 时）进入签名：调档即触发权重重载。
    /// </summary>
    internal static string LoadSignature(ProviderConfig provider, ModelConfig? model = null, InferenceTier? tier = null) =>
        $"{provider.GpuLayers?.ToString() ?? "cpu"}|{provider.Threads?.ToString() ?? "auto"}"
        + (model?.Load is { } load ? "|" + load.Signature() : "")
        + (tier is { } signatureTier ? "|" + signatureTier.Key : "");

    private static (ModelParams Params, string Signature) BuildContextParams(
        string path, ModelConfig model, ProviderConfig provider, LLamaWeights weights,
        bool forceCpu = false, InferenceTier? tier = null)
    {
        // Registering a model with a context window larger than it was trained for would
        // silently extrapolate positions, so the effective context is clamped to the model.
        // An explicit context length from the configuration page is clamped to the trained
        // window only; an "auto" value keeps the practical CPU ceiling so a hand-written
        // registration that inherited the remote default (128k) cannot allocate gigabytes
        // of KV cache by accident.
        var load = model.Load;
        var trained = weights.ContextSize > 0 ? weights.ContextSize : 4096;
        var configured = load is { ContextLength: { } explicitLength, AutoOptimize: false } && explicitLength > 0
            ? explicitLength
            : model.ContextWindow > 0 ? model.ContextWindow : trained;
        var ceiling = load is { ContextLength: { }, AutoOptimize: false }
            ? trained
            : Math.Min(trained, LocalModelProbe.DefaultContextWindow);
        var contextSize = (uint)Math.Max(512, Math.Min(configured, ceiling));
        // 同 BuildLoadParams：load == null（未配置加载页）时必须走提供商级默认，
        // 属性模式 `or { X: null }` 对 null 恒为 false，原写法会 NRE。
        // 档位存在（自动语义 + 提供商级未显式配 GPU）时覆盖 GPU 卸载与 KV 卸载。
        var gpuLayers = forceCpu ? 0
            : tier is { } cpuTier ? cpuTier.GpuLayers
            : load is { AutoOptimize: false, GpuOffload: { } explicitGpu }
                ? explicitGpu
                : provider.GpuLayers ?? 0;
        var threads = load is { AutoOptimize: false, Threads: { } explicitThreads } ? explicitThreads : provider.Threads;
        bool? flashAttention = load?.FlashAttention ?? provider.FlashAttention;
        // KV 量化来源优先级：用户显式配置（加载页 / 提供商级）> 自适应档位 > 关闭。
        // 量化需要 FlashAttention（llama.cpp 硬性前提）：档位要求量化而用户未显式配置
        // FA 时按档位启用；用户已显式关闭 FA 则量化静默回退 f16（与既有语义一致）。
        var userKvQuant = load?.KCacheQuantType ?? provider.KvCacheQuantization;
        var tierKvQuant = tier is { } quantTier && userKvQuant is null ? quantTier.KvQuant : null;
        if (tierKvQuant is { } required && required != "none" && flashAttention is null)
            flashAttention = true;
        var faOn = flashAttention ?? false;
        var vQuant = load?.VCacheQuantType ?? userKvQuant ?? tierKvQuant;
        var kQuant = userKvQuant ?? tierKvQuant;
        var kvQuant = faOn ? NormalizeKvQuant(kQuant) : "none";
        var parameters = new ModelParams(path)
        {
            ContextSize = contextSize,
            GpuLayerCount = gpuLayers,
            Threads = threads is { } threadCount ? Math.Max(1, threadCount) : null,
            FlashAttention = faOn,
            UseMemorymap = load?.TryMmap ?? true,
            UseMemoryLock = load?.KeepModelInMemory ?? false,
            BatchSize = (uint)Math.Clamp(load?.EvaluationBatchSize ?? 2048, 16, 8192),
            UBatchSize = (uint)Math.Clamp(tier?.UBatch ?? load?.PhysicalBatchSize ?? 512, 16, 8192),
            NoKqvOffload = tier is { } offloadTier ? !offloadTier.KvOffload : load is { OffloadKvCacheToGpu: false },
            KVUnified = load?.UnifiedKvCache,
            RopeFrequencyBase = (float?)load?.RopeFrequencyBase,
            RopeFrequencyScale = (float?)load?.RopeFrequencyScale,
        };
        if (kvQuant != "none")
        {
            parameters.TypeK = kvQuant == "q4_0" ? GGMLType.GGML_TYPE_Q4_0 : GGMLType.GGML_TYPE_Q8_0;
            parameters.TypeV = NormalizeKvQuant(vQuant) == "q4_0" ? GGMLType.GGML_TYPE_Q4_0 : GGMLType.GGML_TYPE_Q8_0;
        }
        // Any change to these values invalidates the reusable context (different KV layout).
        var signature = $"{path}|{contextSize}|{gpuLayers}|{threads?.ToString() ?? "auto"}|{faOn}|{kvQuant}|{NormalizeKvQuant(vQuant)}"
                        + (load is null ? "" : $"|{load.Signature()}")
                        + (tier is { } sigTier ? $"|{sigTier.Key}" : "");
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
        LLamaWeights weights, LlmRequest request, string? imageMarker = null, bool? enableThinking = null)
    {
        var thinkingEnabled = enableThinking ?? request.EnableThinking;
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
            prompt = RenderCanonicalPrompt(weights, request, ex, imageMarker, thinkingEnabled);
        }

        // DeepSeek-R1 family models reason before answering: either the template injects
        // the opening tag into the generation prompt (original R1), or the template itself
        // references the ASCII "</think>" delimiter the model emits on its own (R1-0528).
        var trimmed = prompt.TrimEnd('\n', '\r', ' ');
        var startsInThinking = trimmed.EndsWith(ThinkingOpenMarker, StringComparison.Ordinal)
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
        LLamaWeights weights, LlmRequest request, Exception templateError, string? imageMarker = null, bool enableThinking = true)
    {
        var architecture = TryGetMetadata(weights, "general.architecture");
        if (architecture is "gemma4")
            return RenderGemmaTurnPrompt(request.Messages, request.System, enableThinking, imageMarker);

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
    /// 采样参数配置。优先级：模型加载配置页（ModelConfig.Load）&gt; 内置默认。内置默认沿用
    /// DeepSeek 对 R1 系列的建议值：温度 0.6、top-p 0.95；显式设置重复惩罚是关键改进——
    /// 库默认 RepeatPenalty=1（关闭），小参数量本地模型在无惩罚时极易陷入逐词复读循环；
    /// 惩罚窗口 64 token 覆盖近邻重复，且不惩罚换行，避免长代码块与列表的格式被破坏。
    /// 配置页的停止串映射为反提示，种子非空时固定采样种子。
    /// </summary>
    private static InferenceParams BuildInferenceParams(LlmRequest request)
    {
        var load = request.Model.Load;
        var maxTokens = request.MaxTokens is { } requested && requested > 0
            ? requested
            : Math.Clamp(request.Model.MaxOutput, 256, 32_768);
        // 配置页「限制回复长度」开启时，严格压到模型注册的最大输出以内。
        if (load?.LimitResponseLength == true)
            maxTokens = Math.Min(maxTokens, Math.Clamp(request.Model.MaxOutput, 16, 32_768));
        var pipeline = new DefaultSamplingPipeline
        {
            Temperature = (float)(load?.Temperature ?? request.Temperature ?? 0.6),
            TopP = (float)(load?.TopP ?? 0.95),
            TopK = load?.TopK ?? 40,
            MinP = (float)(load?.MinP ?? 0.1),
            RepeatPenalty = (float)(load?.RepeatPenalty ?? 1.1),
            PresencePenalty = (float)(load?.PresencePenalty ?? 0),
            PenaltyCount = 64,
            PenalizeNewline = false,
        };
        if (load?.Seed is { } seed)
            pipeline.Seed = (uint)Math.Clamp(seed, 0, uint.MaxValue);
        return new InferenceParams
        {
            MaxTokens = maxTokens,
            SamplingPipeline = pipeline,
            AntiPrompts = load?.StopStrings is { Count: > 0 } stops ? stops : [],
        };
    }

    /// <summary>
    /// 本地思考预算（按推理等级映射思考 token 上限，对齐远程提供商的预算映射口径）。
    /// 小参数量本地模型 decode 速度有限（几十 tok/s），无预算时单步思考动辄数千 token、
    /// 以分钟计；预算耗尽后由生成循环注入结束标记强制收束进入作答。返回 null 表示
    /// 思考未开启（无预算概念）。多步回合预算按步生效——agent 每步独立调用，预算即
    /// 单步上限，与远程 AnthropicBudget 的语义一致。
    /// </summary>
    internal static int? ThinkingBudgetTokens(LlmRequest request)
    {
        if (!request.EnableThinking) return null;
        if (!request.Model.Capabilities.Thinking) return null;
        var budget = request.ReasoningLevel switch
        {
            ReasoningLevel.Low => 1_024,
            ReasoningLevel.Medium => 3_072,
            ReasoningLevel.High => 6_144,
            ReasoningLevel.Max or ReasoningLevel.XHigh or ReasoningLevel.Ultra => 12_288,
            _ => 0,
        };
        if (budget <= 0) return null;
        // 预算不得超过本步输出窗口：思考必须给回答留出空间。
        var maxOutput = request.MaxTokens ?? request.Model.MaxOutput;
        return Math.Min(budget, Math.Max(256, maxOutput - 256));
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
/// 本地模型回退链解析（P-回退）。规则：
/// 1) 主模型始终在链首；
/// 2) 主模型配置了 FallbackModelId 且能在同提供商内解析到、且不是主模型自身时，
///    追加为第二个成员（不形成链——备用模型自身不再回退，避免循环与延迟叠加）；
/// 3) 解析不到（id 写错 / 指向自身 / 远端提供商）时链长为 1，调用方照常抛错。
/// </summary>
internal static class FallbackChain
{
    public static IReadOnlyList<ModelConfig> Build(Configuration.ProviderConfig provider, ModelConfig model)
    {
        var fallbackId = model.FallbackModelId;
        if (string.IsNullOrWhiteSpace(fallbackId)) return [model];
        var fallback = provider.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, fallbackId, StringComparison.OrdinalIgnoreCase));
        if (fallback is null || ReferenceEquals(fallback, model)) return [model];
        return [model, fallback];
    }
}

/// <summary>
/// Incrementally splits model output into reasoning / answer pieces. Handles both the
/// ASCII "&lt;/think&gt;" delimiter of the DeepSeek-R1-0528 family and the special-token
/// delimiters of the original R1, with tags possibly split across stream chunks.
/// </summary>
internal sealed class ThinkTagParser(bool startsInThinking)
{
    // 开标记与 BuildPrompt 的 ThinkingOpenMarker 同源（模板耦合契约，单测锁定）。
    // 标准的 <think>（R1-0528 家族自带输出）与原版 R1 特殊 token 开标记一并覆盖，
    // 取最早出现者；" thinking" 为模板渲染尾巴的兜底匹配。
    internal static readonly string[] OpenTags =
        ["<think>", LocalLlmClient.ThinkingOpenMarker, "<｜begin▁of▁thinking｜>"];
    internal static readonly string[] CloseTags = ["</think>", "<｜end▁of▁thinking｜>"];

    private readonly StringBuilder _buffer = new();
    private readonly StringBuilder _thinking = new();
    private readonly StringBuilder _answer = new();
    private bool _inThinking = startsInThinking;
    private bool _emittedText;

    /// <summary>Everything classified as reasoning so far, which the answer may still grow into.</summary>
    public string Thinking => _thinking.ToString();

    /// <summary>
    /// Whether the parser is currently inside the thinking segment. The generation loop
    /// reads this to meter thinking against the reasoning-level budget and force the
    /// close tag once the budget is exhausted.
    /// </summary>
    public bool InThinking => _inThinking;

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

            var (open, openTag) = FindEarliestTag(buffer, OpenTags);
            if (open >= 0)
            {
                Add(pieces, false, buffer[..open]);
                _buffer.Remove(0, open + openTag.Length);
                _inThinking = true;
                continue;
            }
            var keep = LongestTagPrefixAtEnd(buffer, OpenTags);
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

    private static (int Index, string Tag) FindCloseTag(string text) => FindEarliestTag(text, CloseTags);

    private static (int Index, string Tag) FindEarliestTag(string text, string[] tags)
    {
        var best = -1;
        var bestTag = "";
        foreach (var tag in tags)
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
