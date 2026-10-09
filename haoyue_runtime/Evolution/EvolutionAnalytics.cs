using System.Globalization;
using System.Text.Json;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Events;

namespace Haoyue.Runtime.Evolution;

/// <summary>
/// Derived analytics over aggregated defect reports and the event journal:
/// a single explainable health score, a per-day trend, kind/tool distributions,
/// and adoption-effectiveness stats. Pure functions so tests can drive them
/// without a daemon.
/// </summary>
public static class EvolutionAnalytics
{
    public sealed record HealthScore(int Score, string Grade);

    public sealed record TrendPoint(
        string Date,
        int ToolFailures,
        int Gaps,
        int VerificationFailures,
        int Feedback,
        int Corrections,
        int Cancels,
        int Retries);

    public sealed record Distribution(
        IReadOnlyDictionary<string, int> ByKind,
        IReadOnlyList<(string Tool, int Count)> TopTools);

    public sealed record AdoptedSkillStat(
        string Fingerprint,
        string SkillName,
        DateTimeOffset AdoptedAt,
        string Kind,
        int UsageCount,
        bool Resolved);

    public sealed record StatsResult(
        int Runs,
        int CandidatesProduced,
        int Adopted,
        int Rejected,
        int Deferred,
        int NoAction,
        int Failed,
        double AdoptionRate,
        IReadOnlyList<AdoptedSkillStat> Skills);

    /// <summary>Health penalty per report kind: score = 100 - Σ min(cap, weight × count).</summary>
    private static readonly (DefectKind Kind, int Weight, int Cap)[] Penalties =
    [
        (DefectKind.UserNegativeFeedback, 10, 30),
        (DefectKind.ToolFailureCluster, 8, 30),
        (DefectKind.VerificationStruggle, 6, 18),
        (DefectKind.CapabilityGap, 6, 18),
        (DefectKind.ProviderInstability, 5, 15),
        (DefectKind.UserCorrection, 4, 12),
        (DefectKind.TurnCancelled, 3, 9),
    ];

    public static HealthScore ComputeHealth(IReadOnlyList<DefectReport> reports)
    {
        var penalty = 0;
        foreach (var (kind, weight, cap) in Penalties)
        {
            var count = reports.Count(r => r.Kind == kind);
            if (count > 0) penalty += Math.Min(cap, weight * count);
        }
        var score = Math.Clamp(100 - penalty, 0, 100);
        return new HealthScore(score, ScoreGrade(score));
    }

    public static string ScoreGrade(int score) =>
        score >= 90 ? "优" : score >= 75 ? "良" : score >= 60 ? "中" : "差";

    /// <summary>User feedback is always high (a direct human signal); volume raises the rest.</summary>
    public static string SeverityOf(DefectKind kind, int occurrences) => kind switch
    {
        DefectKind.UserNegativeFeedback => "high",
        DefectKind.ToolFailureCluster => occurrences >= 6 ? "high" : "medium",
        DefectKind.VerificationStruggle => occurrences >= 6 ? "high" : "medium",
        DefectKind.ProviderInstability => occurrences >= 5 ? "high" : "medium",
        _ => "medium",
    };

    /// <summary>Per-day signal counts for the last <paramref name="days"/> days, oldest first, zero-filled.</summary>
    public static IReadOnlyList<TrendPoint> ComputeTrend(HaoyueDatabase database, int days = 7)
    {
        var today = DateTimeOffset.Now.Date;
        var dates = Enumerable.Range(0, days)
            .Select(offset => today.AddDays(-(days - 1 - offset)).ToString("yyyy-MM-dd"))
            .ToList();
        var points = dates.ToDictionary(
            date => date,
            date => new TrendPoint(date, 0, 0, 0, 0, 0, 0, 0));
        var window = new HashSet<string>(dates);

        foreach (var persisted in database.RecentEvents(EventJournal.RetainedEvents))
        {
            var date = DateTimeOffset.Parse(persisted.Timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind).LocalDateTime.ToString("yyyy-MM-dd");
            if (!window.Contains(date)) continue;

            switch (persisted.Type)
            {
                case nameof(ToolCallCompletedEvent):
                    if (TryRead<ToolCallCompletedEvent>(persisted.Payload) is not { } tool) break;
                    if (tool.Success) break;
                    if (string.Equals(tool.ToolName, "declare_skill", StringComparison.OrdinalIgnoreCase))
                        points[date] = points[date] with { Gaps = points[date].Gaps + 1 };
                    else
                        points[date] = points[date] with { ToolFailures = points[date].ToolFailures + 1 };
                    break;
                case nameof(VerificationCompletedEvent):
                    if (TryRead<VerificationCompletedEvent>(persisted.Payload) is { } verification && !verification.Success)
                        points[date] = points[date] with { VerificationFailures = points[date].VerificationFailures + 1 };
                    break;
                case nameof(UserFeedbackEvent):
                    if (TryRead<UserFeedbackEvent>(persisted.Payload) is { } feedback
                        && string.Equals(feedback.Kind, "negative", StringComparison.OrdinalIgnoreCase))
                        points[date] = points[date] with { Feedback = points[date].Feedback + 1 };
                    break;
                case nameof(UserSteerEvent):
                    points[date] = points[date] with { Corrections = points[date].Corrections + 1 };
                    break;
                case nameof(TurnCompletedEvent):
                    if (TryRead<TurnCompletedEvent>(persisted.Payload) is { } completed && completed.Cancelled)
                        points[date] = points[date] with { Cancels = points[date].Cancels + 1 };
                    break;
                case nameof(ProviderRetryEvent):
                    points[date] = points[date] with { Retries = points[date].Retries + 1 };
                    break;
            }
        }

        return dates.Select(date => points[date]).ToList();
    }

    public static Distribution ComputeDistribution(IReadOnlyList<DefectReport> reports)
    {
        var byKind = reports
            .GroupBy(r => r.Kind.ToString())
            .ToDictionary(g => g.Key, g => g.Count());
        var topTools = reports
            .Where(r => !string.IsNullOrEmpty(r.ToolName))
            .GroupBy(r => r.ToolName!)
            .Select(g => (Tool: g.Key, Count: g.Sum(x => x.Occurrences)))
            .OrderByDescending(x => x.Count)
            .Take(5)
            .ToList();
        return new Distribution(byKind, topTools);
    }

    /// <summary>Adoption-effectiveness stats: totals, adoption rate, and per-skill usage/recurrence.</summary>
    public static StatsResult ComputeStats(
        HaoyueDatabase database, EvolutionStore store, IReadOnlyList<DefectReport> currentReports)
    {
        var records = store.List(null);
        var adopted = records.Where(r => r.Status == EvolutionStatus.Adopted).ToList();
        var rejected = records.Count(r => r.Status == EvolutionStatus.Rejected);
        var decided = adopted.Count + rejected;
        var skills = new List<AdoptedSkillStat>();
        foreach (var record in adopted)
        {
            if (SkillNameFromDir(record.CandidateDir) is not { } name) continue;
            skills.Add(new AdoptedSkillStat(
                record.Fingerprint,
                name,
                record.UpdatedAt,
                record.Kind,
                CountSkillUsage(database, name),
                currentReports.All(r => r.Fingerprint != record.Fingerprint)));
        }
        skills.Sort((a, b) => b.AdoptedAt.CompareTo(a.AdoptedAt));
        var runs = store.ListRuns(200);
        return new StatsResult(
            runs.Count,
            runs.Sum(r => r.Candidates),
            adopted.Count,
            rejected,
            records.Count(r => r.Status == EvolutionStatus.Deferred),
            records.Count(r => r.Status == EvolutionStatus.NoAction),
            records.Count(r => r.Status == EvolutionStatus.Failed),
            decided > 0 ? Math.Round(adopted.Count * 100.0 / decided, 1) : 0,
            skills);
    }

    /// <summary>Candidate dirs are named "&lt;skillName&gt;-&lt;fingerprint8&gt;"; extracts the skill name.</summary>
    public static string? SkillNameFromDir(string? candidateDir)
    {
        if (string.IsNullOrEmpty(candidateDir)) return null;
        var name = Path.GetFileName(candidateDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return name.Length > 9 ? name[..^9] : null;
    }

    /// <summary>Successful declare_skill invocations mentioning the skill (approximate usage counter).</summary>
    private static int CountSkillUsage(HaoyueDatabase database, string skillName)
    {
        var count = 0;
        foreach (var persisted in database.RecentEvents(EventJournal.RetainedEvents))
        {
            if (persisted.Type != nameof(ToolCallStartedEvent)) continue;
            if (TryRead<ToolCallStartedEvent>(persisted.Payload) is not { } started) continue;
            if (!string.Equals(started.ToolName, "declare_skill", StringComparison.OrdinalIgnoreCase)) continue;
            if (started.ArgumentSummary?.Contains(skillName, StringComparison.OrdinalIgnoreCase) == true) count++;
        }
        return count;
    }

    private static T? TryRead<T>(string payload) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(payload); }
        catch (JsonException) { return null; }
    }
}
