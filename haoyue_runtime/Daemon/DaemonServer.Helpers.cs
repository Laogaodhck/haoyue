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
    private WorkspaceInfo? TryDetectWorkspace(string root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
            return _runtime.Workspaces.Detect(root);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Appends one mutating step to the turn's crash-journal record and rewrites the
    /// journal file. Called from the agent via <see cref="Agent.MutatingStepObserved"/>
    /// — only for mutating steps, so the write amplification stays proportional to the
    /// number of file changes, not the number of tool calls. Best effort by design.
    /// </summary>
    private void UpdateTurnJournalSteps(string sessionId, TurnStepRecord step)
    {
        if (!_turnJournal.TryGetValue(sessionId, out var record)) return;
        var steps = (record.Steps ?? []).ToList();
        steps.Add(new TurnStepSummary(step.Tool, step.Target, step.Compensable));
        _turnJournal[sessionId] = record with { Steps = steps };
        try
        {
            ActiveTurnJournal.Write(_activeTurnsFile, _turnJournal.Values.ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Journal is an optimization; never fail the turn over it.
        }
    }

    /// <summary>
    /// 全局 ~/.haoyue/config.json 的字段补丁（深合并）：CLI 委派与桌面端「专家参数」
    /// 共用同一通道，因此只有一处写盘入口。调用方必须已持有 _adminGate。
    /// </summary>
    private string SaveGlobalPatch(JsonObject fields)
    {
        _runtime.ConfigStore.ApplyPatch(fields);
        // 日志口径保持不变（DaemonServerTests 钉住这行）：桌面端与 CLI 都是把补丁「委托」
        // 给 daemon 这个唯一写者，措辞不必区分来源。
        _fileLog.Info($"config patch applied by CLI delegate: {string.Join(", ", fields.Select(field => field.Key))}");
        return "saved";
    }

    private string CurrentMode() =>
        AgentModeExtensions.Parse(
            _runtime.Workspace.Config?.Mode ?? _runtime.ConfigStore.Config.Agent.Mode)
        .ToString().ToLowerInvariant();

    private void SaveMode(string mode)
    {
        var config = _runtime.ConfigStore.Config;
        config.Agent.Mode = mode;
        _runtime.ConfigStore.Save();

        if (_runtime.Workspace.Config is not { } workspaceConfig) return;
        workspaceConfig.Mode = mode;
        Directory.CreateDirectory(_runtime.Workspace.HaoyueDir);
        var path = Path.Combine(_runtime.Workspace.HaoyueDir, "config.json");
        Haoyue.Runtime.Data.AtomicFile.WriteAllText(path, JsonSerializer.Serialize(workspaceConfig, HaoyueJsonContext.Default.WorkspaceConfig));
    }

    private static bool TryNormalizeMode(string? rawMode, out string mode)
    {
        mode = rawMode?.Trim().ToLowerInvariant() ?? "";
        return mode is "plan" or "readonly" or "edit" or "auto";
    }

    private static ReasoningLevel ParseReasoningLevel(JsonNode? node, ReasoningLevel fallback)
    {
        if (node is null) return fallback;
        var value = node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
            ? text
            : null;
        if (ReasoningLevelExtensions.TryParse(value, out var level)) return level;
        throw new DaemonRequestException(
            "params.reasoningLevel must be one of: none, low, medium, high, max, xhigh, ultra");
    }

    /// <summary>
    /// expertId: absent → keep the session's current binding; "" → unbind;
    /// otherwise must match an ExpertCatalog id. Returns the resolved id (null = none).
    /// </summary>
    private static string? ParseExpertId(JsonNode? node, string? current)
    {
        if (node is null) return current;
        if (node is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var text))
            throw new DaemonRequestException("params.expertId must be a string (expert id from expert.list)");
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (ExpertCatalog.Find(text) is null)
            throw new DaemonRequestException(
                $"Unknown expert '{text.Trim()}' — run expert.list for valid ids");
        return text.Trim();
    }

    private string WorkspaceJson(WorkspaceInfo workspace)
    {
        var kinds = new JsonArray(workspace.ProjectKinds.Select(kind => JsonValue.Create(kind)).ToArray());
        return new JsonObject
        {
            ["path"] = workspace.Root,
            ["name"] = workspace.Name,
            ["projectKinds"] = kinds,
            ["mode"] = CurrentMode(),
            ["trusted"] = workspace.Trusted,
            ["hasWorkspaceConfig"] = workspace.HasWorkspaceConfig,
            ["hasWorkspaceMcp"] = workspace.HasWorkspaceMcp,
        }.ToJsonString();
    }

    /// <summary>
    /// Cancels every active turn across all connections and waits (bounded) for them
    /// to finish. Used by administration operations that mutate global state — factory
    /// reset and database rebuild drop SQLite tables, which must not race live writers.
    /// </summary>
    private async Task CancelAllTurnsAsync(TimeSpan timeout)
    {
        var turns = new List<ActiveTurn>();
        foreach (var connection in _connections.Values)
        {
            foreach (var turn in connection.ActiveTurns.Values)
            {
                turn.Cancellation.Cancel();
                turns.Add(turn);
            }
        }
        if (turns.Count == 0) return;
        try
        {
            await Task.WhenAll(turns.Select(turn => turn.Task ?? Task.CompletedTask))
                .WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Timeout or a failing turn must not block the reset; exceptions are
            // observed (and swallowed) below.
        }
        foreach (var turn in turns)
            if (turn.Task is not null) await ObserveAsync(turn.Task).ConfigureAwait(false);
    }

    private async Task RunAdminAsync(
        StreamWriter writer,
        SemaphoreSlim writerGate,
        long id,
        bool exclusive,
        Func<CancellationToken, Task<string>> action,
        CancellationToken ct)
    {
        if (exclusive) await _adminGate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var data = await action(ct).ConfigureAwait(false);
            await WriteAsync(writer, writerGate, id, "result", data, ct).ConfigureAwait(false);
        }
        catch (DaemonRequestException ex)
        {
            await WriteAsync(writer, writerGate, id, "error", ex.Message, ct, code: ex.Code).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            await WriteAsync(writer, writerGate, id, "error", $"Invalid request: {ex.Message}", ct, code: DaemonErrorCode.InvalidRequest).ConfigureAwait(false);
        }
        // 其余异常直接上抛给分发层：由它统一回 internalError 并带上方法名，
        // 避免「同一个故障在两条路径上产出两种错误信封」。
        finally
        {
            if (exclusive) _adminGate.Release();
        }
    }

    private static bool SessionBelongsTo(AgentSession session, WorkspaceInfo workspace) =>
        workspace.IsGlobal
            ? string.IsNullOrWhiteSpace(session.Header.Workspace)
            : string.Equals(session.Header.Workspace, workspace.Root, StringComparison.OrdinalIgnoreCase);
}
