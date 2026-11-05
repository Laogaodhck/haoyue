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
    private async Task RunTurnAsync(
        AgentSession session,
        WorkspaceInfo workspace,
        string message,
        IReadOnlyList<ChatImageAttachment> images,
        ReasoningLevel reasoningLevel,
        long id,
        StreamWriter writer,
        SemaphoreSlim writerGate,
        CancellationToken turnCt,
        CancellationToken connectionCt,
        AgentSteeringQueue steering)
    {
        // 回合身份 = 客户端请求 id：本回合在事件总线上发布的一切（含 SQLite 台账行）
        // 都带同一个 TurnId，events.recent 回放与实时帧的 id 字段因此可按回合归组。
        // Agent.RunTurnAsync 内部的 BeginScope 检测到已有身份时为空操作，不会覆盖。
        using var turnIdentity = TurnStamp.BeginScope(id.ToString());

        // Unique per-turn identity for file write-lock ownership; released when the
        // turn ends even if a tool was interrupted before its own finally ran.
        var owner = $"{session.Header.Id}/{Guid.NewGuid().ToString("N")[..8]}";
        await using var turnRuntime = _useIsolatedTurnRuntime
            ? HaoyueRuntime.CreateIsolated(workspace, _fileLocks, owner, services =>
              {
                  // Register the process-wide instances AFTER the default type
                  // registrations so DI resolves these for every turn.
                  services.AddSingleton<ILlmHttpFactory>(_sharedHttp);
                  services.AddSingleton(_sharedBreaker);
                  services.AddSingleton(_sharedLocalModels);
                  services.AddSingleton(_undoRegistry);
                  services.AddSingleton(_approvals);
              })
            : null;
        var runtime = turnRuntime ?? _runtime;
        using var subscription = runtime.Events.Subscribe();
        var sessionUsage = new SessionUsageAccumulator();
        var forwarder = ForwardEventsAsync(
            subscription, writer, writerGate, id, session.Header.Id, connectionCt, sessionUsage);
        AgentTurnResult? result = null;
        Exception? failure = null;

        try
        {
            runtime.Prompts.SetWorkspaceRoot(workspace.IsGlobal ? null : workspace.PromptsDir);
            runtime.Skills.Attach(workspace);
            if (_useIsolatedTurnRuntime && runtime.Mcp.LoadServerConfigs(workspace).Count > 0)
            {
                // MCP 连接是回合启动的主要慢环节（stdio 服务器进程冷启动 + 握手，即使
                // 并行连接也可能持续数十秒）。把连接进度发布为 status 事件，前端在回合
                // 受理帧之后能看到具体阶段，而不是一直停在"正在准备"。
                var progress = new Progress<string>(text => runtime.Events.Publish(new StatusEvent(text)));
                await runtime.Mcp.ConnectAllAsync(workspace, turnCt, progress).ConfigureAwait(false);
            }

            // Crash-reconciliation hook: mutating steps update the in-memory journal
            // record (and rewrite the crash file) so a process death leaves not just a
            // "was mid-turn" marker but an executed-steps + changed-files ledger.
            if (_useIsolatedTurnRuntime)
            {
                runtime.Agent.MutatingStepObserved = stepRecord =>
                    UpdateTurnJournalSteps(session.Header.Id, stepRecord);
            }

            result = _runTurn is null
                ? await runtime.Agent.RunTurnAsync(
                    session, workspace, message, turnCt, reasoningLevel, images, steering).ConfigureAwait(false)
                : await _runTurn(session, workspace, message, turnCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (turnCt.IsCancellationRequested)
        {
            result = new AgentTurnResult("", true, null);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            subscription.Dispose();
            try { await forwarder.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            if (sessionUsage.HasActivity)
            {
                try
                {
                    var persisted = _runtime.Sessions.RecordUsage(workspace, session.Header.Id, sessionUsage.ToUsage());
                    session.Header.LlmRounds = persisted.LlmRounds;
                    session.Header.ExecutionSteps = persisted.ExecutionSteps;
                    session.Header.InputTokens = persisted.InputTokens;
                    session.Header.TotalInputTokens = persisted.TotalInputTokens;
                    session.Header.CachedInputTokens = persisted.CachedInputTokens;
                    session.Header.OutputTokens = persisted.OutputTokens;
                    session.Header.OutputElapsedMs = persisted.OutputElapsedMs;
                    session.Header.UpdatedAt = persisted.UpdatedAt;
                }
                catch
                {
                    // Usage persistence must not fail the turn.
                }
            }
            _fileLocks.ReleaseAll(owner);
        }

        try
        {
            // Surface the turn's revertible file list on the terminal envelope so the
            // client can offer a one-click undo (agent.undo) without another RPC.
            JsonObject? terminalDetails = null;
            var undoable = runtime.UndoRegistry.Peek(session.Header.Id);
            if (undoable is { Changes.Count: > 0 })
            {
                terminalDetails = new JsonObject
                {
                    ["undoableFiles"] = new JsonArray(undoable.Changes
                        .Select(c => JsonValue.Create(
                            c.AbsolutePath.StartsWith(workspace.Root, StringComparison.OrdinalIgnoreCase)
                                ? c.AbsolutePath[workspace.Root.Length..].TrimStart('/', '\\')
                                : c.AbsolutePath))
                        .ToArray<JsonNode?>()),
                };
            }

            if (failure is not null)
            {
                // 回合失败的终态 error 也必须携带契约错误码（details 已有 undoableFiles 时合并）。
                terminalDetails ??= new JsonObject();
                terminalDetails["code"] = DaemonErrorCode.InternalError.ToWire();
            }

            // 回合终态随帧下发：done/cancelled/error 三态之外，「模型没答完」（截断、拒答、
            // 连续空回答、步数或预算烧光）过去一律报 done，界面把半截回答当完整回答显示。
            terminalDetails ??= new JsonObject();
            terminalDetails["finishReason"] = failure is not null
                ? TurnFinishReason.Error
                : result?.Cancelled == true
                    ? TurnFinishReason.Cancelled
                    : result?.FinishReason is { Length: > 0 } outcome ? outcome : TurnFinishReason.Stop;

            if (failure is not null)
                await WriteAsync(writer, writerGate, id, "error", failure.Message, connectionCt, session.Header.Id, terminalDetails).ConfigureAwait(false);
            else if (result!.Cancelled)
                await WriteAsync(writer, writerGate, id, "cancelled", result.Text, connectionCt, session.Header.Id, terminalDetails).ConfigureAwait(false);
            else
                await WriteAsync(writer, writerGate, id,
                    result.Error is null ? "done" : "error",
                    result.Error ?? result.Text, connectionCt, session.Header.Id, terminalDetails).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The client disconnected while the turn was ending.
        }
    }

    private WorkspaceInfo ResolveWorkspace(JsonObject parameters)
    {
        if (parameters["global"]?.GetValue<bool?>() == true)
            return _globalWorkspace;

        var path = parameters["workspace"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(path)) return _runtime.Workspace;

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DaemonRequestException($"Invalid workspace path: {ex.Message}");
        }

        if (!Directory.Exists(fullPath))
            throw new DaemonRequestException($"Workspace directory not found: {fullPath}");
        return _runtime.Workspaces.Detect(fullPath);
    }

    private static IReadOnlyList<ChatImageAttachment> ParseImages(JsonNode? node)
    {
        if (node is null) return [];
        if (node is not JsonArray array)
            throw new DaemonRequestException("params.images must be an array");
        if (array.Count > MaxImageCount)
            throw new DaemonRequestException($"A turn supports at most {MaxImageCount} images");

        var images = new List<ChatImageAttachment>(array.Count);
        long totalBytes = 0;
        foreach (var item in array)
        {
            if (item is not JsonObject image)
                throw new DaemonRequestException("Each params.images item must be an object");
            var id = image["id"]?.GetValue<string>()?.Trim();
            var name = image["name"]?.GetValue<string>()?.Trim();
            var mediaType = image["mediaType"]?.GetValue<string>()?.Trim().ToLowerInvariant();
            var data = image["data"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(data))
                throw new DaemonRequestException("Each image requires name and base64 data");
            if (mediaType is null || !SupportedImageTypes.Contains(mediaType))
                throw new DaemonRequestException($"Unsupported image type for {name}: {mediaType ?? "unknown"}");
            if (data.Length > ((MaxImageBytes + 2L) / 3L * 4L) + 4L)
                throw new DaemonRequestException($"Image {name} exceeds the {MaxImageBytes / 1024 / 1024} MB limit");

            byte[] decoded;
            try { decoded = Convert.FromBase64String(data); }
            catch (FormatException)
            {
                throw new DaemonRequestException($"Image {name} contains invalid base64 data");
            }
            if (decoded.Length > MaxImageBytes)
                throw new DaemonRequestException($"Image {name} exceeds the {MaxImageBytes / 1024 / 1024} MB limit");
            totalBytes += decoded.Length;
            if (totalBytes > MaxTotalImageBytes)
                throw new DaemonRequestException(
                    $"Images exceed the {MaxTotalImageBytes / 1024 / 1024} MB total limit");

            var safeName = new string(name.Replace('\\', '/').Split('/').Last()
                .Where(character => !char.IsControl(character)).Take(180).ToArray());
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "image";
            images.Add(new ChatImageAttachment(
                string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id[..Math.Min(id.Length, 128)],
                safeName,
                mediaType,
                data,
                decoded.Length));
        }
        return images;
    }

    private AgentSession LoadTurnSession(
        WorkspaceInfo workspace,
        string? requestedSessionId,
        ref AgentSession? legacySession)
    {
        if (!string.IsNullOrWhiteSpace(requestedSessionId))
        {
            var loaded = _runtime.Sessions.Load(workspace, requestedSessionId);
            return loaded ?? throw new DaemonRequestException($"Session {requestedSessionId} not found");
        }

        if (legacySession is not null && SessionBelongsTo(legacySession, workspace))
            return legacySession;

        legacySession = _runtime.Sessions.LoadLatest(workspace)
                        ?? _runtime.Sessions.Create(workspace);
        return legacySession;
    }

    private sealed class ActiveTurn(
        AgentSession session,
        WorkspaceInfo workspace,
        CancellationTokenSource cancellation)
    {
        public AgentSession Session { get; } = session;
        public WorkspaceInfo Workspace { get; } = workspace;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public AgentSteeringQueue Steering { get; } = new();
        public Task? Task { get; set; }
    }

    /// <summary>
    /// Turn-resume semantics after a daemon crash: any session still recorded in the
    /// crash journal was mid-turn when the process died. Best effort only — the
    /// session gets an interruption notice appended to its history and every client
    /// receives a <c>turn.interrupted</c> broadcast; the turn is never re-run
    /// automatically because half-executed tools make a blind retry unsafe. When the
    /// journal carries a mutating-step ledger, the notice lists what may have changed.
    /// </summary>
    internal async Task RecoverInterruptedTurnsAsync(CancellationToken ct)
    {
        var records = ActiveTurnJournal.Read(_activeTurnsFile);
        if (records.Count == 0) return;

        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var workspace = record.IsGlobal
                    ? _runtime.Workspaces.CreateGlobal()
                    : TryDetectWorkspace(record.WorkspaceRoot);
                if (workspace is not null)
                {
                    var session = _runtime.Sessions.Load(workspace, record.SessionId);
                    if (session is not null)
                    {
                        var notice = ">>> [turn interrupted] 上次回合因 Runtime 异常退出而中断，上方可能缺少本轮的最终回答。可重新发送消息或要求重试。";
                        if (record.Steps is { Count: > 0 })
                        {
                            // Reconciliation, not re-run: list what the interrupted turn
                            // touched so the user can verify the workspace against the
                            // conversation record. Builtin file tools are revertible via
                            // agent.undo if the daemon deposited a ledger — but after a
                            // crash the ledger is gone, so only the paths are shown.
                            var lines = record.Steps.Select(s =>
                                $"  - {s.Tool} → {s.Target}{(s.Compensable ? "" : "（不可自动恢复）")}");
                            notice += $"\n本轮已执行到以下修改步骤（共 {record.Steps.Count} 项），请核对这些文件的当前状态：\n{string.Join("\n", lines)}";
                        }
                        _runtime.Sessions.Append(session, ChatMessage.User(notice));
                    }
                }
            }
            catch
            {
                // Recovery is best effort: an unloadable workspace or session is skipped.
            }

            var details = new JsonObject
            {
                ["sessionId"] = record.SessionId,
                ["reason"] = "daemon_crash",
            };
            lock (_interruptedTurnsGate) _interruptedTurns.Add(record.SessionId);
            try
            {
                await BroadcastAsync(0, "turn.interrupted", "上次回合被 Runtime 重启中断", ct, details)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Clients are not connected yet (typical at startup) or are vanishing.
            }
        }

        ActiveTurnJournal.Clear(_activeTurnsFile);
    }
}
