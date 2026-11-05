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

/// <summary>按域拆分的 RPC 处理方法（仅搬运自 DaemonServer.DispatchMethodAsync，逻辑未改动）。</summary>
public sealed partial class DaemonServer
{
    private async Task Handle_evolution_inspect(JsonObject request, long id, ConnectionContext context)
    {
        // Evolution engine E1: read-only aggregation of journaled failure
        // signals into structured defect reports, enriched with a health
        // score, per-day trend and kind/tool distributions (see Evolution/).
        var limit = Params(request)["limit"]?.GetValue<int>() ?? DefectAggregator.DefaultScanLimit;
        var aggregator = new DefectAggregator(_runtime.Database);
        var reportList = aggregator.Aggregate(limit, _evolution.ReflectionSessionIds());
        var reports = new JsonArray();
        foreach (var report in reportList)
            reports.Add((JsonNode)new JsonObject
            {
                ["fingerprint"] = report.Fingerprint,
                ["kind"] = report.Kind.ToString(),
                ["severity"] = report.Severity,
                ["firstSeen"] = report.FirstSeen.ToString("O"),
                ["lastSeen"] = report.LastSeen.ToString("O"),
                ["occurrences"] = report.Occurrences,
                ["toolName"] = report.ToolName,
                ["errorSummary"] = report.ErrorSummary,
                ["sessionId"] = report.SessionId,
            });
        var health = EvolutionAnalytics.ComputeHealth(reportList);
        var trend = new JsonArray();
        foreach (var point in EvolutionAnalytics.ComputeTrend(_runtime.Database))
            trend.Add((JsonNode)new JsonObject
            {
                ["date"] = point.Date,
                ["toolFailures"] = point.ToolFailures,
                ["gaps"] = point.Gaps,
                ["verificationFailures"] = point.VerificationFailures,
                ["feedback"] = point.Feedback,
                ["corrections"] = point.Corrections,
                ["cancels"] = point.Cancels,
                ["retries"] = point.Retries,
            });
        var distribution = EvolutionAnalytics.ComputeDistribution(reportList);
        var byKind = new JsonObject();
        foreach (var (kind, count) in distribution.ByKind)
            byKind[kind] = count;
        var topTools = new JsonArray();
        foreach (var (tool, count) in distribution.TopTools)
            topTools.Add((JsonNode)new JsonObject { ["tool"] = tool, ["count"] = count });
        var result = new JsonObject
        {
            ["scanned"] = Math.Clamp(limit, 1, EventJournal.RetainedEvents),
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["reports"] = reports,
            ["health"] = new JsonObject { ["score"] = health.Score, ["grade"] = health.Grade },
            ["trend"] = trend,
            ["distribution"] = new JsonObject { ["byKind"] = byKind, ["topTools"] = topTools },
        };
        await WriteAsync(context.Writer, context.WriterGate, id, "result", result.ToJsonString(), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_evolution_reflect(JsonObject request, long id, ConnectionContext context)
    {
        // Evolution engine E2+E3: fire-and-forget reflection pass.
        // The turn runs in the background (it is a full agent turn);
        // completion arrives as an EvolutionReflectionCompletedEvent.
        if (!_reflectionGate.Wait(0))
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error", "反思 turn 已在进行中", context.ConnectionCt, code: DaemonErrorCode.Conflict).ConfigureAwait(false);
return;
        }
        try
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "result",
                new JsonObject { ["started"] = true }.ToJsonString(), context.ConnectionCt).ConfigureAwait(false);
        }
        catch
        {
            _reflectionGate.Release();
            throw;
        }
        _ = Task.Run(async () =>
        {
            try { await _reflection.RunAsync("manual", _shutdown.Token).ConfigureAwait(false); }
            finally { _reflectionGate.Release(); }
        }, _shutdown.Token);
    }

    private async Task Handle_evolution_pending_list(JsonObject request, long id, ConnectionContext context)
    {
        var candidates = new JsonArray();
        foreach (var candidate in _reflection.ListCandidates())
        {
            var files = new JsonArray();
            foreach (var file in candidate.Files ?? Array.Empty<ReflectionRunner.CandidateFile>())
                files.Add((JsonNode)new JsonObject { ["name"] = file.Name, ["content"] = file.Content });
            candidates.Add((JsonNode)new JsonObject
            {
                ["fingerprint"] = candidate.Fingerprint,
                ["kind"] = candidate.Kind,
                ["skillName"] = candidate.SkillName,
                ["candidateDir"] = candidate.CandidateDir,
                ["summary"] = candidate.Summary,
                ["createdAt"] = candidate.CreatedAt.ToString("O"),
                ["status"] = candidate.Status,
                ["files"] = files,
            });
        }
        await WriteAsync(context.Writer, context.WriterGate, id, "result", candidates.ToJsonString(), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_evolution_decide(JsonObject request, long id, ConnectionContext context)
    {
        var parameters = Params(request);
        var fingerprint = parameters["fingerprint"]?.GetValue<string>()?.Trim() ?? "";
        var decision = parameters["decision"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "";
        var promptOverride = parameters["prompt"]?.GetValue<string>()?.Trim();
        if (fingerprint.Length == 0 || decision is not ("adopt" or "reject" or "defer"))
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error",
                "params.fingerprint 与 params.decision（adopt|reject|defer）均为必填", context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
return;
        }
        try
        {
            var record = _reflection.Decide(fingerprint, decision, promptOverride);
            var decided = new JsonObject
            {
                ["fingerprint"] = record.Fingerprint,
                ["status"] = record.Status,
                ["updatedAt"] = record.UpdatedAt.ToString("O"),
            };
            await WriteAsync(context.Writer, context.WriterGate, id, "result", decided.ToJsonString(), context.ConnectionCt).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error", ex.Message, context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
        }
    }

    private async Task Handle_evolution_config_get(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.GetEvolutionConfig()), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_evolution_config_set(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.SetEvolutionConfig(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_evolution_history(JsonObject request, long id, ConnectionContext context)
    {
        var limit = Params(request)["limit"]?.GetValue<int>() ?? 20;
        var runs = new JsonArray();
        foreach (var run in _evolution.ListRuns(limit))
            runs.Add((JsonNode)new JsonObject
            {
                ["id"] = run.Id,
                ["sessionId"] = run.SessionId,
                ["trigger"] = run.Trigger,
                ["processed"] = run.Processed,
                ["candidates"] = run.Candidates,
                ["noAction"] = run.NoAction,
                ["skipped"] = run.Skipped,
                ["failed"] = run.Failed,
                ["error"] = run.Error,
                ["createdAt"] = run.CreatedAt.ToString("O"),
            });
        await WriteAsync(context.Writer, context.WriterGate, id, "result", runs.ToJsonString(), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_evolution_stats(JsonObject request, long id, ConnectionContext context)
    {
        // Effectiveness tracking: adoption rate, per-skill usage and whether
        // the originating defect has resurfaced since adoption.
        var aggregator = new DefectAggregator(_runtime.Database);
        var reportList = aggregator.Aggregate(DefectAggregator.DefaultScanLimit, _evolution.ReflectionSessionIds());
        var stats = EvolutionAnalytics.ComputeStats(_runtime.Database, _evolution, reportList);
        var skills = new JsonArray();
        foreach (var skill in stats.Skills)
            skills.Add((JsonNode)new JsonObject
            {
                ["fingerprint"] = skill.Fingerprint,
                ["skillName"] = skill.SkillName,
                ["adoptedAt"] = skill.AdoptedAt.ToString("O"),
                ["kind"] = skill.Kind,
                ["usageCount"] = skill.UsageCount,
                ["resolved"] = skill.Resolved,
            });
        var result = new JsonObject
        {
            ["runs"] = stats.Runs,
            ["candidatesProduced"] = stats.CandidatesProduced,
            ["adopted"] = stats.Adopted,
            ["rejected"] = stats.Rejected,
            ["deferred"] = stats.Deferred,
            ["noAction"] = stats.NoAction,
            ["failed"] = stats.Failed,
            ["adoptionRate"] = stats.AdoptionRate,
            ["skills"] = skills,
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        await WriteAsync(context.Writer, context.WriterGate, id, "result", result.ToJsonString(), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_factory_reset(JsonObject request, long id, ConnectionContext context)
    {
        // A rebuild drops every table: coordinate with turns on ALL
        // connections, not just the caller's, so live SQLite writers
        // cannot race the schema change.
        await CancelAllTurnsAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.FactoryReset()), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_doctor_run(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _admin.DoctorAsync, context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_lock_list(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.ListLocks()), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_doctor(JsonObject request, long id, ConnectionContext context)
    {
        var checks = _runtime.Health.RunChecks(_runtime.Workspace);
        var summary = string.Join("\n", checks.Select(c => $"{(c.Ok ? "[OK]" : "[FAIL]")} {c.Name}: {c.Detail}"));
        await WriteAsync(context.Writer, context.WriterGate, id, "result", summary, context.ConnectionCt).ConfigureAwait(false);
    }

}
