using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Haoyue.Runtime.ComputerUse.SystemControl;

/// <summary>
/// Chrome/Edge/Chromium "Preferences" JSON and Firefox prefs.js homepage handling,
/// shared by the Windows and Linux adapters (paths differ, file formats do not).
/// Also owns the pre-repair backup serialization so the behaviour is testable
/// without touching a real registry or browser profile.
/// </summary>
public static partial class BrowserPreferenceFiles
{
    /// <summary>Hosts / URLs that are treated as legitimate defaults, never flagged as hijacks.</summary>
    public static readonly HashSet<string> KnownSafeHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "google.com", "www.google.com", "microsoft.com", "www.microsoft.com",
        "msn.com", "www.msn.com", "bing.com", "www.bing.com", "edge.microsoft.com",
        "about:blank", "about:home", "about:newtab", "newtab",
    };

    /// <summary>True when a homepage URL is not a well-known default (candidate hijack).</summary>
    public static bool IsSuspicious(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var trimmed = url.Trim().Trim('"');
        if (trimmed.Length == 0) return false;

        // about: scheme has no host — judge by the raw value.
        if (trimmed.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed is not ("about:blank" or "about:home" or "about:newtab");
        }

        // Full URLs: judge by host name only.
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            return !KnownSafeHosts.Contains(uri.Host.TrimEnd('.'));

        // Bare host / special values ("newtab" etc.).
        return !KnownSafeHosts.Contains(trimmed.TrimEnd('/'));
    }

    /// <summary>
    /// Inspects a Chrome/Edge "Preferences" JSON file for forced homepages and
    /// startup URLs. Adds one finding per suspicious value; the file path is the
    /// finding Location so repair can dedupe by file.
    /// </summary>
    public static void InspectPreferencesFile(string browser, string filePath, List<BrowserFinding> findings)
    {
        try
        {
            if (!File.Exists(filePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
            var root = doc.RootElement;

            if (root.TryGetProperty("homepage", out var homepage) &&
                homepage.ValueKind == JsonValueKind.String &&
                IsSuspicious(homepage.GetString()))
            {
                findings.Add(new BrowserFinding(
                    $"{browser}-preferences", filePath, "Homepage",
                    homepage.GetString()!, "浏览器配置文件中的主页被修改"));
            }

            if (root.TryGetProperty("session", out var session) &&
                session.ValueKind == JsonValueKind.Object)
            {
                if (session.TryGetProperty("startup_urls", out var urls) &&
                    urls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var url in urls.EnumerateArray())
                    {
                        if (url.ValueKind == JsonValueKind.String && IsSuspicious(url.GetString()))
                        {
                            findings.Add(new BrowserFinding(
                                $"{browser}-preferences", filePath, "StartupUrl",
                                url.GetString()!, "启动时强制打开的可疑页面"));
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // Corrupt or locked preferences file: skip silently — detection is best-effort.
        }
    }

    /// <summary>
    /// Rewrites a Preferences JSON file: clears startup_urls, restores the
    /// new-tab-page startup mode, and removes the custom homepage.
    /// Returns false when the file needs no change or cannot be written.
    /// </summary>
    public static bool RepairPreferencesFile(string filePath, List<string> actions, List<string> errors)
    {
        try
        {
            if (!File.Exists(filePath)) return false;
            var text = File.ReadAllText(filePath);
            // JsonDocument is immutable; work on a mutable JsonObject copy instead.
            var node = System.Text.Json.Nodes.JsonNode.Parse(text);
            if (node is not System.Text.Json.Nodes.JsonObject obj) return false;

            var changed = false;
            if (obj.Remove("homepage")) changed = true;
            if (obj["homepage_is_newtabpage"] is not null)
            {
                obj["homepage_is_newtabpage"] = true;
                changed = true;
            }
            if (obj["session"] is System.Text.Json.Nodes.JsonObject session)
            {
                if (session.Remove("startup_urls")) changed = true;
                if (session["restore_on_startup"] is not null)
                {
                    session["restore_on_startup"] = 5; // open the new tab page
                    changed = true;
                }
            }

            if (!changed) return false;

            File.WriteAllText(filePath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            actions.Add($"已重置浏览器配置文件: {filePath}");
            return true;
        }
        catch (Exception ex)
        {
            errors.Add($"浏览器配置文件修复失败 {filePath}: {ex.Message}");
            return false;
        }
    }

    [GeneratedRegex(@"user_pref\(""browser\.startup\.homepage"",\s*""(?<url>[^""]*)""\);")]
    private static partial Regex HomepagePrefRegex();

    /// <summary>Inspects a Firefox prefs.js for a suspicious startup homepage.</summary>
    public static void InspectFirefoxPrefs(string filePath, List<BrowserFinding> findings)
    {
        try
        {
            if (!File.Exists(filePath)) return;
            var match = HomepagePrefRegex().Match(File.ReadAllText(filePath));
            if (match.Success && IsSuspicious(match.Groups["url"].Value))
            {
                findings.Add(new BrowserFinding(
                    "firefox-prefs", filePath, "Homepage",
                    match.Groups["url"].Value, "Firefox 启动主页被修改"));
            }
        }
        catch (Exception)
        {
            // unreadable profile: skip silently
        }
    }

    /// <summary>Rewrites the Firefox homepage pref to the target value. Returns true when changed.</summary>
    public static bool RepairFirefoxPrefs(string filePath, string targetHomepage, List<string> actions, List<string> errors)
    {
        try
        {
            if (!File.Exists(filePath)) return false;
            var text = File.ReadAllText(filePath);
            var replacement = $"user_pref(\"browser.startup.homepage\", \"{targetHomepage}\");";
            var updated = HomepagePrefRegex().Replace(text, replacement);
            if (updated == text) return false;
            File.WriteAllText(filePath, updated);
            actions.Add($"已恢复 Firefox 主页: {filePath}");
            return true;
        }
        catch (Exception ex)
        {
            errors.Add($"Firefox 主页修复失败 {filePath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Serializes every finding to <paramref name="backupDirectory"/> before any
    /// mutation so a repair can always be rolled back by hand. Returns the backup
    /// file path, or null when the backup could not be written (caller must abort).
    /// </summary>
    public static string? WriteBackup(string backupDirectory, IReadOnlyList<BrowserFinding> findings)
    {
        try
        {
            Directory.CreateDirectory(backupDirectory);
            var path = Path.Combine(backupDirectory, $"browser-homepage-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            var payload = new
            {
                createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                note = "浏览器主页修复前备份。按 Location 定位注册表值/配置文件/快捷方式，手工恢复 CurrentValue 即可还原。",
                findings,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
