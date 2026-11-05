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
    /// <summary>
    /// Tells every connected client that a background MCP reconnect finished, so the
    /// server list can show real connection results without the client polling.
    /// </summary>
    private async Task BroadcastMcpStatusAsync()
    {
        try
        {
            await BroadcastAsync(0, "mcp.updated", "", _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The daemon is shutting down or a client vanished mid-broadcast.
        }
    }

    private async Task BroadcastHostEventsAsync(ChannelReader<RuntimeEvent> reader, CancellationToken ct)
    {
        await foreach (var evt in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            // 告警/错误先落 daemon.log：无桌面场景（CLI / 独立 daemon）这是唯一的
            // 持久证据；有桌面时与 stdout 旁路、事件台账三处互补。
            switch (evt)
            {
                case WarningEvent warning:
                    _fileLog.Warn(warning.Message);
                    break;
                case ErrorEvent error:
                    _fileLog.Error(error.Detail is { } detail
                        ? $"{error.Message} | {detail}"
                        : error.Message);
                    break;
            }
            if (evt is ScheduledTaskUpcomingEvent upcoming)
            {
                var upcomingDetails = new JsonObject
                {
                    ["taskId"] = upcoming.TaskId,
                    ["name"] = upcoming.Name,
                    ["runAt"] = upcoming.RunAt.ToString("O"),
                };
                await BroadcastAsync(0, "schedule.upcoming", upcoming.Name, ct, upcomingDetails)
                    .ConfigureAwait(false);
                continue;
            }
            // 定时任务的审批请求由 ScheduleService 从隔离回合中继到宿主总线；
            // 广播出去，已连接客户端才有机会批准（无人值守不再静默放行）。
            if (evt is ApprovalRequiredEvent approval)
            {
                var approvalDetails = new JsonObject
                {
                    ["callId"] = approval.CallId,
                    ["tool"] = approval.ToolName,
                    ["summary"] = approval.Summary,
                    ["reason"] = approval.Reason,
                };
                await BroadcastAsync(0, "approval_request", approval.ToolName, ct, approvalDetails)
                    .ConfigureAwait(false);
                continue;
            }
            if (evt is EvolutionReflectionCompletedEvent reflected)
            {
                // Surface reflection outcomes so the desktop can raise its candidate
                // review banner even when the reflection ran unattended (cron/manual).
                var reflectedDetails = new JsonObject
                {
                    ["sessionId"] = reflected.SessionId,
                    ["processed"] = reflected.Processed,
                    ["candidates"] = reflected.Candidates,
                    ["noAction"] = reflected.NoAction,
                    ["skipped"] = reflected.Skipped,
                };
                if (!string.IsNullOrWhiteSpace(reflected.Error)) reflectedDetails["error"] = reflected.Error;
                await BroadcastAsync(0, "evolution.reflected", "进化引擎反思完成", ct, reflectedDetails)
                    .ConfigureAwait(false);
                continue;
            }

            if (evt is MemoryConsolidationCompletedEvent consolidated)
            {
                var consolidatedDetails = new JsonObject
                {
                    ["sessionId"] = consolidated.SessionId,
                    ["sessionsScanned"] = consolidated.SessionsScanned,
                    ["factsAdded"] = consolidated.FactsAdded,
                    ["factsSuperseded"] = consolidated.FactsSuperseded,
                };
                if (!string.IsNullOrWhiteSpace(consolidated.Error)) consolidatedDetails["error"] = consolidated.Error;
                consolidatedDetails["memoryPromoted"] = consolidated.MemoryPromoted;
                if (!string.IsNullOrWhiteSpace(consolidated.MemoryNote))
                    consolidatedDetails["memoryNote"] = consolidated.MemoryNote;
                await BroadcastAsync(0, "memory.consolidated", "记忆固化完成", ct, consolidatedDetails)
                    .ConfigureAwait(false);
                continue;
            }

            if (evt is ModelLoadProgressEvent loadProgress)
            {
                // 本地模型加载进度（预热 / model.preload / 首请求加载）：桌面端加载条。
                var progressDetails = new JsonObject
                {
                    ["model"] = loadProgress.ModelRef,
                    ["phase"] = loadProgress.Phase,
                    ["percent"] = Math.Round(loadProgress.Percent, 4),
                    ["elapsedMs"] = loadProgress.ElapsedMs,
                };
                if (loadProgress.EstimatedRemainingMs is { } remaining) progressDetails["estimatedRemainingMs"] = remaining;
                if (!string.IsNullOrWhiteSpace(loadProgress.Message)) progressDetails["message"] = loadProgress.Message;
                await BroadcastAsync(0, "model.load.progress", "本地模型加载进度", ct, progressDetails)
                    .ConfigureAwait(false);
                continue;
            }
            if (evt is BackgroundTaskEvent backgroundTask)
            {
                var bgDetails = new JsonObject
                {
                    ["taskId"] = backgroundTask.TaskId,
                    ["status"] = backgroundTask.Status,
                };
                if (!string.IsNullOrWhiteSpace(backgroundTask.SessionId)) bgDetails["sessionId"] = backgroundTask.SessionId;
                if (!string.IsNullOrWhiteSpace(backgroundTask.Error)) bgDetails["error"] = backgroundTask.Error;
                await BroadcastAsync(0, "task.updated", backgroundTask.TaskId, ct, bgDetails)
                    .ConfigureAwait(false);
                continue;
            }
            if (evt is TeamRunUpdatedEvent teamRun)
            {
                await BroadcastAsync(0, "team.run.updated", teamRun.RunId, ct, teamRun.ToDetails())
                    .ConfigureAwait(false);
                continue;
            }
            if (evt is not ScheduledTaskCompletedEvent schedule) continue;
            var details = new JsonObject
            {
                ["taskId"] = schedule.TaskId,
                ["name"] = schedule.Name,
                ["status"] = schedule.Status,
            };
            if (!string.IsNullOrWhiteSpace(schedule.SessionId)) details["sessionId"] = schedule.SessionId;
            if (!string.IsNullOrWhiteSpace(schedule.Error)) details["error"] = schedule.Error;
            await BroadcastAsync(0, "schedule.updated", schedule.TaskId, ct, details).ConfigureAwait(false);
        }
    }

    private async Task BroadcastAsync(
        long id,
        string eventName,
        string data,
        CancellationToken ct,
        JsonObject? details = null)
    {
        List<ClientSink> clients;
        lock (_clientsGate) clients = _clients.Values.ToList();
        foreach (var client in clients)
        {
            try
            {
                await WriteAsync(client.Writer, client.WriterGate, id, eventName, data, ct, details: details)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // The client disconnected while the broadcast was in flight.
            }
            catch (Exception ex)
            {
                // 事件泵不能因为单个客户端写失败而结束：泵一死，异常会在 DisposeAsync
                // 处重掀掉 daemon，用户侧就是「发消息没有回复」。
                Console.Error.WriteLine($"Daemon broadcast failed ({eventName}): {ex.Message}");
            }
        }
    }

    private long RegisterClient(StreamWriter writer, SemaphoreSlim writerGate)
    {
        var clientId = Interlocked.Increment(ref _nextClientId);
        lock (_clientsGate) _clients[clientId] = new ClientSink(writer, writerGate);
        return clientId;
    }

    private void UnregisterClient(long clientId)
    {
        lock (_clientsGate) _clients.Remove(clientId);
    }

    private sealed record ClientSink(StreamWriter Writer, SemaphoreSlim WriterGate);
}
