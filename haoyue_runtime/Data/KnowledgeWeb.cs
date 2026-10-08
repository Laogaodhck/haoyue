using System.Net;
using System.Text;

namespace Haoyue.Runtime.Data;

public sealed class KnowledgeWebException(string message) : Exception(message);

/// <summary>
/// Fetches a web page as plain text for URL knowledge sources: GET with a browser-y
/// User-Agent, 15 s timeout, 3 MB body cap, HTML reduced to readable text (script /
/// style dropped, block tags become line breaks, entities decoded, blank lines
/// collapsed). The page &lt;title&gt; (or the host) becomes the suggested source title.
/// </summary>
public static class KnowledgeWeb
{
    public const int MaxBodyBytes = 3 * 1024 * 1024;
    public const int MaxTextChars = 200_000;

    private static readonly HttpClient Client = CreateClient();

    public static (string Title, string Text) Fetch(Uri url)
    {
        HttpResponseMessage response;
        try
        {
            response = Client.GetAsync(url).ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            throw new KnowledgeWebException($"无法访问链接：{ex.Message}");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new KnowledgeWebException($"链接返回错误：HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (mediaType is not ("text/html" or "application/xhtml+xml" or "text/plain" or "application/json"))
                throw new KnowledgeWebException($"不支持的内容类型：{mediaType}（仅支持网页与纯文本）");

            byte[] body;
            try
            {
                body = response.Content.ReadAsByteArrayAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                throw new KnowledgeWebException($"读取链接内容失败：{ex.Message}");
            }
            if (body.Length > MaxBodyBytes)
                throw new KnowledgeWebException($"网页过大：超过 {MaxBodyBytes / 1024 / 1024} MB 限制");

            var charset = response.Content.Headers.ContentType?.CharSet;
            var text = Decode(body, charset);
            if (mediaType.Contains("html"))
            {
                var (title, plain) = HtmlToText(text);
                return (title ?? SuggestTitle(url), plain);
            }
            return (SuggestTitle(url), text.Trim());
        }
    }

    private static string SuggestTitle(Uri url) =>
        string.Equals(url.Host, "localhost", StringComparison.OrdinalIgnoreCase) || url.Host.Length == 0
            ? url.ToString()
            : $"{url.Host}{url.AbsolutePath}".TrimEnd('/');

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36 HaoyueKnowledge");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html, text/plain, */*;q=0.8");
        return client;
    }

    private static string Decode(byte[] body, string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset) &&
            !charset.StartsWith("utf-8", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(charset).GetString(body);
            }
            catch (ArgumentException) { /* unknown charset — fall through to UTF-8 */ }
        }
        return Encoding.UTF8.GetString(body);
    }

    /// <summary>Reduces HTML to readable text; returns the &lt;title&gt; when present.</summary>
    public static (string? Title, string Text) HtmlToText(string html)
    {
        string? title = null;
        var titleStart = html.IndexOf("<title", StringComparison.OrdinalIgnoreCase);
        if (titleStart >= 0)
        {
            var openEnd = html.IndexOf('>', titleStart);
            var close = html.IndexOf("</title", StringComparison.OrdinalIgnoreCase);
            if (openEnd >= 0 && close > openEnd)
            {
                title = WebUtility.HtmlDecode(html[(openEnd + 1)..close]).Trim();
                if (title.Length == 0) title = null;
            }
        }

        var sb = new StringBuilder(html.Length);
        var index = 0;
        while (index < html.Length)
        {
            var comment = MatchComment(html, index);
            if (comment is { } range) { index = range; continue; }

            if (html[index] == '<')
            {
                var close = html.IndexOf('>', index);
                if (close < 0) break;
                var tag = html[index..(close + 1)];
                index = close + 1;
                var lowered = tag.ToLowerInvariant();
                // script/style bodies are skipped wholesale; block tags become separators.
                if (lowered.StartsWith("<script") || lowered.StartsWith("<style") || lowered.StartsWith("<noscript"))
                {
                    var tagName = lowered.StartsWith("<script") ? "script" : lowered.StartsWith("<style") ? "style" : "noscript";
                    var end = html.IndexOf($"</{tagName}", index, StringComparison.OrdinalIgnoreCase);
                    index = end < 0 ? html.Length : html.IndexOf('>', end) + 1;
                    continue;
                }
                if (lowered.StartsWith("<br"))
                    sb.Append('\n');
                else if (lowered is "<p" or "</p" or "<div" or "</div" or "<li" or "<h1" or "<h2" or "<h3"
                    or "<h4" or "<h5" or "<h6" or "</h1" or "</h2" or "</h3" or "</h4" or "</h5" or "</h6"
                    or "<tr" or "</tr" or "<table" or "</table" or "<ul" or "</ul" or "<ol" or "</ol"
                    or "<section" or "</section" or "<article" or "</article" or "<blockquote" or "</blockquote"
                    or "<pre" or "</pre")
                    sb.Append("\n\n");
                continue;
            }

            var nextTag = html.IndexOf('<', index);
            if (nextTag < 0) nextTag = html.Length;
            sb.Append(WebUtility.HtmlDecode(html[index..nextTag]));
            index = nextTag;
        }

        var lines = sb.ToString()
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .ToList();
        var paragraphs = new List<string>();
        var current = new List<string>();
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                if (current.Count > 0)
                {
                    paragraphs.Add(string.Join("\n", current));
                    current.Clear();
                }
            }
            else
            {
                current.Add(line);
            }
        }
        if (current.Count > 0) paragraphs.Add(string.Join("\n", current));

        var result = string.Join("\n\n", paragraphs);
        if (result.Length > MaxTextChars)
            result = result[..MaxTextChars];
        return (title, result);
    }

    private static int? MatchComment(string html, int index) =>
        index + 3 < html.Length && html[index] == '<' && html[index + 1] == '!' && html[index + 2] == '-' && html[index + 3] == '-'
            ? html.IndexOf("-->", index + 4, StringComparison.Ordinal) is { } end && end >= 0 ? end + 3 : html.Length
            : null;
}
