using System.Text.Json;
using System.Text.Json.Nodes;

namespace Haoyue.Runtime.Tools;

/// <summary>
/// Normalizes model-produced tool call arguments. Some models emit arguments that are
/// truncated (token cap) or carry trailing text / syntax errors. Those must be repaired
/// before the JSON is sent back to a provider: strict local servers reject the whole
/// request with HTTP 500 when a tool call's arguments string is not valid JSON.
/// </summary>
internal static class ToolArguments
{
    /// <summary>Returns a valid JSON object string for the raw arguments ("{}" when unrecoverable).</summary>
    public static string Sanitize(string raw)
    {
        var (_, json) = Parse(raw);
        return json;
    }

    /// <summary>
    /// Signature form of the raw arguments for repeated-failure / redundant-call tracking
    /// only — never for provider replay. Recoverable arguments normalize to their repaired
    /// JSON; unrecoverable ones keep the (capped) raw text. Collapsing two different
    /// malformed calls onto the same "{}" signature would falsely read as "retrying a
    /// dead end" and trigger strategy nudges for what are actually distinct attempts.
    /// </summary>
    public static string Signature(string raw)
    {
        var trimmed = raw?.Trim() ?? "";
        if (trimmed.Length == 0) return "{}";
        var (obj, json) = Parse(trimmed);
        if (obj is not null) return json;
        var signature = trimmed;
        if (signature.Length > 200) signature = signature[..200] + "…";
        return signature;
    }

    /// <summary>
    /// Parses raw tool arguments, attempting a best-effort repair for truncated or
    /// trailing-garbage JSON. <c>Obj</c> is null when the arguments cannot be recovered.
    /// </summary>
    public static (JsonObject? Obj, string Json) Parse(string raw)
    {
        var (obj, json, _) = ParseDetailed(raw);
        return (obj, json);
    }

    /// <summary>Full-detail variant of <see cref="Parse"/>: <c>Repaired</c> marks
    /// arguments that needed bracket-balancing or closure repair — the caller should
    /// treat the recovered parameters (especially long command strings) as possibly
    /// semantically incomplete instead of silently executing them.</summary>
    public static (JsonObject? Obj, string Json, bool Repaired) ParseDetailed(string raw)
    {
        var trimmed = raw?.Trim() ?? "";
        if (trimmed.Length == 0) return (new JsonObject(), "{}", false);
        try
        {
            if (JsonNode.Parse(trimmed) is JsonObject valid) return (valid, trimmed, false);
        }
        catch (JsonException) { }

        var start = trimmed.IndexOf('{');
        if (start < 0) return (null, "{}", false);

        var scan = Scan(trimmed, start);
        if (scan.Balanced is not null)
        {
            try
            {
                if (JsonNode.Parse(scan.Balanced) is JsonObject repaired)
                    return (repaired, scan.Balanced, true);
            }
            catch (JsonException) { }
        }

        // Unterminated object (cut off at the token cap): try closing it. A trailing
        // comma, an open string, or missing braces are all handled by trying a few
        // sensible suffixes in order.
        var truncated = trimmed[start..];
        var closeBraces = Math.Max(scan.OpenDepth, 1);
        var suffixes = new[]
        {
            new string('}', closeBraces),
            new string('}', closeBraces + 1),
            "\"" + new string('}', closeBraces),
            "\"" + new string('}', closeBraces + 1),
            "}" + new string('}', closeBraces),
        };
        foreach (var suffix in suffixes)
        {
            try
            {
                if (JsonNode.Parse(truncated + suffix) is JsonObject repaired)
                    return (repaired, truncated + suffix, true);
            }
            catch (JsonException) { }
        }

        return (null, "{}", false);
    }

    private static (string? Balanced, int OpenDepth) Scan(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return (text[start..(i + 1)], 0);
                    break;
            }
        }
        return (null, Math.Max(depth, 0));
    }
}
