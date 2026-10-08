using System.Runtime.InteropServices;

namespace Haoyue.Runtime.ComputerUse.SystemControl;

/// <summary>
/// One detected browser homepage hijack candidate (policy registry value,
/// preference file entry, shell start page, or hijacked shortcut).
/// </summary>
public sealed record BrowserFinding(
    /// <summary>Origin category: chrome-policy / edge-policy / ie-main / chrome-preferences / edge-preferences / firefox-prefs / shortcut.</summary>
    string Source,
    /// <summary>Registry path, preference file path, or shortcut path where the finding lives.</summary>
    string Location,
    /// <summary>What kind of hijack: Homepage / StartupUrl / StartPage / ShortcutArgument.</summary>
    string Kind,
    /// <summary>The hijacked URL(s) currently in effect.</summary>
    string CurrentValue,
    /// <summary>Human-readable explanation shown to the model and user.</summary>
    string Detail);

/// <summary>Result of a browser homepage hijack scan.</summary>
public sealed record BrowserScanReport(
    string Platform,
    bool Supported,
    IReadOnlyList<BrowserFinding> Findings,
    string? Error = null);

/// <summary>Result of a repair pass: what was fixed, what failed, and where the backup lives.</summary>
public sealed record BrowserRepairOutcome(
    bool Success,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> Errors,
    string? BackupPath,
    int RepairedCount);

/// <summary>
/// Per-OS implementation boundary for system control: hardware scanning, system
/// configuration reads, process listings, and browser homepage hijack handling.
/// Adapters must degrade gracefully (report errors in-band) instead of throwing
/// for recoverable problems such as missing tools or insufficient permissions.
/// </summary>
public interface ISystemControlAdapter
{
    string PlatformName { get; }

    /// <summary>Hardware inventory: CPU, memory, GPU, disks, volumes, network, board/BIOS.</summary>
    Task<string> ScanHardwareAsync(CancellationToken ct);

    /// <summary>System configuration: OS edition/build, identity, uptime, locale, power, display.</summary>
    Task<string> GetSystemConfigAsync(CancellationToken ct);

    /// <summary>Running processes sorted by CPU; optional case-insensitive name substring filter.</summary>
    Task<string> ListProcessesAsync(string? filter, int limit, CancellationToken ct);

    /// <summary>Detects homepage hijacks across policies, preferences, and shortcuts. Read-only.</summary>
    Task<BrowserScanReport> DetectBrowserHijackAsync(CancellationToken ct);

    /// <summary>
    /// Repairs every hijack the adapter can find in a fresh scan. Must back up all
    /// findings to <paramref name="backupDirectory"/> before mutating anything.
    /// </summary>
    Task<BrowserRepairOutcome> RepairBrowserHomepageAsync(string targetHomepage, string backupDirectory, CancellationToken ct);
}

/// <summary>
/// Chooses the platform adapter for system control. Windows and Linux are fully
/// supported; any other OS degrades to an adapter that reports unsupported clearly.
/// </summary>
public static class SystemControlFactory
{
    public static ISystemControlAdapter Create() =>
        OperatingSystem.IsWindows() ? new WindowsSystemAdapter()
        : OperatingSystem.IsLinux() ? new LinuxSystemAdapter()
        : new UnsupportedSystemAdapter();
}

/// <summary>Placeholder adapter for platforms without a dedicated implementation.</summary>
public sealed class UnsupportedSystemAdapter : ISystemControlAdapter
{
    public string PlatformName => "Unsupported";

    private static string Unsupported(string what) =>
        $"[不支持的系统] 当前平台（{RuntimeInformation.OSDescription}）暂无系统操控适配器，无法执行{what}。支持 Windows 与 Linux。";

    public Task<string> ScanHardwareAsync(CancellationToken ct) =>
        Task.FromResult(Unsupported("硬件扫描"));

    public Task<string> GetSystemConfigAsync(CancellationToken ct) =>
        Task.FromResult(Unsupported("系统配置读取"));

    public Task<string> ListProcessesAsync(string? filter, int limit, CancellationToken ct) =>
        Task.FromResult(Unsupported("进程查询"));

    public Task<BrowserScanReport> DetectBrowserHijackAsync(CancellationToken ct) =>
        Task.FromResult(new BrowserScanReport("Unsupported", false, [], Unsupported("浏览器检测")));

    public Task<BrowserRepairOutcome> RepairBrowserHomepageAsync(string targetHomepage, string backupDirectory, CancellationToken ct) =>
        Task.FromResult(new BrowserRepairOutcome(false, [], [Unsupported("浏览器修复")], null, 0));
}
