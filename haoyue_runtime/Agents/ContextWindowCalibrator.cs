using System.Collections.Concurrent;

namespace Haoyue.Runtime.Agents;

/// <summary>
/// Runtime calibration of declared context windows (M7). A model entry whose
/// <c>ContextWindow</c> is declared larger than reality overflows on every long
/// request; the provider error text is the only ground truth. When a request fails
/// with a context-length error, the estimated token size of that request is recorded
/// as a ceiling for the model and <see cref="ContextPlanner.FitToWindow"/> uses it in
/// place of the declared window on subsequent turns. Process-lifetime scope is
/// deliberate: config edits should restart the runtime anyway.
/// </summary>
public static class ContextWindowCalibrator
{
    private readonly record struct Entry(int Ceiling, int Observations);

    private static readonly ConcurrentDictionary<string, Entry> Ceilings = new(StringComparer.Ordinal);

    /// <summary>
    /// Provider error fragments that indicate a context-window overflow (as opposed to
    /// auth/network/rate errors). Matched case-insensitively; intentionally broad on
    /// the "tokens" phrasing because providers word this error very differently.
    /// </summary>
    private static readonly string[] OverflowMarkers =
    [
        "context length",
        "context_length_exceeded",
        "maximum context",
        "context window",
        "prompt is too long",
        "input is too long",
        "too many tokens",
        "input tokens exceed",
        "exceeds the model's maximum",
        "reduce the length",
        "request too large",
        "上下文长度",
        "超出上下文",
        "token 数超",
    ];

    public static bool IsContextOverflow(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && OverflowMarkers.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>Records an observed overflow: the failed request carried roughly
    /// <paramref name="estimatedTokens"/>. Only monotonic decreases are kept — the
    /// smallest failing size is the best ceiling estimate we have.</summary>
    public static void RecordOverflow(string providerId, string modelId, int estimatedTokens)
    {
        if (estimatedTokens <= 0) return;
        // Add headroom so the calibrated request sits comfortably under the observed edge.
        var ceiling = Math.Max(1_024, estimatedTokens - estimatedTokens / 10);
        Ceilings.AddOrUpdate(
            Key(providerId, modelId),
            new Entry(ceiling, 1),
            (_, existing) => ceiling < existing.Ceiling ? new Entry(ceiling, existing.Observations + 1) : existing);
    }

    /// <summary>The calibrated ceiling for this model, or null when no overflow was
    /// observed (the declared window is then used unchanged).</summary>
    public static int? GetCeiling(string providerId, string modelId) =>
        Ceilings.TryGetValue(Key(providerId, modelId), out var entry) ? entry.Ceiling : null;

    /// <summary>Test/inspection hook.</summary>
    public static void Reset() => Ceilings.Clear();

    private static string Key(string providerId, string modelId) =>
        string.Concat(providerId, "|", modelId);
}
