using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Evolution;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Experts;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Scheduling;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Daemon;

/// <summary>DaemonServer 的按域分部类（成员仅搬运自 DaemonServer.cs，逻辑未改动）。</summary>
public sealed partial class DaemonServer
{
    private void OnMcpStatusChanged() => _ = BroadcastMcpStatusAsync();

    /// <summary>
    /// Rearms the unattended auto-reflection poll from the current evolution config.
    /// Called at construction and again whenever SetEvolutionConfig persists changes.
    /// </summary>
    private void ScheduleAutoReflect()
    {
        _autoReflectTimer?.Dispose();
        _autoReflectTimer = null;
        var config = _runtime.ConfigStore.Config.Evolution;
        if (!config.AutoReflect && !config.ThresholdEnabled) return;
        _lastReflectAt = DateTimeOffset.UtcNow;
        _autoReflectTimer = new Timer(
            _ => AutoReflectTick(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Polls whether an unattended reflection is due: either the configured interval
    /// elapsed, or enough pending defect signals accumulated (threshold mode, with a
    /// 30-minute throttle). Whichever condition comes first wins.
    /// </summary>
    private void AutoReflectTick()
    {
        if (_shutdown.IsCancellationRequested) return;
        var config = _runtime.ConfigStore.Config.Evolution;
        var now = DateTimeOffset.UtcNow;
        var dueByInterval = config.AutoReflect &&
            now - _lastReflectAt >= TimeSpan.FromMinutes(Math.Clamp(config.IntervalMinutes, 30, 10080));
        var dueByThreshold = false;
        if (!dueByInterval && config.ThresholdEnabled &&
            now - _lastReflectAt >= TimeSpan.FromMinutes(30))
        {
            try
            {
                var pending = new DefectAggregator(_runtime.Database)
                    .Aggregate(DefectAggregator.DefaultScanLimit, _evolution.ReflectionSessionIds())
                    .Count(r => _evolution.Get(r.Fingerprint) is not { } existing ||
                                !EvolutionStatus.BlocksRetry(existing.Status));
                dueByThreshold = pending >= Math.Clamp(config.ThresholdSignals, 1, 50);
            }
            catch (Exception)
            {
                // A poll tick must never kill the daemon process; the next tick retries.
                return;
            }
        }
        if (!dueByInterval && !dueByThreshold) return;
        _lastReflectAt = now;
        StartAutoReflectTurn(dueByInterval ? "auto" : "threshold");
    }

    /// <summary>
    /// L-D 会话保留策略维护的单航班触发：归档超过保留期未更新的会话、清理无引用
    /// 孤儿图片缓存、距上次超过一天时 VACUUM。读当前配置的保留天数，后台执行，
    /// 任何失败都吞掉并留待下一周期——维护绝不能影响 daemon 主循环。
    /// </summary>
    private void StartSessionMaintenance()
    {
        if (_shutdown.IsCancellationRequested) return;
        if (!_maintenanceGate.Wait(0)) return;
        _ = Task.Run(() =>
        {
            try
            {
                var retentionDays = _runtime.ConfigStore.Config.Agent.SessionRetentionDays;
                _runtime.Sessions.RunMaintenance(retentionDays);
            }
            catch (Exception)
            {
                // Best-effort housekeeping; the next tick retries.
            }
            finally { _maintenanceGate.Release(); }
        }, _shutdown.Token);
    }

    /// <summary>
    /// 本地模型库策略装配：把 Agent 配置（驻留上限 / 并发槽 / 空闲回收阈值）应用到
    /// 进程级共享的权重库。启动与配置热更新路径都可调用；并发数变更若有活跃推理
    /// 则延后生效（库内部处理）。
    /// </summary>
    /// <summary>上次已告警的并发值（0 = 未告警过）。同一值只告警一次，避免配置抖动刷屏。</summary>
    private int _lastConcurrencyWarning;

    internal void ConfigureLocalModelCache()
    {
        var agent = _runtime.ConfigStore.Config.Agent;
        _sharedLocalModels.Configure(
            agent.LocalModelMaxResident, agent.LocalModelConcurrency, agent.LocalModelIdleUnloadMinutes);
        // 并发 > 1 关闭 KV 前缀复用：这条静默退化让多轮大 prompt 每轮从头 prefill。
        // 与 GPU 后端自检同一待遇——广播 WarningEvent 进事件台账，文件日志同步落盘。
        var warning = Providers.LocalModelCache.ConcurrencyWarning(agent.LocalModelConcurrency);
        if (warning is null)
        {
            _lastConcurrencyWarning = 0;
        }
        else if (_lastConcurrencyWarning != agent.LocalModelConcurrency)
        {
            _lastConcurrencyWarning = agent.LocalModelConcurrency;
            _runtime.Events.Publish(new WarningEvent(warning));
        }
    }

    /// <summary>
    /// 本地模型预热（Agent.LocalModelPreload 开启时）：后台把活跃本地模型的权重
    /// 装入驻留槽——不阻塞启动、不占推理槽、失败静默（下一次请求照常懒加载）。
    /// 进度以 model.load.progress 广播（此时通常还没有客户端，事件进 journal，
    /// 重连的桌面端可经 events.recent 回放）。
    /// </summary>
    private void StartLocalModelPreload()
    {
        if (_shutdown.IsCancellationRequested) return;
        if (!_runtime.ConfigStore.Config.Agent.LocalModelPreload) return;
        _ = Task.Run(() =>
        {
            try
            {
                var active = _runtime.Providers.ResolveActive(null);
                if (!active.Provider.IsLocal) return;
                var path = Providers.LocalModels.ResolveModelPath(active.Provider, active.Model);
                if (!File.Exists(path)) return;
                var tier = Providers.LocalLlmClient.TierFor(
                    _sharedLocalModels.Tuning, active.Provider, active.Model, path, _sharedLocalModels.Metadata);
                var mmproj = _sharedLocalModels.Metadata.ResolveMmproj(
                    Providers.LocalModels.ResolveDirectory(active.Provider.ModelsDirectory), active.Model.MmprojPath);
                _ = _sharedLocalModels.PreloadAsync(
                    path,
                    Providers.LocalLlmClient.BuildLoadParams(path, active.Provider, active.Model, tier),
                    Providers.LocalLlmClient.LoadSignature(active.Provider, active.Model, tier) + "|" + (mmproj ?? "nommproj"),
                    mmproj,
                    LocalLoadProgress(),
                    _shutdown.Token);
            }
            catch (Exception)
            {
                // 预热是纯优化：任何失败都退化为下一次请求的懒加载。
            }
        }, _shutdown.Token);
    }

    /// <summary>
    /// GPU 后端启动自检：配置（或自动档位）请求 GPU offload 而原生后端不支持时广播一条
    /// WarningEvent——事件台账持久化、桌面可经 events.recent 回放、doctor.run 的
    /// 「Local GPU Backend」体检项与运行时 stderr 警告共用同一判断源
    /// （LocalLlmClient.GpuBackendDiagnostic）。纯优化：任何失败都不影响启动。
    /// </summary>
    private void PublishGpuBackendWarning()
    {
        try
        {
            var localProviders = _runtime.ConfigStore.Config.Providers
                .Where(provider => provider.Kind.Equals("local", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (localProviders.Count == 0) return;

            var explicitRequests = localProviders
                .SelectMany(provider => provider.Models
                    .Select(model => Providers.LocalLlmClient.ConfiguredGpuLayers(provider, model)))
                .Where(layers => layers > 0)
                .ToList();
            // 自动语义（AutoOptimize）下档位按 nvidia-smi 探测请求 offload：机器有
            // NVIDIA GPU 而后端是 CPU 构建时，同样被静默拖慢，必须报出来。
            var autoOnGpuMachine = Providers.GpuProbe.Probe() is not null
                && localProviders.Any(provider => provider.Models.Any(
                    model => model.Load is null || model.Load.AutoOptimize));

            string? detail = explicitRequests.Count > 0
                ? Providers.LocalLlmClient.GpuBackendDiagnostic(explicitRequests.Max())
                : autoOnGpuMachine
                    ? $"The loaded llama native backend does not support GPU offload (CPU build of"
                        + $" LLamaSharp.Backend), but auto-optimized local models would request it on this"
                        + $" machine. Inference runs fully on CPU and large prompts prefill extremely slowly."
                        + $" {Providers.LocalLlmClient.GpuBackendFixGuidance}"
                    : null;
            if (detail is null) return;
            _runtime.Events.Publish(new WarningEvent(detail));
        }
        catch (Exception)
        {
            // 自检是纯优化：失败时 doctor 体检仍会报出同一问题，不影响启动。
        }
    }

    /// <summary>本地模型空闲回收：归还超过阈值的常驻权重（阈值为 0 时库里直接跳过）。</summary>
    private void StartLocalModelIdleReclaim()
    {
        if (_shutdown.IsCancellationRequested) return;
        var minutes = _runtime.ConfigStore.Config.Agent.LocalModelIdleUnloadMinutes;
        if (minutes <= 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _sharedLocalModels.UnloadIdleAsync(TimeSpan.FromMinutes(minutes), _shutdown.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort; the next tick retries.
            }
        }, _shutdown.Token);
    }

    /// <summary>
    /// 把库内的加载进度桥接为运行时事件（journal + model.load.progress 广播）。
    /// ModelRef 用文件名——本地模型 id 即 GGUF 文件名。
    /// </summary>
    private IProgress<Providers.LocalLoadProgress> LocalLoadProgress() =>
        new Progress<Providers.LocalLoadProgress>(PublishModelLoadProgress);

    private void PublishModelLoadProgress(Providers.LocalLoadProgress progress)
    {
        try
        {
            _runtime.Events.Publish(new ModelLoadProgressEvent(
                Path.GetFileName(progress.ModelPath),
                progress.Phase.ToString(),
                progress.Percent,
                progress.ElapsedMs,
                progress.EstimatedRemainingMs,
                progress.Message));
        }
        catch (Exception)
        {
            // 进度事件绝不影响加载。
        }
    }

    /// <summary>
    /// H-C 记忆固化的后台触发：单航班与墙钟预算都在 ConsolidationRunner 内部，
    /// 这里只负责 fire-and-forget 与彻底容错（固化是背景杂务，失败留待下一周期）。
    /// 固化本身要跑一个隔离回合，用的是活跃模型——本地 GGUF 与用户回合并发加载会
    /// 驻留两份权重，显存放不下时原生层直接 abort 掉整个 daemon（用户侧表现为
    /// 「发消息没有回复」）。因此有用户回合在飞时先让路，两分钟后重试。
    /// </summary>
    private void StartConsolidation()
    {
        if (_shutdown.IsCancellationRequested) return;
        if (!_runtime.ConfigStore.Config.Agent.MemoryConsolidationEnabled) return;
        if (_sessionTurns.Count > 0)
        {
            // 定时器常驻但不重复触发：每次让路都重新设 2 分钟，多次请求合并成一次重试。
            _consolidationDeferralTimer ??= new Timer(
                _ => StartConsolidation(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _consolidationDeferralTimer.Change(ConsolidationDeferral, Timeout.InfiniteTimeSpan);
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await _consolidation.RunAsync("auto", _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (Exception)
            {
                // Best-effort housekeeping; the next tick retries.
            }
        }, _shutdown.Token);
    }

    /// <summary>
    /// Fires one reflection turn if none is running. Conflicts (a manual or scheduled
    /// reflection still in flight) are silently skipped — the next tick retries, and
    /// outcomes reach clients through the evolution.reflected broadcast either way.
    /// </summary>
    private void StartAutoReflectTurn(string trigger)
    {
        if (_shutdown.IsCancellationRequested) return;
        if (!_reflectionGate.Wait(0)) return;
        _ = Task.Run(async () =>
        {
            try { await _reflection.RunAsync(trigger, _shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            finally { _reflectionGate.Release(); }
        }, _shutdown.Token);
    }
}
