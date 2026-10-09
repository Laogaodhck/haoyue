using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Events;

namespace Haoyue.Runtime.Evolution;

/// <summary>Class of defect surfaced by the aggregator (evolution engine E1).</summary>
public enum DefectKind
{
    /// <summary>The same tool failing repeatedly with the same normalized error.</summary>
    ToolFailureCluster,
    /// <summary>The verification chain failing several times in a row (repair-loop struggle).</summary>
    VerificationStruggle,
    /// <summary>The agent tried to load a skill that does not exist, or repeatedly produced
    /// malformed tool arguments — a capability gap rather than a one-off mistake.</summary>
    CapabilityGap,
    /// <summary>The user explicitly marked a finished turn as unsatisfactory (P4 thumbs-down).
    /// Each feedback event becomes its own report so reflection can study the session.</summary>
    UserNegativeFeedback,
    /// <summary>The same model ref retried repeatedly — provider instability rather than
    /// a one-off network blip.</summary>
    ProviderInstability,
    /// <summary>The user had to steer the same session repeatedly — the agent kept
    /// drifting off course.</summary>
    UserCorrection,
    /// <summary>The user cancelled several turns in the same session.</summary>
    TurnCancelled,
}

/// <summary>
/// One aggregated defect. <see cref="Fingerprint"/> is stable for
/// (kind, tool, normalized error) so the later reflection stage (P2) can
/// skip reports it has already turned into a candidate or rejected.
/// </summary>
public sealed record DefectReport(
    string Fingerprint,
    DefectKind Kind,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    int Occurrences,
    string? ToolName,
    string? ErrorSummary,
    string? SessionId,
    string? Severity = null);

/// <summary>
/// Evolution engine stage E1: read-only aggregation of journaled failure events
/// into structured <see cref="DefectReport"/>s. Deliberately a batch query over
/// the SQLite journal rather than a live EventBus subscriber — every signal it
/// needs is already persisted by <see cref="EventJournal"/>, and a query can be
/// triggered manually (evolution.inspect RPC) or on a schedule without holding
/// a subscription open. Reflection-turn events carry no special marker yet; when
/// P2 introduces them the aggregator must exclude events with origin=evolution.
/// </summary>
public sealed class DefectAggregator(HaoyueDatabase database)
{
    /// <summary>Default number of journal rows scanned (the journal keeps 5000).</summary>
    public const int DefaultScanLimit = 2000;

    /// <summary>Failures needed inside <see cref="ClusterWindow"/> to count as a cluster.</summary>
    public const int ClusterThreshold = 3;

    /// <summary>Window inside which repeated same-error failures form a cluster.</summary>
    public static readonly TimeSpan ClusterWindow = TimeSpan.FromMinutes(30);

    /// <summary>Cap for normalized error text so fingerprints stay stable across noise.</summary>
    public const int ErrorSummaryChars = 160;

    private static readonly JsonSerializerOptions PayloadOptions = new();

    /// <summary>Scans the most recent journal rows and returns aggregated defects, oldest first.
    /// <paramref name="excludeSessions"/> lists reflection-turn session ids (from the evolution
    /// decision log) so the engine never aggregates its own failures. Note: session attribution
    /// brackets events between TurnStarted/TurnCompleted, which is unreliable when concurrent
    /// turns interleave their events — acceptable for defect aggregation purposes.</summary>
    public IReadOnlyList<DefectReport> Aggregate(
        int limit = DefaultScanLimit,
        IReadOnlyCollection<string>? excludeSessions = null)
    {
        var journal = database.RecentEvents(Math.Clamp(limit, 1, EventJournal.RetainedEvents));

        var currentSession = (string?)null;
        var sessionExcluded = false;
        var toolFailures = new Dictionary<ClusterKey, List<(DateTimeOffset Ts, string? Session)>>();
        var verificationRuns = new List<List<(DateTimeOffset Ts, VerificationCompletedEvent Event)>>();
        var currentVerificationRun = new List<(DateTimeOffset Ts, VerificationCompletedEvent Event)>();
        var capabilityGaps = new Dictionary<string, List<(DateTimeOffset Ts, string? Session)>>();
        var providerRetries = new Dictionary<ClusterKey, List<DateTimeOffset>>();
        var corrections = new Dictionary<string, List<(DateTimeOffset Ts, string Instruction)>>();
        var cancels = new Dictionary<string, List<DateTimeOffset>>();
        var feedbackReports = new List<DefectReport>();

        foreach (var persisted in journal)
        {
            var ts = DateTimeOffset.Parse(persisted.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

            switch (persisted.Type)
            {
                case nameof(TurnStartedEvent):
                    currentSession = TryDeserialize<TurnStartedEvent>(persisted.Payload)?.SessionId;
                    sessionExcluded = currentSession is not null
                        && excludeSessions?.Contains(currentSession) == true;
                    break;

                case nameof(TurnCompletedEvent):
                    if (TryDeserialize<TurnCompletedEvent>(persisted.Payload) is { } completed
                        && completed.Cancelled
                        && (completed.SessionId is null || excludeSessions?.Contains(completed.SessionId) != true))
                    {
                        var cancelKey = completed.SessionId ?? "-";
                        cancels.TryAdd(cancelKey, []);
                        cancels[cancelKey].Add(ts);
                    }
                    currentSession = null;
                    sessionExcluded = false;
                    break;

                case nameof(ProviderRetryEvent):
                    if (sessionExcluded) break;
                    if (TryDeserialize<ProviderRetryEvent>(persisted.Payload) is not { } retry) break;
                    var retryKey = new ClusterKey(retry.ModelRef, NormalizeError(retry.Reason));
                    providerRetries.TryAdd(retryKey, []);
                    providerRetries[retryKey].Add(ts);
                    break;

                case nameof(UserSteerEvent):
                    if (sessionExcluded) break;
                    if (TryDeserialize<UserSteerEvent>(persisted.Payload) is not { } steer) break;
                    var correctionKey = currentSession ?? "-";
                    corrections.TryAdd(correctionKey, []);
                    corrections[correctionKey].Add((ts, steer.Instruction));
                    break;

                case nameof(ToolCallCompletedEvent):
                    if (sessionExcluded) break;
                    var tool = TryDeserialize<ToolCallCompletedEvent>(persisted.Payload);
                    if (tool is null || tool.Success) break;
                    var error = NormalizeError(tool.ResultSummary);
                    if (error.Length == 0) break;

                    List<(DateTimeOffset Ts, string? Session)> bucket;
                    if (string.Equals(tool.ToolName, "declare_skill", StringComparison.OrdinalIgnoreCase))
                    {
                        capabilityGaps.TryAdd(error, []);
                        bucket = capabilityGaps[error];
                    }
                    else
                    {
                        var key = new ClusterKey(tool.ToolName, error);
                        toolFailures.TryAdd(key, []);
                        bucket = toolFailures[key];
                    }
                    bucket.Add((ts, currentSession));
                    break;

                case nameof(VerificationCompletedEvent):
                    if (sessionExcluded) break;
                    var verification = TryDeserialize<VerificationCompletedEvent>(persisted.Payload);
                    if (verification is null) break;
                    if (verification.Success)
                    {
                        // A success closes the current failing run; "recovered after N
                        // failures" is exactly the struggle signal worth reporting.
                        if (currentVerificationRun.Count > 0)
                        {
                            currentVerificationRun.Add((ts, verification));
                            verificationRuns.Add(currentVerificationRun);
                            currentVerificationRun = new List<(DateTimeOffset Ts, VerificationCompletedEvent Event)>();
                        }
                    }
                    else
                    {
                        currentVerificationRun.Add((ts, verification));
                    }
                    break;

                case nameof(UserFeedbackEvent):
                    // P4: one thumbs-down is one defect report — no threshold. The
                    // fingerprint embeds the journal timestamp so repeated feedback on
                    // the same session stays distinct; reflection reads the transcript.
                    if (TryDeserialize<UserFeedbackEvent>(persisted.Payload) is not { } feedback) break;
                    if (!string.Equals(feedback.Kind, "negative", StringComparison.OrdinalIgnoreCase)) break;
                    feedbackReports.Add(new DefectReport(
                        Fingerprint(DefectKind.UserNegativeFeedback, feedback.SessionId,
                            persisted.Timestamp),
                        DefectKind.UserNegativeFeedback,
                        ts,
                        ts,
                        1,
                        null,
                        string.IsNullOrWhiteSpace(feedback.Reason)
                            ? "用户标记该回合不满意"
                            : $"用户标记该回合不满意：{feedback.Reason.Trim()}",
                        feedback.SessionId,
                        EvolutionAnalytics.SeverityOf(DefectKind.UserNegativeFeedback, 1)));
                    break;
            }
        }

        // A failing verification run still open at the end of the journal also counts.
        if (currentVerificationRun.Count > 0)
            verificationRuns.Add(currentVerificationRun);

        var reports = new List<DefectReport>();
        reports.AddRange(BuildToolClusters(toolFailures));
        reports.AddRange(BuildVerificationStruggles(verificationRuns));
        reports.AddRange(BuildCapabilityGaps(capabilityGaps));
        reports.AddRange(BuildProviderClusters(providerRetries));
        reports.AddRange(BuildCorrections(corrections));
        reports.AddRange(BuildCancels(cancels));
        reports.AddRange(feedbackReports);
        return reports.OrderBy(r => r.FirstSeen).ToList();
    }

    private record struct ClusterKey(string ToolName, string Error);

    /// <summary>Best (largest) sliding-window span containing ≥<see cref="ClusterThreshold"/> points; null if none.</summary>
    private static (int Start, int End)? BestWindow(List<DateTimeOffset> ordered)
    {
        var bestStart = 0;
        var bestEnd = -1;
        for (var i = 0; i < ordered.Count; i++)
        {
            var j = i;
            while (j + 1 < ordered.Count && ordered[j + 1] - ordered[i] <= ClusterWindow) j++;
            if (j - i + 1 >= ClusterThreshold && j > bestEnd)
            {
                bestStart = i;
                bestEnd = j;
            }
        }
        return bestEnd < 0 ? null : (bestStart, bestEnd);
    }

    private IEnumerable<DefectReport> BuildToolClusters(
        Dictionary<ClusterKey, List<(DateTimeOffset Ts, string? Session)>> failures)
    {
        foreach (var (key, timestamps) in failures)
        {
            if (timestamps.Count == 0) continue;
            var ordered = timestamps.OrderBy(t => t.Ts).ToList();

            var window = BestWindow(ordered.Select(t => t.Ts).ToList());
            if (window is null) continue;
            var (bestStart, bestEnd) = window.Value;

            yield return new DefectReport(
                Fingerprint(DefectKind.ToolFailureCluster, key.ToolName, key.Error),
                DefectKind.ToolFailureCluster,
                ordered[bestStart].Ts,
                ordered[bestEnd].Ts,
                bestEnd - bestStart + 1,
                key.ToolName,
                key.Error,
                ordered[bestEnd].Session,
                EvolutionAnalytics.SeverityOf(DefectKind.ToolFailureCluster, bestEnd - bestStart + 1));
        }
    }

    private IEnumerable<DefectReport> BuildVerificationStruggles(
        List<List<(DateTimeOffset Ts, VerificationCompletedEvent Event)>> runs)
    {
        foreach (var run in runs)
        {
            if (run.Count(v => !v.Event.Success) < ClusterThreshold) continue;
            var lastFailure = run.Last(v => !v.Event.Success);
            var summary = NormalizeError(lastFailure.Event.Summary);
            yield return new DefectReport(
                Fingerprint(DefectKind.VerificationStruggle, "verify", summary),
                DefectKind.VerificationStruggle,
                run[0].Ts,
                run[^1].Ts,
                run.Count(v => !v.Event.Success),
                "verify",
                summary,
                null,
                EvolutionAnalytics.SeverityOf(DefectKind.VerificationStruggle, run.Count(v => !v.Event.Success)));
        }
    }

    private IEnumerable<DefectReport> BuildCapabilityGaps(
        Dictionary<string, List<(DateTimeOffset Ts, string? Session)>> gaps)
    {
        foreach (var (error, timestamps) in gaps)
        {
            if (timestamps.Count == 0) continue;
            // A single declare_skill failure is already a gap — no threshold.
            var ordered = timestamps.OrderBy(t => t.Ts).ToList();
            yield return new DefectReport(
                Fingerprint(DefectKind.CapabilityGap, "declare_skill", error),
                DefectKind.CapabilityGap,
                ordered[0].Ts,
                ordered[^1].Ts,
                ordered.Count,
                "declare_skill",
                error,
                ordered[^1].Session,
                EvolutionAnalytics.SeverityOf(DefectKind.CapabilityGap, ordered.Count));
        }
    }

    private IEnumerable<DefectReport> BuildProviderClusters(Dictionary<ClusterKey, List<DateTimeOffset>> retries)
    {
        foreach (var (key, timestamps) in retries)
        {
            if (timestamps.Count == 0) continue;
            var ordered = timestamps.OrderBy(t => t).ToList();
            var window = BestWindow(ordered);
            if (window is null) continue;
            var (start, end) = window.Value;
            yield return new DefectReport(
                Fingerprint(DefectKind.ProviderInstability, key.ToolName, key.Error),
                DefectKind.ProviderInstability,
                ordered[start],
                ordered[end],
                end - start + 1,
                key.ToolName,
                string.IsNullOrEmpty(key.Error)
                    ? "同一模型短窗口内连续重试"
                    : $"同一模型短窗口内连续重试：{key.Error}",
                null,
                EvolutionAnalytics.SeverityOf(DefectKind.ProviderInstability, end - start + 1));
        }
    }

    private IEnumerable<DefectReport> BuildCorrections(
        Dictionary<string, List<(DateTimeOffset Ts, string Instruction)>> grouped)
    {
        foreach (var (session, entries) in grouped)
        {
            if (entries.Count < ClusterThreshold) continue;
            var ordered = entries.OrderBy(e => e.Ts).ToList();
            var lastInstruction = NormalizeError(ordered[^1].Instruction);
            yield return new DefectReport(
                Fingerprint(DefectKind.UserCorrection, session, lastInstruction),
                DefectKind.UserCorrection,
                ordered[0].Ts,
                ordered[^1].Ts,
                ordered.Count,
                null,
                string.IsNullOrEmpty(lastInstruction)
                    ? $"同一会话内用户连续纠偏 {ordered.Count} 次"
                    : $"同一会话内用户连续纠偏：{lastInstruction}",
                session,
                EvolutionAnalytics.SeverityOf(DefectKind.UserCorrection, ordered.Count));
        }
    }

    private IEnumerable<DefectReport> BuildCancels(Dictionary<string, List<DateTimeOffset>> grouped)
    {
        foreach (var (session, timestamps) in grouped)
        {
            if (timestamps.Count < 2) continue;
            var ordered = timestamps.OrderBy(t => t).ToList();
            yield return new DefectReport(
                Fingerprint(DefectKind.TurnCancelled, session, ""),
                DefectKind.TurnCancelled,
                ordered[0],
                ordered[^1],
                ordered.Count,
                null,
                $"同一会话内用户中断回合 {ordered.Count} 次",
                session,
                EvolutionAnalytics.SeverityOf(DefectKind.TurnCancelled, ordered.Count));
        }
    }

    /// <summary>Collapses an error summary into a stable, compact fingerprint component.</summary>
    public static string NormalizeError(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return "";
        var text = string.Join(' ', summary.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > ErrorSummaryChars) text = text[..ErrorSummaryChars];
        return text.ToLowerInvariant();
    }

    private static string Fingerprint(DefectKind kind, string tool, string error)
    {
        var material = $"{kind}|{tool}|{error}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static T? TryDeserialize<T>(string payload) where T : class
    {
        try
        {
            // Truncated payloads collapse to {"type","truncated"} and deserialize to defaults;
            // reject those so a single oversized row cannot fabricate a synthetic event.
            return JsonSerializer.Deserialize<T>(payload, PayloadOptions) is { } value
                && JsonNodeLooksReal(payload)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool JsonNodeLooksReal(string payload)
    {
        try { return JsonNode.Parse(payload)?["truncated"]?.GetValue<bool>() != true; }
        catch (JsonException) { return false; }
    }
}
