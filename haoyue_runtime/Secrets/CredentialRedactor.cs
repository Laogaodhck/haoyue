using System.Text.RegularExpressions;

namespace Haoyue.Runtime.Secrets;

/// <summary>
/// Strips credential material out of text before it is persisted (audit trails,
/// captured command lines) or surfaced in a summary. Commands routinely carry
/// <c>Authorization: Bearer …</c> headers, <c>?key=…</c> query strings and inline
/// <c>--api-key</c> flags; those must not end up readable in a plain JSONL file on disk.
/// Replacement keeps the surrounding syntax so the record still says which header or flag
/// was used. Idempotent: redacted output no longer matches any rule.
/// </summary>
public static partial class CredentialRedactor
{
    public const string Marker = "[redacted]";

    public static string Redact(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";
        var result = KeyValueRules().Replace(text, m => m.Groups[1].Value + Marker);
        result = HeaderRules().Replace(result, m => m.Groups[1].Value + Marker);
        result = UrlSecretRules().Replace(result, m => m.Groups[1].Value + Marker + "@");
        result = QueryRules().Replace(result, m => m.Groups[1].Value + Marker);
        result = FlagRules().Replace(result, m => m.Groups[1].Value + Marker);
        result = AwsRules().Replace(result, m => m.Groups[1].Value + new string('*', 12));
        result = TokenRules().Replace(result, m => m.Groups[1].Value + m.Groups[2].Value + new string('*', 12));
        return result;
    }

    // "password": "…" / api_key = '…' / token: …  — keeps the key, drops the value.
    [GeneratedRegex(
        @"(?i)\b(passwo(?:rd)?|api[_-]?key|apikey|access[_-]?key(?:[_-]?id|[_-]?secret)?|secret|auth[_-]?token|access[_-]?token|refresh[_-]?token|client[_-]?secret|bearer)\b(\s*[:=]\s*[!-~]?)\s*[""']?([^""'\s,;}\]]{6,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueRules();

    // Authorization: Bearer … / X-Api-Key: … — HTTP header form, with or without quotes.
    [GeneratedRegex(
        @"(?i)\b(authorization\s*:?\s*[""']?(?:bearer|basic|token|apikey|api-key)\s+|x-(?:api-?key|auth-?token)\s*:?\s*[""']?)([^""'\s,;]{6,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex HeaderRules();

    // scheme://user:password@host — only the password part is dropped.
    [GeneratedRegex(
        @"(?i)(\b[a-z][a-z0-9+.\-]*://[^\s/:@]*:)([^\s/@]{6,})(@)",
        RegexOptions.CultureInvariant)]
    private static partial Regex UrlSecretRules();

    // ?key=… / &api_key=… / &access_token=…
    [GeneratedRegex(
        @"(?i)([?&](?:key|api[_-]?key|apikey|access[_-]?token|token|sig|signature)=)([^&\s""']{6,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex QueryRules();

    // --api-key=… / --password … / --access-key-id … style CLI flags.
    [GeneratedRegex(
        @"(?i)(--(?:api[_-]?key|apikey|token|password|secret|access[_-]?key(?:[_-]?(?:id|secret))?|secret[_-]?access[_-]?key)(?:=|\s+))(\S{6,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex FlagRules();

    // AWS access key ids: fixed prefix welded straight onto 16 base-32 characters, no separator.
    [GeneratedRegex(
        @"\b(AKIA|ASIA)[A-Z0-9]{16}",
        RegexOptions.CultureInvariant)]
    private static partial Regex AwsRules();

    // Well-known provider/convention token shapes: OpenAI sk-, GitHub ghp_/gho_, Slack xox,
    // AWS AKIA, and PEM private key blocks.
    [GeneratedRegex(
        @"(?i)\b(sk|proj|ghp|gho|ghu|ghs|ghr|xoxb|xoxp|xoxa|xoxr|AKIA|asia)([-_])([A-Za-z0-9_\-]{8,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex TokenRules();
}
