using System.Net;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Tools;

namespace Haoyue.Tests;

/// <summary>
/// 知识库 URL 导入的 SSRF 防线：scheme / 白名单 / 私网硬拦截 / 重定向逐跳校验。
/// 全部用例使用 IP 字面量或注入的假 handler，不碰真实网络。
/// </summary>
public class KnowledgeUrlPolicyTests
{
    // ─────────────────────────── 公网 IP 判定 ───────────────────────────

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]      // 172.16/12 之外
    [InlineData("192.169.0.1")]     // 192.168/16 之外
    [InlineData("169.253.0.1")]     // 169.254/16 之外
    [InlineData("2606:4700:4700::1111")]
    public void IsPublicAddress_PublicLiterals_True(string literal)
    {
        Assert.True(KnowledgeUrlPolicy.IsPublicAddress(IPAddress.Parse(literal)));
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]   // 云元数据地址
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("100.64.0.1")]        // CGNAT
    [InlineData("224.0.0.1")]         // 多播
    [InlineData("::ffff:127.0.0.1")]  // IPv4-mapped 环回
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("fe80::1")]           // 链路本地
    [InlineData("fc00::1")]           // ULA
    [InlineData("2001:db8::1")]       // 文档段
    public void IsPublicAddress_NonPublicLiterals_False(string literal)
    {
        Assert.False(KnowledgeUrlPolicy.IsPublicAddress(IPAddress.Parse(literal)));
    }

    // ─────────────────────────── 策略校验 ───────────────────────────

    [Fact]
    public void Check_RejectsNonHttpSchemes()
    {
        var (ok, reason) = KnowledgeUrlPolicy.Check(new Uri("ftp://8.8.8.8/x"), null);
        Assert.False(ok);
        Assert.Contains("http/https", reason);
    }

    [Fact]
    public void Check_PublicIpLiteral_NoRules_Allowed()
    {
        var (ok, _) = KnowledgeUrlPolicy.Check(new Uri("http://8.8.8.8/page"), null);
        Assert.True(ok);
    }

    [Fact]
    public void Check_MetadataAndPrivateTargets_RejectedEvenWithoutAllowlist()
    {
        foreach (var url in new[]
                 {
                     "http://169.254.169.254/latest/meta-data/",
                     "http://10.0.0.5/admin",
                     "http://192.168.1.1/",
                     "http://[fe80::1]/",
                 })
        {
            var (ok, reason) = KnowledgeUrlPolicy.Check(new Uri(url), null);
            Assert.False(ok, url);
            Assert.Contains("非公网", reason);
        }
    }

    [Fact]
    public void Check_LocalhostAlwaysAllowed()
    {
        Assert.True(KnowledgeUrlPolicy.Check(new Uri("http://localhost:3000/docs"), null).Ok);
        Assert.True(KnowledgeUrlPolicy.Check(new Uri("http://127.0.0.1:8080/"), null).Ok);
    }

    [Fact]
    public void Check_AllowlistBlocksPublicTarget_WhenEnabled()
    {
        var rules = WebSiteAllowlist.Parse(["https://8.8.8.8"]);
        var (ok, reason) = KnowledgeUrlPolicy.Check(new Uri("https://1.1.1.1/"), rules);
        Assert.False(ok);
        Assert.Contains("白名单", reason);
        Assert.True(KnowledgeUrlPolicy.Check(new Uri("https://8.8.8.8/"), rules).Ok);
    }

    [Fact]
    public void Check_AllowlistCannotWhitelistPrivateTargets()
    {
        // 白名单是授权层，私网硬拦截是底线层：即便用户把内网地址加进白名单也不放行。
        var rules = WebSiteAllowlist.Parse(["http://169.254.169.254"]);
        var (ok, _) = KnowledgeUrlPolicy.Check(new Uri("http://169.254.169.254/"), rules);
        Assert.False(ok);
    }

    [Fact]
    public void Check_UnresolvableHost_RejectedWithReason()
    {
        var (ok, reason) = KnowledgeUrlPolicy.Check(
            new Uri("http://no-such-host.invalid/"), null);
        Assert.False(ok);
        Assert.Contains("无法解析", reason);
    }

    // ─────────────────────────── 重定向逐跳校验 ───────────────────────────

    [Fact]
    public void Fetch_RedirectToPrivateTarget_RejectedBeforeSecondHop()
    {
        // 公网 URL 一条 302 指向云元数据地址：第二跳的策略校验必须先拦下。
        var handler = new RouteHandler(request =>
            request.RequestUri!.Host == "8.8.8.8"
                ? Redirect("http://169.254.169.254/latest/meta-data/")
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("secret", System.Text.Encoding.UTF8, "text/plain"),
                });

        var ex = Assert.Throws<KnowledgeWebException>(() =>
            KnowledgeWeb.Fetch(new Uri("http://8.8.8.8/start"), null, handler));
        Assert.Contains("非公网", ex.Message);
    }

    [Fact]
    public void Fetch_RedirectToPublicTarget_Followed()
    {
        var handler = new RouteHandler(request =>
            request.RequestUri!.Host == "8.8.8.8"
                ? Redirect("http://1.1.1.1/final")
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><title>终点</title><body>正文</body></html>",
                        System.Text.Encoding.UTF8, "text/html"),
                });

        var (title, text) = KnowledgeWeb.Fetch(new Uri("http://8.8.8.8/start"), null, handler);
        Assert.Equal("终点", title);
        Assert.Contains("正文", text);
    }

    [Fact]
    public void Fetch_RedirectLoopBeyondCap_Rejected()
    {
        var handler = new RouteHandler(_ => Redirect("http://8.8.8.8/loop"));
        var ex = Assert.Throws<KnowledgeWebException>(() =>
            KnowledgeWeb.Fetch(new Uri("http://8.8.8.8/loop"), null, handler));
        Assert.Contains("重定向次数超过", ex.Message);
    }

    [Fact]
    public void Fetch_RedirectWithoutLocation_TreatedAsError()
    {
        var handler = new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.Found));
        Assert.Throws<KnowledgeWebException>(() =>
            KnowledgeWeb.Fetch(new Uri("http://8.8.8.8/"), null, handler));
    }

    private static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } };

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
