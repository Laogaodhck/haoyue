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
    private async Task Handle_speech_transcribe(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            ct => _admin.TranscribeAsync(Params(request), ct), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_speech_synthesize(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            ct => _admin.SynthesizeAsync(Params(request), ct), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_routing_get(JsonObject request, long id, ConnectionContext context)
    {
        await WriteAsync(context.Writer, context.WriterGate, id, "result", _admin.GetRoutingConfig(), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_routing_set(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.SetRoutingConfig(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_prompt_optimize(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            token => _admin.OptimizePromptAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_provider_list(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.ListProviders()), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_provider_upsert(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.UpsertProvider(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_provider_use(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.UseProvider(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_provider_remove(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => Task.FromResult(_admin.RemoveProvider(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_provider_test(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            token => _admin.TestProvidersAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_provider_models_fetch(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            token => _admin.FetchProviderModelsAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_local_models(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.ScanLocalModels(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_model_catalog(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.ModelCatalog()), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_model_test(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            token => _admin.TestModelAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_model_status(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.ModelStatus(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_model_preload(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            token => _admin.PreloadModelAsync(Params(request), LocalLoadProgress(), token),
            context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_model_unload(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            token => _admin.UnloadLocalModelsAsync(token), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_model_update(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
            _ => _admin.UpdateModel(Params(request)), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_model_config_get(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.GetModelConfig(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_model_benchmark(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            token => _admin.BenchmarkModelAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_usage_get(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.Usage(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_usage_timeline(JsonObject request, long id, ConnectionContext context)
    {
        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
            _ => Task.FromResult(_admin.UsageTimeline(Params(request))), context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_model_list(JsonObject request, long id, ConnectionContext context)
    {
        var models = _runtime.Models.All().Select(m => m.Ref).ToList();
        var json = JsonSerializer.Serialize(models, HaoyueJsonContext.Default.ListString);
        await WriteAsync(context.Writer, context.WriterGate, id, "result", json, context.ConnectionCt).ConfigureAwait(false);
    }

    private async Task Handle_model_switch(JsonObject request, long id, ConnectionContext context)
    {
        var modelRef = request["params"]?["model"]?.GetValue<string>();
        if (string.IsNullOrEmpty(modelRef))
        {
            await WriteAsync(context.Writer, context.WriterGate, id, "error", "params.model is required", context.ConnectionCt, code: DaemonErrorCode.InvalidParams).ConfigureAwait(false);
return;
        }
        await _adminGate.WaitAsync(context.ConnectionCt).ConfigureAwait(false);

        try
        {
            var model = _runtime.Models.Resolve(modelRef);
            if (model is null)
                await WriteAsync(context.Writer, context.WriterGate, id, "error", $"Unknown model {modelRef}", context.ConnectionCt, code: DaemonErrorCode.NotFound).ConfigureAwait(false);
            else
            {
                var config = _runtime.ConfigStore.Config;
                config.Provider = model.Provider.Id;
                config.Model = model.Model.Id;
                _runtime.ConfigStore.Save();
                await WriteAsync(context.Writer, context.WriterGate, id, "result", $"switched to {model.Ref}", context.ConnectionCt).ConfigureAwait(false);
            }
        }
        finally
        {
            _adminGate.Release();
        }
    }

}
