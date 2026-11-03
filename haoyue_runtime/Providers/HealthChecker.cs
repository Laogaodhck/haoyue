using System.Diagnostics;
using Haoyue.Runtime.Configuration;
using LLama.Native;

namespace Haoyue.Runtime.Providers;

public sealed record HealthReport(string ProviderId, bool Online, double LatencyMs, string Detail);
public sealed record HealthCheckResult(string Name, bool Ok, string Detail);

public interface IHealthChecker
{
    Task<HealthReport> CheckAsync(ProviderConfig provider, CancellationToken ct = default);
    IReadOnlyList<HealthCheckResult> RunChecks(Workspaces.WorkspaceInfo workspace);
}

/// <summary>Probes a remote provider through the real client request pipeline to measure
/// availability and latency; local providers check model files on disk.</summary>
public sealed class HealthChecker(ILlmClientFactory clientFactory, Configuration.IConfigStore configStore) : IHealthChecker
{
    public IReadOnlyList<HealthCheckResult> RunChecks(Workspaces.WorkspaceInfo workspace)
    {
        var results = new List<HealthCheckResult>();

        // 0. Configuration File Integrity Check
        var configOk = !configStore.HasAnomaly;
        results.Add(new HealthCheckResult("Config File", configOk,
            configOk ? "Configuration valid" : $"配置文件异常: {configStore.AnomalyDetail}"));

        // 1. Workspace Root Check
        var rootOk = Directory.Exists(workspace.Root);
        results.Add(new HealthCheckResult("Workspace Root", rootOk, rootOk ? workspace.Root : $"Directory not found: {workspace.Root}"));

        // 2. Haoyue Directory Check
        var haoyueOk = Directory.Exists(workspace.HaoyueDir);
        results.Add(new HealthCheckResult("Haoyue Metadata Dir", haoyueOk, haoyueOk ? workspace.HaoyueDir : "Not initialized (run haoyue init)"));

        // 3. Provider Configuration Check
        var providers = configStore.Config.Providers;
        var hasProvider = providers.Count > 0;
        results.Add(new HealthCheckResult("Provider Config", hasProvider, hasProvider ? $"{providers.Count} provider(s) configured. Active provider: {configStore.Config.Provider ?? "-"}, model: {configStore.Config.Model ?? "-"}" : "No providers configured"));

        // 4. Local GPU Backend Check — CPU 构建的 llama 后端会在用户毫不知情的情况下把
        // 本地推理全程拖到 CPU（同机实测 11 倍差距）。体检显式报出并附重建指引，桌面
        // 设置页的诊断区直接渲染本项；与 LocalLlmClient 运行时警告同一判断源。
        var localProviders = configStore.Config.Providers
            .Where(provider => ProviderKinds.IsLocal(provider.Kind))
            .ToList();
        if (localProviders.Count == 0)
        {
            results.Add(new HealthCheckResult("Local GPU Backend", true, "No local provider configured"));
        }
        else if (NativeApi.llama_supports_gpu_offload())
        {
            results.Add(new HealthCheckResult("Local GPU Backend", true, "GPU offload supported by the loaded llama backend"));
        }
        else
        {
            var explicitRequests = localProviders
                .SelectMany(provider => provider.Models.Select(model => (provider, model)))
                .Select(pair => LocalLlmClient.ConfiguredGpuLayers(pair.provider, pair.model))
                .Where(layers => layers > 0)
                .ToList();
            // 自动语义（AutoOptimize）下档位按 nvidia-smi 探测请求 offload：机器有
            // NVIDIA GPU 而后端是 CPU 构建时，同样会被静默拖慢，必须报出来。
            var autoOnGpuMachine = GpuProbe.Probe() is { } gpu
                && localProviders.Any(provider => provider.Models.Any(
                    model => model.Load is null || model.Load.AutoOptimize));
            if (explicitRequests.Count > 0)
            {
                results.Add(new HealthCheckResult("Local GPU Backend", false,
                    LocalLlmClient.GpuBackendDiagnostic(explicitRequests.Max())!));
            }
            else if (autoOnGpuMachine)
            {
                results.Add(new HealthCheckResult("Local GPU Backend", false,
                    $"The loaded llama native backend does not support GPU offload (CPU build of"
                    + $" LLamaSharp.Backend), but auto-optimized local models would request it on this machine."
                    + $" Inference runs fully on CPU and large prompts prefill extremely slowly."
                    + $" {LocalLlmClient.GpuBackendFixGuidance}"));
            }
            else
            {
                results.Add(new HealthCheckResult("Local GPU Backend", true, "CPU inference requested (no GPU offload configured)"));
            }
        }

        // 5. Memory File Check — MEMORY.md 是可选文件，Agent 首次写入时才生成，
        // LoadMemory 对缺失已有优雅处理；只有目录都建不出来（权限/只读）才算异常。
        var memoryFile = workspace.MemoryFile;
        if (File.Exists(memoryFile))
        {
            results.Add(new HealthCheckResult("Workspace Memory", true, memoryFile));
        }
        else
        {
            try
            {
                Directory.CreateDirectory(workspace.MemoryDir);
                results.Add(new HealthCheckResult("Workspace Memory", true, $"Ready (no memories saved yet): {memoryFile}"));
            }
            catch (Exception ex)
            {
                results.Add(new HealthCheckResult("Workspace Memory", false, $"Cannot create memory directory {workspace.MemoryDir}: {ex.Message}"));
            }
        }

        return results;
    }

    public async Task<HealthReport> CheckAsync(ProviderConfig provider, CancellationToken ct = default)
    {
        if (ProviderKinds.IsLocal(provider.Kind))
            return CheckLocal(provider);

        // 与 provider test（ProviderManager.TestModelAsync）完全同一条请求管线：
        // 经 ILlmClientFactory 取客户端、构造 LlmRequest、走 client.StreamAsync。
        // 禁止在此手写 HTTP payload —— 各端点的真实请求语义（方舟 reasoning 模型
        // 的 max_completion_tokens、Anthropic 的 messages 端点等）只在客户端内正确，
        // 手写 payload 曾导致对 ark 的 403 误报（doctor 显示 offline 而真实链路正常）。
        //
        // 探测目标优先取「活动模型」而非 provider 的第一个模型：tokenhub 实测
        // 首模型 deepseek/deepseek-flash 免费额度耗尽返回 402，而活动模型
        // deepseek-v4-flash-202605 真实调用正常 —— 用首模型探测会把可用提供商
        // 误报为 offline（与 ark 403 误报同源：探测目标与真实使用不一致）。
        var model = ResolveProbeModel(provider);

        var request = new LlmRequest
        {
            Provider = provider,
            Model = model,
            Messages = [ChatMessage.User("Reply with the single word: ok")],
            MaxTokens = 16,
            Purpose = LlmPurpose.Auxiliary,
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Min(provider.TimeoutSeconds, 15)));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var client = clientFactory.GetClient(ProviderKinds.ToClientKind(provider.Kind));
            var text = "";
            await foreach (var evt in client.StreamAsync(request, cts.Token).ConfigureAwait(false))
                if (evt is LlmCompleted done)
                    text = done.Completion.Text;
            stopwatch.Stop();

            return new HealthReport(provider.Id, true, stopwatch.Elapsed.TotalMilliseconds,
                string.IsNullOrWhiteSpace(text) ? "ok (empty reply)" : "ok");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HealthReport(provider.Id, false, stopwatch.Elapsed.TotalMilliseconds, "timeout");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is LlmException or HttpRequestException)
        {
            stopwatch.Stop();
            return new HealthReport(provider.Id, false, stopwatch.Elapsed.TotalMilliseconds, ex.Message);
        }
    }

    /// <summary>
    /// Local models have no endpoint to probe: availability is whether the GGUF files the
    /// registered models point at exist on disk.
    /// </summary>
    private static HealthReport CheckLocal(ProviderConfig provider)
    {
        var present = provider.Models
            .Select(model => LocalModels.ResolveModelPath(provider, model))
            .Where(File.Exists)
            .ToList();

        if (present.Count == 0)
        {
            var directory = LocalModels.ResolveDirectory(provider.ModelsDirectory);
            return new HealthReport(provider.Id, false, 0, provider.Models.Count == 0
                ? $"No models registered. Put *.gguf files into {directory} and add them."
                : $"No model file found. Checked {directory} and the paths saved on the models.");
        }

        var first = new FileInfo(present[0]);
        var gigabytes = first.Length >= 1L << 30;
        var size = $"{first.Length / (double)(gigabytes ? 1L << 30 : 1L << 20):F1} {(gigabytes ? "GB" : "MB")}";
        return new HealthReport(provider.Id, true, 0,
            $"{present.Count}/{provider.Models.Count} model file(s) ready, e.g. {first.Name} ({size})");
    }

    /// <summary>选择探测模型：优先活动模型（配置 provider/model 对应当前真实使用），
    /// 否则取该 provider 第一个非空模型，最后兜底通用 ID。避免用「第一个模型」探测时
    /// 选中额度耗尽 / 已下线的模型而把可用提供商误报为 offline。</summary>
    private ModelConfig ResolveProbeModel(ProviderConfig provider)
    {
        var activeProvider = configStore.Config.Provider;
        var activeModel = configStore.Config.Model;
        if (!string.IsNullOrWhiteSpace(activeProvider)
            && provider.Id.Equals(activeProvider, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(activeModel))
        {
            var byId = provider.Models.FirstOrDefault(m =>
                m.Id.Equals(activeModel, StringComparison.OrdinalIgnoreCase)
                || m.Id.EndsWith($"/{activeModel}", StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
                return byId;
        }

        return provider.Models.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Id))
            ?? new ModelConfig
            {
                Id = ProviderKinds.IsAnthropic(provider.Kind)
                    ? "claude-3-haiku-20240307"
                    : "gpt-4o-mini",
            };
    }
}
