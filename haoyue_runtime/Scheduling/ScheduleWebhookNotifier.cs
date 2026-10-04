using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;

namespace Haoyue.Runtime.Scheduling;

/// <summary>
/// Posts scheduled-task outcomes to a user-configured webhook URL. This is the only
/// notify channel that works while no desktop client is connected: the daemon always
/// broadcasts <c>schedule.updated</c> events to connected desktops, but a cron task
/// that fails unattended at 3 a.m. otherwise stays silent.
///
/// Delivery is best-effort by design — a webhook outage must never delay or fail the
/// scheduler itself, so transport errors are swallowed (no retries; the outcome is
/// still recorded in the store and visible in the desktop/CLI history).
/// </summary>
public sealed class ScheduleWebhookNotifier : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;

    /// <param name="handler">Test seam: inject a stub handler to capture POSTs.</param>
    public ScheduleWebhookNotifier(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout;
    }

    /// <summary>
    /// POSTs one JSON payload describing the task outcome. A null/empty URL is a no-op.
    /// Returns without throwing: transport failures are logged to nothing but swallowed.
    /// </summary>
    public async Task NotifyAsync(string? url, ScheduledTask task, string? sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        var payload = new JsonObject
        {
            ["event"] = "schedule.completed",
            ["taskId"] = task.Id,
            ["name"] = task.DisplayName,
            ["status"] = task.LastStatus,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        if (sessionId is not null) payload["sessionId"] = sessionId;
        if (!string.IsNullOrWhiteSpace(task.LastError)) payload["error"] = task.LastError;
        if (!string.IsNullOrWhiteSpace(task.LastOutput)) payload["output"] = task.LastOutput;

        try
        {
            // UnsafeRelaxed keeps CJK readable: the default encoder \uXXXX-escapes
            // every non-ASCII character, producing unreadable payloads for webhook
            // consumers. This body is never embedded into HTML, so relaxed escaping is safe.
            var options = new System.Text.Json.JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            using var content = new StringContent(payload.ToJsonString(options), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or ObjectDisposedException)
        {
            // Best-effort delivery: never let a webhook outage affect scheduling.
        }
    }

    public void Dispose() => _http.Dispose();
}
