using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Providers;

namespace Haoyue.Runtime.Tools.Builtin;

/// <summary>
/// Captures the current operating system screen and attaches it to the conversation context
/// so vision-capable models can inspect what is currently displayed on screen.
/// </summary>
public sealed class CaptureScreenTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override string Name => "capture_screen";
    public override bool RequiresWorkspace => false;
    public override bool RequiresNetwork => false;
    public override bool RequiresVision => true;
    public override string StatusLabel => "Capturing screen";

    /// <summary>
    /// 附件硬上限（原始字节数）：超过后不再进上下文，只保留落盘副本并文本回告。
    /// Windows 主路径已在采集阶段降采样（见 WindowsScreenCapture.MaxEdge），此上限
    /// 兜底覆盖无法降采样的平台回退路径，防止一张超截图挤爆上下文窗口预算。
    /// </summary>
    internal const int MaxAttachmentBytes = 6 * 1024 * 1024;

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("description", ToolSchema.String("Optional reason or description of what to inspect on screen."), false),
        ("monitor_index", ToolSchema.Integer("Monitor index to capture (0 for primary display). Defaults to 0."), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var reason = arguments["description"]?.GetValue<string>();
        var monitorIndex = Math.Max(0, arguments["monitor_index"]?.GetValue<int>() ?? 0);

        try
        {
            var captureResult = await CaptureScreenBytesAsync(monitorIndex, ct).ConfigureAwait(false);
            if (!captureResult.Success || captureResult.Bytes is null || captureResult.Bytes.Length == 0)
            {
                return ToolResult.Fail(captureResult.Error ?? "Failed to capture screenshot.");
            }

            var imageId = Guid.NewGuid().ToString("N");
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var imageName = $"screenshot_{timestamp}.png";

            // Persist copy to workspace cache or temp directory
            string? savedPath = null;
            try
            {
                var targetDir = context.Workspace.IsGlobal
                    ? Path.Combine(Path.GetTempPath(), "haoyue", "screenshots")
                    : Path.Combine(context.Workspace.CacheDir, "screenshots");
                Directory.CreateDirectory(targetDir);
                savedPath = Path.Combine(targetDir, imageName);
                await File.WriteAllBytesAsync(savedPath, captureResult.Bytes, ct).ConfigureAwait(false);
            }
            catch
            {
                // Non-critical persistence failure
            }

            var kb = captureResult.Bytes.Length / 1024.0;
            var sizeNote = captureResult.Width > 0 && captureResult.Height > 0
                ? $"Captured screen ({captureResult.Width}x{captureResult.Height}, {kb:0} KB)"
                : $"Captured screen ({kb:0} KB, dimensions unavailable)";
            if (captureResult.Bytes.Length > MaxAttachmentBytes)
            {
                // 超限降级：图片不进上下文（EstimateImageTokens 与窗口裁剪都收不住这种量级），
                // 模型可以走视觉之外的路径（如让用户描述、或对落盘副本跑 OCR）。
                return ToolResult.Ok(
                    $"{sizeNote}。截图超过附件上限（{MaxAttachmentBytes / 1024 / 1024} MB），未附加到上下文。" +
                    (savedPath is null ? "" : $"已保存到：{savedPath}。") +
                    "如需分析该截图，请改用区域截图工具或降低屏幕分辨率后重试。" +
                    (string.IsNullOrWhiteSpace(reason) ? "" : $"\nPurpose: {reason}"),
                    "Screenshot too large for context");
            }

            var base64Data = Convert.ToBase64String(captureResult.Bytes);

            var attachment = new ChatImageAttachment(
                Id: imageId,
                Name: imageName,
                MediaType: "image/png",
                Data: base64Data,
                SizeBytes: captureResult.Bytes.Length);

            var summary = sizeNote;
            var output = $"{sizeNote}." +
                         (string.IsNullOrWhiteSpace(reason) ? "" : $"\nPurpose: {reason}") +
                         (savedPath is null ? "" : $"\nSaved to: {savedPath}") +
                         "\nThe screenshot image is attached to this turn context for your visual inspection.";

            return ToolResult.Ok(output, [attachment], summary);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to capture screen: {ex.Message}");
        }
    }

    private static async Task<ScreenCaptureResult> CaptureScreenBytesAsync(int monitorIndex, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var gdiResult = await Task.Run(() => WindowsScreenCapture.CaptureScreen(monitorIndex), ct).ConfigureAwait(false);
            if (gdiResult.Success && gdiResult.Bytes is { Length: > 0 })
            {
                return gdiResult;
            }
            return await CaptureWindowsPowerShellAsync(monitorIndex, ct).ConfigureAwait(false);
        }

        if (OperatingSystem.IsMacOS())
        {
            return await CaptureMacScreenAsync(monitorIndex, ct).ConfigureAwait(false);
        }

        if (OperatingSystem.IsLinux())
        {
            return await CaptureLinuxScreenAsync(monitorIndex, ct).ConfigureAwait(false);
        }

        return ScreenCaptureResult.Failed("Screen capture is not supported on this operating system.");
    }

    /// <summary>
    /// 从 PNG 的 IHDR 块直接读取像素尺寸。平台回退路径（PowerShell/screencapture/grim）
    /// 的辅助工具不回传几何信息，此前宽高恒为 0，工具输出里显示 "0x0"（L6）。
    /// 解析失败（非 PNG/截断）时返回 (0, 0)，由调用方降级为「尺寸未知」文本。
    /// </summary>
    internal static (int Width, int Height) ReadPngDimensions(byte[] png)
    {
        // 8 字节签名 + IHDR 长度(4) + "IHDR"(4) + 宽(4) + 高(4) = 至少 24 字节。
        if (png is null || png.Length < 24)
        {
            return (0, 0);
        }

        if (png[0] != 0x89 || png[1] != (byte)'P' || png[2] != (byte)'N' || png[3] != (byte)'G'
            || png[12] != (byte)'I' || png[13] != (byte)'H' || png[14] != (byte)'D' || png[15] != (byte)'R')
        {
            return (0, 0);
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        return width > 0 && height > 0 ? (width, height) : (0, 0);
    }

    /// <summary>
    /// 构建 Windows 回退路径（GDI+ 不可用时的 PowerShell 采集）脚本。
    /// 按 monitor_index 选择屏幕（AllScreens 顺序），越界时写 stderr 并非零退出（L6：
    /// 此前回退路径写死 PrimaryScreen，monitor_index 被静默忽略）。
    /// </summary>
    internal static string BuildWindowsFallbackScript(int monitorIndex, string tempFile)
    {
        var safePath = tempFile.Replace("'", "''");
        return $$"""
            Add-Type -AssemblyName System.Windows.Forms,System.Drawing;
            $screens = [Windows.Forms.Screen]::AllScreens;
            if ({{monitorIndex}} -ge $screens.Length) { Write-Error "monitor_index {{monitorIndex}} out of range: $($screens.Length) screen(s) detected (index 0 = primary display)."; exit 1 }
            $s = $screens[{{monitorIndex}}];
            $b = New-Object Drawing.Bitmap($s.Bounds.Width, $s.Bounds.Height);
            $g = [Drawing.Graphics]::FromImage($b);
            $g.CopyFromScreen($s.Bounds.X, $s.Bounds.Y, 0, 0, $b.Size);
            $b.Save('{{safePath}}', [Drawing.Imaging.ImageFormat]::Png);
            $b.Dispose();
            $g.Dispose();
            """;
    }

    /// <summary>
    /// 构建 macOS screencapture 参数。screencapture 的 -D 显示器序号是 1-based，
    /// 而工具约定 monitor_index 0 = 主屏，因此 +1 后传入（L6）。
    /// </summary>
    internal static string BuildMacArgs(int monitorIndex, string tempFile)
    {
        var display = monitorIndex + 1;
        return $"-x -D {display} \"{tempFile}\"";
    }

    private static async Task<ScreenCaptureResult> CaptureWindowsPowerShellAsync(int monitorIndex, CancellationToken ct)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"screen_{Guid.NewGuid():N}.png");
        var script = BuildWindowsFallbackScript(monitorIndex, tempFile);
        try
        {
            var psi = new ProcessStartInfo("powershell", $"-NoProfile -NonInteractive -Command \"{script}\"")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return ScreenCaptureResult.Failed("Failed to launch PowerShell.");
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode == 0 && File.Exists(tempFile))
            {
                var bytes = await File.ReadAllBytesAsync(tempFile, ct).ConfigureAwait(false);
                var (w, h) = ReadPngDimensions(bytes);
                return ScreenCaptureResult.Ok(bytes, w, h);
            }
            var error = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            return ScreenCaptureResult.Failed($"PowerShell screenshot capture failed: {error}");
        }
        catch (Exception ex)
        {
            return ScreenCaptureResult.Failed($"PowerShell screenshot capture error: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    private static async Task<ScreenCaptureResult> CaptureMacScreenAsync(int monitorIndex, CancellationToken ct)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"screen_{Guid.NewGuid():N}.png");
        try
        {
            var psi = new ProcessStartInfo("screencapture", BuildMacArgs(monitorIndex, tempFile))
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return ScreenCaptureResult.Failed("Failed to start screencapture.");
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(tempFile))
            {
                var error = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                return ScreenCaptureResult.Failed(
                    $"screencapture failed with exit code {process.ExitCode} (monitor_index {monitorIndex}): {error}");
            }
            var bytes = await File.ReadAllBytesAsync(tempFile, ct).ConfigureAwait(false);
            var (w, h) = ReadPngDimensions(bytes);
            return ScreenCaptureResult.Ok(bytes, w, h);
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    internal static async Task<ScreenCaptureResult> CaptureLinuxScreenAsync(int monitorIndex, CancellationToken ct)
    {
        // 逐显示器选择需要输出枚举（swaymsg/xrandr 解析），grim/import/scrot 这类
        // 全桌面回退工具给不出来。与其静默截错区域，不如明确失败（L6）。
        if (monitorIndex > 0)
        {
            return ScreenCaptureResult.Failed(
                $"monitor_index {monitorIndex} is not supported on this Linux capture path: " +
                "grim/import/scrot capture the full virtual screen (use monitor_index 0).");
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"screen_{Guid.NewGuid():N}.png");
        try
        {
            // Try grim (Wayland) then import / scrot (X11)
            var commands = new[]
            {
                ("grim", $"\"{tempFile}\""),
                ("import", $"-window root \"{tempFile}\""),
                ("scrot", $"\"{tempFile}\""),
            };

            foreach (var (tool, args) in commands)
            {
                try
                {
                    var psi = new ProcessStartInfo(tool, args)
                    {
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var process = Process.Start(psi);
                    if (process is null) continue;
                    await process.WaitForExitAsync(ct).ConfigureAwait(false);
                    if (process.ExitCode == 0 && File.Exists(tempFile))
                    {
                        var bytes = await File.ReadAllBytesAsync(tempFile, ct).ConfigureAwait(false);
                        var (w, h) = ReadPngDimensions(bytes);
                        return ScreenCaptureResult.Ok(bytes, w, h);
                    }
                }
                catch
                {
                    // Fallthrough to next command
                }
            }

            return ScreenCaptureResult.Failed("No supported screen capture tool found on Linux (checked grim, import, scrot).");
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    internal sealed record ScreenCaptureResult(bool Success, byte[]? Bytes, int Width, int Height, string? Error)
    {
        public static ScreenCaptureResult Ok(byte[] bytes, int width, int height) =>
            new(true, bytes, width, height, null);

        public static ScreenCaptureResult Failed(string error) =>
            new(false, null, 0, 0, error);
    }

    // ---------------------------------------------------------------- Windows GDI+ Capture

    internal static class WindowsScreenCapture
    {
        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const int SRCCOPY = 0x00CC0020;

        [DllImport("gdi32.dll", EntryPoint = "CreateDCA", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr CreateDC(string lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);

        // GDI+ P/Invoke
        [StructLayout(LayoutKind.Sequential)]
        private struct GdiplusStartupInput
        {
            public uint GdiplusVersion;
            public IntPtr DebugEventCallback;
            public bool SuppressBackgroundThread;
            public bool SuppressExternalCodecs;

            public static GdiplusStartupInput Default => new() { GdiplusVersion = 1 };
        }

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdiplusStartup(out IntPtr token, ref GdiplusStartupInput input, out IntPtr output);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdiplusShutdown(IntPtr token);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipCreateBitmapFromHBITMAP(IntPtr hbm, IntPtr hpal, out IntPtr bitmap);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipDisposeImage(IntPtr image);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipSaveImageToStream(IntPtr image, IntPtr stream, ref Guid clsidEncoder, IntPtr encoderParams);

        // Downscale P/Invokes (H4): oversized 4K screens otherwise produce multi-MB base64
        // payloads that blow the context-window image budget. Longest edge is clamped to
        // MaxEdge — the vision-model sweet spot (~1568px for 1-megapixel-equivalent tiles).
        internal const int MaxEdge = 1568;
        private const int PixelFormat32bppARGB = 0x0026200A;
        private const int InterpolationModeHighQualityBicubic = 7;
        private const int PixelOffsetModeHalf = 4;
        private const int UnitPixel = 2;

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, IntPtr scan0, out IntPtr bitmap);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipGetImageGraphicsContext(IntPtr image, out IntPtr graphics);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipSetInterpolationMode(IntPtr graphics, int mode);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipSetPixelOffsetMode(IntPtr graphics, int mode);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipDrawImageRectRectI(IntPtr graphics, IntPtr image,
            int dstX, int dstY, int dstWidth, int dstHeight,
            int srcX, int srcY, int srcWidth, int srcHeight,
            int srcUnit, IntPtr imageAttributes, IntPtr callback, IntPtr callbackData);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipDeleteGraphics(IntPtr graphics);

        // OLE / COM Stream for in-memory saving
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CreateStreamOnHGlobal(IntPtr hGlobal, bool fDeleteOnRelease, out IntPtr ppstm);

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int GetHGlobalFromStream(IntPtr pstm, out IntPtr phglobal);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern nuint GlobalSize(IntPtr hMem);

        // PNG Encoder GUID: 557cf406-1a04-11d3-9a73-0000f81ef32e
        private static Guid PngEncoderGuid = new("557cf406-1a04-11d3-9a73-0000f81ef32e");

        // Monitor enumeration (L6: monitor_index support for index > 0)
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEXW
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

        [DllImport("gdi32.dll", EntryPoint = "CreateDCW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateDCW(string? lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

        internal static ScreenCaptureResult CaptureScreen(int monitorIndex)
        {
            // index 0 保持历史主屏路径（GetDC(NULL) + 主屏 metrics），默认行为逐位不变；
            // index > 0 走显式显示器枚举，越界明确报错（此前被静默忽略，L6）。
            if (monitorIndex > 0)
            {
                return CaptureSpecificMonitor(monitorIndex);
            }

            var width = GetSystemMetrics(SM_CXSCREEN);
            var height = GetSystemMetrics(SM_CYSCREEN);

            if (width <= 0 || height <= 0)
            {
                return ScreenCaptureResult.Failed("Unable to determine screen dimensions.");
            }

            var hdcScreen = GetDC(IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero)
            {
                return ScreenCaptureResult.Failed("Failed to acquire desktop DC.");
            }

            try
            {
                return CaptureFrom(hdcScreen, 0, 0, width, height);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdcScreen);
            }
        }

        private static ScreenCaptureResult CaptureSpecificMonitor(int monitorIndex)
        {
            var monitors = new List<(RECT Bounds, string Device)>();
            bool Enumerate(IntPtr hMonitor, IntPtr _, IntPtr __, IntPtr ___)
            {
                var info = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };
                if (GetMonitorInfoW(hMonitor, ref info))
                {
                    monitors.Add((info.rcMonitor, info.szDevice));
                }
                return true;
            }

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Enumerate, IntPtr.Zero);

            if (monitors.Count == 0)
            {
                return ScreenCaptureResult.Failed(
                    $"Unable to enumerate display monitors (requested monitor_index {monitorIndex}).");
            }
            if (monitorIndex >= monitors.Count)
            {
                return ScreenCaptureResult.Failed(
                    $"monitor_index {monitorIndex} out of range: {monitors.Count} monitor(s) detected " +
                    "(index 0 = primary display).");
            }

            var (bounds, device) = monitors[monitorIndex];
            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            if (width <= 0 || height <= 0)
            {
                return ScreenCaptureResult.Failed(
                    $"Monitor {monitorIndex} ({device}) has invalid bounds " +
                    $"({bounds.Left},{bounds.Top})-({bounds.Right},{bounds.Bottom}).");
            }

            var hdcMonitor = CreateDCW(null, device, null, IntPtr.Zero);
            if (hdcMonitor == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                return ScreenCaptureResult.Failed(
                    $"Failed to create DC for monitor {monitorIndex} ({device}, Win32 error {err}).");
            }

            try
            {
                // 显示器 DC 的原点即该显示器左上角，BitBlt 源坐标取 (0, 0)。
                return CaptureFrom(hdcMonitor, 0, 0, width, height);
            }
            finally
            {
                DeleteDC(hdcMonitor);
            }
        }

        /// <summary>
        /// 共享采集管线：把源区域 BitBlt 进兼容位图，再经 GDI+ 编码为 PNG
        /// （超大帧先降采样，见 MaxEdge 注释）。
        /// </summary>
        private static ScreenCaptureResult CaptureFrom(IntPtr hdcSource, int srcX, int srcY, int width, int height)
        {
            var hdcMem = CreateCompatibleDC(hdcSource);
            if (hdcMem == IntPtr.Zero)
            {
                return ScreenCaptureResult.Failed("Failed to create compatible memory DC.");
            }

            var hBitmap = CreateCompatibleBitmap(hdcSource, width, height);
            if (hBitmap == IntPtr.Zero)
            {
                DeleteDC(hdcMem);
                return ScreenCaptureResult.Failed("Failed to create compatible bitmap.");
            }

            var hOld = SelectObject(hdcMem, hBitmap);

            try
            {
                if (!BitBlt(hdcMem, 0, 0, width, height, hdcSource, srcX, srcY, SRCCOPY))
                {
                    var err = Marshal.GetLastWin32Error();
                    return ScreenCaptureResult.Failed($"BitBlt screen capture failed (Win32 error: {err}).");
                }

                return EncodeBitmapToPng(hBitmap, width, height);
            }
            finally
            {
                SelectObject(hdcMem, hOld);
                DeleteObject(hBitmap);
                DeleteDC(hdcMem);
            }
        }

        private static ScreenCaptureResult EncodeBitmapToPng(IntPtr hBitmap, int width, int height)
        {
            var startupInput = GdiplusStartupInput.Default;
            var status = GdiplusStartup(out var token, ref startupInput, out _);
            if (status != 0)
            {
                return ScreenCaptureResult.Failed($"GdiplusStartup failed with code {status}.");
            }

            try
            {
                status = GdipCreateBitmapFromHBITMAP(hBitmap, IntPtr.Zero, out var pBitmap);
                if (status != 0 || pBitmap == IntPtr.Zero)
                {
                    return ScreenCaptureResult.Failed($"GdipCreateBitmapFromHBITMAP failed with code {status}.");
                }

                // Downscale oversized captures before encoding (H4). pBitmap is
                // disposed and replaced by the scaled image on success; `activeImage`
                // is the one the finally block releases.
                var activeImage = pBitmap;
                var reportWidth = width;
                var reportHeight = height;
                var maxEdge = Math.Max(width, height);
                if (maxEdge > MaxEdge)
                {
                    var scale = (double)MaxEdge / maxEdge;
                    var dstWidth = Math.Max(1, (int)Math.Round(width * scale));
                    var dstHeight = Math.Max(1, (int)Math.Round(height * scale));
                    if (GdipCreateBitmapFromScan0(dstWidth, dstHeight, 0, PixelFormat32bppARGB, IntPtr.Zero, out var pScaled) == 0
                        && pScaled != IntPtr.Zero
                        && GdipGetImageGraphicsContext(pScaled, out var graphics) == 0
                        && graphics != IntPtr.Zero)
                    {
                        GdipSetInterpolationMode(graphics, InterpolationModeHighQualityBicubic);
                        GdipSetPixelOffsetMode(graphics, PixelOffsetModeHalf);
                        GdipDrawImageRectRectI(graphics, pBitmap,
                            0, 0, dstWidth, dstHeight,
                            0, 0, width, height,
                            UnitPixel, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                        GdipDeleteGraphics(graphics);
                        GdipDisposeImage(pBitmap);
                        activeImage = pScaled;
                        reportWidth = dstWidth;
                        reportHeight = dstHeight;
                    }
                    else if (pScaled != IntPtr.Zero)
                    {
                        GdipDisposeImage(pScaled); // 降采样失败不阻断采集，按原尺寸编码
                    }
                }

                try
                {
                    var streamStatus = CreateStreamOnHGlobal(IntPtr.Zero, true, out var pStream);
                    if (streamStatus != 0 || pStream == IntPtr.Zero)
                    {
                        return ScreenCaptureResult.Failed("CreateStreamOnHGlobal failed.");
                    }

                    try
                    {
                        var encoderGuid = PngEncoderGuid;
                        status = GdipSaveImageToStream(activeImage, pStream, ref encoderGuid, IntPtr.Zero);
                        if (status != 0)
                        {
                            return ScreenCaptureResult.Failed($"GdipSaveImageToStream failed with code {status}.");
                        }

                        if (GetHGlobalFromStream(pStream, out var hGlobal) != 0 || hGlobal == IntPtr.Zero)
                        {
                            return ScreenCaptureResult.Failed("GetHGlobalFromStream failed.");
                        }

                        var size = (int)GlobalSize(hGlobal);
                        if (size <= 0)
                        {
                            return ScreenCaptureResult.Failed("Captured PNG stream size is 0.");
                        }

                        var ptr = GlobalLock(hGlobal);
                        if (ptr == IntPtr.Zero)
                        {
                            return ScreenCaptureResult.Failed("GlobalLock failed.");
                        }

                        try
                        {
                            var buffer = new byte[size];
                            Marshal.Copy(ptr, buffer, 0, size);
                            return ScreenCaptureResult.Ok(buffer, reportWidth, reportHeight);
                        }
                        finally
                        {
                            GlobalUnlock(hGlobal);
                        }
                    }
                    finally
                    {
                        Marshal.Release(pStream);
                    }
                }
                finally
                {
                    GdipDisposeImage(activeImage);
                }
            }
            finally
            {
                GdiplusShutdown(token);
            }
        }
    }
}
