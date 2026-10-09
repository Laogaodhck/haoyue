namespace Haoyue.Runtime.NetworkOps;

/// <summary>
/// Windows adapter: maps logical commands onto ipconfig / netsh / route / netstat /
/// tracert / nslookup / getmac. Commands are built as argv (never a shell string).
/// </summary>
public sealed class WindowsNetworkAdapter : INetworkOpsAdapter
{
    public string PlatformName => "Windows";

    public IReadOnlyList<NetworkCommandSpec> SupportedCommands { get; } =
        NetworkOpsCatalog.Commands
            .Where(c => c.Name is not ("ip" or "nmcli" or "ethtool"))
            .ToArray();

    public async Task<NetworkOutcome> ExecuteAsync(
        string command, string? target, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken ct)
    {
        var spec = SupportedCommands.FirstOrDefault(c => c.Name.Equals(command, StringComparison.OrdinalIgnoreCase));
        if (spec is null)
            return NetworkOutcome.Failed($"Windows 不支持命令 {command}。可用命令：{string.Join("、", SupportedCommands.Select(c => c.Name))}。");

        var argv = new List<string>();
        switch (command.ToLowerInvariant())
        {
            case "ping":
                if (spec.NeedsTarget && string.IsNullOrWhiteSpace(target))
                    return NetworkOutcome.Failed("命令 ping 需要 target 参数（主机名或 IP）。");
                argv.Add("ping");
                argv.AddRange(args.Count > 0 ? args : (IReadOnlyList<string>)["-n", "4"]);
                argv.Add(target!.Trim());
                break;
            case "traceroute":
                if (spec.NeedsTarget && string.IsNullOrWhiteSpace(target))
                    return NetworkOutcome.Failed("命令 traceroute 需要 target 参数（主机名或 IP）。");
                argv.Add("tracert");
                argv.AddRange(args);
                argv.Add(target!.Trim());
                break;
            case "interfaces":
                argv.AddRange(["ipconfig", "/all"]);
                argv.AddRange(args);
                break;
            case "routes":
                argv.AddRange(["route", "print"]);
                argv.AddRange(args);
                break;
            case "connections":
                argv.AddRange(["netstat", "-ano"]);
                argv.AddRange(args);
                break;
            case "arp":
                argv.AddRange(["arp", "-a"]);
                argv.AddRange(args);
                break;
            case "dns_lookup":
                if (spec.NeedsTarget && string.IsNullOrWhiteSpace(target))
                    return NetworkOutcome.Failed("命令 dns_lookup 需要 target 参数（域名或 IP）。");
                argv.Add("nslookup");
                argv.AddRange(args);
                argv.Add(target!.Trim());
                break;
            case "dns_flush":
                argv.AddRange(["ipconfig", "/flushdns"]);
                break;
            case "getmac":
                argv.AddRange(["getmac", "/fo", "list", "/v"]);
                break;
            case "netsh":
                argv.Add("netsh");
                argv.AddRange(args);
                break;
            default:
                return NetworkOutcome.Failed($"命令 {command} 无法在 Windows 上构造。");
        }

        return await NetworkProcessRunner.RunAsync(argv, timeoutSeconds, ct).ConfigureAwait(false);
    }

    public async Task<string> DiagnoseSectionAsync(string section, CancellationToken ct)
    {
        switch (section)
        {
            case "dns":
            {
                // nslookup without target prints the default resolver — the cheapest
                // read-only DNS configuration probe on Windows.
                var outcome = await NetworkProcessRunner.RunAsync(["nslookup"], 30, ct).ConfigureAwait(false);
                return outcome.Output;
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
