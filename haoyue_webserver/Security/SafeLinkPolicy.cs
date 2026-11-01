using System.Net;

namespace haoyue_webserver.Security;

/// <summary>
/// URL scheme allow-list for user-authored marketplace content. Markdig's DisableHtml()
/// only removes raw tags; it deliberately leaves link destinations untouched, so
/// javascript:/data: hrefs would otherwise reach the page verbatim.
/// </summary>
public static class SafeLinkPolicy
{
    private static readonly HashSet<string> NavigationSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "mailto"
    };

    private static readonly HashSet<string> ImageSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https"
    };

    public static bool IsSafeLink(string? url) => IsAllowed(url, NavigationSchemes);

    public static bool IsSafeImage(string? url) => IsAllowed(url, ImageSchemes);

    /// <summary>Homepage must be an absolute http(s) URL; relative and mailto values are rejected.</summary>
    public static bool IsSafeHomepage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        var normalized = Normalize(url);
        return HasScheme(normalized, out var scheme)
               && (scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                   || scheme.Equals("https", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAllowed(string? url, HashSet<string> allowedSchemes)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        var normalized = Normalize(url);
        // No scheme means a relative/reference URL; the browser resolves it against this site.
        if (!HasScheme(normalized, out var scheme))
        {
            return true;
        }

        return allowedSchemes.Contains(scheme);
    }

    private static string Normalize(string url)
    {
        // Browsers strip ASCII whitespace/control characters before parsing the scheme,
        // so "java\nscript:" must be rejected just like "javascript:".
        var decoded = WebUtility.HtmlDecode(url);
        var builder = new char[decoded.Length];
        var length = 0;
        foreach (var ch in decoded)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                continue;
            }

            builder[length++] = ch;
        }

        return new string(builder, 0, length);
    }

    private static bool HasScheme(string url, out string scheme)
    {
        scheme = string.Empty;
        if (url.Length == 0 || !char.IsAsciiLetter(url[0]))
        {
            return false;
        }

        for (var i = 0; i < url.Length; i++)
        {
            var ch = url[i];
            if (ch == ':')
            {
                scheme = url[..i];
                return scheme.Length > 0;
            }

            var valid = char.IsAsciiLetterOrDigit(ch) || ch is '+' or '-' or '.';
            if (!valid)
            {
                return false;
            }
        }

        return false;
    }
}
