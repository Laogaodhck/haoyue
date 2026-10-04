using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;

namespace Haoyue.Runtime.ComputerUse;

/// <summary>
/// Tool for structural inspection of the desktop and active application UI hierarchy.
/// Gives the AI agent exact control names, IDs, bounding boxes, and center coordinates.
/// </summary>
public sealed class ComputerInspectTool(
    IPromptProvider prompts,
    IComputerDriver driver) : BuiltinTool(prompts)
{
    public override string Name => "computer_inspect";
    public override string StatusLabel => "Inspecting desktop UI";
    public override bool Mutating => false;
    public override bool RequiresWorkspace => false;

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("take_screenshot", ToolSchema.Boolean("Whether to also take and attach a screenshot along with UI elements"), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var takeScreenshot = GetBool(arguments, "take_screenshot");
        var hierarchy = await driver.GetVisualElementsAsync(ct).ConfigureAwait(false);

        ScreenCapture? capture = null;
        if (takeScreenshot)
        {
            capture = await driver.CaptureScreenAsync(0, ct).ConfigureAwait(false);
        }

        var sb = new StringBuilder();
        var metrics = driver.ScreenCapture.GetScreenMetrics();
        sb.AppendLine($"[Display]: {metrics.PhysicalWidth}x{metrics.PhysicalHeight} (Scale: {metrics.ScaleFactor * 100:0}%, Logical: {metrics.LogicalWidth}x{metrics.LogicalHeight})");
        sb.AppendLine($"[Active Window]: {hierarchy.ActiveWindowTitle ?? "(None/Desktop)"}");
        if (hierarchy.Success)
        {
            sb.AppendLine($"[Interactive Elements Found]: {hierarchy.Elements.Count}");
            foreach (var el in hierarchy.Elements.Take(40))
            {
                var label = string.IsNullOrWhiteSpace(el.Name) ? "(unnamed)" : el.Name;
                sb.AppendLine($"  - [{el.ControlType}] \"{label}\" Center:({el.CenterX}, {el.CenterY}) Bounds:[{el.X}, {el.Y}, {el.Width}x{el.Height}] Id:{el.Id}");
                if (el.Children is { Count: > 0 })
                {
                    foreach (var child in el.Children.Take(20))
                    {
                        var childLabel = string.IsNullOrWhiteSpace(child.Name) ? "(unnamed)" : child.Name;
                        sb.AppendLine($"      * [{child.ControlType}] \"{childLabel}\" Center:({child.CenterX}, {child.CenterY}) Bounds:[{child.X}, {child.Y}, {child.Width}x{child.Height}] Id:{child.Id}");
                    }
                }
            }
        }
        else
        {
            sb.AppendLine($"[UI Inspection Warning]: {hierarchy.Error ?? "Could not retrieve control hierarchy, falling back to visual coordinates."}");
        }

        List<ChatImageAttachment>? attachments = null;
        string? base64 = null;
        if (capture is not null)
        {
            base64 = capture.ToBase64();
            attachments =
            [
                new ChatImageAttachment(
                    Id: Guid.NewGuid().ToString("N")[..8],
                    Name: "computer_inspect.png",
                    MediaType: capture.Format,
                    Data: base64,
                    SizeBytes: capture.Bytes.Length)
            ];
            sb.AppendLine("\n[Screenshot attached for visual confirmation]");
        }

        context.Events.Publish(new ComputerActionEvent(
            Action: "inspect",
            X: null,
            Y: null,
            Target: hierarchy.ActiveWindowTitle,
            Success: hierarchy.Success,
            Error: hierarchy.Error,
            Base64Screenshot: base64));

        return ToolResult.Ok(sb.ToString(), attachments, $"Inspected UI: {hierarchy.ActiveWindowTitle ?? "Desktop"}");
    }
}


/// <summary>
/// High-precision screen coordinate transformation engine.
/// Maps coordinates produced by AI models (based on downsampled screenshots,
/// DPI-virtualized viewports, or normalized coordinates) to exact physical display pixels.
/// </summary>
public static class ScreenCoordinateTransformer
{
    public static (int physicalX, int physicalY) ToPhysicalCoordinates(
        int modelX,
        int modelY,
        ScreenCapture? referenceCapture,
        ScreenMetrics metrics)
    {
        var targetPhysW = referenceCapture?.EffectivePhysicalWidth ?? metrics.PhysicalWidth;
        var targetPhysH = referenceCapture?.EffectivePhysicalHeight ?? metrics.PhysicalHeight;
        var originX = referenceCapture?.OriginX ?? metrics.OriginX;
        var originY = referenceCapture?.OriginY ?? metrics.OriginY;

        if (targetPhysW <= 0) targetPhysW = 1920;
        if (targetPhysH <= 0) targetPhysH = 1080;

        // When the AI model observed a captured image of specific width/height:
        if (referenceCapture is not null && referenceCapture.Width > 0 && referenceCapture.Height > 0)
        {
            var scaleX = (double)targetPhysW / referenceCapture.Width;
            var scaleY = (double)targetPhysH / referenceCapture.Height;

            var mappedX = originX + (int)Math.Round(modelX * scaleX);
            var mappedY = originY + (int)Math.Round(modelY * scaleY);

            return (
                Math.Clamp(mappedX, originX, originX + targetPhysW - 1),
                Math.Clamp(mappedY, originY, originY + targetPhysH - 1));
        }

        // Direct coordinates with clamping to physical display boundaries
        return (
            Math.Clamp(modelX, originX, originX + targetPhysW - 1),
            Math.Clamp(modelY, originY, originY + targetPhysH - 1));
    }
}

/// <summary>
/// Converts model-facing scroll semantics (wheel notches, positive = content moves
/// down/right) into the driver-boundary convention (Windows/X11 wheel delta,
/// 120 per notch, positive = up/left).
/// </summary>
public static class ScrollNormalizer
{
    public const int WheelDelta = 120;
    public const int MaxNotches = 50;

    public static (int driverDeltaX, int driverDeltaY) FromModelDeltas(int modelDeltaX, int modelDeltaY) =>
    (
        Math.Clamp(modelDeltaX, -MaxNotches, MaxNotches) * WheelDelta,
        -Math.Clamp(modelDeltaY, -MaxNotches, MaxNotches) * WheelDelta
    );
}

/// <summary>
/// Unified computer operation tool supporting clicks, cursor movement, typing, key combinations,
/// scrolling, clipboard, app launch, element-based clicking, and screen capture.
/// Compatible with standard AI computer use conventions.
/// </summary>
public sealed class ComputerTool(
    IPromptProvider prompts,
    IComputerDriver driver,
    ComputerUseConfig config) : BuiltinTool(prompts)
{
    public override string Name => "computer";
    public override string StatusLabel => "Operating computer";
    public override bool Mutating => true;
    public override bool RequiresWorkspace => false;

    private const int MaxWaitMs = 15000;
    private static readonly TimeSpan StepWindowIdleReset = TimeSpan.FromMinutes(2);

    private readonly Lock _stepGate = new();
    private DateTimeOffset _stepWindowStart = DateTimeOffset.MinValue;
    private int _stepsThisWindow;

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("action", ToolSchema.String("Action: 'screenshot', 'left_click', 'right_click', 'middle_click', 'double_click', 'triple_click', 'element_click', 'drag', 'mouse_move', 'cursor_position', 'type', 'key', 'scroll', 'wait', 'clipboard_get', 'clipboard_set', 'open', 'focus_window', 'list_windows'"), true),
        ("coordinate", ToolSchema.Array("Coordinate pair [x, y]: click position, move target, drag start, or scroll anchor (pixel coordinates of the most recent screenshot)", ToolSchema.Integer("pixel value")), false),
        ("destination", ToolSchema.Array("End coordinate pair [x, y] for drag", ToolSchema.Integer("pixel value")), false),
        ("x", ToolSchema.Integer("X coordinate (alternative to coordinate array)"), false),
        ("y", ToolSchema.Integer("Y coordinate (alternative to coordinate array)"), false),
        ("text", ToolSchema.String("Text to type, key combo to press, clipboard content (clipboard_set), target to open (app name / file / URL), or window title to focus"), false),
        ("element_id", ToolSchema.String("Element Id from a previous computer_inspect result (element_click)"), false),
        ("delta_x", ToolSchema.Integer("Horizontal scroll in wheel notches (positive = right)"), false),
        ("delta_y", ToolSchema.Integer("Vertical scroll in wheel notches (positive = scroll down)"), false),
        ("duration_ms", ToolSchema.Integer("Milliseconds to wait (wait action, max 15000)"), false),
        ("auto_screenshot", ToolSchema.Boolean("Whether to capture and return screenshot after the action (default false)"), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var rawAction = GetString(arguments, "action")?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(rawAction))
        {
            return ToolResult.Fail("The 'action' parameter is required.");
        }

        if (!TryAcquireStep(config.MaxStepsPerTurn, out var stepsUsed))
        {
            return ToolResult.Fail(
                $"Computer step budget exhausted: {stepsUsed} actions in this burst (limit {config.MaxStepsPerTurn}). " +
                "Report progress to the user before continuing; the budget resets after a pause.");
        }

        var (x, y) = ResolveCoordinates(arguments);
        var (toX, toY) = arguments["destination"] is JsonArray dest && dest.Count >= 2
            ? (ReadIntNode(dest[0]), ReadIntNode(dest[1]))
            : (null, null);
        var text = GetString(arguments, "text") ?? "";
        var elementId = GetString(arguments, "element_id");
        var deltaX = ReadIntNode(arguments["delta_x"]) ?? 0;
        var deltaY = ReadIntNode(arguments["delta_y"]) ?? 0;
        var durationMs = ReadIntNode(arguments["duration_ms"]) ?? 0;
        var autoScreenshot = arguments.ContainsKey("auto_screenshot") && GetBool(arguments, "auto_screenshot");

        var metrics = driver.ScreenCapture.GetScreenMetrics();
        var lastCapture = driver.ScreenCapture.LastCapture;

        if (x.HasValue && y.HasValue)
        {
            var (physX, physY) = ScreenCoordinateTransformer.ToPhysicalCoordinates(x.Value, y.Value, lastCapture, metrics);
            x = physX;
            y = physY;
        }
        if (toX.HasValue && toY.HasValue)
        {
            var (physX, physY) = ScreenCoordinateTransformer.ToPhysicalCoordinates(toX.Value, toY.Value, lastCapture, metrics);
            toX = physX;
            toY = physY;
        }

        ActionResult result;
        switch (rawAction)
        {
            case "screenshot":
                autoScreenshot = true;
                result = ActionResult.Ok($"Screenshot captured ({metrics.PhysicalWidth}x{metrics.PhysicalHeight}, Scale: {metrics.ScaleFactor * 100:0}%)", "screenshot");
                break;

            case "left_click" or "click":
                if (!x.HasValue || !y.HasValue) return ToolResult.Fail("Click action requires coordinates [x, y].");
                result = await driver.ClickAsync(x.Value, y.Value, MouseButton.Left, 1, ct).ConfigureAwait(false);
                break;

            case "right_click":
                if (!x.HasValue || !y.HasValue) return ToolResult.Fail("Right click requires coordinates [x, y].");
                result = await driver.ClickAsync(x.Value, y.Value, MouseButton.Right, 1, ct).ConfigureAwait(false);
                break;

            case "middle_click":
                if (!x.HasValue || !y.HasValue) return ToolResult.Fail("Middle click requires coordinates [x, y].");
                result = await driver.ClickAsync(x.Value, y.Value, MouseButton.Middle, 1, ct).ConfigureAwait(false);
                break;

            case "double_click":
                if (!x.HasValue || !y.HasValue) return ToolResult.Fail("Double click requires coordinates [x, y].");
                result = await driver.ClickAsync(x.Value, y.Value, MouseButton.Left, 2, ct).ConfigureAwait(false);
                break;

            case "triple_click":
                if (!x.HasValue || !y.HasValue) return ToolResult.Fail("Triple click requires coordinates [x, y].");
                result = await driver.ClickAsync(x.Value, y.Value, MouseButton.Left, 3, ct).ConfigureAwait(false);
                break;

            case "element_click" or "click_element":
            {
                if (string.IsNullOrWhiteSpace(elementId))
                    return ToolResult.Fail("Element click requires 'element_id' from a previous computer_inspect result.");
                var element = await driver.FindElementAsync(elementId, ct).ConfigureAwait(false);
                if (element is null)
                    return ToolResult.Fail($"Element '{elementId}' not found (stale hierarchy or unsupported driver). Run computer_inspect first, then use an element Id.");
                result = await driver.ClickAsync(element.CenterX, element.CenterY, MouseButton.Left, 1, ct).ConfigureAwait(false);
                if (result.Success)
                {
                    var label = string.IsNullOrWhiteSpace(element.Name) ? "(unnamed)" : element.Name;
                    result = result with { Message = $"{result.Message} — element [{element.ControlType}] \"{label}\"" };
                }
                break;
            }

            case "drag":
            {
                if (!x.HasValue || !y.HasValue || !toX.HasValue || !toY.HasValue)
                    return ToolResult.Fail("Drag requires 'coordinate' [x1, y1] (start) and 'destination' [x2, y2] (end).");
                result = await driver.DragAsync(x.Value, y.Value, toX.Value, toY.Value, ct).ConfigureAwait(false);
                break;
            }

            case "mouse_move" or "move":
                if (!x.HasValue || !y.HasValue) return ToolResult.Fail("Mouse move requires coordinates [x, y].");
                result = await driver.MoveMouseAsync(x.Value, y.Value, ct).ConfigureAwait(false);
                break;

            case "cursor_position":
                result = await driver.GetCursorPositionAsync(ct).ConfigureAwait(false);
                break;

            case "type":
                if (string.IsNullOrEmpty(text)) return ToolResult.Fail("Type action requires non-empty 'text'.");
                result = await driver.TypeTextAsync(text, ct).ConfigureAwait(false);
                break;

            case "key":
                if (string.IsNullOrEmpty(text)) return ToolResult.Fail("Key action requires non-empty 'text' specifying key combo.");
                result = await driver.SendKeyAsync(text, ct).ConfigureAwait(false);
                break;

            case "scroll":
            {
                var (driverX, driverY) = ScrollNormalizer.FromModelDeltas(deltaX, deltaY);
                result = await driver.ScrollAsync(x ?? -1, y ?? -1, driverX, driverY, ct).ConfigureAwait(false);
                if (result.Success)
                {
                    var dir = deltaY != 0 ? (deltaY > 0 ? "down" : "up") : deltaX > 0 ? "right" : "left";
                    result = result with { Message = $"Scrolled {Math.Abs(deltaY != 0 ? deltaY : deltaX)} notches {dir}" };
                }
                break;
            }

            case "wait":
            {
                var ms = Math.Clamp(durationMs, 0, MaxWaitMs);
                if (ms == 0) return ToolResult.Fail("Wait requires 'duration_ms' (1-15000).");
                await Task.Delay(ms, ct).ConfigureAwait(false);
                result = ActionResult.Ok($"Waited {ms} ms", "wait");
                break;
            }

            case "clipboard_get":
            {
                if (driver.Clipboard is null) return ToolResult.Fail("Clipboard is not supported by this driver.");
                var clip = await driver.Clipboard.GetTextAsync(ct).ConfigureAwait(false);
                result = clip is null
                    ? ActionResult.Failed("Clipboard is empty or unreadable.", "clipboard_get")
                    : ActionResult.Ok(clip.Length <= 2000
                        ? clip
                        : $"{clip[..2000]}\n…(truncated, {clip.Length} characters total)", "clipboard_get");
                break;
            }

            case "clipboard_set":
            {
                if (string.IsNullOrEmpty(text)) return ToolResult.Fail("Clipboard set requires non-empty 'text'.");
                if (driver.Clipboard is null)
                    result = ActionResult.Failed("Clipboard is not supported by this driver.", "clipboard_set");
                else
                    result = await driver.Clipboard.SetTextAsync(text, ct).ConfigureAwait(false);
                break;
            }

            case "open" or "launch":
                if (string.IsNullOrWhiteSpace(text)) return ToolResult.Fail("Open requires 'text' naming the app, file, or URL to launch.");
                result = await driver.LaunchAppAsync(text, ct).ConfigureAwait(false);
                break;

            case "focus_window":
                if (string.IsNullOrWhiteSpace(text)) return ToolResult.Fail("Focus window action requires non-empty 'text' specifying window title or ID.");
                var focused = await driver.WindowManager.FocusWindowAsync(text, ct).ConfigureAwait(false);
                result = focused
                    ? ActionResult.Ok($"Focused window matching '{text}'", "focus_window", target: text)
                    : ActionResult.Failed($"Could not find or focus window matching '{text}'", "focus_window");
                break;

            case "list_windows":
                var windows = await driver.WindowManager.ListWindowsAsync(ct).ConfigureAwait(false);
                var winSb = new StringBuilder();
                winSb.AppendLine($"Found {windows.Count} open windows:");
                foreach (var w in windows)
                {
                    var proc = string.IsNullOrWhiteSpace(w.ProcessName) ? "" : $" [{w.ProcessName}]";
                    winSb.AppendLine($"  - [{(w.IsActive ? "ACTIVE" : "WINDOW")}] \"{w.Title}\"{proc} ({w.Width}x{w.Height} at {w.X},{w.Y}) Id:{w.Id}");
                }
                result = ActionResult.Ok(winSb.ToString().TrimEnd(), "list_windows");
                break;

            default:
                return ToolResult.Fail($"Unsupported action '{rawAction}'. Supported: screenshot, left_click, right_click, middle_click, double_click, triple_click, element_click, drag, mouse_move, cursor_position, type, key, scroll, wait, clipboard_get, clipboard_set, open, focus_window, list_windows.");
        }

        if (config.ActionDelayMs > 0 && rawAction != "screenshot" && rawAction != "wait")
        {
            await Task.Delay(config.ActionDelayMs, ct).ConfigureAwait(false);
        }

        ScreenCapture? postCapture = null;
        if (autoScreenshot)
        {
            postCapture = await driver.CaptureScreenAsync(0, ct).ConfigureAwait(false);
        }

        List<ChatImageAttachment>? images = null;
        string? base64 = null;
        if (postCapture is not null)
        {
            base64 = postCapture.ToBase64();
            images =
            [
                new ChatImageAttachment(
                    Id: Guid.NewGuid().ToString("N")[..8],
                    Name: $"computer_{rawAction}.png",
                    MediaType: postCapture.Format,
                    Data: base64,
                    SizeBytes: postCapture.Bytes.Length)
            ];
        }

        // Publish event for Desktop Live Panel and Daemon subscribers
        context.Events.Publish(new ComputerActionEvent(
            Action: rawAction,
            X: x,
            Y: y,
            Target: !string.IsNullOrEmpty(text) ? text : elementId,
            Success: result.Success,
            Error: result.Success ? null : result.Message,
            Base64Screenshot: base64));

        if (!result.Success)
        {
            return new ToolResult
            {
                Success = false,
                Output = $"Computer action '{rawAction}' failed: {result.Message}",
                Summary = $"Computer {rawAction} failed",
                Images = images
            };
        }

        var message = result.Message ?? $"Executed {rawAction}";

        // For cursor_position, also express the position in the screenshot's pixel
        // space so the model can calibrate its coordinate frame.
        if (rawAction == "cursor_position" && result.Success && result.CoordinateX.HasValue && result.CoordinateY.HasValue)
        {
            var originX = lastCapture?.OriginX ?? metrics.OriginX;
            var originY = lastCapture?.OriginY ?? metrics.OriginY;
            var physW = lastCapture?.EffectivePhysicalWidth ?? metrics.PhysicalWidth;
            var physH = lastCapture?.EffectivePhysicalHeight ?? metrics.PhysicalHeight;
            if (lastCapture is not null && lastCapture.Width > 0 && physW > 0 && physH > 0)
            {
                var modelX = (int)Math.Round((result.CoordinateX.Value - originX) * lastCapture.Width / (double)physW);
                var modelY = (int)Math.Round((result.CoordinateY.Value - originY) * lastCapture.Height / (double)physH);
                message += $"\n(≈ ({modelX}, {modelY}) in the most recent screenshot's pixel space)";
            }
        }

        if (images is not null)
        {
            message += "\n(Latest screen state captured and attached)";
        }

        return ToolResult.Ok(message, images, $"Computer: {rawAction}");
    }

    // Burst guard: allows MaxStepsPerTurn actions within a window that resets after
    // an idle pause, so long interactive sessions are not cut off but runaway loops stop.
    private bool TryAcquireStep(int maxSteps, out int stepsUsed)
    {
        lock (_stepGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _stepWindowStart > StepWindowIdleReset)
            {
                _stepWindowStart = now;
                _stepsThisWindow = 0;
            }
            _stepsThisWindow++;
            stepsUsed = _stepsThisWindow;
            return maxSteps <= 0 || _stepsThisWindow <= maxSteps;
        }
    }

    private static (int? x, int? y) ResolveCoordinates(JsonObject args)
    {
        if (args["coordinate"] is JsonArray arr && arr.Count >= 2)
        {
            var cx = ReadIntNode(arr[0]);
            var cy = ReadIntNode(arr[1]);
            if (cx.HasValue && cy.HasValue) return (cx, cy);
        }

        var x = ReadIntNode(args["x"]);
        var y = ReadIntNode(args["y"]);
        return (x, y);
    }

    private static int? ReadIntNode(JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            if (node is JsonValue val)
            {
                if (val.TryGetValue<int>(out var i)) return i;
                if (val.TryGetValue<double>(out var d)) return (int)d;
                if (val.TryGetValue<long>(out var l)) return (int)l;
                if (int.TryParse(val.ToString(), out var parsed)) return parsed;
            }
        }
        catch { }
        return null;
    }
}

