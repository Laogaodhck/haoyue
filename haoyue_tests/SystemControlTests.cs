using System.Text.Json.Nodes;
using Haoyue.Runtime;
using Haoyue.Runtime.ComputerUse;
using Haoyue.Runtime.ComputerUse.SystemControl;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Workspaces;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// System Control 子模块（computer_scan / computer_sysinfo / computer_browser_repair）
/// 的适配器选择、注册接线、文件系统扫描与浏览器修复工具行为测试。
/// </summary>
public sealed class SystemControlTests : IAsyncDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"haoyue_sysctl_test_{Guid.NewGuid():N}");

    public SystemControlTests() => Directory.CreateDirectory(_tempDir);

    public ValueTask DisposeAsync()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        return ValueTask.CompletedTask;
    }

    private static ToolContext Context(string dir) => new()
    {
        Workspace = new WorkspaceInfo { Root = dir, ProjectKinds = [] },
        Events = new EventBus(),
        Agent = new AgentConfig(),
    };

    // ---------- 工厂与配置 ----------

    [Fact]
    public void Factory_ResolvesPlatformAdapter()
    {
        var adapter = SystemControlFactory.Create();
        if (OperatingSystem.IsWindows()) Assert.IsType<WindowsSystemAdapter>(adapter);
        else if (OperatingSystem.IsLinux()) Assert.IsType<LinuxSystemAdapter>(adapter);
        else Assert.IsType<UnsupportedSystemAdapter>(adapter);
        Assert.NotEmpty(adapter.PlatformName);
    }

    [Fact]
    public void DefaultConfig_SystemControlEnabled()
    {
        var config = new HaoyueConfig();
        Assert.True(config.ComputerUse.SystemControlEnabled);
    }

    [Fact]
    public void Runtime_Enabled_RegistersSystemControlTools()
    {
        var store = new ConfigStore(Path.Combine(_tempDir, "c1.json"), Path.Combine(_tempDir, "s1.json"));
        store.Config.ComputerUse.Enabled = true;
        store.Config.ComputerUse.Driver = "vision";
        using var runtime = HaoyueRuntime.Create(_tempDir, store);

        Assert.NotNull(runtime.Tools.Resolve("computer_scan"));
        Assert.NotNull(runtime.Tools.Resolve("computer_sysinfo"));
        Assert.NotNull(runtime.Tools.Resolve("computer_browser_repair"));
    }

    [Fact]
    public void Runtime_SystemControlDisabled_DoesNotRegister()
    {
        var store = new ConfigStore(Path.Combine(_tempDir, "c2.json"), Path.Combine(_tempDir, "s2.json"));
        store.Config.ComputerUse.Enabled = true;
        store.Config.ComputerUse.Driver = "vision";
        store.Config.ComputerUse.SystemControlEnabled = false;
        using var runtime = HaoyueRuntime.Create(_tempDir, store);

        Assert.NotNull(runtime.Tools.Resolve("computer_exec"));
        Assert.Null(runtime.Tools.Resolve("computer_scan"));
        Assert.Null(runtime.Tools.Resolve("computer_sysinfo"));
        Assert.Null(runtime.Tools.Resolve("computer_browser_repair"));
    }

    // ---------- Python 检测 ----------

    [Fact]
    public void PythonLocator_ReturnsPathOrNullOrNeverThrows()
    {
        var install = PythonLocator.Find();
        if (install is not null)
        {
            Assert.True(File.Exists(install.Executable));
            Assert.NotEmpty(install.Source);
        }
        // 未安装时返回 null 是合法结果（降级提示而非异常）。
    }

    // ---------- 文件系统扫描 ----------

    [Fact]
    public void ScanFilesystem_ListsTree_AndLargestFiles()
    {
        var sub = Path.Combine(_tempDir, "a");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(_tempDir, "root.txt"), new string('x', 2048));
        File.WriteAllText(Path.Combine(sub, "b.bin"), new string('y', 1024));
        Directory.CreateDirectory(Path.Combine(_tempDir, ".hidden"));

        var (rootPath, report) = SystemScanTool.ScanFilesystem(_tempDir, 3, 500, showHidden: false);

        Assert.Equal(Path.GetFullPath(_tempDir), rootPath);
        Assert.Contains("root.txt", report);
        Assert.Contains("a/", report);
        Assert.Contains("b.bin", report);
        Assert.DoesNotContain(".hidden", report);
        Assert.Contains("2.0 KB", report);
        Assert.Contains("[最大的文件]", report);
        Assert.Contains("[汇总]", report);
    }

    [Fact]
    public void ScanFilesystem_RespectsEntryLimit_AndReportsTruncation()
    {
        for (var i = 0; i < 20; i++)
            File.WriteAllText(Path.Combine(_tempDir, $"f{i:00}.txt"), "z");

        var (_, report) = SystemScanTool.ScanFilesystem(_tempDir, 1, 5, showHidden: false);

        Assert.Contains("[截断]", report);
    }

    [Fact]
    public void ScanFilesystem_MissingPath_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            SystemScanTool.ScanFilesystem(Path.Combine(_tempDir, "no_such_dir"), 3, 100, false));
    }

    // ---------- computer_scan 工具 ----------

    [Fact]
    public async Task ComputerScan_Hardware_ReturnsAdapterReport()
    {
        var tool = new SystemScanTool(new FilePromptProvider(), new StubAdapter());
        var result = await tool.ExecuteAsync(
            new JsonObject { ["target"] = "hardware" }, Context(_tempDir), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Stub", result.Output);
        Assert.Contains("stub hardware", result.Output);
    }

    [Fact]
    public async Task ComputerScan_MissingPath_FailsGracefully()
    {
        var tool = new SystemScanTool(new FilePromptProvider(), new StubAdapter());
        var result = await tool.ExecuteAsync(
            new JsonObject { ["target"] = "filesystem", ["path"] = Path.Combine(_tempDir, "nope") },
            Context(_tempDir), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("路径不存在", result.Output);
    }

    [Fact]
    public async Task ComputerScan_UnknownTarget_Fails()
    {
        var tool = new SystemScanTool(new FilePromptProvider(), new StubAdapter());
        var result = await tool.ExecuteAsync(
            new JsonObject { ["target"] = "registry" }, Context(_tempDir), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("hardware", result.Output);
    }

    // ---------- computer_sysinfo 工具 ----------

    [Fact]
    public async Task ComputerSysInfo_Config_ReportsPythonStatus_AndRuntimeFacts()
    {
        var tool = new SystemInfoTool(new FilePromptProvider(), new StubAdapter(), new ComputerUseConfig());
        var result = await tool.ExecuteAsync(
            new JsonObject { ["section"] = "config" }, Context(_tempDir), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("stub config", result.Output);
        Assert.Contains("Python:", result.Output);
        Assert.Contains(".NET Runtime:", result.Output);
    }

    [Fact]
    public async Task ComputerSysInfo_UnknownSection_Fails()
    {
        var tool = new SystemInfoTool(new FilePromptProvider(), new StubAdapter(), new ComputerUseConfig());
        var result = await tool.ExecuteAsync(
            new JsonObject { ["section"] = "weather" }, Context(_tempDir), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("config", result.Output);
    }

    // ---------- computer_browser_repair 工具 ----------

    [Fact]
    public async Task BrowserRepair_Detect_FormatsFindings()
    {
        var stub = new StubAdapter
        {
            Scan = new BrowserScanReport("Stub", true,
            [
                new BrowserFinding("chrome-policy", @"HKCU\Software\Policies\Google\Chrome",
                    "Homepage", "http://hijack.example", "策略强制的主页（高度可疑）"),
            ]),
        };
        var tool = new BrowserRepairTool(new FilePromptProvider(), stub);
        var result = await tool.ExecuteAsync(
            new JsonObject { ["action"] = "detect" }, Context(_tempDir), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("hijack.example", result.Output);
        Assert.Contains("chrome-policy", result.Output);
        Assert.Contains("action='repair'", result.Output);
    }

    [Fact]
    public async Task BrowserRepair_Detect_NoFindings_Succeeds()
    {
        var tool = new BrowserRepairTool(new FilePromptProvider(), new StubAdapter());
        var result = await tool.ExecuteAsync(
            new JsonObject { ["action"] = "detect" }, Context(_tempDir), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("未发现主页劫持项", result.Output);
    }

    [Fact]
    public async Task BrowserRepair_Repair_ReportsBackup_AndErrors()
    {
        var stub = new StubAdapter
        {
            Repair = new BrowserRepairOutcome(
                Success: false,
                Actions: ["已删除注册表值 HKCU\\Software\\Policies\\Google\\Chrome\\Homepage"],
                Errors: ["HKLM\\Software\\Policies\\Google\\Chrome: 需要管理员权限"],
                BackupPath: Path.Combine(_tempDir, "backup.json"),
                RepairedCount: 1),
        };
        var tool = new BrowserRepairTool(new FilePromptProvider(), stub);
        var result = await tool.ExecuteAsync(
            new JsonObject { ["action"] = "repair", ["homepage"] = "https://example.com" },
            Context(_tempDir), CancellationToken.None);

        Assert.False(result.Success); // 存在失败项 → 部分完成
        Assert.Contains("backup.json", result.Output);
        Assert.Contains("https://example.com", result.Output);
        Assert.Contains("需要管理员权限", result.Output);
        Assert.Contains("已删除注册表值", result.Output);
    }

    [Fact]
    public async Task BrowserRepair_UnknownAction_Fails()
    {
        var tool = new BrowserRepairTool(new FilePromptProvider(), new StubAdapter());
        var result = await tool.ExecuteAsync(
            new JsonObject { ["action"] = "fixit" }, Context(_tempDir), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("detect", result.Output);
    }

    // ---------- 共享辅助逻辑 ----------

    [Theory]
    [InlineData("http://hijack.example", true)]
    [InlineData("https://weird-homepage.cn/?from=ads", true)]
    [InlineData("about:blank", false)]
    [InlineData("https://www.google.com", false)]
    [InlineData("https://www.bing.com/", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSuspicious_ClassifiesUrls(string? url, bool expected)
    {
        Assert.Equal(expected, BrowserPreferenceFiles.IsSuspicious(url));
    }

    [Fact]
    public void WriteBackup_CreatesJsonFile_WithFindings()
    {
        var dir = Path.Combine(_tempDir, "backups");
        var path = BrowserPreferenceFiles.WriteBackup(dir,
        [
            new BrowserFinding("ie-main", "HKCU\\Software\\Microsoft\\Internet Explorer\\Main",
                "Start Page", "http://x.example", "test"),
        ]);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        var json = File.ReadAllText(path!);
        Assert.Contains("ie-main", json);
        Assert.Contains("http://x.example", json);
    }

    // ---------- 真实环境冒烟（平台门控，只读安全） ----------

    [Fact]
    public async Task WindowsAdapter_DetectBrowserHijack_Smoke()
    {
        if (!OperatingSystem.IsWindows()) return;
        var report = await new WindowsSystemAdapter().DetectBrowserHijackAsync(CancellationToken.None);
        Assert.True(report.Supported);
        Assert.NotNull(report.Findings);
    }

    [Fact]
    public async Task WindowsAdapter_SystemConfig_Smoke()
    {
        if (!OperatingSystem.IsWindows()) return;
        var text = await new WindowsSystemAdapter().GetSystemConfigAsync(CancellationToken.None);
        Assert.Contains("=== OS ===", text);
    }

    [Fact]
    public async Task WindowsAdapter_ListProcesses_Smoke()
    {
        if (!OperatingSystem.IsWindows()) return;
        var text = await new WindowsSystemAdapter().ListProcessesAsync(null, 20, CancellationToken.None);
        Assert.Contains("PID", text);
    }

    // ---------- Stub ----------

    private sealed class StubAdapter : ISystemControlAdapter
    {
        public string PlatformName => "Stub";

        public BrowserScanReport Scan { get; set; } = new("Stub", true, []);
        public BrowserRepairOutcome Repair { get; set; } = new(true, [], [], null, 0);

        public Task<string> ScanHardwareAsync(CancellationToken ct) => Task.FromResult("stub hardware");
        public Task<string> GetSystemConfigAsync(CancellationToken ct) => Task.FromResult("stub config");
        public Task<string> ListProcessesAsync(string? filter, int limit, CancellationToken ct) => Task.FromResult("stub processes");
        public Task<BrowserScanReport> DetectBrowserHijackAsync(CancellationToken ct) => Task.FromResult(Scan);
        public Task<BrowserRepairOutcome> RepairBrowserHomepageAsync(string targetHomepage, string backupDirectory, CancellationToken ct) => Task.FromResult(Repair);
    }
}
