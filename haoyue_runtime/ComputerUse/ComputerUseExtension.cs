namespace Haoyue.Runtime.ComputerUse;

using System;
using System.Threading.Tasks;
using Haoyue.Runtime.Extensions;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;

/// <summary>
/// Runtime extension providing Computer Use tools and capabilities.
/// Implements IRuntimeExtension so core runtime has zero static coupling to Computer Use.
/// </summary>
public sealed class ComputerUseExtension : IRuntimeExtension
{
    private IComputerDriver? _activeDriver;
    private IDisposable? _promptRegistration;

    public string Id => "computer_use";
    public string Name => "Computer Use Subsystem";

    public bool IsEnabled(HaoyueRuntime runtime)
    {
        var config = runtime.ConfigStore.Config.ComputerUse;
        return config is not null && config.Enabled;
    }

    public void Initialize(HaoyueRuntime runtime)
    {
        var config = runtime.ConfigStore.Config.ComputerUse;
        if (config is null || !config.Enabled) return;

        _activeDriver = ComputerDriverFactory.CreateDriver(config);
    }

    public void RegisterTools(IToolRegistry registry, HaoyueRuntime runtime)
    {
        if (_activeDriver is null) return;
        var config = runtime.ConfigStore.Config.ComputerUse!;

        // Behavioral contract for operating the computer (observe → act → verify,
        // safety rules). Registered only while the extension is enabled.
        var prompts = runtime.Prompts;
        _promptRegistration = runtime.PromptRegistry.Register(new PromptContribution(
            "computer_use", PromptSlot.Memory, (_, _) =>
                ValueTask.FromResult<string?>(prompts.TryGet("builtin/computer"))));

        registry.Register(new ComputerInspectTool(runtime.Prompts, _activeDriver));
        registry.Register(new ComputerTool(runtime.Prompts, _activeDriver, config));

        if (config.ShellEnabled)
            registry.Register(new ComputerExecTool(runtime.Prompts, config));
    }

    public async ValueTask DisposeAsync()
    {
        _promptRegistration?.Dispose();
        _promptRegistration = null;

        if (_activeDriver is not null)
        {
            var driver = _activeDriver;
            _activeDriver = null;
            await driver.DisposeAsync().ConfigureAwait(false);
        }
    }
}
