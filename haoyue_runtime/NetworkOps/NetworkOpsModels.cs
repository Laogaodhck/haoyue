using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Haoyue.Runtime.NetworkOps;

/// <summary>
/// One logical network-ops command exposed to the model. The adapter maps the logical
/// name onto the platform binary; the model never writes a raw command line.
/// </summary>
/// <param name="Name">Logical command name the model passes (e.g. "connections").</param>
/// <param name="Description">Chinese one-liner shown in the tool schema enum and help.</param>
/// <param name="NeedsTarget">True when <paramref name="Name"/> requires a target argument.</param>
/// <param name="Mutating">True when the command always changes state (e.g. dns_flush).</param>
/// <param name="VerbBasedMutation">True when mutation depends on argument verbs (netsh / ip / nmcli).</param>
public sealed record NetworkCommandSpec(
    string Name,
    string Description,
    bool NeedsTarget,
    bool Mutating,
    bool VerbBasedMutation);

/// <summary>
/// The command directory shared by every platform adapter. Adding an entry here plus
/// an argv branch in each adapter is all it takes to expose another ops command.
/// </summary>
public static class NetworkOpsCatalog
{
    /// <summary>Argument verbs that turn a free-form command (netsh / ip / nmcli) into a state change.</summary>
    private static readonly HashSet<string> MutatingVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "set", "add", "delete", "del", "remove", "reset", "flush", "enable", "disable",
        "up", "down", "replace", "change", "install", "uninstall", "rename", "modify",
        "connect", "disconnect", "import", "export",
    };

    public static readonly IReadOnlyList<NetworkCommandSpec> Commands =
    [
        new("ping", "探测目标主机连通性与延迟", true, false, false),
        new("traceroute", "跟踪到目标主机的路由路径（Windows tracert / Linux traceroute）", true, false, false),
        new("interfaces", "查看网络接口与 IP 配置（Windows ipconfig /all，Linux ip addr）", false, false, false),
        new("routes", "查看路由表（Windows route print，Linux ip route）", false, false, false),
        new("connections", "查看活动连接与监听端口（Windows netstat -ano，Linux ss -tulpn）", false, false, false),
        new("arp", "查看 ARP / 邻居表", false, false, false),
        new("dns_lookup", "用 nslookup 解析域名或反向查询 IP", true, false, false),
        new("dns_flush", "刷新本机 DNS 解析缓存（需 AllowMutating）", false, true, false),
        new("getmac", "查看网卡 MAC 地址（仅 Windows）", false, false, false),
        new("netsh", "Windows 网络配置工具，args 为 netsh 子命令；含 set/add/delete 等动词视为变更类（仅 Windows）", false, false, true),
        new("ip", "Linux iproute2 工具，args 为 ip 子命令；含 add/del/set 等动词视为变更类（仅 Linux）", false, false, true),
        new("nmcli", "NetworkManager 命令行，args 为 nmcli 子命令；含 up/down/modify 等动词视为变更类（仅 Linux）", false, false, true),
        new("ethtool", "查看以太网网卡驱动与链路信息（仅 Linux，需目标接口名）", true, false, false),
    ];

    public static NetworkCommandSpec? Resolve(string name) =>
        Commands.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when this invocation changes network state and therefore requires AllowMutating.</summary>
    public static bool IsMutating(NetworkCommandSpec spec, IReadOnlyList<string> args) =>
        spec.Mutating || (spec.VerbBasedMutation && args.Any(IsMutatingVerb));

    public static bool IsMutatingVerb(string arg) => MutatingVerbs.Contains(arg);

    /// <summary>
    /// Structural guard for adapter-supplied arguments. Arguments are passed to the
    /// child process via argv (never through a shell), so metacharacters are harmless;
    /// control characters and absurd lengths are still rejected defensively.
    /// </summary>
    public static string? ValidateArgs(IReadOnlyList<string> args)
    {
        if (args.Count > 32) return "args 数量超过上限（32 个）。";
        foreach (var arg in args)
        {
            if (arg.Length == 0) return "args 不允许空参数。";
            if (arg.Length > 256) return $"单个参数过长（>256 字符）：{arg[..32]}…";
            if (arg.Any(char.IsControl)) return $"参数包含控制字符：{arg[..Math.Min(32, arg.Length)]}";
        }
        return null;
    }
}

/// <summary>
/// Exit code + captured output of one network-ops command. Tools render these
/// in-band: a non-zero exit is still informative output, not a thrown error.
/// </summary>
public sealed record NetworkOutcome(int ExitCode, string Output)
{
    public static NetworkOutcome Failed(string error) => new(-1, error);
}

/// <summary>
/// Cross-platform argv runner for network tools. Arguments go through
/// ProcessStartInfo.ArgumentList — no shell in the middle, so injection by way of
/// the model-supplied args is structurally impossible. Windows console tools emit
/// OEM codepage bytes when piped (GBK on zh-CN systems), matching the decoding
/// used by ComputerExecTool for cmd.
/// </summary>
internal static class NetworkProcessRunner
{
    static NetworkProcessRunner()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
    }

    public static async Task<NetworkOutcome> RunAsync(
        IReadOnlyList<string> argv, int timeoutSeconds, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 600));

        Encoding outputEncoding = OperatingSystem.IsWindows()
            ? Encoding.GetEncoding(GetOemCodePage())
            : new UTF8Encoding(false);

        var psi = new ProcessStartInfo
        {
            FileName = argv[0],
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding = outputEncoding,
        };
        foreach (var arg in argv.Skip(1))
            psi.ArgumentList.Add(arg);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return NetworkOutcome.Failed($"命令 {argv[0]} 无法启动：{ex.Message}（可能未安装或不在 PATH 中）。");
        }
        catch (Exception ex)
        {
            return NetworkOutcome.Failed($"进程启动失败: {ex.Message}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return NetworkOutcome.Failed("调用已取消。");
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { }
        }

        string stdout, stderr;
        try
        {
            stdout = await stdoutTask.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            stdout = "";
            stderr = "(output unavailable: process tree did not close in time)";
        }

        if (timedOut)
            return NetworkOutcome.Failed($"命令超时（{timeout.TotalSeconds:0}s）。ping/trace 类命令可通过 args 限制次数（如 -n 4 / -c 4）。");

        var sb = new StringBuilder();
        if (stdout.TrimEnd() is { Length: > 0 } trimmedOut) sb.Append(trimmedOut);
        if (stderr.TrimEnd() is { Length: > 0 } trimmedErr)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append("[stderr] ").Append(trimmedErr);
        }
        if (process.ExitCode != 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append($"[exit {process.ExitCode}]");
        }

        return new NetworkOutcome(process.ExitCode, sb.ToString());
    }

    [DllImport("kernel32.dll", EntryPoint = "GetOEMCP")]
    private static extern int GetOemCodePage();
}
