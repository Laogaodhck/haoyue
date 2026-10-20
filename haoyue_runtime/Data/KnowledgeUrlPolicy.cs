using System.Net;
using System.Net.Sockets;
using Haoyue.Runtime.Tools;

namespace Haoyue.Runtime.Data;

/// <summary>
/// 知识库 URL 导入的链接安全策略（SSRF 防线）。与 <see cref="WebSiteAllowlist"/> 共用
/// 同一份「允许的网站」配置，并在此之上叠加私网硬拦截——白名单关闭（空规则）时也必须
/// 生效，否则导入 http://169.254.169.254/（云元数据）或内网地址就成了模型/客户端
/// 可触达的探测面。localhost / 127.x / ::1 保持放行：与 web_fetch 的白名单语义一致，
/// 本地文档站点的导入是常见开发用法。
/// </summary>
public static class KnowledgeUrlPolicy
{
    /// <summary>
    /// 完整校验：scheme → 白名单（启用时）→ 公网主机（DNS 解析后判定）。返回
    /// (放行, 拒绝原因)；拒绝原因面向用户，说明缺什么、去哪配。
    /// </summary>
    public static (bool Ok, string? Reason) Check(Uri url, IReadOnlyList<WebSiteRule>? rules)
    {
        if (url.Scheme is not ("http" or "https"))
            return (false, $"仅支持 http/https 链接（收到 {url.Scheme}）。");

        if (rules is { Count: > 0 })
        {
            var (allowed, denyReason) = WebSiteAllowlist.Evaluate(url.ToString(), rules);
            if (!allowed)
                return (false, $"知识库导入受网站白名单限制：{denyReason}");
        }

        return CheckPublicHost(url);
    }

    /// <summary>
    /// 公网主机校验：字面量 IP 直接判定，主机名解析后判定（解析失败即拒绝——
    /// 反正随后也抓取不到，提前给出明确原因）。环回地址放行，其余私网/链路本地/
    /// 保留段一律拒绝。
    /// </summary>
    public static (bool Ok, string? Reason) CheckPublicHost(Uri url)
    {
        var host = url.Host;
        if (string.IsNullOrWhiteSpace(host))
            return (false, "链接缺少主机名。");

        if (WebSiteAllowlist.IsLocalHost(host))
            return (true, null); // 本地文档站点：与白名单语义一致，放行

        if (IPAddress.TryParse(host, out var literal))
            return IsPublicAddress(literal)
                ? (true, null)
                : (false, $"拒绝访问非公网地址：{host}。知识库导入仅允许公网或本地主机地址。");

        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(host);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return (false, $"无法解析主机名 {host}：{ex.Message}");
        }
        if (addresses.Length == 0)
            return (false, $"无法解析主机名 {host}。");

        foreach (var address in addresses)
        {
            if (!IsPublicAddress(address))
                return (false, $"主机名 {host} 解析到非公网地址（{address}），已拒绝。知识库导入仅允许公网或本地主机地址。");
        }
        return (true, null);
    }

    /// <summary>
    /// 公网 IP 判定：环回、私网、链路本地、ULA、多播、保留段（bogon）全部算非公网。
    /// IPv4-mapped IPv6 先映射回 IPv4 再判，避免 ::ffff:127.0.0.1 绕过。
    /// </summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] switch
            {
                0 or 10 or 127 => false,                       // 本网络 / 私网 / 环回
                100 => b[1] is >= 64 and <= 127 ? false : true, // CGNAT 100.64.0.0/10
                172 => b[1] is >= 16 and <= 31 ? false : true, // 私网 172.16.0.0/12
                169 => b[1] != 254,                            // 链路本地 169.254.0.0/16
                192 => b[1] switch
                {
                    0 => b[2] is 0 or 2 ? false : true,        // 192.0.0.0/24、192.0.2.0/24
                    168 => false,                              // 私网 192.168.0.0/16
                    _ => true,
                },
                198 => b[1] switch
                {
                    18 or 19 => false,                          // 基准测试 198.18.0.0/15
                    51 => b[2] == 100 ? false : true,           // 文档 198.51.100.0/24
                    _ => true,
                },
                203 => b[1] == 0 && b[2] == 113 ? false : true, // 文档 203.0.113.0/24
                >= 224 => false,                                // 多播 / 保留
                _ => true,
            };
        }

        if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal)
            return false;
        var v6 = ip.GetAddressBytes();
        if (v6[0] == 0xFC || v6[0] == 0xFD) return false;       // ULA fc00::/7
        if (v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x0D && v6[3] == 0xB8) return false; // 文档 2001:db8::/32
        return true;
    }
}
