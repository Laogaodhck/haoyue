using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Haoyue.Runtime.ComputerUse.SystemControl;

/// <summary>
/// Windows implementation: hardware/system/process info via CIM (PowerShell),
/// browser homepage hijack detection and repair via registry (user-scope policies
/// and IE start page), Chrome/Edge preference files, Firefox prefs.js, and browser
/// shortcut arguments. HKLM policy removal requires elevation — failures are
/// reported in the outcome instead of thrown.
/// </summary>
public sealed class WindowsSystemAdapter : ISystemControlAdapter
{
    public string PlatformName => "Windows";

    private const string HardwareScript = """
$ProgressPreference='SilentlyContinue'
Write-Output '=== CPU ==='
Get-CimInstance Win32_Processor | Select-Object Name,Manufacturer,NumberOfCores,NumberOfLogicalProcessors,@{n='Clock_MHz';e={$_.MaxClockSpeed}} | Format-List | Out-String -Width 220
Write-Output '=== Memory ==='
$cs = Get-CimInstance Win32_ComputerSystem
'Total: {0:N1} GB' -f ($cs.TotalPhysicalMemory / 1GB)
Get-CimInstance Win32_PhysicalMemory | Select-Object BankLabel,Manufacturer,@{n='Capacity_GB';e={[math]::Round($_.Capacity/1GB,1)}},Speed | Format-Table -AutoSize | Out-String -Width 220
Write-Output '=== GPU ==='
Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,Status,@{n='VRAM_GB';e={[math]::Round($_.AdapterRAM/1GB,1)}} | Format-List | Out-String -Width 220
Write-Output '=== Physical Disks ==='
Get-CimInstance Win32_DiskDrive | Select-Object Model,InterfaceType,@{n='Size_GB';e={[math]::Round($_.Size/1GB,0)}} | Format-Table -AutoSize | Out-String -Width 220
Write-Output '=== Logical Volumes ==='
Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | Select-Object DeviceID,VolumeName,FileSystem,@{n='Total_GB';e={[math]::Round($_.Size/1GB,0)}},@{n='Free_GB';e={[math]::Round($_.FreeSpace/1GB,0)}} | Format-Table -AutoSize | Out-String -Width 220
Write-Output '=== Network Adapters ==='
Get-CimInstance Win32_NetworkAdapter -Filter 'PhysicalAdapter=True AND NetEnabled=True' | Select-Object Name,MACAddress,Speed | Format-List | Out-String -Width 220
Write-Output '=== Motherboard / BIOS ==='
Get-CimInstance Win32_BaseBoard | Select-Object Manufacturer,Product,Version | Format-List | Out-String -Width 220
Get-CimInstance Win32_BIOS | Select-Object Manufacturer,SMBIOSBIOSVersion,ReleaseDate | Format-List | Out-String -Width 220
""";

    private const string SystemConfigScript = """
$ProgressPreference='SilentlyContinue'
Write-Output '=== OS ==='
Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber,OSArchitecture,LastBootUpTime | Format-List | Out-String -Width 220
Write-Output '=== Machine ==='
$cs = Get-CimInstance Win32_ComputerSystem
$os = Get-CimInstance Win32_OperatingSystem
'ComputerName: ' + $env:COMPUTERNAME
'User: ' + $env:USERDOMAIN + '\' + $env:USERNAME
'Manufacturer: ' + $cs.Manufacturer
'Model: ' + $cs.Model
'Uptime: ' + ((Get-Date) - $os.LastBootUpTime)
Write-Output '=== Locale & Time Zone ==='
'TimeZone: ' + (Get-TimeZone).Id
'Culture: ' + (Get-Culture).Name
'UI Culture: ' + (Get-UICulture).Name
Write-Output '=== Power Scheme ==='
powercfg /getactivescheme
Write-Output '=== Display ==='
Add-Type -AssemblyName System.Windows.Forms
foreach ($s in [System.Windows.Forms.Screen]::AllScreens) {
  $tag = ''
  if ($s.Primary) { $tag = ' (primary)' }
  Write-Output ($s.DeviceName + ' ' + $s.Bounds.Width + 'x' + $s.Bounds.Height + $tag)
}
Write-Output '=== Page File ==='
Get-CimInstance Win32_PageFileUsage | Select-Object Name,@{n='Size_MB';e={$_.AllocatedBaseSize}},@{n='Used_MB';e={$_.CurrentUsage}} | Format-Table -AutoSize | Out-String -Width 220
""";

    private const string ProcessScript = """
$ProgressPreference='SilentlyContinue'
Get-Process | ForEach-Object {
  $cpu = 0
  if ($_.CPU) { $cpu = [math]::Round($_.CPU, 1) }
  [PSCustomObject]@{ Name = $_.Name; PID = $_.Id; CPU_s = $cpu; Mem_MB = [math]::Round($_.WorkingSet64 / 1MB, 0) }
} | Sort-Object CPU_s -Descending | Select-Object -First 200 | Format-Table -AutoSize | Out-String -Width 200
""";

    private const string ShortcutScanScript = """
$ProgressPreference='SilentlyContinue'
$dirs = @(
  [Environment]::GetFolderPath('Desktop'),
  [Environment]::GetFolderPath('CommonDesktopDirectory'),
  (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch')
) | Where-Object { $_ -and (Test-Path $_) }
$ws = New-Object -ComObject WScript.Shell
foreach ($d in $dirs) {
  Get-ChildItem -LiteralPath $d -Recurse -Filter *.lnk -ErrorAction SilentlyContinue | ForEach-Object {
    try {
      $s = $ws.CreateShortcut($_.FullName)
      if ($s.TargetPath -match '(?i)chrome|msedge|firefox|iexplore' -and $s.Arguments -match '(?i)https?://|www\.') {
        "HIJACKED`t$($_.FullName)`t$($s.Arguments)"
      }
    } catch { }
  }
}
""";

    public async Task<string> ScanHardwareAsync(CancellationToken ct)
    {
        var result = await ShellCommandRunner.RunAsync(HardwareScript, timeoutSeconds: 120, ct: ct).ConfigureAwait(false);
        return Render(result, "硬件扫描");
    }

    public async Task<string> GetSystemConfigAsync(CancellationToken ct)
    {
        var result = await ShellCommandRunner.RunAsync(SystemConfigScript, timeoutSeconds: 90, ct: ct).ConfigureAwait(false);
        return Render(result, "系统配置读取");
    }

    public async Task<string> ListProcessesAsync(string? filter, int limit, CancellationToken ct)
    {
        var result = await ShellCommandRunner.RunAsync(ProcessScript, timeoutSeconds: 90, ct: ct).ConfigureAwait(false);
        if (result.Error is not null)
            return $"[错误] {result.Error}";

        // Out-String emits a leading blank line — locate the real table header first.
        var lines = result.Output.Replace("\r", "").Split('\n');
        var headerIndex = Array.FindIndex(lines, l => !string.IsNullOrWhiteSpace(l));
        var header = headerIndex >= 0 ? lines[headerIndex] : "";
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

    public async Task<BrowserScanReport> DetectBrowserHijackAsync(CancellationToken ct)
    {
        var findings = new List<BrowserFinding>();

        // 1. Policy registry keys (hijackers love forcing homepages through policy).
        InspectPolicyKey(Registry.CurrentUser, @"Software\Policies\Google\Chrome", "HKCU", "chrome-policy", findings);
        InspectPolicyKey(Registry.LocalMachine, @"Software\Policies\Google\Chrome", "HKLM", "chrome-policy", findings);
        InspectPolicyKey(Registry.CurrentUser, @"Software\Policies\Microsoft\Edge", "HKCU", "edge-policy", findings);
        InspectPolicyKey(Registry.LocalMachine, @"Software\Policies\Microsoft\Edge", "HKLM", "edge-policy", findings);

        // 2. IE / Shell start page.
        try
        {
            using var main = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Internet Explorer\Main");
            var startPage = main?.GetValue("Start Page") as string;
            if (!string.IsNullOrWhiteSpace(startPage) && BrowserPreferenceFiles.IsSuspicious(startPage))
            {
                findings.Add(new BrowserFinding(
                    "ie-main", @"HKCU\Software\Microsoft\Internet Explorer\Main",
                    "Start Page", startPage, "IE/Shell 默认主页被修改"));
            }
        }
        catch (Exception)
        {
            // Registry read failure: skip, other probes still run.
        }

        // 3. Chrome/Edge profile preference files (every profile).
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        ScanPreferenceProfiles("chrome", Path.Combine(localAppData, "Google", "Chrome", "User Data"), findings);
        ScanPreferenceProfiles("edge", Path.Combine(localAppData, "Microsoft", "Edge", "User Data"), findings);

        // 4. Firefox profiles.
        ScanFirefoxProfiles(findings);

        // 5. Hijacked browser shortcuts (appended URL arguments).
        try
        {
            var shortcut = await ShellCommandRunner.RunAsync(ShortcutScanScript, timeoutSeconds: 60, ct: ct).ConfigureAwait(false);
            if (shortcut.Error is null)
            {
                foreach (var line in shortcut.Output.Split('\n'))
                {
                    var parts = line.TrimEnd('\r').Split('\t');
                    if (parts.Length >= 3 && parts[0] == "HIJACKED")
                    {
                        findings.Add(new BrowserFinding(
                            "shortcut", parts[1], "ShortcutArgument", parts[2],
                            "浏览器快捷方式被追加了劫持网址参数"));
                    }
                }
            }
        }
        catch (Exception)
        {
            // Shortcut scan is best-effort.
        }

        return new BrowserScanReport("Windows", Supported: true, findings);
    }

    public async Task<BrowserRepairOutcome> RepairBrowserHomepageAsync(string targetHomepage, string backupDirectory, CancellationToken ct)
    {
        // Fresh scan: never repair based on stale findings.
        var scan = await DetectBrowserHijackAsync(ct).ConfigureAwait(false);
        if (scan.Findings.Count == 0)
            return new BrowserRepairOutcome(true, ["未发现主页劫持项，无需修复"], [], null, 0);

        var backupPath = BrowserPreferenceFiles.WriteBackup(backupDirectory, scan.Findings);
        if (backupPath is null)
            return new BrowserRepairOutcome(false, [], ["备份写入失败，已中止修复（保证可回滚）"], null, 0);

        var actions = new List<string>();
        var errors = new List<string>();

        // 1. Remove policy registry values (HKCU freely; HKLM needs elevation).
        RemovePolicyValues(Registry.CurrentUser, @"Software\Policies\Google\Chrome", "HKCU", actions, errors);
        RemovePolicyValues(Registry.CurrentUser, @"Software\Policies\Microsoft\Edge", "HKCU", actions, errors);
        RemovePolicyValues(Registry.LocalMachine, @"Software\Policies\Google\Chrome", "HKLM", actions, errors);
        RemovePolicyValues(Registry.LocalMachine, @"Software\Policies\Microsoft\Edge", "HKLM", actions, errors);

        // 2. Restore IE/Shell start page.
        try
        {
            using var main = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Internet Explorer\Main", writable: true);
            if (main is not null && main.GetValue("Start Page") is string current &&
                BrowserPreferenceFiles.IsSuspicious(current))
            {
                main.SetValue("Start Page", targetHomepage);
                actions.Add($"已将 IE/Shell 起始页恢复为 {targetHomepage}");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"IE 起始页修复失败: {ex.Message}");
        }

        // 3. Preference files (one file may hold several findings — dedupe by path).
        foreach (var file in scan.Findings
                     .Where(f => f.Source is "chrome-preferences" or "edge-preferences")
                     .Select(f => f.Location)
                     .Distinct())
        {
            BrowserPreferenceFiles.RepairPreferencesFile(file, actions, errors);
        }

        // 4. Firefox prefs.js.
        foreach (var file in scan.Findings
                     .Where(f => f.Source == "firefox-prefs")
                     .Select(f => f.Location)
                     .Distinct())
        {
            BrowserPreferenceFiles.RepairFirefoxPrefs(file, targetHomepage, actions, errors);
        }

        // 5. Strip hijacked shortcut arguments.
        foreach (var shortcut in scan.Findings.Where(f => f.Source == "shortcut"))
        {
            await RepairShortcutAsync(shortcut.Location, actions, errors, ct).ConfigureAwait(false);
        }

        return new BrowserRepairOutcome(errors.Count == 0, actions, errors, backupPath, actions.Count);
    }

    private static string Render(ShellOutcome result, string label)
    {
        if (result.Error is not null)
            return $"[错误] {result.Error}";
        if (!result.Success)
            return $"{result.Output}\n[注意] {label}部分查询失败（exit {result.ExitCode}），以上为可用结果。";
        return result.Output;
    }

    private static void InspectPolicyKey(
        RegistryKey root, string subKey, string hive, string source, List<BrowserFinding> findings)
    {
        try
        {
            using var key = root.OpenSubKey(subKey);
            if (key is null) return;

            if (key.GetValue("Homepage") is string homepage && homepage.Length > 0)
            {
                findings.Add(new BrowserFinding(
                    source, $"{hive}\\{subKey}", "Homepage", homepage,
                    BrowserPreferenceFiles.IsSuspicious(homepage)
                        ? "策略强制的主页（高度可疑）"
                        : "策略设置了主页"));
            }

            if (key.OpenSubKey("RestoreOnStartupURLs") is { } urls)
            {
                using (urls)
                {
                    foreach (var name in urls.GetValueNames())
                    {
                        if (urls.GetValue(name) is string url && url.Length > 0)
                        {
                            findings.Add(new BrowserFinding(
                                source, $"{hive}\\{subKey}\\RestoreOnStartupURLs",
                                "StartupUrl", url, "策略强制的启动页"));
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // Registry read failure: skip, other probes still run.
        }
    }

    private static void RemovePolicyValues(
        RegistryKey root, string subKey, string hive, List<string> actions, List<string> errors)
    {
        try
        {
            using var key = root.OpenSubKey(subKey, writable: true);
            if (key is null) return;
            foreach (var valueName in new[] { "Homepage", "HomepageIsNewTabPage", "RestoreOnStartup" })
            {
                if (key.GetValue(valueName) is not null)
                {
                    key.DeleteValue(valueName, throwOnMissingValue: false);
                    actions.Add($"已删除注册表值 {hive}\\{subKey}\\{valueName}");
                }
            }
            if (key.OpenSubKey("RestoreOnStartupURLs") is not null)
            {
                key.DeleteSubKeyTree("RestoreOnStartupURLs");
                actions.Add($"已删除策略启动页 {hive}\\{subKey}\\RestoreOnStartupURLs");
            }
        }
        catch (UnauthorizedAccessException)
        {
            errors.Add($"{hive}\\{subKey}: 需要管理员权限（HKLM 策略键），请以管理员身份运行后重试");
        }
        catch (Exception ex)
        {
            errors.Add($"{hive}\\{subKey}: {ex.Message}");
        }
    }

    private static void ScanPreferenceProfiles(string browser, string userDataDir, List<BrowserFinding> findings)
    {
        try
        {
            if (!Directory.Exists(userDataDir)) return;
            foreach (var profile in Directory.EnumerateDirectories(userDataDir))
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

    private static void ScanFirefoxProfiles(List<BrowserFinding> findings)
    {
        try
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var profilesDir = Path.Combine(roaming, "Mozilla", "Firefox", "Profiles");
            if (!Directory.Exists(profilesDir)) return;
            foreach (var profile in Directory.EnumerateDirectories(profilesDir))
            {
                BrowserPreferenceFiles.InspectFirefoxPrefs(
                    Path.Combine(profile, "prefs.js"), findings);
            }
        }
        catch (Exception)
        {
            // Profile enumeration failure: skip, other probes still run.
        }
    }

    private static async Task RepairShortcutAsync(string shortcutPath, List<string> actions, List<string> errors, CancellationToken ct)
    {
        var escaped = shortcutPath.Replace("'", "''");
        var script = $$"""
$ProgressPreference='SilentlyContinue'
$ws = New-Object -ComObject WScript.Shell
$s = $ws.CreateShortcut('{{escaped}}')
if ($s) {
  $s.Arguments = ''
  $s.Save()
  Write-Output 'OK'
}
""";
        var result = await ShellCommandRunner.RunAsync(script, timeoutSeconds: 60, ct: ct).ConfigureAwait(false);
        if (result.Success && result.Output.Contains("OK"))
            actions.Add($"已清除快捷方式劫持参数: {shortcutPath}");
        else
            errors.Add($"快捷方式修复失败 {shortcutPath}: {result.Error ?? result.Output}");
    }
}
