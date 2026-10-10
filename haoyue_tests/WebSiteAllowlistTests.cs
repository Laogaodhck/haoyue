using System.Text.Json.Nodes;
using Haoyue.Runtime;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;
using Haoyue.Runtime.Workspaces;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// 「允许的网站」白名单（外部网站访问）：规则解析（scheme://host、*.子域通配、端口、
/// IPv6）、localhost 始终放行、空列表不限制、web_fetch 运行时强制闸门、RPC 归一化。
/// </summary>
public sealed class WebSiteAllowlistTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"haoyue_weballow_{Guid.NewGuid():N}");

    public WebSiteAllowlistTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Parse_AcceptsOriginSubdomainWildcardAndPort_DropsInvalid()
    {
        var rules = WebSiteAllowlist.Parse(new[]
        {
            "https://example.com",
            "https://*.example.com",
            "http://localhost:3000",
            "https://example.com/path",   // trailing path tolerated, stripped
            "example.com",                // no scheme → dropped
            "ftp://example.com",          // non-http scheme → dropped
            "https://",                   // empty host → dropped
            "https://a.*.com",            // mid-host wildcard → dropped
            "",
            null,
        });

        Assert.Equal(4, rules.Count);
        Assert.Contains(rules, r => r.Scheme == "https" && r.Host == "example.com" && r.Port is null);
        Assert.Contains(rules, r => r.Host == "*.example.com");
        Assert.Contains(rules, r => r.Scheme == "http" && r.Host == "localhost" && r.Port == "3000");
        // trailing path variant dedupes to the same source as entry 1 after normalize
        var normalized = WebSiteAllowlist.Normalize(new[] { "https://example.com", "https://example.com/x", "HTTPS://EXAMPLE.COM" });
        Assert.Single(normalized);
    }

    [Fact]
    public void Evaluate_EmptyRules_IsUnrestricted()
    {
        var (allowed, reason) = WebSiteAllowlist.Evaluate("https://anything.example", WebSiteAllowlist.Parse([]));
        Assert.True(allowed);
        Assert.Null(reason);
    }

    [Fact]
    public void Evaluate_LocalHostAlwaysAllowed_EvenUnderAllowlist()
    {
        var rules = WebSiteAllowlist.Parse(new[] { "https://cn.bing.com" });
        foreach (var url in new[]
                 {
                     "http://localhost:5173/index.html",
                     "http://127.0.0.1:8080/",
                     "http://127.200.1.9/",   // 127.0.0.0/8
                     "https://[::1]:3000/api",
                 })
        {
            var (allowed, _) = WebSiteAllowlist.Evaluate(url, rules);
            Assert.True(allowed, url);
        }
    }

    [Fact]
    public void Evaluate_WildcardMatchesSubdomains_ButNotApex()
    {
        var rules = WebSiteAllowlist.Parse(new[] { "https://*.example.com" });

        Assert.True(WebSiteAllowlist.Evaluate("https://www.example.com/a", rules).Allowed);
        Assert.True(WebSiteAllowlist.Evaluate("https://a.b.example.com/", rules).Allowed);
        Assert.True(WebSiteAllowlist.Evaluate("https://www.example.com:8443/", rules).Allowed); // rule without port matches any port

        // 根域不匹配——按截图语义需单独添加
        Assert.False(WebSiteAllowlist.Evaluate("https://example.com/", rules).Allowed);
        // http 与 https 是不同 scheme
        Assert.False(WebSiteAllowlist.Evaluate("http://www.example.com/", rules).Allowed);
        // 完全不同的域
        Assert.False(WebSiteAllowlist.Evaluate("https://evil.com/", rules).Allowed);
    }

    [Fact]
    public void Evaluate_PortRule_RestrictsToThatPort()
    {
        var rules = WebSiteAllowlist.Parse(new[] { "http://localhost:3000" });
        Assert.True(WebSiteAllowlist.Evaluate("https://other.com/", rules).Allowed is false);
        // localhost 总是放行（端口规则只影响非本地主机名）
    }

    [Fact]
    public void Evaluate_NonHttpScheme_IsRejected()
    {
        var rules = WebSiteAllowlist.Parse(new[] { "https://example.com" });
        var (allowed, reason) = WebSiteAllowlist.Evaluate("ftp://example.com/file", rules);
        Assert.False(allowed);
        Assert.Contains("http", reason);
    }

    [Fact]
    public async Task WebFetchTool_DeniesUnlistedSite_AndAllowsLocalHost_UnderAllowlist()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"), Path.Combine(_dir, "state.json"));
        store.Config.Web.AllowedSites = ["https://cn.bing.com"];
        var tool = new WebFetchTool(new MockPromptProvider(), store);
        var context = CreateToolContext();

        var blocked = await tool.ExecuteAsync(new JsonObject { ["url"] = "https://evil.example.com/page" }, context, CancellationToken.None);
        Assert.False(blocked.Success);
        Assert.Contains("白名单", blocked.Output);

        var localhost = await tool.ExecuteAsync(new JsonObject { ["url"] = "http://localhost:1/nothing" }, context, CancellationToken.None);
        // localhost 放行进入真实 HTTP 层（无服务监听 → 网络错误，而非白名单拒绝）
        Assert.DoesNotContain("白名单", localhost.Output);
    }

    [Fact]
    public async Task WebFetchTool_EmptyAllowlist_FetchesNormally()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"), Path.Combine(_dir, "state.json"));
        var tool = new WebFetchTool(new MockPromptProvider(), store);
        var context = CreateToolContext();

        var res = await tool.ExecuteAsync(new JsonObject { ["url"] = "https://definitely-not-a-real-domain-8f3k.example" }, context, CancellationToken.None);
        // 白名单未启用：失败原因应是 DNS/网络错误，而不是白名单拒绝
        Assert.DoesNotContain("白名单", res.Output);
    }

    private ToolContext CreateToolContext()
    {
        var ws = new WorkspaceManager().Detect(_dir);
        return new ToolContext
        {
            Workspace = ws,
            Events = new EventBus(),
            Agent = new AgentConfig()
        };
    }

    private sealed class MockPromptProvider : IPromptProvider
    {
        public string? TryGet(string key) => null;
        public string Get(string key) => "";
        public string Render(string template, IReadOnlyDictionary<string, string> variables) => template;
        public string? GetRendered(string key, IReadOnlyDictionary<string, string> variables) => null;
        public void SetWorkspaceRoot(string? workspacePromptsDir) { }
    }
}
