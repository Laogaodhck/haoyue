namespace Haoyue.Runtime.ComputerUse.Abstractions;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Single-responsibility contract for window enumeration and focus management.
/// </summary>
public interface IWindowManager
{
    Task<string?> GetActiveWindowTitleAsync(CancellationToken ct);
    Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken ct);
    Task<bool> FocusWindowAsync(string titleOrId, CancellationToken ct);

    /// <summary>Launches a document, URL, or application via the platform shell (e.g. ShellExecute).</summary>
    Task<ActionResult> LaunchAppAsync(string target, CancellationToken ct) =>
        Task.FromResult(ActionResult.Failed("Launching applications is not supported by this driver.", "open"));
}
