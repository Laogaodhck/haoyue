using System.Text.Json;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Secrets;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Daemon;

/// <summary>
/// Providers / models surface: provider CRUD, local model scan + lifecycle, and
/// model benchmarks. Split out of DaemonAdminApi.cs by domain (ADR-20261015);
/// the line-budget guard measures the whole class across its partial files.
/// </summary>
internal sealed partial class DaemonAdminApi
{
    public string ListProviders()
    {
        var config = runtime.ConfigStore.Config;
        var active = config.Provider;
        var providers = new JsonArray();
        foreach (var provider in config.Providers.OrderBy(item => item.Priority).ThenBy(item => item.Id))
            providers.Add((JsonNode)ProviderJson(provider, provider.Id.Equals(active, StringComparison.OrdinalIgnoreCase)));
        return providers.ToJsonString();
    }

    public string UpsertProvider(JsonObject parameters)
    {
        var id = RequiredString(parameters, "id");
        var config = runtime.ConfigStore.Config;
        var provider = config.FindProvider(id);
        var isNew = provider is null;
        if (provider is null)
        {
            // Copy-on-Write: the new provider is NOT appended to the live list here.
            // Concurrent turns enumerate Config.Providers while this request runs, and a
            // structural List<T> mutation would throw InvalidOperationException in the
            // enumerator and crash the turn. The list reference is swapped atomically
            // below, after validation.
            provider = new ProviderConfig { Id = id };
        }

        var kind = OptionalString(parameters, "kind") ?? provider.Kind;
        if (kind is not ("openai" or "anthropic" or "local"))
            throw new DaemonRequestException("Provider kind must be openai, anthropic or local");
        provider.Kind = kind;
        provider.Name = OptionalString(parameters, "name") ?? provider.Name;
        provider.BaseUrl = OptionalString(parameters, "baseUrl") ?? provider.BaseUrl;
        provider.Proxy = OptionalString(parameters, "proxy") ?? provider.Proxy;
        provider.Enabled = parameters["enabled"]?.GetValue<bool?>() ?? provider.Enabled;
        provider.Priority = parameters["priority"]?.GetValue<int?>() ?? provider.Priority;
        provider.TimeoutSeconds = parameters["timeoutSeconds"]?.GetValue<int?>() ?? provider.TimeoutSeconds;
        if (parameters.ContainsKey("modelListUrl"))
            provider.ModelListUrl = OptionalString(parameters, "modelListUrl");
        if (parameters.ContainsKey("modelsDirectory"))
            provider.ModelsDirectory = OptionalString(parameters, "modelsDirectory");
        provider.PromptCaching = parameters["promptCaching"]?.GetValue<bool?>() ?? provider.PromptCaching;
        ApplyProviderAdvanced(parameters, provider);

        if (parameters["clearApiKey"]?.GetValue<bool>() == true)
            provider.ApiKey = null;
        else if (OptionalString(parameters, "apiKey") is { } apiKey)
        {
            // 加密不可用（文件库钥缺失/损坏）时拒绝保存：把明文写进配置文件是最坏结果。
            try
            {
                provider.ApiKey = SecretResolver.Encrypt($"provider/{provider.Id}", apiKey);
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException
                                           or InvalidOperationException or IOException or NotSupportedException)
            {
                throw new DaemonRequestException(
                    $"当前平台无法安全保存 API Key（{ex.Message}），配置未改动。存储方案：{SecretResolver.StorageScheme}。");
            }
        }

        if (parameters["modelDetails"] is JsonArray modelDetails)
        {
            var existing = provider.Models.ToDictionary(model => model.Id, StringComparer.OrdinalIgnoreCase);
            provider.Models = modelDetails
                .Select(node => node as JsonObject)
                .Where(node => node is not null && !string.IsNullOrWhiteSpace(node["id"]?.GetValue<string>()))
                .Select(node =>
                {
                    var modelId = node!["id"]!.GetValue<string>().Trim();
                    var model = existing.TryGetValue(modelId, out var m) ? m : new ModelConfig { Id = modelId };
                    if (node.ContainsKey("alias"))
                        model.Alias = node["alias"]?.GetValue<string>()?.Trim() is { Length: > 0 } alias ? alias : null;
                    if (node["contextWindow"]?.GetValue<int?>() is { } cw && cw > 0)
                        model.ContextWindow = cw;
                    if (node["maxOutput"]?.GetValue<int?>() is { } mo && mo > 0)
                        model.MaxOutput = mo;
                    if (node["vision"]?.GetValue<bool?>() is { } v)
                        model.Capabilities.Vision = v;
                    if (node["toolCalling"]?.GetValue<bool?>() is { } tools)
                        model.Capabilities.ToolCalling = tools;
                    if (node.ContainsKey("localPath"))
                        model.LocalPath = OptionalString(node, "localPath");
                    if (node.ContainsKey("mmprojPath"))
                        model.MmprojPath = OptionalString(node, "mmprojPath");
                    if (node["load"] is JsonObject loadNode)
                        model.Load = LocalModelSettings.FromJson(loadNode);
                    else if (node.ContainsKey("load") && node["load"] is null)
                        model.Load = null; // 显式传 null：清除加载配置，回到「自动」
                    ApplyModelAdvanced(node, model);
                    return model;
                })
                .ToList();
        }
        else if (parameters["models"] is JsonArray models)
        {
            var existing = provider.Models.ToDictionary(model => model.Id, StringComparer.OrdinalIgnoreCase);
            provider.Models = models
                .Select(node => node?.GetValue<string>()?.Trim())
                .Where(modelId => !string.IsNullOrWhiteSpace(modelId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(modelId => existing.TryGetValue(modelId!, out var model)
                    ? model
                    : new ModelConfig { Id = modelId! })
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(provider.BaseUrl) && !provider.IsLocal)
        {
            throw new DaemonRequestException("Provider baseUrl is required");
        }
        if (provider.Models.Count == 0)
        {
            throw new DaemonRequestException("At least one model is required");
        }
        // Local entries are only useful if the GGUF file can actually be found, so reject a
        // registration that names a missing file instead of failing at chat time.
        if (provider.IsLocal)
        {
            var missing = provider.Models
                .Select(model => (model.Id, Path: LocalModels.ResolveModelPath(provider, model)))
                .Where(entry => !File.Exists(entry.Path))
                .ToList();
            if (missing.Count > 0)
            {
                throw new DaemonRequestException(
                    $"Local model file not found: {string.Join(", ", missing.Select(entry => entry.Id))}");
            }
            var projectors = provider.Models
                .Where(model => LocalModels.IsProjectorFileName(model.LocalPath ?? model.Id))
                .Select(model => model.Id)
                .ToList();
            if (projectors.Count > 0)
            {
                // mmproj files hold the vision encoder, not a chat model; loading one always
                // fails, so reject the registration instead of shipping a broken entry.
                throw new DaemonRequestException(
                    $"这些文件是多模态投影器（mmproj），不能注册为聊天模型，会被同目录主模型自动识别：{string.Join(", ", projectors)}");
            }
        }

        // Validation passed: publish the new provider via an atomic list swap
        // (Copy-on-Write) so concurrent enumerations never observe a torn list.
        if (isNew)
            config.Providers = [.. config.Providers, provider];

        runtime.ConfigStore.Save();
        return ProviderJson(provider, provider.Id.Equals(config.Provider, StringComparison.OrdinalIgnoreCase)).ToJsonString();
    }

    /// <summary>
    /// Lists the GGUF files of a local models directory so a front end can offer one-click
    /// registration. Without a directory argument the directory a local provider would use
    /// is reported, which is the configured value, then HAOYUE_MODELS_DIR, then the
    /// repository or per-user models folder.
    /// </summary>
    public string ScanLocalModels(JsonObject parameters)
    {
        var configured = OptionalString(parameters, "directory");
        var directory = LocalModels.ResolveDirectory(configured);
        var models = new JsonArray(LocalModelProbe.Scan(configured).Select(model => (JsonNode)new JsonObject
        {
            ["id"] = model.ModelId,
            ["path"] = model.Path,
            ["sizeBytes"] = model.SizeBytes,
            ["displayName"] = model.DisplayName,
            ["architecture"] = model.Architecture,
            ["trainedContext"] = model.TrainedContext,
            ["contextWindow"] = model.ContextWindow,
            ["maxOutput"] = model.MaxOutput,
            // 2.6 量化引导：低效档位才有 advice（空串不透出该键）。
            ["quantization"] = model.Quantization is { } quant && quant.HasAdvice
                ? (JsonNode)new JsonObject
                {
                    ["label"] = quant.Label ?? "",
                    ["bitsPerWeight"] = quant.BitsPerWeight,
                    ["advice"] = quant.Advice,
                }
                : null,
        }).ToArray());

        return new JsonObject
        {
            ["directory"] = directory,
            ["exists"] = Directory.Exists(directory),
            ["models"] = models,
        }.ToJsonString();
    }

    public string UseProvider(JsonObject parameters)
    {
        var id = RequiredString(parameters, "id");
        var provider = runtime.ConfigStore.Config.FindProvider(id)
                       ?? throw new DaemonRequestException($"Provider not found: {id}");
        var config = runtime.ConfigStore.Config;
        config.Provider = provider.Id;
        if (config.Model is null || !provider.Models.Any(model => model.Id.Equals(config.Model, StringComparison.OrdinalIgnoreCase)))
            config.Model = provider.Models.FirstOrDefault()?.Id;
        runtime.ConfigStore.Save();
        return provider.Id;
    }

    public string RemoveProvider(JsonObject parameters)
    {
        var id = RequiredString(parameters, "id");
        var config = runtime.ConfigStore.Config;
        var provider = config.FindProvider(id)
                       ?? throw new DaemonRequestException($"Provider not found: {id}");
        // Copy-on-Write: concurrent turns enumerate Config.Providers; a structural
        // Remove on the live list would crash their enumerators. Swap the reference.
        config.Providers = config.Providers
            .Where(item => !item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (id.Equals(config.Provider, StringComparison.OrdinalIgnoreCase))
        {
            config.Provider = null;
            config.Model = null;
        }
        runtime.ConfigStore.Save();
        return id;
    }

    public async Task<string> TestProvidersAsync(JsonObject parameters, CancellationToken ct)
    {
        var id = OptionalString(parameters, "id");
        var providers = runtime.ConfigStore.Config.Providers
            .Where(provider => id is null || provider.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (providers.Count == 0) throw new DaemonRequestException("No matching providers");

        var reports = await Task.WhenAll(providers.Select(provider => runtime.Health.CheckAsync(provider, ct)))
            .ConfigureAwait(false);
        var data = new JsonArray();
        foreach (var report in reports)
        {
            data.Add((JsonNode)new JsonObject
            {
                ["id"] = report.ProviderId,
                ["online"] = report.Online,
                ["latencyMs"] = report.LatencyMs,
                ["detail"] = report.Detail,
            });
        }
        return data.ToJsonString();
    }

    public string ModelCatalog()
    {
        string? activeRef = null;
        try { activeRef = runtime.Providers.ResolveActive(runtime.Workspace.Config).Ref; }
        catch (Exception) { }

        var models = new JsonArray();
        foreach (var model in runtime.Models.All(includeDisabledProviders: true))
        {
            models.Add((JsonNode)new JsonObject
            {
                ["ref"] = model.Ref,
                ["active"] = model.Ref.Equals(activeRef, StringComparison.OrdinalIgnoreCase),
                ["provider"] = model.Provider.Id,
                ["providerEnabled"] = model.Provider.Enabled,
                ["id"] = model.Model.Id,
                ["alias"] = model.Model.Alias,
                ["contextWindow"] = model.Model.ContextWindow,
                ["maxOutput"] = model.Model.MaxOutput,
                ["tags"] = Strings(model.Model.Tags ?? []),
                ["capabilities"] = new JsonObject
                {
                    ["streaming"] = model.Capabilities.Streaming,
                    ["tools"] = model.Capabilities.ToolCalling,
                    ["thinking"] = model.Capabilities.Thinking,
                    ["vision"] = model.Capabilities.Vision,
                    // 图像生成与嵌入能力此前没进目录：界面上「图像生成模型」「语义检索模型」
                    // 两个选择器因此只能让用户手填 ref，选错要到调用时才失败。
                    ["image"] = model.Capabilities.Image,
                    ["embedding"] = model.Capabilities.Embedding,
                    ["reasoning"] = model.Capabilities.Reasoning,
                    ["maxReasoningLevel"] = model.Capabilities.MaxReasoningLevel.ToWireValue(),
                    ["mcp"] = model.Capabilities.Mcp,
                },
            });
        }
        return models.ToJsonString();
    }

    public async Task<string> TestModelAsync(JsonObject parameters, CancellationToken ct)
    {
        var reference = RequiredString(parameters, "model");
        var model = runtime.Models.Resolve(reference)
                    ?? throw new DaemonRequestException($"Model not found: {reference}");
        var result = await runtime.Providers.TestModelAsync(model, ct).ConfigureAwait(false);
        return new JsonObject
        {
            ["model"] = model.Ref,
            ["success"] = result.Success,
            ["detail"] = result.Detail,
            ["latencyMs"] = result.LatencyMs,
        }.ToJsonString();
    }

    /// <summary>
    /// 本地模型基准测试（P6）：对指定模型以固定 prompt 流式生成若干轮，采集每轮的
    /// TTFT / decode 速率 / prefill 速率 / KV 复用命中率（InferencePerformance）并输出
    /// 聚合统计。仅支持本地提供商；首轮结果含模型加载时间（TTFT 口径覆盖加载等待），
    /// 聚合时单列。验收指标口径与 haoyue_doc/CUDA本地推理加速优化方案-2026-10-11.md §4 一致。
    /// </summary>
    public async Task<string> BenchmarkModelAsync(JsonObject parameters, CancellationToken ct)
    {
        var reference = RequiredString(parameters, "model");
        var model = runtime.Models.Resolve(reference)
                    ?? throw new DaemonRequestException($"Model not found: {reference}");
        if (!model.Provider.IsLocal)
            throw new DaemonRequestException($"model.benchmark 仅支持本地模型（{model.Ref} 是 {model.Provider.Kind}）");
        var runs = Math.Clamp(parameters["runs"] is { } r && r.GetValue<int>() > 0 ? r.GetValue<int>() : 3, 1, 10);
        var maxTokens = Math.Clamp(parameters["maxTokens"] is { } m && m.GetValue<int>() > 0 ? m.GetValue<int>() : 256, 16, 4096);
        var prompt = (parameters["prompt"]?.GetValue<string>() is { Length: > 0 } custom)
            ? custom
            : "用 300 字左右介绍艾灸的起源与原理，分三段。";

        var results = new JsonArray();
        var ttfts = new List<double>();
        var decodes = new List<double>();
        var prefills = new List<double>();
        string? error = null;
        for (var run = 1; run <= runs && error is null; run++)
        {
            try
            {
                LlmCompletion? completion = null;
                await foreach (var evt in runtime.Providers.StreamAsync(
                    candidate => new LlmRequest
                    {
                        Provider = candidate.Provider,
                        Model = candidate.Model,
                        Messages = [ChatMessage.User(prompt)],
                        MaxTokens = maxTokens,
                        EnableThinking = false,
                        Purpose = LlmPurpose.Auxiliary,
                    },
                    workspace: null,
                    ct,
                    candidateFilter: candidate => candidate.Ref == model.Ref).ConfigureAwait(false))
                {
                    if (evt is LlmCompleted done) completion = done.Completion;
                }

                var performance = completion?.Performance;
                var row = new JsonObject
                {
                    ["run"] = run,
                    ["finishReason"] = completion?.FinishReason ?? "",
                    ["outputChars"] = completion?.Text.Length ?? 0,
                };
                if (performance is { } perf)
                {
                    row["ttftMs"] = Math.Round(perf.TtftMs, 1);
                    // 加载等待单列：首轮的 TTFT 含权重装载，本字段让「冷启动等待」与
                    // 「净 prefill 速率」可分辨（稳态命中为 0）。
                    row["loadWaitMs"] = Math.Round(perf.LoadWaitMs, 1);
                    row["totalMs"] = Math.Round(perf.TotalMs, 1);
                    row["decodeMs"] = Math.Round(perf.DecodeMs, 1);
                    row["decodeTokensPerSecond"] = Math.Round(perf.DecodeTokensPerSecond, 2);
                    row["prefillTokensPerSecond"] = Math.Round(perf.PrefillTokensPerSecond, 2);
                    row["promptTokens"] = perf.PromptTokens;
                    row["generatedTokens"] = perf.GeneratedTokens;
                    row["cachedTokens"] = perf.CachedTokens;
                    row["cacheHitRate"] = Math.Round(perf.CacheHitRate, 4);
                    row["prefixReuse"] = perf.PrefixReuse;
                    if (perf.Hint is { } hint) row["hint"] = hint;
                    if (run > 1)
                    {
                        // 首轮含模型加载等待（TTFT 覆盖加载，另见 loadWaitMs 字段），
                        // 聚合并剔除以反映稳态性能。
                        ttfts.Add(perf.TtftMs);
                        if (perf.DecodeTokensPerSecond > 0) decodes.Add(perf.DecodeTokensPerSecond);
                        if (perf.PrefillTokensPerSecond > 0) prefills.Add(perf.PrefillTokensPerSecond);
                    }
                }
                else
                {
                    row["note"] = "本轮未产生性能观测（远程模型或空的完成事件）";
                }
                results.Add((JsonNode)row);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex.Message;
                results.Add((JsonNode)new JsonObject { ["run"] = run, ["error"] = ex.Message });
            }
        }

        double Percentile(List<double> values, double fraction)
        {
            if (values.Count == 0) return 0;
            var sorted = values.OrderBy(v => v).ToList();
            return sorted[Math.Clamp((int)(fraction * sorted.Count), 0, sorted.Count - 1)];
        }

        var response = new JsonObject
        {
            ["model"] = model.Ref,
            ["runs"] = runs,
            ["maxTokens"] = maxTokens,
            ["results"] = results,
            ["aggregate"] = new JsonObject
            {
                // 首轮 TTFT 含模型加载，稳态聚合基于第 2 轮起的数据；runs=1 时为空。
                ["steadyRuns"] = ttfts.Count,
                ["ttftMsMean"] = ttfts.Count > 0 ? Math.Round(ttfts.Average(), 1) : 0,
                ["ttftMsP50"] = Math.Round(Percentile(ttfts, 0.5), 1),
                ["decodeTokensPerSecondMean"] = decodes.Count > 0 ? Math.Round(decodes.Average(), 2) : 0,
                ["decodeTokensPerSecondP50"] = Math.Round(Percentile(decodes, 0.5), 2),
                ["prefillTokensPerSecondMean"] = prefills.Count > 0 ? Math.Round(prefills.Average(), 2) : 0,
            },
            ["error"] = error,
        };
        if (sharedLocalModels?.Tuning is { } tuning)
        {
            response["tuning"] = new JsonObject
            {
                ["modelPath"] = tuning.ModelPath,
                ["tierLevel"] = tuning.Current?.Level,
                ["gpuLayers"] = tuning.Current?.GpuLayers,
                ["kvQuant"] = tuning.Current?.KvQuant,
                ["ubatch"] = tuning.Current?.UBatch,
                ["tierReason"] = tuning.Current?.Reason,
                ["lastAdjustment"] = tuning.LastAdjustment,
            };
        }
        return response.ToJsonString();
    }

    /// <summary>
    /// Pre-flight check for a model without loading it: local GGUF models report the
    /// resolved file path, existence and size, remote models report their endpoint.
    /// The desktop calls this before a full load so a missing file fails fast with a
    /// clear reason instead of a slow load error.
    /// </summary>
    public string ModelStatus(JsonObject parameters)
    {
        var reference = RequiredString(parameters, "model");
        var model = runtime.Models.Resolve(reference)
                    ?? throw new DaemonRequestException($"Model not found: {reference}");

        var status = new JsonObject
        {
            ["model"] = model.Ref,
            ["provider"] = model.Provider.Id,
            ["kind"] = model.Provider.IsLocal ? "local" : model.Provider.Kind,
        };
        if (model.Provider.IsLocal)
        {
            var path = Providers.LocalModels.ResolveModelPath(model.Provider, model.Model);
            var file = new FileInfo(path);
            status["path"] = path;
            if (Providers.LocalModels.IsProjectorFileName(Path.GetFileName(path)))
            {
                // 投影器文件永远无法作为聊天模型加载：预检直接按不可用报告，
                // 让前端「加载验证」给出可操作的原因而不是等真实加载报错。
                status["exists"] = false;
                status["sizeBytes"] = file.Exists ? file.Length : 0;
                status["reason"] = "该文件是多模态投影器（mmproj），仅用作视觉投影器，不能作为聊天模型加载。请在提供商编辑中移除该条目。";
                return status.ToJsonString();
            }
            status["exists"] = file.Exists;
            status["sizeBytes"] = file.Exists ? file.Length : 0;
            if (!file.Exists)
                status["reason"] = $"模型文件不存在：{path}。请检查模型目录配置或重新扫描注册。";
        }
        else
        {
            status["exists"] = true;
            status["baseUrl"] = model.Provider.BaseUrl;
        }
        return status.ToJsonString();
    }

    /// <summary>
    /// 本地模型预热（model.preload）：把指定模型的权重装进驻留槽（不占用推理槽、
    /// 不阻塞调用方之外的请求）。已驻留时零成本返回；失败抛错（消息含重试/降级/
    /// 版本诊断）。进度经调用方（daemon）以 model.load.progress 广播。
    /// </summary>
    public async Task<string> PreloadModelAsync(JsonObject parameters, IProgress<LocalLoadProgress> progress, CancellationToken ct)
    {
        var reference = RequiredString(parameters, "model");
        var model = runtime.Models.Resolve(reference)
                    ?? throw new DaemonRequestException($"Model not found: {reference}");
        if (!model.Provider.IsLocal)
            throw new DaemonRequestException($"model.preload 仅支持本地模型（{model.Ref} 是 {model.Provider.Kind}）");
        if (sharedLocalModels is null)
            throw new DaemonRequestException("本地模型库未初始化");

        var path = Providers.LocalModels.ResolveModelPath(model.Provider, model.Model);
        if (!File.Exists(path))
            throw new DaemonRequestException($"模型文件不存在：{path}");
        if (Providers.LocalModels.IsProjectorFileName(Path.GetFileName(path)))
            throw new DaemonRequestException($"{model.Model.Id} 是多模态投影器文件（mmproj），不能作为聊天模型加载。");

        var tier = Providers.LocalLlmClient.TierFor(
            sharedLocalModels.Tuning, model.Provider, model.Model, path, sharedLocalModels.Metadata);
        var mmprojPath = sharedLocalModels.Metadata.ResolveMmproj(
            Providers.LocalModels.ResolveDirectory(model.Provider.ModelsDirectory), model.Model.MmprojPath);
        var report = await sharedLocalModels.PreloadAsync(
            path,
            Providers.LocalLlmClient.BuildLoadParams(path, model.Provider, model.Model, tier),
            Providers.LocalLlmClient.LoadSignature(model.Provider, model.Model, tier) + "|" + (mmprojPath ?? "nommproj"),
            mmprojPath,
            progress,
            ct).ConfigureAwait(false);

        return new JsonObject
        {
            ["model"] = model.Ref,
            ["path"] = path,
            ["resident"] = sharedLocalModels.IsLoaded,
            ["loadReport"] = report is null ? null : new JsonObject
            {
                ["attempts"] = report.Attempts,
                ["degradedToCpu"] = report.DegradedToCpu,
                ["verified"] = report.Verified,
                ["loadDurationMs"] = report.LoadDurationMs,
                ["effectiveGpuLayers"] = report.EffectiveGpuLayers,
                ["note"] = report.Note,
            },
        }.ToJsonString();
    }

    /// <summary>
    /// 主动卸载全部常驻本地权重（model.unload）：归还内存/显存。有活跃推理的槽
    /// 标记待销毁，由最后一个推理结束后释放。返回卸载前常驻的模型数。
    /// </summary>
    public async Task<string> UnloadLocalModelsAsync(CancellationToken ct)
    {
        if (sharedLocalModels is null)
            throw new DaemonRequestException("本地模型库未初始化");
        var resident = sharedLocalModels.ResidentCount;
        await sharedLocalModels.UnloadAsync(ct).ConfigureAwait(false);
        return new JsonObject
        {
            ["unloaded"] = resident,
            ["resident"] = sharedLocalModels.ResidentCount,
        }.ToJsonString();
    }

    public async Task<string> FetchProviderModelsAsync(JsonObject parameters, CancellationToken ct)
    {
        var id = RequiredString(parameters, "id");
        var provider = runtime.ConfigStore.Config.FindProvider(id)
                       ?? throw new DaemonRequestException($"Provider not found: {id}");

        // Local providers have no /models endpoint: the models directory is scanned and the
        // entries are registered with the sizes taken from each GGUF header.
        if (provider.IsLocal)
            return RegisterScannedModels(provider);

        var url = OptionalString(parameters, "url") ?? provider.ModelListUrl;
        var ids = await runtime.Providers.FetchModelsAsync(provider, url, ct).ConfigureAwait(false);
        var existing = provider.Models.ToDictionary(model => model.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var modelId in ids)
        {
            if (existing.ContainsKey(modelId)) continue;
            var model = new ModelConfig { Id = modelId };
            provider.Models.Add(model);
            existing[modelId] = model;
        }
        runtime.ConfigStore.Save();
        return Strings(ids).ToJsonString();
    }

    /// <summary>
    /// Registers every GGUF file of a local provider that is not registered yet, using the
    /// sizes read from each file header, and returns the model ids like the HTTP path does.
    /// </summary>
    private string RegisterScannedModels(ProviderConfig provider)
    {
        var existing = provider.Models.ToDictionary(model => model.Id, StringComparer.OrdinalIgnoreCase);
        var ids = new List<string>();
        foreach (var scanned in LocalModelProbe.Scan(provider.ModelsDirectory))
        {
            ids.Add(scanned.ModelId);
            if (existing.ContainsKey(scanned.ModelId)) continue;
            provider.Models.Add(LocalModelProbe.ToConfig(scanned));
        }
        runtime.ConfigStore.Save();
        return Strings(ids).ToJsonString();
    }

    public async Task<string> UpdateModel(JsonObject parameters)
    {
        var providerId = RequiredString(parameters, "provider");
        var modelId = RequiredString(parameters, "id");
        var provider = runtime.ConfigStore.Config.FindProvider(providerId)
                       ?? throw new DaemonRequestException($"Provider not found: {providerId}");
        var model = provider.Models.FirstOrDefault(item =>
            item.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new DaemonRequestException($"Model not found: {providerId}/{modelId}");
        var originalLoadJson = model.Load?.ToJson().ToJsonString();

        if (parameters.ContainsKey("alias"))
            model.Alias = parameters["alias"]?.GetValue<string>()?.Trim() is { Length: > 0 } alias ? alias : null;
        if (parameters["contextWindow"]?.GetValue<int?>() is { } contextWindow)
        {
            if (contextWindow <= 0)
                throw new DaemonRequestException("params.contextWindow must be positive");
            model.ContextWindow = contextWindow;
        }
        if (parameters["maxOutput"]?.GetValue<int?>() is { } maxOutput)
        {
            if (maxOutput <= 0)
                throw new DaemonRequestException("params.maxOutput must be positive");
            model.MaxOutput = maxOutput;
        }
        if (parameters["vision"]?.GetValue<bool?>() is { } vision)
            model.Capabilities.Vision = vision;
        if (parameters["mmprojPath"] is JsonValue mmprojValue)
        {
            if (mmprojValue.TryGetValue(out string? mmprojPath) && !string.IsNullOrWhiteSpace(mmprojPath))
                model.MmprojPath = mmprojPath.Trim();
            else
                model.MmprojPath = null;
        }
        if (parameters["fallbackModelId"] is JsonValue fallbackValue)
        {
            // 备用模型：空串清除；自引用拒绝（链不形成环）；存在性在解析时宽容处理
            // （id 写错时回退链长为 1，请求路径照常报错，不阻断保存）。
            if (fallbackValue.TryGetValue(out string? fallbackId) && !string.IsNullOrWhiteSpace(fallbackId))
            {
                var trimmed = fallbackId.Trim();
                if (trimmed.Equals(modelId, StringComparison.OrdinalIgnoreCase))
                    throw new DaemonRequestException("params.fallbackModelId 不能指向模型自身");
                model.FallbackModelId = trimmed;
            }
            else
            {
                model.FallbackModelId = null;
            }
        }
        if (parameters["load"] is JsonObject loadNode)
            model.Load = LocalModelSettings.FromJson(loadNode);
        else if (parameters.ContainsKey("load"))
            model.Load = null; // 显式传 null：清除加载配置，回到「自动」

        // 加载设置变化必须立刻让常驻权重失效，下一次请求会按新配置重新加载。
        var changedLoad = !string.Equals(originalLoadJson, model.Load?.ToJson().ToJsonString(), StringComparison.Ordinal);
        if (changedLoad && sharedLocalModels is { IsLoaded: true })
            await sharedLocalModels.UnloadAsync().ConfigureAwait(false);
        runtime.ConfigStore.Save();
        return new JsonObject
        {
            ["provider"] = provider.Id,
            ["id"] = model.Id,
            ["alias"] = model.Alias,
            ["contextWindow"] = model.ContextWindow,
            ["maxOutput"] = model.MaxOutput,
            ["vision"] = model.Capabilities.Vision,
            ["mmprojPath"] = model.MmprojPath,
            ["load"] = model.Load?.ToJson(),
        }.ToJsonString();
    }

    /// <summary>
    /// 模型配置页的全量读取：模型元数据 + 加载设置 + 多模态投影器解析结果 + 当前常驻
    /// 状态与最近一次加载报告（重试 / 降级 / 自检信息）。
    /// </summary>
    public string GetModelConfig(JsonObject parameters)
    {
        var providerId = RequiredString(parameters, "provider");
        var modelId = RequiredString(parameters, "id");
        var provider = runtime.ConfigStore.Config.FindProvider(providerId)
                       ?? throw new DaemonRequestException($"Provider not found: {providerId}");
        var model = provider.Models.FirstOrDefault(item =>
            item.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new DaemonRequestException($"Model not found: {providerId}/{modelId}");

        var result = new JsonObject
        {
            ["provider"] = provider.Id,
            ["providerName"] = provider.DisplayName,
            ["providerKind"] = provider.Kind,
            ["id"] = model.Id,
            ["alias"] = model.Alias,
            ["contextWindow"] = model.ContextWindow,
            ["maxOutput"] = model.MaxOutput,
            ["vision"] = model.Capabilities.Vision,
            ["localPath"] = model.LocalPath,
            ["mmprojPath"] = model.MmprojPath,
            ["load"] = model.Load?.ToJson(),
            ["providerDefaults"] = new JsonObject
            {
                ["gpuLayers"] = provider.GpuLayers,
                ["threads"] = provider.Threads,
                ["flashAttention"] = provider.FlashAttention,
                ["kvCacheQuantization"] = provider.KvCacheQuantization,
                ["modelsDirectory"] = LocalModels.ResolveDirectory(provider.ModelsDirectory),
            },
        };

        // 本地模型：文件状态 + 自动探测的 mmproj + 常驻状态。
        if (provider.IsLocal)
        {
            var path = LocalModels.ResolveModelPath(provider, model);
            var file = new FileInfo(path);
            result["resolvedPath"] = path;
            result["fileExists"] = file.Exists;
            result["fileSizeBytes"] = file.Exists ? file.Length : 0;
            result["autoMmprojPath"] = LocalModels.ResolveMmprojPath(provider, model);
            result["isMultimodal"] = LocalModels.ResolveMmprojPath(provider, model) is not null
                                     || model.Capabilities.Vision;
            result["fallbackModelId"] = model.FallbackModelId;
            if (sharedLocalModels is { } local)
            {
                result["residentInMemory"] = local.IsLoaded;
                result["residentCount"] = local.ResidentCount;
                result["maxResidentModels"] = local.MaxResidentModels;
                result["concurrency"] = local.MaxConcurrency;
                result["supportsPrefixReuse"] = local.SupportsPrefixReuse;
                result["idleUnloadMinutes"] = local.IdleUnloadMinutes;
                result["estimatedLoadMs"] = local.Fingerprints.EstimatedLoadMs(path);
                var slots = new JsonArray();
                foreach (var residency in local.Snapshot())
                {
                    slots.Add((JsonNode)new JsonObject
                    {
                        ["path"] = residency.Path,
                        ["loadSignature"] = residency.LoadSignature,
                        ["weightBytes"] = residency.WeightBytes,
                        ["activeLeases"] = residency.RefCount,
                        ["lastUsedUtc"] = residency.LastUsedUtc.ToString("O"),
                        ["degradedToCpu"] = residency.Report?.DegradedToCpu,
                        ["verified"] = residency.Report?.Verified,
                    });
                }
                result["residentSlots"] = slots;
                var report = local.LastLoadReport;
                if (report is not null)
                {
                    result["loadReport"] = new JsonObject
                    {
                        ["attempts"] = report.Attempts,
                        ["degradedToCpu"] = report.DegradedToCpu,
                        ["verified"] = report.Verified,
                        ["loadDurationMs"] = report.LoadDurationMs,
                        ["effectiveGpuLayers"] = report.EffectiveGpuLayers,
                        ["note"] = report.Note,
                    };
                }
                if (local.Fingerprints.LastFailure(path) is { } failure)
                {
                    result["lastFailure"] = new JsonObject
                    {
                        ["at"] = failure.At.ToString("O"),
                        ["error"] = failure.Error,
                        ["consecutiveFailures"] = local.Fingerprints.ConsecutiveFailures(path),
                    };
                }
            }
        }
        return result.ToJsonString();
    }

}
