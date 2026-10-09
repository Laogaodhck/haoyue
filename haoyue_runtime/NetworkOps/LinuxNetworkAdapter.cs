using Haoyue.Runtime.ComputerUse.SystemControl;

namespace Haoyue.Runtime.NetworkOps;

/// <summary>
/// Linux adapter: iproute2 (ip / ss) first with netstat fallback, nslookup for DNS,
/// traceroute / ethtool / nmcli detected on PATH. Missing binaries degrade in-band.
/// </summary>
public sealed class LinuxNetworkAdapter : INetworkOpsAdapter
{
    public string PlatformName => "Linux";

    public IReadOnlyList<NetworkCommandSpec> SupportedCommands { get; } =
        NetworkOpsCatalog.Commands
            .Where(c => c.Name is not ("getmac" or "netsh"))
            .ToArray();

    public async Task<NetworkOutcome> ExecuteAsync(
        string command, string? target, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken ct)
    {
        var spec = SupportedCommands.FirstOrDefault(c => c.Name.Equals(command, StringComparison.OrdinalIgnoreCase));
        if (spec is null)
            return NetworkOutcome.Failed($"Linux 不支持命令 {command}。可用命令：{string.Join("、", SupportedCommands.Select(c => c.Name))}。");

        var argv = new List<string>();
        switch (command.ToLowerInvariant())
        {
            case "ping":
                if (spec.NeedsTarget && string.IsNullOrWhiteSpace(target))
                    return NetworkOutcome.Failed("命令 ping 需要 target 参数（主机名或 IP）。");
                argv.Add("ping");
                // Without -c Linux ping never terminates on its own; the timeout would
                // be the only stopper. Default to a bounded 4-packet probe.
                argv.AddRange(args.Count > 0 ? args : (IReadOnlyList<string>)["-c", "4"]);
                argv.Add(target!.Trim());
                break;
            case "traceroute":
                if (spec.NeedsTarget && string.IsNullOrWhiteSpace(target))
                    return NetworkOutcome.Failed("命令 traceroute 需要 target 参数（主机名或 IP）。");
                if (ExecutableLocator.FindOnPath("traceroute") is null)
                    return NetworkOutcome.Failed("traceroute 未安装（多数发行版：sudo apt install traceroute 或 sudo dnf install traceroute）。");
                argv.Add("traceroute");
                argv.AddRange(args);
                argv.Add(target!.Trim());
                break;
            case "interfaces":
                argv.AddRange(["ip", "addr", "show"]);
                argv.AddRange(args);
                break;
            case "routes":
                argv.AddRange(["ip", "route", "show"]);
                argv.AddRange(args);
                break;
            case "connections":
                if (ExecutableLocator.FindOnPath("ss") is not null)
                {
                    argv.AddRange(["ss", "-tulpn"]);
                }
                else
                {
                    argv.AddRange(["netstat", "-tulnp"]);
                }
                argv.AddRange(args);
                break;
            case "arp":
                argv.AddRange(["ip", "neigh", "show"]);
                break;
            case "dns_lookup":
                if (spec.NeedsTarget && string.IsNullOrWhiteSpace(target))
                    return NetworkOutcome.Failed("命令 dns_lookup 需要 target 参数（域名或 IP）。");
                argv.Add("nslookup");
                argv.AddRange(args);
                argv.Add(target!.Trim());
                break;
            case "dns_flush":
                if (ExecutableLocator.FindOnPath("resolvectl") is not null)
                    argv.AddRange(["resolvectl", "flush-caches"]);
                else if (ExecutableLocator.FindOnPath("systemd-resolve") is not null)
                    argv.AddRange(["systemd-resolve", "--flush-caches"]);
                else
                    return NetworkOutcome.Failed("未检测到 systemd-resolved（resolvectl / systemd-resolve），无法刷新 DNS 缓存；nscd 用户可重启 nscd 服务。");
                break;
            case "ip":
                argv.Add("ip");
                argv.AddRange(args);
                break;
            case "nmcli":
                if (ExecutableLocator.FindOnPath("nmcli") is null)
                    return NetworkOutcome.Failed("nmcli 未安装（NetworkManager 命令行不可用）。");
                argv.Add("nmcli");
                argv.AddRange(args);
                break;
            case "ethtool":
                if (spec.NeedsTarget && string.IsNullOrWhiteSpace(target))
                    return NetworkOutcome.Failed("命令 ethtool 需要 target 参数（网卡接口名，如 eth0）。");
                if (ExecutableLocator.FindOnPath("ethtool") is null)
                    return NetworkOutcome.Failed("ethtool 未安装（多数发行版：sudo apt install ethtool 或 sudo dnf install ethtool）。");
                argv.Add("ethtool");
                argv.Add(target!.Trim());
                argv.AddRange(args);
                break;
            default:
                return NetworkOutcome.Failed($"命令 {command} 无法在 Linux 上构造。");
        }

        return await NetworkProcessRunner.RunAsync(argv, timeoutSeconds, ct).ConfigureAwait(false);
    }

    public async Task<string> DiagnoseSectionAsync(string section, CancellationToken ct)
    {
        switch (section)
        {
            case "dns":
            {
                try
                {
                    var resolv = await File.ReadAllTextAsync("/etc/resolv.conf", ct).ConfigureAwait(false);
                    return string.IsNullOrWhiteSpace(resolv) ? "/etc/resolv.conf 为空。" : resolv.TrimEnd();
                }
                catch (IOException ex)
                {
                    return $"无法读取 /etc/resolv.conf：{ex.Message}";
                }
            }
            case "interfaces":
            case "routes":
            case "connections":
            case "arp":
            {
                var outcome = await ExecuteAsync(section, null, [], 60, ct).ConfigureAwait(false);
                return outcome.Output;
            }
            default:
                return $"未知诊断 section：{section}";
        }
    }
}
