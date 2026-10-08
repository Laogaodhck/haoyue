using System.Text;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;

namespace Haoyue.Runtime.ComputerUse.SystemControl;

/// <summary>
/// computer_browser_repair: detects and repairs browser homepage hijacking.
/// action='detect' is read-only and reports every suspicious homepage/startup
/// source (policy registry keys, IE/Shell start page, Chrome/Edge Preferences
/// JSON, Firefox prefs.js, hijacked shortcuts). action='repair' backs up all
/// findings to ~/.haoyue/browser-repair-backups/ first, then removes forced
/// policies, restores the homepage (default about:blank), clears hijacked
/// shortcut arguments, and rewrites preference files. Permission failures
/// (HKLM / /etc policies) are reported instead of aborting the whole repair.
/// </summary>
public sealed class BrowserRepairTool(IPromptProvider prompts, ISystemControlAdapter adapter) : BuiltinTool(prompts)
{
    public override string Name => "computer_browser_repair";
    public override string StatusLabel => "Repairing browser";
    public override bool Mutating => true;
    public override bool RequiresWorkspace => false;

    private static string BackupDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".haoyue", "browser-repair-backups");

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("action", ToolSchema.String("detect: 扫描劫持项（只读）; repair: 备份后自动修复", "detect", "repair"), true),
        ("homepage", ToolSchema.String("修复后恢复到的主页 URL（默认 about:blank）"), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var action = GetString(arguments, "action")?.Trim().ToLowerInvariant();
        var homepage = GetString(arguments, "homepage")?.Trim();
        if (string.IsNullOrEmpty(homepage)) homepage = "about:blank";

        switch (action)
        {
            case "detect":
            {
                var scan = await adapter.DetectBrowserHijackAsync(ct).ConfigureAwait(false);
                var report = FormatScan(scan);
                context.Events.Publish(new Haoyue.Runtime.Events.ComputerActionEvent(
                    Action: "browser_detect", X: null, Y: null,
                    Target: adapter.PlatformName, Success: scan.Supported,
                    Error: scan.Error, Base64Screenshot: null));
                return scan.Supported
                    ? ToolResult.Ok(report, $"Detected {scan.Findings.Count} hijack candidates")
                    : ToolResult.Fail(report);
            }

            case "repair":
            {
                var outcome = await adapter.RepairBrowserHomepageAsync(homepage, BackupDirectory, ct).ConfigureAwait(false);
                context.Events.Publish(new Haoyue.Runtime.Events.ComputerActionEvent(
                    Action: "browser_repair", X: null, Y: null,
                    Target: adapter.PlatformName, Success: outcome.Success,
                    Error: outcome.Errors.Count > 0 ? string.Join("; ", outcome.Errors) : null,
                    Base64Screenshot: null));

                var sb = new StringBuilder();
                sb.AppendLine(outcome.Success ? "[修复完成]" : "[修复部分完成，存在失败项]");
                sb.AppendLine($"恢复目标主页: {homepage}");
                if (outcome.BackupPath is not null)
                    sb.AppendLine($"备份文件: {outcome.BackupPath}");
                if (outcome.Actions.Count > 0)
                {
                    sb.AppendLine("\n[已执行的操作]");
                    foreach (var a in outcome.Actions) sb.AppendLine($"  ✓ {a}");
                }
                if (outcome.Errors.Count > 0)
                {
                    sb.AppendLine("\n[失败项]");
                    foreach (var e in outcome.Errors) sb.AppendLine($"  ✗ {e}");
                }
                sb.AppendLine("\n提示：部分设置需重启浏览器后生效。若浏览器仍被劫持，建议在浏览器设置中手动检查扩展与快捷方式。");
                return outcome.Success
                    ? ToolResult.Ok(sb.ToString(), $"Repaired browser homepage ({outcome.RepairedCount} actions)")
                    : ToolResult.Fail(sb.ToString());
            }

            default:
                return ToolResult.Fail("action 必须是 'detect' 或 'repair'。");
        }
    }

    internal static string FormatScan(BrowserScanReport scan)
    {
        if (!scan.Supported)
            return $"[平台不支持] {scan.Error ?? "当前系统没有浏览器修复适配器。"}";

        var sb = new StringBuilder();
        sb.AppendLine($"[平台] {scan.Platform}");
        if (scan.Findings.Count == 0)
        {
            sb.AppendLine("[结果] 未发现主页劫持项。浏览器主页配置正常。");
            return sb.ToString();
        }

        sb.AppendLine($"[结果] 发现 {scan.Findings.Count} 个可疑项：");
        for (var i = 0; i < scan.Findings.Count; i++)
        {
            var f = scan.Findings[i];
            sb.AppendLine($"\n[{i + 1}] 来源: {f.Source} · 类型: {f.Kind}");
            sb.AppendLine($"    位置: {f.Location}");
            sb.AppendLine($"    当前值: {f.CurrentValue}");
            sb.AppendLine($"    说明: {f.Detail}");
        }
        sb.AppendLine("\n下一步：确认后使用 action='repair' 自动修复（会先备份以上全部发现）。");
        return sb.ToString();
    }
}
