using System.Text;

namespace Haoyue.Runtime.ComputerUse.SystemControl;

/// <summary>
/// Linux implementation: hardware/system/process info via standard tools
/// (lscpu/free/lsblk/lspci/df/ip, /proc fallbacks) with per-probe degradation when
/// a tool is missing, and browser homepage hijack handling via Chrome/Chromium/Edge
/// Preferences JSON, Firefox prefs.js, and /etc policy JSON files. Policy files
/// under /etc typically need root — write failures are reported as sudo hints.
/// </summary>
public sealed class LinuxSystemAdapter : ISystemControlAdapter
{
    public string PlatformName => "Linux";

    private const string HardwareScript = """
echo '=== CPU ==='
if command -v lscpu >/dev/null 2>&1; then
  lscpu | grep -E 'Model name|^CPU\(s\)|Core|Socket|MHz' | head -10
else
  grep -m1 'model name' /proc/cpuinfo 2>/dev/null || echo '(CPU info unavailable)'
fi
echo '=== Memory ==='
free -h 2>/dev/null || head -3 /proc/meminfo
echo '=== GPU ==='
if command -v lspci >/dev/null 2>&1; then
  lspci | grep -Ei 'vga|3d|display' || echo '(no GPU found)'
else
  echo '(lspci unavailable)'
fi
echo '=== Block Devices ==='
if command -v lsblk >/dev/null 2>&1; then
  lsblk -o NAME,SIZE,TYPE,FSTYPE,MOUNTPOINT 2>/dev/null || echo '(lsblk failed)'
else
  echo '(lsblk unavailable)'
fi
echo '=== Filesystems ==='
df -h 2>/dev/null | head -12
echo '=== Network ==='
if command -v ip >/dev/null 2>&1; then
  ip -brief address 2>/dev/null || echo '(ip failed)'
elif command -v ifconfig >/dev/null 2>&1; then
  ifconfig | head -20
else
  echo '(no network tool)'
fi
echo '=== Motherboard ==='
if [ -r /sys/devices/virtual/dmi/id/board_vendor ]; then
  echo "Vendor: $(cat /sys/devices/virtual/dmi/id/board_vendor 2>/dev/null) / Product: $(cat /sys/devices/virtual/dmi/id/board_name 2>/dev/null)"
else
  echo '(DMI info unavailable, may need root)'
fi
""";

    private const string SystemConfigScript = """
echo '=== Kernel ==='
uname -a
echo '=== Distribution ==='
grep -E '^(NAME|VERSION|PRETTY_NAME)=' /etc/os-release 2>/dev/null || echo '(/etc/os-release unavailable)'
echo '=== Uptime / Load ==='
uptime 2>/dev/null || cat /proc/uptime
echo '=== Desktop Environment ==='
echo "DE: ${XDG_CURRENT_DESKTOP:-unknown} / Session: ${XDG_SESSION_TYPE:-unknown}"
echo '=== Time & Locale ==='
timedatectl 2>/dev/null | head -5 || (date; cat /etc/timezone 2>/dev/null)
grep -E '^(LANG|LC_ALL)=' /etc/locale.conf 2>/dev/null || echo "$LANG"
echo '=== Root Filesystem ==='
df -h / 2>/dev/null
echo '=== Init System ==='
ps -p 1 -o comm= 2>/dev/null || echo '(unknown)'
""";

    private const string ProcessScript = """
ps -eo pid,ppid,user,pcpu,pmem,comm --sort=-pcpu 2>/dev/null | head -120
""";

    private static readonly string[] PolicyManagedDirs =
    [
        "/etc/opt/chrome/policies/managed",
        "/etc/chromium/policies/managed",
        "/etc/opt/edge/policies/managed",
    ];

    private static readonly (string Browser, string UserDir)[] PreferenceRoots =
    [
        ("chrome", "google-chrome"),
        ("chromium", "chromium"),
        ("edge", "microsoft-edge"),
    ];

    public async Task<string> ScanHardwareAsync(CancellationToken ct)
    {
        var result = await ShellCommandRunner.RunAsync(HardwareScript, timeoutSeconds: 60, ct: ct).ConfigureAwait(false);
        return result.Error is not null ? $"[错误] {result.Error}" : result.Output;
    }

    public async Task<string> GetSystemConfigAsync(CancellationToken ct)
    {
        var result = await ShellCommandRunner.RunAsync(SystemConfigScript, timeoutSeconds: 60, ct: ct).ConfigureAwait(false);
        return result.Error is not null ? $"[错误] {result.Error}" : result.Output;
    }

    public async Task<string> ListProcessesAsync(string? filter, int limit, CancellationToken ct)
    {
        var result = await ShellCommandRunner.RunAsync(ProcessScript, timeoutSeconds: 60, ct: ct).ConfigureAwait(false);
        if (result.Error is not null)
            return $"[错误] {result.Error}";

        var lines = result.Output.Replace("\r", "").Split('\n');
        var headerIndex = Array.FindIndex(lines, l => !string.IsNullOrWhiteSpace(l));
        var header = headerIndex >= 0 ? lines[headerIndex] : "PID  PPID  USER  %CPU  %MEM  COMMAND";
        var rows = lines.Skip(headerIndex + 1).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (!string.IsNullOrWhiteSpace(filter))
            rows = rows.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        var sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine(string.IsNullOrWhiteSpace(filter) ? "(按 CPU 占用降序)" : $"(名称包含 \"{filter}\"，按 CPU 降序)");
        foreach (var row in rows.Take(Math.Clamp(limit, 1, 200)))
            sb.AppendLine(row.TrimEnd());
        return sb.ToString();
    }

    public Task<BrowserScanReport> DetectBrowserHijackAsync(CancellationToken ct)
    {
        var findings = new List<BrowserFinding>();

        // 1. Chrome/Chromium/Edge profile preference files.
        var configHome = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var (browser, dir) in PreferenceRoots)
        {
            var root = Path.Combine(configHome, dir);
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var profile in Directory.EnumerateDirectories(root))
                {
                    BrowserPreferenceFiles.InspectPreferencesFile(
                        browser, Path.Combine(profile, "Preferences"), findings);
                }
            }
            catch (Exception)
            {
                // Profile enumeration failure: skip, other probes still run.
            }
        }

        // 2. Firefox profiles.
        try
        {
            var firefoxRoot = Path.Combine(configHome, "mozilla", "firefox");
            if (Directory.Exists(firefoxRoot))
            {
                foreach (var profile in Directory.EnumerateDirectories(firefoxRoot))
                {
                    BrowserPreferenceFiles.InspectFirefoxPrefs(
                        Path.Combine(profile, "prefs.js"), findings);
                }
            }
        }
        catch (Exception)
        {
            // Profile enumeration failure: skip, other probes still run.
        }

        // 3. System-wide policy JSON files (managed by admin tools or hijackers with root).
        foreach (var dir in PolicyManagedDirs)
        {
            InspectPolicyDirectory(dir, findings);
        }

        return Task.FromResult(new BrowserScanReport("Linux", Supported: true, findings));
    }

    public Task<BrowserRepairOutcome> RepairBrowserHomepageAsync(string targetHomepage, string backupDirectory, CancellationToken ct)
    {
        var scan = DetectBrowserHijackAsync(ct).GetAwaiter().GetResult();
        if (scan.Findings.Count == 0)
            return Task.FromResult(new BrowserRepairOutcome(true, ["未发现主页劫持项，无需修复"], [], null, 0));

        var backupPath = BrowserPreferenceFiles.WriteBackup(backupDirectory, scan.Findings);
        if (backupPath is null)
            return Task.FromResult(new BrowserRepairOutcome(false, [], ["备份写入失败，已中止修复（保证可回滚）"], null, 0));

        var actions = new List<string>();
        var errors = new List<string>();

        // 1. Preference files (dedupe by path).
        foreach (var file in scan.Findings
                     .Where(f => f.Source.EndsWith("-preferences", StringComparison.Ordinal))
                     .Select(f => f.Location)
                     .Distinct())
        {
            BrowserPreferenceFiles.RepairPreferencesFile(file, actions, errors);
        }

        // 2. Firefox prefs.js.
        foreach (var file in scan.Findings
                     .Where(f => f.Source == "firefox-prefs")
                     .Select(f => f.Location)
                     .Distinct())
        {
            BrowserPreferenceFiles.RepairFirefoxPrefs(file, targetHomepage, actions, errors);
        }

        // 3. Policy files under /etc — need root; report clearly when denied.
        foreach (var file in scan.Findings
                     .Where(f => f.Source.EndsWith("-policy", StringComparison.Ordinal))
                     .Select(f => f.Location)
                     .Distinct())
        {
            try
            {
                File.Delete(file);
                actions.Add($"已删除策略文件: {file}");
            }
            catch (UnauthorizedAccessException)
            {
                errors.Add($"{file}: 需要 root 权限，请用 sudo 手动删除后重试");
            }
            catch (Exception ex)
            {
                errors.Add($"策略文件删除失败 {file}: {ex.Message}");
            }
        }

        return Task.FromResult(new BrowserRepairOutcome(errors.Count == 0, actions, errors, backupPath, actions.Count));
    }

    private static void InspectPolicyDirectory(string dir, List<BrowserFinding> findings)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    if (root.ValueKind != System.Text.Json.JsonValueKind.Object) continue;

                    if (root.TryGetProperty("Homepage", out var homepage) &&
                        homepage.ValueKind == System.Text.Json.JsonValueKind.String &&
                        BrowserPreferenceFiles.IsSuspicious(homepage.GetString()))
                    {
                        findings.Add(new BrowserFinding(
                            "linux-policy", file, "Homepage", homepage.GetString()!,
                            "系统级策略强制的主页（高度可疑）"));
                    }
                    if (root.TryGetProperty("RestoreOnStartupURLs", out var urls) &&
                        urls.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var url in urls.EnumerateArray())
                        {
                            if (url.ValueKind == System.Text.Json.JsonValueKind.String &&
                                BrowserPreferenceFiles.IsSuspicious(url.GetString()))
                            {
                                findings.Add(new BrowserFinding(
                                    "linux-policy", file, "StartupUrl", url.GetString()!,
                                    "系统级策略强制的启动页"));
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // Malformed policy JSON: skip.
                }
            }
        }
        catch (Exception)
        {
            // Policy dir unreadable (permission): skip.
        }
    }
}
