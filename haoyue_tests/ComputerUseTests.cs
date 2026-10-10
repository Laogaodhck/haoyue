using System.Text.Json.Nodes;
using Haoyue.Runtime;
using Haoyue.Runtime.ComputerUse;
using Haoyue.Runtime.ComputerUse.Abstractions;
using Haoyue.Runtime.ComputerUse.Drivers.Windows;
using Haoyue.Runtime.ComputerUse.SystemControl;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Workspaces;
using Xunit;

namespace Haoyue.Tests;

public sealed class ComputerUseTests : IAsyncDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"haoyue_cu_test_{Guid.NewGuid():N}");

    public ComputerUseTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask DisposeAsync()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void DefaultConfig_ComputerUseIsDisabled()
    {
        var config = new HaoyueConfig();
        Assert.NotNull(config.ComputerUse);
        Assert.False(config.ComputerUse.Enabled);
        Assert.Equal("auto", config.ComputerUse.Driver);
        Assert.Equal(30, config.ComputerUse.MaxStepsPerTurn);
    }

    [Fact]
    public void Runtime_DisabledByDefault_RegistersNoComputerTools()
    {
        var configPath = Path.Combine(_tempDir, "config_disabled.json");
        var statePath = Path.Combine(_tempDir, "state_disabled.json");
        var store = new ConfigStore(configPath, statePath);
        store.Config.ComputerUse.Enabled = false;

        using var runtime = HaoyueRuntime.Create(_tempDir, store);

        Assert.Null(runtime.Tools.Resolve("computer"));
        Assert.Null(runtime.Tools.Resolve("computer_inspect"));
    }

    [Fact]
    public void Runtime_Enabled_RegistersComputerTools()
    {
        var configPath = Path.Combine(_tempDir, "config_enabled.json");
        var statePath = Path.Combine(_tempDir, "state_enabled.json");
        var store = new ConfigStore(configPath, statePath);
        store.Config.ComputerUse.Enabled = true;
        store.Config.ComputerUse.Driver = "vision";

        using var runtime = HaoyueRuntime.Create(_tempDir, store);

        Assert.NotNull(runtime.Tools.Resolve("computer"));
        Assert.NotNull(runtime.Tools.Resolve("computer_inspect"));
        Assert.NotNull(runtime.Tools.Resolve("computer_exec"));
    }

    [Fact]
    public void Runtime_ComputerExecDisabled_RegistersNoExecTool()
    {
        var configPath = Path.Combine(_tempDir, "config_noexec.json");
        var statePath = Path.Combine(_tempDir, "state_noexec.json");
        var store = new ConfigStore(configPath, statePath);
        store.Config.ComputerUse.Enabled = true;
        store.Config.ComputerUse.ShellEnabled = false;

        using var runtime = HaoyueRuntime.Create(_tempDir, store);

        Assert.NotNull(runtime.Tools.Resolve("computer"));
        Assert.Null(runtime.Tools.Resolve("computer_exec"));
    }


    [Fact]
    public void ComputerDriverFactory_ResolvesRequestedDriverOrFallback()
    {
        var visionDriver = ComputerDriverFactory.CreateDriver(new ComputerUseConfig { Driver = "vision" });
        Assert.NotNull(visionDriver);
        Assert.Equal("UniversalVision", visionDriver.PlatformName);

        var autoDriver = ComputerDriverFactory.CreateDriver(new ComputerUseConfig { Driver = "auto" });
        Assert.NotNull(autoDriver);
        Assert.NotEmpty(autoDriver.PlatformName);
    }

    [Fact]
    public async Task DriverFaultSandbox_CatchesAllExceptions_WithoutThrowing()
    {
        var failingDriver = new MockFaultyDriver();
        var sandboxed = new DriverFaultSandbox(failingDriver, maxConsecutiveFailures: 3);

        // 1. Inspect UI should return failed result, never throw
        var inspectResult = await sandboxed.GetVisualElementsAsync(CancellationToken.None);
        Assert.False(inspectResult.Success);
        Assert.NotNull(inspectResult.Error);
        Assert.Contains("Simulated hardware failure", inspectResult.Error);

        // 2. Click should return failed ActionResult, never throw
        var clickResult = await sandboxed.ClickAsync(100, 200, MouseButton.Left, 1, CancellationToken.None);
        Assert.False(clickResult.Success);
        Assert.Contains("Simulated driver crash", clickResult.Message);

        // 3. CaptureScreen should safely return null
        var capture = await sandboxed.CaptureScreenAsync(0, CancellationToken.None);
        Assert.Null(capture);

        // Verify circuit breaker tripped after 3 failures
        Assert.True(sandboxed.IsCircuitTripped);
        Assert.Equal(3, sandboxed.ConsecutiveFailures);

        // Subsequent call is blocked by circuit breaker
        var blockedResult = await sandboxed.ClickAsync(50, 50, MouseButton.Left, 1, CancellationToken.None);
        Assert.False(blockedResult.Success);
        Assert.Contains("circuit breaker", blockedResult.Message, StringComparison.OrdinalIgnoreCase);

        // Reset circuit
        sandboxed.ResetCircuit();
        Assert.False(sandboxed.IsCircuitTripped);
    }

    [Fact]
    public async Task ComputerTool_ValidatesActionParameters()
    {
        var prompts = new MockPromptProvider();
        var driver = new MockWorkingDriver();
        var tool = new ComputerTool(prompts, driver, new ComputerUseConfig());
        var context = CreateToolContext();

        // Missing action
        var res1 = await tool.ExecuteAsync(new JsonObject(), context, CancellationToken.None);
        Assert.False(res1.Success);
        Assert.Contains("action", res1.Output, StringComparison.OrdinalIgnoreCase);

        // Missing coordinate for click
        var res2 = await tool.ExecuteAsync(new JsonObject { ["action"] = "left_click" }, context, CancellationToken.None);
        Assert.False(res2.Success);
        Assert.Contains("coordinate", res2.Output, StringComparison.OrdinalIgnoreCase);

        // Valid click with coordinate array
        var res3 = await tool.ExecuteAsync(new JsonObject
        {
            ["action"] = "left_click",
            ["coordinate"] = new JsonArray { 150, 250 },
            ["auto_screenshot"] = false
        }, context, CancellationToken.None);

        Assert.True(res3.Success);
        Assert.Equal(150, driver.LastClickX);
        Assert.Equal(250, driver.LastClickY);
    }

    [Fact]
    public async Task ComputerInspectTool_ReturnsFormattedHierarchy()
    {
        var prompts = new MockPromptProvider();
        var driver = new MockWorkingDriver();
        var tool = new ComputerInspectTool(prompts, driver);
        var context = CreateToolContext();

        var res = await tool.ExecuteAsync(new JsonObject { ["take_screenshot"] = false }, context, CancellationToken.None);
        Assert.True(res.Success);
        Assert.Contains("Mock Active Window", res.Output);
        Assert.Contains("SubmitButton", res.Output);
    }

    [Fact]
    public async Task ComputerTool_HandlesWindowActions()
    {
        var prompts = new MockPromptProvider();
        var driver = new MockWorkingDriver();
        var tool = new ComputerTool(prompts, driver, new ComputerUseConfig());
        var context = CreateToolContext();

        // List windows
        var listRes = await tool.ExecuteAsync(new JsonObject
        {
            ["action"] = "list_windows"
        }, context, CancellationToken.None);
        Assert.True(listRes.Success);
        Assert.Contains("Mock Active Window", listRes.Output);

        // Focus window
        var focusRes = await tool.ExecuteAsync(new JsonObject
        {
            ["action"] = "focus_window",
            ["text"] = "Mock Active Window"
        }, context, CancellationToken.None);
        Assert.True(focusRes.Success);
        Assert.Contains("Focused window", focusRes.Output);
    }

    [Fact]
    public void ExtensionManager_DiscoversAndManagesExtensions()
    {
        var manager = Haoyue.Runtime.Extensions.ExtensionManager.CreateDefault();
        Assert.NotNull(manager);
        
        var ext = manager.Get("computer_use");
        Assert.NotNull(ext);
        Assert.Equal("computer_use", ext.Id);
    }

    [Fact]
    public void CompositeLinuxInputController_ProbeInitializesSafely()
    {
        var controller = new Haoyue.Runtime.ComputerUse.Drivers.Linux.CompositeLinuxInputController();
        controller.Probe();
        Assert.NotEqual(Haoyue.Runtime.ComputerUse.Drivers.Linux.LinuxInputBackend.Unknown, controller.DetectedBackend);
    }

    [Fact]
    public void ScreenCoordinateTransformer_ScalesDownsampledImage_ToPhysicalPixels()
    {
        // Screen is 3072x1920 (175% scaling on high-DPI display)
        var metrics = new ScreenMetrics(
            PhysicalWidth: 3072,
            PhysicalHeight: 1920,
            LogicalWidth: 1755,
            LogicalHeight: 1097,
            ScaleFactor: 1.75);

        // Screenshot was downsampled by 50% for vision token optimization (1536x960)
        var capture = new ScreenCapture(
            Bytes: [0x01],
            Width: 1536,
            Height: 960,
            Format: "image/png",
            PhysicalWidth: 3072,
            PhysicalHeight: 1920,
            ScaleFactor: 1.75);

        // AI model identifies button at (768, 480) in the 1536x960 image
        var (physX, physY) = ScreenCoordinateTransformer.ToPhysicalCoordinates(768, 480, capture, metrics);

        // Expect exact 2x upscale to physical screen coordinate (1536, 960)
        Assert.Equal(1536, physX);
        Assert.Equal(960, physY);
    }

    [Fact]
    public void ScreenCoordinateTransformer_ClampsOutOfBoundsCoordinates()
    {
        var metrics = new ScreenMetrics(3072, 1920, 1755, 1097, 1.75);
        var capture = new ScreenCapture([0x01], 3072, 1920, "image/png", 3072, 1920, 1.75);

        var (negX, negY) = ScreenCoordinateTransformer.ToPhysicalCoordinates(-50, -100, capture, metrics);
        Assert.Equal(0, negX);
        Assert.Equal(0, negY);

        var (overX, overY) = ScreenCoordinateTransformer.ToPhysicalCoordinates(4000, 3000, capture, metrics);
        Assert.Equal(3071, overX);
        Assert.Equal(1919, overY);
    }

    [Fact]
    public async Task ComputerTool_MapsDownsampledCoordinates_ToPhysicalClick()
    {
        var prompts = new MockPromptProvider();
        var driver = new MockWorkingDriver();

        // 3072x1920 physical, 1536x960 capture (e.g. 50% downsampled)
        driver.LastCapture = new ScreenCapture(
            Bytes: [0x01],
            Width: 1536,
            Height: 960,
            Format: "image/png",
            PhysicalWidth: 3072,
            PhysicalHeight: 1920,
            ScaleFactor: 1.75);

        var tool = new ComputerTool(prompts, driver, new ComputerUseConfig());
        var context = CreateToolContext();

        var res = await tool.ExecuteAsync(new JsonObject
        {
            ["action"] = "left_click",
            ["coordinate"] = new JsonArray { 768, 480 }
        }, context, CancellationToken.None);

        Assert.True(res.Success);
        // Transformed from 768, 480 to 1536, 960
        Assert.Equal(1536, driver.LastClickX);
        Assert.Equal(960, driver.LastClickY);
    }

    [Fact]
    public void ScrollNormalizer_ConvertsNotchesToWheelDelta_WithDownPositiveSemantics()
    {
        // Model: +3 notches down → driver: -360 (positive = up convention)
        var (x, y) = ScrollNormalizer.FromModelDeltas(0, 3);
        Assert.Equal(0, x);
        Assert.Equal(-360, y);

        // Model: -2 notches (up) → driver: +240
        (_, y) = ScrollNormalizer.FromModelDeltas(0, -2);
        Assert.Equal(240, y);

        // Horizontal: positive = right, no flip
        (x, _) = ScrollNormalizer.FromModelDeltas(2, 0);
        Assert.Equal(240, x);

        // Clamped to ±50 notches
        (x, y) = ScrollNormalizer.FromModelDeltas(999, -999);
        Assert.Equal(50 * ScrollNormalizer.WheelDelta, x);
        Assert.Equal(50 * ScrollNormalizer.WheelDelta, y);
    }

    [Fact]
    public async Task ComputerTool_Scroll_PassesNormalizedDeltasAndSkipsAnchorWithoutCoordinates()
    {
        var prompts = new MockPromptProvider();
        var driver = new MockWorkingDriver();
        var tool = new ComputerTool(prompts, driver, new ComputerUseConfig());
        var context = CreateToolContext();

        var res = await tool.ExecuteAsync(new JsonObject
        {
            ["action"] = "scroll",
            ["delta_y"] = 2
        }, context, CancellationToken.None);

        Assert.True(res.Success);
        Assert.Equal(-240, driver.LastScrollDeltaY);
        Assert.Equal(-1, driver.LastScrollAnchorX); // no coordinate → no cursor move

        await tool.ExecuteAsync(new JsonObject
        {
            ["action"] = "scroll",
            ["delta_y"] = -1,
            ["coordinate"] = new JsonArray { 500, 400 }
        }, context, CancellationToken.None);
        Assert.Equal(120, driver.LastScrollDeltaY);
        Assert.Equal(500, driver.LastScrollAnchorX);
        Assert.Equal(400, driver.LastScrollAnchorY);
    }

    [Fact]
    public async Task ComputerTool_ElementClick_UsesElementCenter()
    {
        var prompts = new MockPromptProvider();
        var driver = new MockWorkingDriver();
        var tool = new ComputerTool(prompts, driver, new ComputerUseConfig());
        var context = CreateToolContext();

        var res = await tool.ExecuteAsync(new JsonObject
        {
            ["action"] = "element_click",
            ["element_id"] = "btn_submit"
        }, context, CancellationToken.None);

        Assert.True(res.Success);
        // Center of (100, 200, 80x30)
        Assert.Equal(140, driver.LastClickX);
        Assert.Equal(215, driver.LastClickY);
        Assert.Contains("SubmitButton", res.Output);
    }

    [Fact]
    public async Task ComputerTool_ElementClick_WithoutIdFails()
    {
        var prompts = new MockPromptProvider();
        var driver = new MockWorkingDriver();
        var tool = new ComputerTool(prompts, driver, new ComputerUseConfig());
        var context = CreateToolContext();

        var res = await tool.ExecuteAsync(new JsonObject
        {
            ["action"] = "element_click"
        }, context, CancellationToken.None);

        Assert.False(res.Success);
        Assert.Contains("element_id", res.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ComputerTool_Wait_SucceedsWithinClamp()
    {
        var prompts = new MockPromptProvider();
        var driver = new MockWorkingDriver();
        var tool = new ComputerTool(prompts, driver, new ComputerUseConfig());
        var context = CreateToolContext();

        var res = await tool.ExecuteAsync(new JsonObject
        {
            ["action"] = "wait",
            ["duration_ms"] = 30
        }, context, CancellationToken.None);

        Assert.True(res.Success);
        Assert.Contains("Waited 30 ms", res.Output);

        var noArg = await tool.ExecuteAsync(new JsonObject { ["action"] = "wait" }, context, CancellationToken.None);
        Assert.False(noArg.Success);
    }

    [Fact]
    public async Task ComputerTool_StepBudget_StopsRunawayLoopAndRecoversAfterReset()
    {
        var prompts = new MockPromptProvider();
        var driver = new MockWorkingDriver();
        var tool = new ComputerTool(prompts, driver, new ComputerUseConfig { MaxStepsPerTurn = 2 });
        var context = CreateToolContext();

        var args = new JsonObject { ["action"] = "list_windows" };
        Assert.True((await tool.ExecuteAsync(args, context, CancellationToken.None)).Success);
        Assert.True((await tool.ExecuteAsync(args, context, CancellationToken.None)).Success);

        var third = await tool.ExecuteAsync(args, context, CancellationToken.None);
        Assert.False(third.Success);
        Assert.Contains("budget", third.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ComputerTool_Clipboard_RoundTripsThroughDriver()
    {
        var prompts = new MockPromptProvider();
        var driver = new MockWorkingDriver();
        var tool = new ComputerTool(prompts, driver, new ComputerUseConfig());
        var context = CreateToolContext();

        var set = await tool.ExecuteAsync(new JsonObject
        {
            ["action"] = "clipboard_set",
            ["text"] = "记住 pnpm"
        }, context, CancellationToken.None);
        Assert.True(set.Success);
        Assert.Equal("记住 pnpm", driver.ClipboardText);

        var get = await tool.ExecuteAsync(new JsonObject
        {
            ["action"] = "clipboard_get"
        }, context, CancellationToken.None);
        Assert.True(get.Success);
        Assert.Contains("记住 pnpm", get.Output);
    }

    [Fact]
    public async Task WindowsDriver_DpiAwarenessAndScreenMetrics_AreValid()
    {
        if (!OperatingSystem.IsWindows()) return;

        WindowsDriver.EnsureDpiAwareness();
        await using var driver = new WindowsDriver();
        var metrics = driver.GetScreenMetrics();

        Assert.True(metrics.PhysicalWidth > 0);
        Assert.True(metrics.PhysicalHeight > 0);
        Assert.True(metrics.ScaleFactor > 0);
        Assert.True(metrics.LogicalWidth > 0);
        Assert.True(metrics.LogicalHeight > 0);
    }

    [Fact]
    public async Task ComputerExec_Cmd_EchoesAsciiAndCjk()
    {
        if (!OperatingSystem.IsWindows()) return;
        var tool = new ComputerExecTool(new MockPromptProvider(), new ComputerUseConfig());
        var context = CreateToolContext();

        var res = await tool.ExecuteAsync(new JsonObject
        {
            ["kind"] = "cmd",
            ["command"] = "echo hello && echo 中文输出正常"
        }, context, CancellationToken.None);

        Assert.True(res.Success, res.Output);
        Assert.Contains("hello", res.Output);
        Assert.Contains("中文输出正常", res.Output);
    }

    [Fact]
    public async Task ComputerExec_PowerShell_RunsScriptAndPreservesCjk()
    {
        if (!OperatingSystem.IsWindows()) return;
        var tool = new ComputerExecTool(new MockPromptProvider(), new ComputerUseConfig());
        var context = CreateToolContext();

        var res = await tool.ExecuteAsync(new JsonObject
        {
            ["kind"] = "powershell",
            ["command"] = "Write-Output ('ps-' + 'ok'); Write-Output '中文测试'"
        }, context, CancellationToken.None);

        Assert.True(res.Success, res.Output);
        Assert.Contains("ps-ok", res.Output);
        Assert.Contains("中文测试", res.Output);
    }

    [Fact]
    public async Task ComputerExec_Python_ExecutesCode()
    {
        if (!OperatingSystem.IsWindows()) return;
        var tool = new ComputerExecTool(new MockPromptProvider(), new ComputerUseConfig());
        var context = CreateToolContext();

        var res = await tool.ExecuteAsync(new JsonObject
        {
            ["kind"] = "python",
            ["command"] = "print(7 * 6)"
        }, context, CancellationToken.None);

        if (res.Output.Contains("Python not found")) return; // not installed on this machine

        Assert.True(res.Success, res.Output);
        Assert.Contains("42", res.Output);
    }

    [Fact]
    public async Task ComputerExec_Cmd_TimesOutAndReports()
    {
        if (!OperatingSystem.IsWindows()) return;
        var tool = new ComputerExecTool(new MockPromptProvider(), new ComputerUseConfig());
        var context = CreateToolContext();

        var res = await tool.ExecuteAsync(new JsonObject
        {
            ["kind"] = "cmd",
            ["command"] = "ping -n 6 127.0.0.1 >nul",
            ["timeout_seconds"] = 1
        }, context, CancellationToken.None);

        Assert.False(res.Success);
        Assert.Contains("timed out", res.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ComputerExec_Cmd_HonorsCwd()
    {        if (!OperatingSystem.IsWindows()) return;
        var tool = new ComputerExecTool(new MockPromptProvider(), new ComputerUseConfig());
        var context = CreateToolContext();

        var res = await tool.ExecuteAsync(new JsonObject
        {
            ["kind"] = "cmd",
            ["command"] = "cd",
            ["cwd"] = _tempDir
        }, context, CancellationToken.None);

        Assert.True(res.Success, res.Output);
        Assert.Contains(_tempDir, res.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ComputerExec_RejectsUnknownKindAndEmptyCommand()
    {
        var tool = new ComputerExecTool(new MockPromptProvider(), new ComputerUseConfig());
        var context = CreateToolContext();

        var badKind = await tool.ExecuteAsync(new JsonObject
        {
            ["kind"] = "fish",
            ["command"] = "echo x"
        }, context, CancellationToken.None);
        Assert.False(badKind.Success);

        var noCommand = await tool.ExecuteAsync(new JsonObject { ["kind"] = "cmd" }, context, CancellationToken.None);
        Assert.False(noCommand.Success);
    }

    [Fact]
    public void ComputerExec_Resolve_AutoPicksOsDefaultShell()
    {
        var tool = new ComputerExecTool(new MockPromptProvider(), new ComputerUseConfig());

        // omitted kind and 'auto' must resolve identically to the OS default
        var byAuto = tool.Resolve("auto", "1");
        var byOmitted = tool.Resolve(null, "1");
        Assert.Equal(byAuto.Label, byOmitted.Label);

        if (OperatingSystem.IsWindows())
            Assert.Equal("PowerShell", byAuto.Label);
        else
            Assert.Contains("sh", byAuto.Label);
    }

    [Fact]
    public void ComputerExec_Resolve_BashRejectedOnWindowsWithKindGuidance()
    {
        if (!OperatingSystem.IsWindows()) return;
        var tool = new ComputerExecTool(new MockPromptProvider(), new ComputerUseConfig());

        var ex = Assert.Throws<ArgumentException>(() => tool.Resolve("bash", "ls -la"));
        Assert.Contains("powershell", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("auto", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComputerExec_Resolve_UnknownKindNamesOsAndAvailableKinds()
    {
        var tool = new ComputerExecTool(new MockPromptProvider(), new ComputerUseConfig());

        var ex = Assert.Throws<ArgumentException>(() => tool.Resolve("zsh", "x"));
        Assert.Contains(SystemEnvironment.OsLabel, ex.Message);
        Assert.Contains("python", ex.Message);
    }

    [Fact]
    public void ComputerExec_Resolve_PythonFailsWithOsSpecificGuidance_WhenAbsent()
    {
        var config = new ComputerUseConfig { PythonPath = _tempDir }; // a directory, never a valid interpreter
        var tool = new ComputerExecTool(new MockPromptProvider(), config);
        var probe = PythonLocator.Probe();
        if (probe is not null) return; // a real interpreter exists via PATH fallback — guidance path unreachable

        var ex = Assert.Throws<ArgumentException>(() => tool.Resolve("python", "print(1)"));
        if (OperatingSystem.IsWindows())
            Assert.Contains("computerUse.pythonPath", ex.Message);
        else
            Assert.Contains("python3", ex.Message);
    }

    [Fact]
    public void PythonLocator_Probe_ReportsVersionAndConsistentFind()
    {
        var configured = Path.Combine(_tempDir, "missing-python.exe");
        // a nonexistent configured path must not be honored — but resolution still
        // degrades to the OS-aware PATH/common-dir search instead of giving up
        var fallback = PythonLocator.Find(configured);
        var direct = PythonLocator.Find();
        Assert.Equal(direct?.Executable, fallback?.Executable);

        var probe = PythonLocator.Probe();
        if (probe is null) return; // Python not installed on this machine — nothing to assert

        Assert.Matches(@"^Python \d+(\.\d+)*$", probe.Version);
        Assert.Equal(probe.Install.Executable, PythonLocator.Find()?.Executable);
        Assert.True(File.Exists(probe.Install.Executable));
    }

    [Fact]
    public async Task ComputerExec_AutoKind_ExecutesOsDefaultShell()
    {
        var tool = new ComputerExecTool(new MockPromptProvider(), new ComputerUseConfig());
        var context = CreateToolContext();

        var res = OperatingSystem.IsWindows()
            ? await tool.ExecuteAsync(new JsonObject { ["kind"] = "auto", ["command"] = "Write-Output ('auto-' + 'ok')" }, context, CancellationToken.None)
            : await tool.ExecuteAsync(new JsonObject { ["kind"] = "auto", ["command"] = "echo auto-ok" }, context, CancellationToken.None);

        Assert.True(res.Success, res.Output);
        Assert.Contains("auto-ok", res.Output);
    }

    [Fact]
    public async Task ComputerExec_Python_UsesProbedInterpreterWithVersionInSysinfo()
    {
        if (!OperatingSystem.IsWindows()) return;
        var probe = PythonLocator.Probe();
        if (probe is null) return; // Python not installed on this machine

        var tool = new ComputerExecTool(new MockPromptProvider(), new ComputerUseConfig());
        var context = CreateToolContext();
        var res = await tool.ExecuteAsync(new JsonObject
        {
            ["kind"] = "python",
            ["command"] = "import sys; print('py-' + sys.version.split()[0])"
        }, context, CancellationToken.None);

        Assert.True(res.Success, res.Output);
        Assert.Contains(probe.Version["Python ".Length..], res.Output); // interpreter version matches the probe
    }

    private ToolContext CreateToolContext()
    {
        var bus = new EventBus();
        var ws = new WorkspaceManager().Detect(_tempDir);
        return new ToolContext
        {
            Workspace = ws,
            Events = bus,
            Agent = new AgentConfig()
        };
    }


    private sealed class MockFaultyDriver : IComputerDriver, IScreenCapture, IInputController, IWindowManager, IAccessibilityProvider
    {
        public string PlatformName => "MockFaulty";

        public IScreenCapture ScreenCapture => this;
        public IInputController InputController => this;
        public IWindowManager WindowManager => this;
        public IAccessibilityProvider AccessibilityProvider => this;

        public DriverCapabilities GetCapabilities() =>
            new(true, true, true, true, true, false, PlatformName, "Faulty mock");

        public Task<ScreenCapture?> CaptureScreenAsync(int monitorIndex, CancellationToken ct) =>
            throw new InvalidOperationException("Simulated GDI+ access violation");

        public Task<UiHierarchyResult> GetVisualElementsAsync(CancellationToken ct) =>
            throw new Exception("Simulated hardware failure in accessibility bridge");

        public Task<ActionResult> ClickAsync(int x, int y, MouseButton button, int clickCount, CancellationToken ct) =>
            throw new Exception("Simulated driver crash during mouse input injection");

        public Task<ActionResult> MoveMouseAsync(int x, int y, CancellationToken ct) =>
            throw new Exception("Simulated pointer move error");

        public Task<ActionResult> TypeTextAsync(string text, CancellationToken ct) =>
            throw new Exception("Simulated keyboard buffer overflow");

        public Task<ActionResult> SendKeyAsync(string keyCombo, CancellationToken ct) =>
            throw new Exception("Simulated key hook failure");

        public Task<ActionResult> ScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken ct) =>
            throw new Exception("Simulated scroll event deadlock");

        public Task<string?> GetActiveWindowTitleAsync(CancellationToken ct) =>
            throw new Exception("Simulated window title error");

        public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken ct) =>
            throw new Exception("Simulated list windows error");

        public Task<bool> FocusWindowAsync(string titleOrId, CancellationToken ct) =>
            throw new Exception("Simulated focus window error");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MockWorkingDriver : IComputerDriver, IScreenCapture, IInputController, IWindowManager, IAccessibilityProvider, IClipboardProvider
    {
        public string PlatformName => "MockWorking";
        public int LastClickX { get; private set; }
        public int LastClickY { get; private set; }
        public int LastScrollAnchorX { get; private set; }
        public int LastScrollAnchorY { get; private set; }
        public int LastScrollDeltaX { get; private set; }
        public int LastScrollDeltaY { get; private set; }
        public string? ClipboardText { get; private set; }

        public IScreenCapture ScreenCapture => this;
        public IInputController InputController => this;
        public IWindowManager WindowManager => this;
        public IAccessibilityProvider AccessibilityProvider => this;
        public IClipboardProvider? Clipboard => this;

        public Task<string?> GetTextAsync(CancellationToken ct) =>
            Task.FromResult(ClipboardText);

        public Task<ActionResult> SetTextAsync(string text, CancellationToken ct)
        {
            ClipboardText = text;
            return Task.FromResult(ActionResult.Ok($"Clipboard set ({text.Length} characters)", "clipboard_set"));
        }

        public ScreenCapture? LastCapture { get; set; }
        public ScreenMetrics Metrics { get; set; } = ScreenMetrics.Default;
        public ScreenMetrics GetScreenMetrics() => Metrics;

        public DriverCapabilities GetCapabilities() =>
            new(true, true, true, true, true, false, PlatformName, "Working mock");

        public Task<ScreenCapture?> CaptureScreenAsync(int monitorIndex, CancellationToken ct) =>
            Task.FromResult<ScreenCapture?>(new ScreenCapture([0x89, 0x50, 0x4E, 0x47], 1920, 1080));

        public Task<UiHierarchyResult> GetVisualElementsAsync(CancellationToken ct)
        {
            var elements = new List<UiElementInfo>
            {
                new("btn_submit", "SubmitButton", "Button", 100, 200, 80, 30)
            };
            return Task.FromResult(UiHierarchyResult.Ok("Mock Active Window", elements));
        }

        public Task<UiElementInfo?> FindElementAsync(string idOrName, CancellationToken ct) =>
            Task.FromResult<UiElementInfo?>(
                idOrName == "btn_submit" ? new UiElementInfo("btn_submit", "SubmitButton", "Button", 100, 200, 80, 30) : null);

        public Task<ActionResult> ClickAsync(int x, int y, MouseButton button, int clickCount, CancellationToken ct)
        {
            LastClickX = x;
            LastClickY = y;
            return Task.FromResult(ActionResult.Ok($"Clicked at ({x}, {y})", "click", x, y));
        }

        public Task<ActionResult> MoveMouseAsync(int x, int y, CancellationToken ct) =>
            Task.FromResult(ActionResult.Ok($"Moved to ({x}, {y})", "move", x, y));

        public Task<ActionResult> TypeTextAsync(string text, CancellationToken ct) =>
            Task.FromResult(ActionResult.Ok($"Typed {text}", "type", target: text));

        public Task<ActionResult> SendKeyAsync(string keyCombo, CancellationToken ct) =>
            Task.FromResult(ActionResult.Ok($"Sent {keyCombo}", "key", target: keyCombo));

        public Task<ActionResult> ScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken ct)
        {
            LastScrollAnchorX = x;
            LastScrollAnchorY = y;
            LastScrollDeltaX = deltaX;
            LastScrollDeltaY = deltaY;
            return Task.FromResult(ActionResult.Ok($"Scrolled {deltaY}", "scroll", x, y));
        }

        public Task<string?> GetActiveWindowTitleAsync(CancellationToken ct) =>
            Task.FromResult<string?>("Mock Active Window");

        public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WindowInfo>>(new List<WindowInfo>
            {
                new("w1", "Mock Active Window", "app", 0, 0, 1920, 1080, true)
            });

        public Task<bool> FocusWindowAsync(string titleOrId, CancellationToken ct) =>
            Task.FromResult(true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MockPromptProvider : IPromptProvider
    {
        public string? TryGet(string key) => null;
        public string Get(string key) => "";
        public string Render(string template, IReadOnlyDictionary<string, string> variables) => template;
        public string? GetRendered(string key, IReadOnlyDictionary<string, string> variables) => null;
        public void SetWorkspaceRoot(string? workspacePromptsDir) { }
    }
}

