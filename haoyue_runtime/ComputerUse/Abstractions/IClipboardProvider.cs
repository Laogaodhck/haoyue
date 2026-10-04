namespace Haoyue.Runtime.ComputerUse.Abstractions;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Single-responsibility contract for text clipboard access.
/// Used to transfer long or IME-sensitive text reliably (set + ctrl+v paste
/// instead of per-character synthetic typing).
/// </summary>
public interface IClipboardProvider
{
    Task<string?> GetTextAsync(CancellationToken ct);
    Task<ActionResult> SetTextAsync(string text, CancellationToken ct);
}
