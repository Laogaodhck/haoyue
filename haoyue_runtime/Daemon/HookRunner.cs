using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
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

/// <summary>
/// 事件钩子执行器：task.completed / schedule.completed 触发时按配置自动执行
/// Webhook POST 或本地命令，并记录最近一次执行状态供「查看钩子配置和问题」。
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

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private const int CommandOutputLimit = 2_000;

    private static readonly string[] Triggers = { "task.completed", "schedule.completed" };
    private static readonly string[] Actions = { "webhook", "command" };

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
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
            var matched = hooks.Where(h => h.Enabled && string.Equals(h.Trigger, trigger, StringComparison.Ordinal)).ToList();
            foreach (var hook in matched)
            {
                _ = Task.Run(() =>
                {
                    var outcome = ExecuteOneAsync(hook, payload, CancellationToken.None).GetAwaiter().GetResult();
                    Record(hook.Name, outcome);
                });
            }
        }
        catch (Exception)
        {
            // Hooks are best-effort by design; a misconfigured store must never
            // affect the agent or scheduler paths that triggered them.
        }
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

        var outcome = await ExecuteOneAsync(hook, null, ct).ConfigureAwait(false);
        Record(hook.Name, outcome);
        return outcome;
    }

    private static async Task<HookRunOutcome> ExecuteOneAsync(HookConfig hook, JsonObject? payload, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            if (string.Equals(hook.Action, "webhook", StringComparison.Ordinal))
                return await ExecuteWebhookAsync(hook, payload, ct).ConfigureAwait(false);
            if (string.Equals(hook.Action, "command", StringComparison.Ordinal))
                return await ExecuteCommandAsync(hook, payload, ct).ConfigureAwait(false);
            return new HookRunOutcome(false, $"未知动作类型「{hook.Action}」", watch.Elapsed.TotalMilliseconds);
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
        try
        {
            using var process = new Process { StartInfo = psi };
            if (!process.Start())
                return new HookRunOutcome(false, "进程启动失败", 0);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(CommandTimeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
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
            return new HookRunOutcome(false, $"执行超过 {CommandTimeout.TotalSeconds} 秒，已中止", 0);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new HookRunOutcome(false, ex.Message, 0);
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

    /// <summary>配置问题诊断：返回第一条问题描述；null = 配置可用。</summary>
    public static string? Validate(HookConfig hook)
    {
        if (string.IsNullOrWhiteSpace(hook.Name))
            return "钩子名称不能为空";
        if (!Triggers.Contains(hook.Trigger))
            return $"触发事件「{hook.Trigger}」不受支持（可选：{string.Join(" / ", Triggers)}）";
        if (!Actions.Contains(hook.Action))
            return $"动作类型「{hook.Action}」不受支持（可选：{string.Join(" / ", Actions)}）";
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
