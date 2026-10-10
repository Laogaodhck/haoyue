using System.Net;

namespace Haoyue.Runtime.Tools;

/// <summary>One parsed allowlist entry: scheme + host pattern ("*.example.com" allowed).</summary>
public sealed record WebSiteRule(string Scheme, string Host, string? Port, string Source);

/// <summary>
/// 外部网站访问白名单（对应截图中的「允许的网站」功能）。语义：
/// - 空规则列表 = 不限制（功能未启用，向后兼容）；
/// - localhost / 127.x.x.x / ::1 始终允许，无论列表内容；
/// - 规则形式 scheme://host，host 支持 "*.example.com" 通配任意层级子域，
///   根域 example.com 需单独添加（与主流浏览器扩展语义一致）；
/// - 规则可带端口（https://example.com:8443）；不带端口时匹配任意端口；
/// - 仅 http/https 参与匹配，其余 scheme 一律拒绝。
/// 工具每次执行时从配置现读（无缓存），桌面端增删立即生效。
/// </summary>
public static class WebSiteAllowlist
{
    public static bool IsLocalHost(string host)
    {
        var h = host.Trim('[', ']').ToLowerInvariant();
        return h is "localhost" or "::1" || h.StartsWith("127.");
    }

    /// <summary>Parses raw config entries; invalid ones are skipped with their reason
    /// so the caller (settings UI) can surface them while the runtime stays strict.</summary>
    public static List<WebSiteRule> Parse(IEnumerable<string?> entries) =>
        entries.Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => ParseOne(e!.Trim()))
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();

    private static WebSiteRule? ParseOne(string entry)
    {
        // scheme://host[:port] — scheme is mandatory; bare "example.com" is rejected
        // so "http://localhost:3000" style intent can never silently become "any scheme".
        var schemeEnd = entry.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0) return null;
        var scheme = entry[..schemeEnd].ToLowerInvariant();
        if (scheme is not ("http" or "https")) return null;

        var hostPart = entry[(schemeEnd + 3)..];
        if (hostPart.Contains('/')) hostPart = hostPart[..hostPart.IndexOf('/')]; // tolerate trailing path
        if (hostPart.Length == 0) return null;

        string? port = null;
        if (hostPart.StartsWith('['))
        {
            // [::1]:8080 — IPv6 literal matched exactly; optional port after ']'
            var close = hostPart.IndexOf(']');
            if (close is > -1 && close < hostPart.Length - 1 && hostPart[close + 1] == ':')
            {
                port = hostPart[(close + 2)..];
                hostPart = hostPart[..(close + 1)];
            }
        }
        else if (hostPart.Count(c => c == ':') == 1)
        {
            var colon = hostPart.IndexOf(':');
            port = hostPart[(colon + 1)..];
            hostPart = hostPart[..colon];
            if (port.Length == 0 || !ushort.TryParse(port, out _)) return null;
        }
        // multiple ':' without brackets → bare IPv6 literal, matched exactly, no port

        var host = hostPart.ToLowerInvariant();
        if (host.Length == 0 || host.Contains('*') && !host.StartsWith("*.") || host == "*")
            return null; // only leading "*." wildcards are supported

        // canonical storage form: lowercase scheme/host, path stripped — so
        // "https://example.com/x" and "HTTPS://EXAMPLE.COM" dedupe to one entry
        return new WebSiteRule(scheme, host, port, $"{scheme}://{host}{(port is null ? "" : $":{port}")}");
    }

    /// <summary>Verdict for one URL against the parsed rules. Null rules = feature off.</summary>
    public static (bool Allowed, string? DenyReason) Evaluate(string url, IReadOnlyList<WebSiteRule>? rules)
    {
        if (rules is null || rules.Count == 0) return (true, null);

        Uri? uri;
        try { uri = new Uri(url); }
        catch { return (false, $"URL 无法解析：{url}"); }

        if (uri.Scheme is not ("http" or "https"))
            return (false, $"仅允许 http/https 访问（收到 {uri.Scheme}）。");

        if (IsLocalHost(uri.Host)) return (true, null); // 本地主机始终可用

        var host = uri.Host.ToLowerInvariant();
        var port = uri.IsDefaultPort ? null : uri.Port.ToString();

        foreach (var rule in rules)
        {
            if (rule.Scheme != uri.Scheme) continue;
            if (rule.Port is not null && rule.Port != port) continue;
            if (rule.Host.StartsWith("*."))
            {
                var suffix = rule.Host[1..]; // ".example.com"
                if (host.EndsWith(suffix, StringComparison.Ordinal)) return (true, null);
            }
            else if (string.Equals(rule.Host, host, StringComparison.Ordinal))
            {
                return (true, null);
            }
        }

        return (false,
            $"外部网站访问受白名单限制：{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")} 不在允许列表中。" +
            "可在桌面端「设置 → 允许的网站」添加，或由用户在配置 web.allowedSites 中加入该站点后重试。");
    }

    /// <summary>Normalizes user-supplied entries for storage: trims, drops invalid,
    /// dedupes (case-insensitive) — used by the settings RPC before saving.</summary>
    public static List<string> Normalize(IEnumerable<string?> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> result = [];
        foreach (var rule in Parse(entries))
        {
            if (seen.Add(rule.Source))
                result.Add(rule.Source);
        }
        return result;
    }
}
