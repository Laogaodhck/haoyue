using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Tools.HeadlessBrowser;
using Haoyue.Runtime.Prompts;

namespace Haoyue.Runtime.Tools.Builtin;

/// <summary>
/// 内置无头浏览器：驱动系统 Edge/Chrome 的专用无头实例（CDP 协议），支持加载页面、
/// 在页面上下文执行 JavaScript、提取文本/HTML——用于调试前端页面（本地 dev server）
/// 与抓取需要真实浏览器渲染的数据（SPA、登录前的动态内容除外）。单内核单会话：
/// 同一 daemon 进程至多一个无头实例，所有操作串行；open 持有会话直到 close 或
/// 进程退出。外部站点访问与 web_fetch 共用「允许的网站」白名单（config.web.allowedSites）。
/// </summary>
public sealed class BrowserTool(IPromptProvider prompts, IConfigStore configStore) : BuiltinTool(prompts)
{
    public override string Name => "browser";
    public override string StatusLabel => "Browsing page";
    public override bool RequiresWorkspace => false;
    public override bool RequiresNetwork => true;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static HeadlessBrowserSession? Session;

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("action", ToolSchema.String("Action: open (default), eval, text, html, or close", "open", "eval", "text", "html", "close"), false),
        ("url", ToolSchema.String("HTTP, HTTPS, or data: URL to load (open); a bare domain gets https:// prepended"), false),
        ("script", ToolSchema.String("JavaScript to evaluate in the page context (eval); the last expression's value is returned as JSON"), false),
        ("wait_ms", ToolSchema.Integer("Extra settle time after page load in milliseconds, default 500 (open)"), false),
        ("timeout_seconds", ToolSchema.Integer("Per-action timeout in seconds, default 30"), false),
        ("max_chars", ToolSchema.Integer("Maximum characters of text/html to return, default 12000 (text/html)"), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var action = (GetString(arguments, "action") ?? "open").Trim().ToLowerInvariant();
        var timeout = TimeSpan.FromSeconds(Math.Clamp(GetInt(arguments, "timeout_seconds") ?? 30, 5, 120));

        // 单内核串行：无头实例是进程级重资源，并发操作既拖慢也容易交错状态。
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            switch (action)
            {
                case "open":
                    return await OpenAsync(arguments, context, timeout, ct).ConfigureAwait(false);
                case "eval":
                    return await EvalActionAsync(arguments, context, timeout, ct).ConfigureAwait(false);
                case "text":
                case "html":
                    return await ExtractAsync(action, arguments, context, timeout, ct).ConfigureAwait(false);
                case "close":
                    return await CloseAsync().ConfigureAwait(false);
                default:
                    return ToolResult.Fail($"Unknown action '{action}'. Use open, eval, text, html, or close.");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolResult.Fail($"browser {action} timed out after {timeout.TotalSeconds:0}s.");
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<ToolResult> OpenAsync(JsonObject arguments, ToolContext context, TimeSpan timeout, CancellationToken ct)
    {
        var url = GetString(arguments, "url");
        if (string.IsNullOrWhiteSpace(url))
            return ToolResult.Fail("url is required for the open action.");

        var isDataUrl = url.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
        if (!isDataUrl
            && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // 裸域名补全为 https；file:/chrome: 等本地/内部 scheme 直接拒绝——
            // file:// 会借无头渲染绕过文件读取边界。
            if (url.Contains("://", StringComparison.Ordinal))
                return ToolResult.Fail(
                    $"仅支持 http/https/data 的 URL（收到 {url.Split(':')[0]}）。本地文件请用 read_file / glob 读取。");
            url = "https://" + url;
        }

        // 允许的网站：与 web_fetch 同一份运行时强制白名单，浏览器渲染不是绕过通道。
        // data: URL 不是外部站点，不参与白名单。
        if (!isDataUrl)
        {
            var rules = WebSiteAllowlist.Parse(configStore.Config.Web?.AllowedSites ?? []);
            var (allowed, denyReason) = WebSiteAllowlist.Evaluate(url, rules);
            if (!allowed)
            {
                context.Events.Publish(new Haoyue.Runtime.Events.WarningEvent(
                    $"browser 被允许的网站白名单拒绝：{url}"));
                return ToolResult.Fail(denyReason!);
            }
        }

        var session = await EnsureSessionAsync(timeout, ct).ConfigureAwait(false);
        var settle = TimeSpan.FromMilliseconds(Math.Clamp(GetInt(arguments, "wait_ms") ?? 500, 0, 30_000));
        var info = await session.NavigateAsync(url, settle, timeout, ct).ConfigureAwait(false);

        var maxChars = Math.Clamp(GetInt(arguments, "max_chars") ?? 3000, 500, 10_000);
        var pageText = await ExtractTextAsync(session, timeout, ct).ConfigureAwait(false);
        if (pageText.Length > maxChars) pageText = pageText[..maxChars] + "\n… [truncated]";

        var output = $"URL: {info["url"]}\nTitle: {info["title"]}\n\n{pageText}";
        return ToolResult.Ok(context.Truncate(output, "page text"),
            $"Opened {info["url"]} — {info["title"]}");
    }

    private async Task<ToolResult> EvalActionAsync(JsonObject arguments, ToolContext context, TimeSpan timeout, CancellationToken ct)
    {
        var script = GetString(arguments, "script");
        if (string.IsNullOrWhiteSpace(script))
            return ToolResult.Fail("script is required for the eval action.");
        if (Session is null)
            return ToolResult.Fail("No page is open. Call browser with action=open and a URL first.");

        var result = await Session.EvalAsync(script, timeout, ct).ConfigureAwait(false);
        var value = result?["value"] is { } node
            ? node.ToJsonString()
            : result is null ? "undefined" : result.ToJsonString();
        return ToolResult.Ok(context.Truncate(value, "eval result"), "Script evaluated");
    }

    private async Task<ToolResult> ExtractAsync(string action, JsonObject arguments, ToolContext context, TimeSpan timeout, CancellationToken ct)
    {
        if (Session is null)
            return ToolResult.Fail("No page is open. Call browser with action=open and a URL first.");

        var maxChars = Math.Clamp(GetInt(arguments, "max_chars") ?? 12_000, 500, Math.Max(500, context.MaxOutputChars));
        var script = action == "text"
            ? "document.body ? document.body.innerText : document.documentElement.innerText"
            : "document.documentElement.outerHTML";
        var session = Session;
        var result = await session.EvalAsync(script, timeout, ct).ConfigureAwait(false);
        var value = result?["value"]?.GetValue<string>() ?? "";

        if (value.Length > maxChars)
        {
            // H-B：头尾保留——页面结论/页脚链接常在尾部。
            var tail = Math.Max(1, maxChars / 4);
            value = value[..(maxChars - tail)] + $"\n… [middle omitted: {value.Length - maxChars} chars] …\n" + value[^tail..];
        }
        return ToolResult.Ok(value, $"Page {action}: {value.Length} characters");
    }

    private async Task<string> ExtractTextAsync(HeadlessBrowserSession session, TimeSpan timeout, CancellationToken ct)
    {
        var result = await session.EvalAsync(
            "document.body ? document.body.innerText : document.documentElement.innerText", timeout, ct).ConfigureAwait(false);
        return result?["value"]?.GetValue<string>() ?? "";
    }

    private static async Task<HeadlessBrowserSession> EnsureSessionAsync(TimeSpan timeout, CancellationToken ct)
    {
        // 进程被外部杀掉等场景：旧会话失去活性时先清理再重启，而不是让 eval 空抛。
        if (Session is { } alive)
        {
            if (alive.IsAlive) return alive;
            Session = null;
            await alive.DisposeAsync().ConfigureAwait(false);
        }
        Session = await HeadlessBrowserSession.StartAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        return Session;
    }

    private static async Task<ToolResult> CloseAsync()
    {
        if (Session is null) return ToolResult.Ok("No browser session is open.");
        var session = Session;
        Session = null;
        await session.DisposeAsync().ConfigureAwait(false);
        return ToolResult.Ok("Browser session closed.", "Browser closed");
    }
}
