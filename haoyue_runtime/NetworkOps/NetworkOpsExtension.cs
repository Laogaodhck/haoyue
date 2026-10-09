namespace Haoyue.Runtime.NetworkOps;

using Haoyue.Runtime.Extensions;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;

/// <summary>
/// Runtime extension providing Network Ops tools (network_diagnose / network_cmd).
/// Implements IRuntimeExtension so core runtime has zero static coupling to the
/// plugin; discovered by ExtensionManager via reflection like Computer Use.
/// </summary>
public sealed class NetworkOpsExtension : IRuntimeExtension
{
    private IDisposable? _promptRegistration;
    private readonly List<IDisposable> _toolRegistrations = [];

    public string Id => "network_ops";
    public string Name => "Network Operations";

    public bool IsEnabled(HaoyueRuntime runtime) =>
        runtime.ConfigStore.Config.NetworkOps?.Enabled ?? false;

    public void Initialize(HaoyueRuntime runtime)
    {
        // Stateless: the adapter is created per RegisterTools call; nothing to hold.
    }

    public void RegisterTools(IToolRegistry registry, HaoyueRuntime runtime)
    {
        var config = runtime.ConfigStore.Config.NetworkOps!;
        var adapter = NetworkOpsAdapterFactory.Create();

        _promptRegistration = runtime.PromptRegistry.Register(new PromptContribution(
            "network_ops", PromptSlot.Memory, (_, _) =>
                ValueTask.FromResult<string?>(runtime.Prompts.TryGet("builtin/network_ops")), DegradeRank: 40));

        _toolRegistrations.Add(registry.Register(new NetworkDiagnoseTool(runtime.Prompts, adapter)));
        _toolRegistrations.Add(registry.Register(new NetworkCommandTool(runtime.Prompts, adapter, config)));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var registration in _toolRegistrations)
        {
            try { registration.Dispose(); } catch { }
        }
        _toolRegistrations.Clear();

        _promptRegistration?.Dispose();
        _promptRegistration = null;
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}
