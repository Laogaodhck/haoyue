namespace Haoyue.Runtime.NetworkOps;

/// <summary>
/// Configuration for the Network Ops plugin. Disabled by default; the desktop
/// advanced-settings toggle turns it on without affecting other extensions.
/// </summary>
public sealed class NetworkOpsConfig
{
    /// <summary>Whether network ops tools (network_diagnose / network_cmd) are registered.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Whether state-changing commands (route add, ip link set, netsh set…, DNS cache
    /// flush) may run. When false only read-only queries are allowed; read-only agents
    /// are additionally blocked from the whole tool by the agent mode filter.
    /// </summary>
    public bool AllowMutating { get; set; } = true;

    /// <summary>Per-command timeout in seconds (1..600).</summary>
    public int TimeoutSeconds { get; set; } = 60;
}
