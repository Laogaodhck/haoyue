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
    private async Task Handle_schedule_list(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.ListSchedules()), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_schedule_update(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.UpsertSchedule(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_schedule_toggle(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.ToggleSchedule(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_schedule_delete(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.DeleteSchedule(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_schedule_run(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.RunSchedule(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_task_start(JsonObject request, long id, ConnectionContext context)
    {
        var parameters = Params(request);
        var prompt = parameters["message"]?.GetValue<string>()
                     ?? parameters["prompt"]?.GetValue<string>()
                     ?? "";
        if (string.IsNullOrWhiteSpace(prompt))
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error",
                "params.message is required", context.ConnectionCt,
                code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
return;
        }
        WorkspaceInfo taskWorkspace;
        try
        {
            taskWorkspace = ResolveWorkspace(parameters);
        }
        catch (DaemonRequestException ex)
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error", ex.Message,
                context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
return;
        }
        BackgroundTaskInfo task;
        try
        {
            task = _backgroundTasks.Start(
                taskWorkspace, prompt, parameters["title"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
                                       or IOException or UnauthorizedAccessException)
        {
            // 任务目录无法安全落在项目内时拒绝受理，而不是退回全局目录。
            await WriteAsync(context.Writer, context.WriterGate, id, "error", ex.Message,
                context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
            return;
        }
        await WriteAsync(context.Writer, context.WriterGate, id, "result",
            JsonSerializer.Serialize(task, HaoyueJsonContext.Compact.BackgroundTaskInfo),
            context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_task_get(JsonObject request, long id, ConnectionContext context)
    {
        var taskId = Params(request)["id"]?.GetValue<string>();
        var task = taskId is null ? null : _backgroundTasks.Get(taskId);
        if (task is null)
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error",
                $"Background task not found: {taskId}", context.ConnectionCt,
                code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
return;
        }
        await WriteAsync(context.Writer, context.WriterGate, id, "result",
            JsonSerializer.Serialize(task, HaoyueJsonContext.Compact.BackgroundTaskInfo),
            context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_task_list(JsonObject request, long id, ConnectionContext context)
    {
        await WriteAsync(context.Writer, context.WriterGate, id, "result",
            JsonSerializer.Serialize(_backgroundTasks.List(), HaoyueJsonContext.Compact.ListBackgroundTaskInfo),
            context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_task_cancel(JsonObject request, long id, ConnectionContext context)
    {
        var taskId = Params(request)["id"]?.GetValue<string>();
        var cancelled = taskId is null ? null : _backgroundTasks.Cancel(taskId);
        if (cancelled is null)
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error",
                $"Background task not found or already finished: {taskId}",
                context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
return;
        }
        await WriteAsync(context.Writer, context.WriterGate, id, "result",
            JsonSerializer.Serialize(cancelled, HaoyueJsonContext.Compact.BackgroundTaskInfo),
            context.ConnectionCt).ConfigureAwait(false);
    }

}
