using System.Text.Json;
using System.Collections.Concurrent;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;

namespace Haoyue.Runtime.Providers;

public interface IUsageTracker
{
    void Record(UsageEntry entry);
    IReadOnlyList<UsageEntry> ReadAll(DateTimeOffset? since = null);
    IReadOnlyList<UsageAggregate> Aggregate(DateTimeOffset? since = null);
}

/// <summary>Append-only JSONL usage log with in-memory aggregation.</summary>
public sealed class UsageTracker(IEventBus eventBus, string? filePath = null) : IUsageTracker
{
    private readonly string _file = filePath ?? HaoyuePaths.UsageFile;
    // Isolated concurrent turn runtimes share the append-only usage file. A process-wide
    // lock prevents two instances from opening/writing the same file at the same time.
    private static readonly ConcurrentDictionary<string, Lock> FileGates = new(StringComparer.OrdinalIgnoreCase);
    private Lock Gate => FileGates.GetOrAdd(_file, static _ => new Lock());

    public void Record(UsageEntry entry)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var line = JsonSerializer.Serialize(entry, HaoyueJsonContext.Compact.UsageEntry);
            File.AppendAllText(_file, line + Environment.NewLine);
        }

        eventBus.Publish(new UsageRecordedEvent(
            entry.Provider, entry.Model, entry.InputTokens, entry.OutputTokens,
            entry.Cost, TimeSpan.FromMilliseconds(entry.ElapsedMs),
            entry.TotalInputTokens, entry.CachedInputTokens,
            // 本地推理遥测随实时事件一起下发（桌面读数）；无测量的行不带该字段。
            entry.TtftMs is { } ttft
                ? new InferencePerformance
                {
                    TtftMs = ttft,
                    LoadWaitMs = entry.LoadWaitMs ?? 0,
                    PrefillTokensPerSecond = entry.PrefillTokensPerSecond ?? 0,
                    DecodeTokensPerSecond = entry.DecodeTokensPerSecond ?? 0,
                    CacheHitRate = entry.CacheHitRate ?? 0,
                    PrefixReuse = entry.PrefixReuse ?? false,
                }
                : null));
    }

    public IReadOnlyList<UsageEntry> ReadAll(DateTimeOffset? since = null)
    {
        lock (Gate)
        {
            if (!File.Exists(_file)) return [];
            var entries = new List<UsageEntry>();
            foreach (var line in File.ReadLines(_file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize(line, HaoyueJsonContext.Compact.UsageEntry);
                    if (entry is not null && (since is null || entry.Timestamp >= since))
                        entries.Add(entry);
                }
                catch (JsonException) { /* skip corrupt line */ }
            }
            return entries;
        }
    }

    public IReadOnlyList<UsageAggregate> Aggregate(DateTimeOffset? since = null) =>
        ReadAll(since)
            .GroupBy(e => (e.Provider, e.Model))
            .Select(g =>
            {
                // 推理遥测均值只覆盖有测量的行（本地 provider）；历史行/云端调用不掺入。
                var measured = g.Where(e => e.TtftMs is not null).ToList();
                static double? AverageOrNull(IEnumerable<double> values)
                {
                    var list = values as IList<double> ?? values.ToList();
                    return list.Count > 0 ? list.Average() : null;
                }
                return new UsageAggregate
                {
                    Provider = g.Key.Provider,
                    Model = g.Key.Model,
                    Calls = g.Count(),
                    Failures = g.Count(e => !e.Success),
                    InputTokens = g.Sum(e => e.InputTokens),
                    // Older JSONL entries predate TotalInputTokens; fall back without rewriting
                    // the user's append-only history.
                    TotalInputTokens = g.Sum(e => e.TotalInputTokens > 0 ? e.TotalInputTokens : e.InputTokens),
                    CachedInputTokens = g.Sum(e => e.CachedInputTokens),
                    CacheCreationInputTokens = g.Sum(e => e.CacheCreationInputTokens),
                    OutputTokens = g.Sum(e => e.OutputTokens),
                    Cost = g.Sum(e => e.Cost),
                    AvgLatencyMs = g.Average(e => e.ElapsedMs),
                    TelemetryCalls = measured.Count,
                    AvgTtftMs = AverageOrNull(measured.Select(e => e.TtftMs!.Value)),
                    AvgLoadWaitMs = AverageOrNull(measured.Select(e => e.LoadWaitMs ?? 0)),
                    AvgPrefillTokensPerSecond = AverageOrNull(
                        measured.Where(e => e.PrefillTokensPerSecond is not null).Select(e => e.PrefillTokensPerSecond!.Value)),
                    AvgDecodeTokensPerSecond = AverageOrNull(
                        measured.Where(e => e.DecodeTokensPerSecond is not null).Select(e => e.DecodeTokensPerSecond!.Value)),
                    AvgCacheHitRate = AverageOrNull(
                        measured.Where(e => e.CacheHitRate is not null).Select(e => e.CacheHitRate!.Value)),
                    PrefixReuseRate = measured.Count > 0
                        ? measured.Average(e => e.PrefixReuse == true ? 1.0 : 0.0)
                        : null,
                };
            })
            .OrderByDescending(a => a.TotalTokens)
            .ToList();
}
