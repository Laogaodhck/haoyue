using System.Runtime.InteropServices;

namespace Haoyue.Runtime.NetworkOps;

/// <summary>
/// Per-OS boundary for the network ops plugin: maps logical commands from
/// <see cref="NetworkOpsCatalog"/> onto platform binaries and composes the
/// diagnostic bundle. Adapters degrade in-band (return messages instead of
/// throwing) for missing tools or insufficient permissions.
/// </summary>
public interface INetworkOpsAdapter
{
    /// <summary>"Windows" / "Linux" / "Unsupported" — shown in tool output headers.</summary>
    string PlatformName { get; }

    /// <summary>The logical commands this platform supports (schema enum + help text).</summary>
    IReadOnlyList<NetworkCommandSpec> SupportedCommands { get; }

    /// <summary>
    /// Builds the argv for one logical command and runs it. Returns the exit code
    /// and combined output; failures come back as in-band text, never exceptions.
    /// </summary>
    Task<NetworkOutcome> ExecuteAsync(string command, string? target, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken ct);

    /// <summary>Runs one diagnose section ("interfaces" | "routes" | "connections" | "arp" | "dns").</summary>
    Task<string> DiagnoseSectionAsync(string section, CancellationToken ct);
}

/// <summary>Chooses the platform adapter for network ops; other OSes degrade clearly.</summary>
public static class NetworkOpsAdapterFactory
{
    public static INetworkOpsAdapter Create() =>
        OperatingSystem.IsWindows() ? new WindowsNetworkAdapter()
        : OperatingSystem.IsLinux() ? new LinuxNetworkAdapter()
        : new UnsupportedNetworkAdapter();
}

/// <summary>Placeholder adapter for platforms without network ops support.</summary>
public sealed class UnsupportedNetworkAdapter : INetworkOpsAdapter
{
    public string PlatformName => "Unsupported";

    private static string Unsupported(string what) =>
        $"[不支持的系统] 当前平台（{RuntimeInformation.OSDescription}）暂无网络运维适配器，无法执行{what}。支持 Windows 与 Linux。";

    public IReadOnlyList<NetworkCommandSpec> SupportedCommands { get; } = [];

    public Task<NetworkOutcome> ExecuteAsync(string command, string? target, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken ct) =>
        Task.FromResult(NetworkOutcome.Failed(Unsupported($"网络命令 {command}")));

    public Task<string> DiagnoseSectionAsync(string section, CancellationToken ct) =>
        Task.FromResult(Unsupported("网络诊断"));
}
