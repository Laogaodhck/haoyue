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
    private async Task Handle_feedback_turn(JsonObject request, long id, ConnectionContext context)
    {
        // P4: explicit user feedback on a finished turn. Negative feedback
        // is journaled and becomes a defect report for the next reflection.
        var parameters = Params(request);
        var sessionId = parameters["sessionId"]?.GetValue<string>()?.Trim() ?? "";
        if (sessionId.Length == 0)
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error", "params.sessionId 为必填", context.ConnectionCt).ConfigureAwait(false);
return;
        }
        var kind = parameters["kind"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "negative";
        var reason = parameters["reason"]?.GetValue<string>()?.Trim();
        _runtime.Events.Publish(new UserFeedbackEvent(sessionId, kind, reason));
        await WriteAsync(context.Writer, context.WriterGate, id, "result",
            new JsonObject { ["recorded"] = true }.ToJsonString(), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_expert_list(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.ListExperts()), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_knowledge_list(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.ListKnowledge(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_knowledge_search(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            ct => _admin.SearchKnowledge(Params(request), ct), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_knowledge_save(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.SaveKnowledge(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_knowledge_delete(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.DeleteKnowledge(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_knowledge_import(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.ImportKnowledge(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_knowledge_tags(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.KnowledgeTags(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_knowledge_export(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.ExportKnowledge(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_knowledge_synonyms_get(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.GetKnowledgeSynonyms()), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_knowledge_synonyms_save(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.SaveKnowledgeSynonyms(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_knowledge_notebook_list(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.ListKnowledgeNotebooks(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_knowledge_notebook_save(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.SaveKnowledgeNotebook(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_knowledge_notebook_delete(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.DeleteKnowledgeNotebook(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_knowledge_source_add(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            ct => _admin.AddKnowledgeSource(Params(request), ct), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_knowledge_source_list(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.ListKnowledgeSources(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_knowledge_source_read(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.ReadKnowledgeSource(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_knowledge_source_delete(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.DeleteKnowledgeSource(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_knowledge_source_refresh(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            ct => _admin.RefreshKnowledgeSource(Params(request), ct), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_knowledge_retrieve(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            ct => _admin.RetrieveKnowledge(Params(request), ct), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_knowledge_semantic_status(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.SemanticStatus(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_knowledge_semantic_build(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            ct => _admin.BuildSemanticIndex(Params(request), ct), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_memory_get(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.GetMemory(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_memory_save(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.SaveMemory(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_rules_list(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.ListRules(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_rules_save(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.SaveRules(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_rules_delete(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.DeleteRules(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_project_list(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.ListProjects()), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_project_upsert(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.UpsertProject(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_project_remove(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.RemoveProject(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

}
