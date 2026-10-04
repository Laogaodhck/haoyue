namespace Haoyue.Runtime.ComputerUse.Abstractions;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Single-responsibility contract for mouse and keyboard simulation (Action).
/// </summary>
/// <remarks>
/// Scroll deltas use the Windows/X11 wheel convention (120 per notch, positive = up)
/// at the driver boundary; model-facing semantics are normalized in the tool layer.
/// </remarks>
public interface IInputController
{
    Task<ActionResult> ClickAsync(int x, int y, MouseButton button, int clickCount, CancellationToken ct);
    Task<ActionResult> MoveMouseAsync(int x, int y, CancellationToken ct);
    Task<ActionResult> ScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken ct);
    Task<ActionResult> TypeTextAsync(string text, CancellationToken ct);
    Task<ActionResult> SendKeyAsync(string keyCombo, CancellationToken ct);

    Task<ActionResult> DragAsync(int fromX, int fromY, int toX, int toY, CancellationToken ct) =>
        Task.FromResult(ActionResult.Failed("Drag is not supported by this driver.", "drag"));

    Task<ActionResult> GetCursorPositionAsync(CancellationToken ct) =>
        Task.FromResult(ActionResult.Failed("Cursor position is not supported by this driver.", "cursor_position"));
}
