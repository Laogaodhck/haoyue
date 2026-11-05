using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Haoyue.Runtime.Configuration;

namespace Haoyue.Runtime.Daemon;

/// <summary>单条钩子的最近执行状态（进程内；配置本身持久化在 HooksConfig）。</summary>
public sealed record HookRunStatus(
    string Name,
    long RunCount,
    bool LastOk,
    DateTimeOffset? LastTriggeredAt,
    string? LastError,
    double? LastDurationMs);

/// <summary>一次触发的执行结果（hooks.test 与诊断展示用）。</summary>
public sealed record HookRunOutcome(
    bool Ok,
    string? Error,
    double DurationMs,
    string? Output = null);

/// <summary>一次同步评估的结果：是否阻断 + 阻断原因 + 各钩子输出。</summary>
public sealed record HookDecision(
    bool Blocked,
    string? Reason,
    IReadOnlyList<HookRunOutcome> Outcomes);

/// <summary>
/// 事件钩子执行器：按触发事件自动执行 Webhook POST 或本地命令，并记录最近一次
/// 执行状态与 JSONL 台账。支持 5 类触发：
/// session.start / tool.before / tool.after / task.completed / schedule.completed。
///
/// 安全边界：command 动作以当前进程身份启动子进程，可读写本机文件并可联网——
/// 这是用户显式配置的自动化行为，执行器只做参数化启动（不拼 shell、不隐式展开）。
/// Webhook 动作只向用户配置的 URL POST JSON；Bearer 密钥只发不读回。
///
/// 进程内静态状态（运行时单进程）；测试可用 ResetForTests() 清空。
/// </summary>
public static class HookRunner
{
    private static readonly ConcurrentDictionary<string, HookRunStatus> Status =
        new(StringComparer.Ordinal);

    private static readonly HttpClient Http = CreateHttp();

    private const int CommandOutputLimit = 2_000;

    private static readonly string[] Triggers =
    {
        "session.start",
        "tool.before",
        "tool.after",
        "task.completed",
        "schedule.completed",
    };

    private static readonly string[] Actions = { "webhook", "command" };
    private static readonly string[] FailurePolicies = { "warn", "block" };

    /// <summary>支持「等待完成」语义的触发事件（tool.before / session.start 必须同步等待）。</summary>
    private static readonly string[] WaitableTriggers = { "tool.after", "task.completed", "schedule.completed" };

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("haoyue-hooks/1.0");
        return client;
    }

    /// <summary>按触发事件跑全部启用的钩子（fire-and-forget：绝不拖慢或失败主流程）。</summary>
    public static void Trigger(string trigger, JsonObject? payload, IConfigStore configStore)
    {
        try
        {
            var hooks = configStore.Config.Hooks.Hooks;
            if (hooks.Count == 0) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await EvaluateAsync(trigger, payload, configStore, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 钩子失败不影响主流程。
                }
            });
        }
        catch (Exception)
        {
            // Hooks are best-effort by design; a misconfigured store must never
            // affect the agent or scheduler paths that triggered them.
        }
    }

    /// <summary>
    /// 可等待触发：存在 WaitForCompletion 钩子时等待评估（整体超时受各钩子超时约束），
    /// 否则退回 fire-and-forget。任何失败/超时都不向调用方抛出，绝不阻断主流程。
    /// </summary>
    public static async Task TriggerAsync(
        string trigger, JsonObject? payload, IConfigStore configStore, CancellationToken ct = default)
    {
        try
        {
            var hooks = configStore.Config.Hooks.Hooks;
            if (hooks.Count == 0) return;
            var target = TargetOf(payload);
            var waiting = hooks
                .Where(hook => hook.Enabled
                               && hook.WaitForCompletion
                               && WaitableTriggers.Contains(hook.Trigger)
                               && string.Equals(hook.Trigger, trigger, StringComparison.Ordinal)
                               && MatchesTarget(hook.Match, target))
                .ToList();
            if (waiting.Count == 0)
            {
                Trigger(trigger, payload, configStore);
                return;
            }

            var seconds = Math.Min(120, waiting.Sum(hook => Math.Clamp(hook.TimeoutSeconds, 1, 120)));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, seconds)));
            await EvaluateAsync(trigger, payload, configStore, cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 等待模式仍以「不阻断、不拖垮主流程」为底线。
        }
    }

    /// <summary>
    /// 同步评估一次触发：按启用状态、事件、Match 通配符筛选后逐条执行；
    /// 失败且 FailurePolicy=block（仅 tool.before）时返回 Blocked。
    /// 永不向调用方抛异常（取消除外——取消除表示主流程已结束）。
    /// </summary>
    public static async Task<HookDecision> EvaluateAsync(
        string trigger, JsonObject? payload, IConfigStore configStore, CancellationToken ct)
    {
        var outcomes = new List<HookRunOutcome>();
        var root = HookLedger.ResolveRoot(payload);
        var target = TargetOf(payload);
        try
        {
            var hooks = configStore.Config.Hooks.Hooks;
            var matched = hooks
                .Where(hook => hook.Enabled
                               && string.Equals(hook.Trigger, trigger, StringComparison.Ordinal)
                               && MatchesTarget(hook.Match, target))
                .OrderBy(hook => hook.Name, StringComparer.Ordinal)
                .ToList();
            foreach (var hook in matched)
            {
                var outcome = await ExecuteWithTimeoutAsync(hook, payload, ct).ConfigureAwait(false);
                Record(hook.Name, outcome);
                outcomes.Add(outcome);
                HookLedger.Append(root, new HookRunRecord(
                    DateTimeOffset.UtcNow, trigger, hook.Name, target,
                    outcome.Ok, outcome.DurationMs, outcome.Error, outcome.Output),
                    configStore.Config.Hooks.MaxLedgerRecords);

                if (outcome.Ok || ct.IsCancellationRequested) continue;
                if (string.Equals(trigger, "tool.before", StringComparison.Ordinal)
                    && string.Equals(hook.FailurePolicy, "block", StringComparison.Ordinal))
                {
                    return new HookDecision(true,
                        $"hook \"{hook.Name}\" failed: {outcome.Error ?? "unknown error"}", outcomes);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 配置损坏/执行器异常都按放行处理，避免钩子把主流程拖死。
        }
        return new HookDecision(false, null, outcomes);
    }

    /// <summary>立即执行指定钩子一次并返回结果（hooks.test）。</summary>
    public static async Task<HookRunOutcome> TestAsync(string name, IConfigStore configStore, CancellationToken ct)
    {
        var hook = configStore.Config.Hooks.Hooks.FirstOrDefault(
            h => string.Equals(h.Name, name, StringComparison.Ordinal));
        if (hook is null)
            return new HookRunOutcome(false, $"钩子「{name}」不存在", 0);
        var problem = Validate(hook);
        if (problem is not null)
            return new HookRunOutcome(false, problem, 0);

        var outcome = await ExecuteWithTimeoutAsync(hook, null, ct).ConfigureAwait(false);
        Record(hook.Name, outcome);
        HookLedger.Append(HaoyuePaths.Home, new HookRunRecord(
            DateTimeOffset.UtcNow, hook.Trigger, hook.Name, "", outcome.Ok,
            outcome.DurationMs, outcome.Error, outcome.Output),
            configStore.Config.Hooks.MaxLedgerRecords);
        return outcome;
    }

    private static async Task<HookRunOutcome> ExecuteWithTimeoutAsync(
        HookConfig hook, JsonObject? payload, CancellationToken ct)
    {
        var seconds = Math.Clamp(hook.TimeoutSeconds, 1, 120);
        if (string.Equals(hook.Action, "webhook", StringComparison.Ordinal))
            seconds = Math.Min(seconds, 30);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            return await ExecuteOneAsync(hook, payload, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new HookRunOutcome(false, $"执行超过 {seconds} 秒，已中止", seconds * 1000d);
        }
    }

    private static async Task<HookRunOutcome> ExecuteOneAsync(HookConfig hook, JsonObject? payload, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            HookRunOutcome outcome;
            if (string.Equals(hook.Action, "webhook", StringComparison.Ordinal))
                outcome = await ExecuteWebhookAsync(hook, payload, ct).ConfigureAwait(false);
            else if (string.Equals(hook.Action, "command", StringComparison.Ordinal))
                outcome = await ExecuteCommandAsync(hook, payload, ct).ConfigureAwait(false);
            else
                outcome = new HookRunOutcome(false, $"未知动作类型「{hook.Action}」", 0);
            return outcome with { DurationMs = watch.Elapsed.TotalMilliseconds };
        }
        catch (OperationCanceledException)
        {
            return new HookRunOutcome(false, "执行超时或已取消", watch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            return new HookRunOutcome(false, ex.Message, watch.Elapsed.TotalMilliseconds);
        }
    }

    private static async Task<HookRunOutcome> ExecuteWebhookAsync(HookConfig hook, JsonObject? payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hook.WebhookUrl))
            return new HookRunOutcome(false, "Webhook 地址为空", 0);

        var body = new JsonObject
        {
            ["hook"] = hook.Name,
            ["trigger"] = hook.Trigger,
            ["occurredAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        if (payload is not null) body["payload"] = (JsonNode)payload.DeepClone();

        try
        {
            var options = new System.Text.Json.JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            using var content = new StringContent(body.ToJsonString(options), Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, hook.WebhookUrl) { Content = content };
            if (!string.IsNullOrWhiteSpace(hook.WebhookSecret))
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {hook.WebhookSecret}");
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var success = response.IsSuccessStatusCode;
            var detail = success
                ? $"HTTP {(int)response.StatusCode}"
                : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim();
            return new HookRunOutcome(success, success ? null : detail, 0, detail);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new HookRunOutcome(false, ex.Message, 0);
        }
    }

    private static async Task<HookRunOutcome> ExecuteCommandAsync(HookConfig hook, JsonObject? payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hook.Command))
            return new HookRunOutcome(false, "命令为空", 0);

        var psi = new ProcessStartInfo
        {
            FileName = ResolveExecutable(hook.Command),
            Arguments = ExtractArguments(hook.Command),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        Process? process = null;
        try
        {
            process = new Process { StartInfo = psi };
            if (!process.Start())
                return new HookRunOutcome(false, "进程启动失败", 0);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var ok = process.ExitCode == 0;
            var detail = ok
                ? Truncate(stdout)
                : Truncate(string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stderr}（退出码 {process.ExitCode}）");
            return new HookRunOutcome(ok, ok ? null : $"退出码 {process.ExitCode}", 0, detail);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 进程可能已退出或无权限终止；超时结果本身已足够。
            }
            return new HookRunOutcome(false, "执行超时或已取消", 0);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new HookRunOutcome(false, ex.Message, 0);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string ResolveExecutable(string command)
    {
        var trimmed = command.TrimStart();
        var end = trimmed.IndexOfAny(new[] { ' ', '\t' });
        return end < 0 ? trimmed : trimmed[..end];
    }

    private static string ExtractArguments(string command)
    {
        var trimmed = command.TrimStart();
        var end = trimmed.IndexOfAny(new[] { ' ', '\t' });
        return end < 0 ? "" : trimmed[end..].Trim();
    }

    private static string Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return text.Length <= CommandOutputLimit ? text : $"{text[..CommandOutputLimit]}…（已截断）";
    }

    private static void Record(string name, HookRunOutcome outcome)
    {
        Status[name] = new HookRunStatus(
            name,
            (Status.TryGetValue(name, out var previous) ? previous.RunCount : 0) + 1,
            outcome.Ok,
            DateTimeOffset.UtcNow,
            outcome.Ok ? null : outcome.Error,
            outcome.DurationMs);
    }

    private static string TargetOf(JsonObject? payload)
    {
        try
        {
            return payload?["target"]?.GetValue<string>() ?? "";
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    /// <summary>Match 为逗号分隔通配符（* ?），空表示全部匹配。</summary>
    internal static bool MatchesTarget(string? match, string target)
    {
        if (string.IsNullOrWhiteSpace(match)) return true;
        foreach (var raw in match.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pattern = "^" + Regex.Escape(raw).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            if (Regex.IsMatch(target, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;
        }
        return false;
    }

    /// <summary>配置问题诊断：返回第一条问题描述；null = 配置可用。</summary>
    public static string? Validate(HookConfig hook)
    {
        if (string.IsNullOrWhiteSpace(hook.Name))
            return "钩子名称不能为空";
        if (!Triggers.Contains(hook.Trigger))
            return $"触发事件「{hook.Trigger}」不受支持（可选：{string.Join(" / ", Triggers)}）";
        if (!Actions.Contains(hook.Action))
            return $"动作类型「{hook.Action}」不受支持（可选：{string.Join(" / ", Actions)}）";
        if (!FailurePolicies.Contains(hook.FailurePolicy))
            return $"失败策略「{hook.FailurePolicy}」不受支持（可选：warn / block）";
        if (hook.TimeoutSeconds is < 1 or > 120)
            return "超时秒数必须在 1..120 之间";
        if (string.Equals(hook.Action, "webhook", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(hook.WebhookUrl))
                return "Webhook 动作需要填写目标地址";
            if (!Uri.TryCreate(hook.WebhookUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return "Webhook 地址必须是 http(s) 绝对地址";
        }
        if (string.Equals(hook.Action, "command", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(hook.Command))
            return "命令动作需要填写要执行的命令";
        return null;
    }

    /// <summary>返回全部钩子最近执行状态（进程内；按名称排序）。</summary>
    public static IReadOnlyList<HookRunStatus> Snapshot() =>
        Status.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();

    /// <summary>测试专用：清空进程内执行状态。</summary>
    public static void ResetForTests() => Status.Clear();
}
