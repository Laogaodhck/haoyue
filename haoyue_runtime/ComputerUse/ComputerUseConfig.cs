namespace Haoyue.Runtime.ComputerUse;

/// <summary>
/// Configuration for Computer Use subsystem.
/// Can be independently toggled on/off without affecting normal Haoyue operations.
/// </summary>
public sealed class ComputerUseConfig
{
    /// <summary>Whether Computer Use tools are registered and enabled.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Driver selection: "auto", "windows", "linux", "mac", or "vision". Default "auto".</summary>
    public string Driver { get; set; } = "auto";

    /// <summary>Whether computer_exec (PowerShell / cmd / Python) is registered alongside the Computer Use tools.</summary>
    public bool ShellEnabled { get; set; } = true;

    /// <summary>Whether system-control tools (computer_scan / computer_sysinfo / computer_browser_repair) are registered.</summary>
    public bool SystemControlEnabled { get; set; } = true;

    /// <summary>Python executable for computer_exec; empty auto-detects python.exe / py.exe on PATH.</summary>
    public string PythonPath { get; set; } = "";

    /// <summary>
    /// Burst limit of computer actions before the tool asks the agent to report progress.
    /// The window resets automatically after a pause, so long interactive sessions keep working.
    /// 0 or negative disables the limit.
    /// </summary>
    public int MaxStepsPerTurn { get; set; } = 30;

    /// <summary>Delay in milliseconds between successive atomic actions to allow UI stabilization.</summary>
    public int ActionDelayMs { get; set; } = 300;
}
