namespace Haoyue.Runtime.ComputerUse;

/// <summary>
/// Modular lifecycle and registration facade for the Computer Use subsystem.
/// Enables completely decoupled plugging and unplugging without leaving residue in core runtime.
/// </summary>
public static class ComputerUseModule
{
    private static IComputerDriver? _activeDriver;

    /// <summary>
    /// Registers computer use tools if enabled in configuration.
    /// If disabled (default), performs zero work, registers nothing, and has zero runtime footprint.
    /// </summary>
    public static void Initialize(HaoyueRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var config = runtime.ConfigStore.Config.ComputerUse;
        if (config is null || !config.Enabled)
        {
            return;
        }

        var driver = ComputerDriverFactory.CreateDriver(config);
        _activeDriver = driver;

        runtime.Tools.Register(new ComputerInspectTool(runtime.Prompts, driver));
        runtime.Tools.Register(new ComputerTool(runtime.Prompts, driver, config));

        if (config.ShellEnabled)
            runtime.Tools.Register(new ComputerExecTool(runtime.Prompts, config));

        if (config.SystemControlEnabled)
        {
            var adapter = SystemControl.SystemControlFactory.Create();
            runtime.Tools.Register(new SystemControl.SystemScanTool(runtime.Prompts, adapter));
            runtime.Tools.Register(new SystemControl.SystemInfoTool(runtime.Prompts, adapter, config));
            runtime.Tools.Register(new SystemControl.BrowserRepairTool(runtime.Prompts, adapter));
        }

    }

    /// <summary>
    /// Gracefully releases any active driver resources.
    /// </summary>
    public static async ValueTask DisposeAsync()
    {
        if (_activeDriver is not null)
        {
            var driver = _activeDriver;
            _activeDriver = null;
            await driver.DisposeAsync().ConfigureAwait(false);
        }
    }
}
