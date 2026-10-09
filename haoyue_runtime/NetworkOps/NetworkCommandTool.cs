using System.Text.Json.Nodes;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;

namespace Haoyue.Runtime.NetworkOps;

/// <summary>
/// network_cmd: runs one logical command from the network-ops directory. Arguments
/// are passed to the child process via argv — no shell — so the model cannot inject
/// commands. State-changing invocations additionally require AllowMutating in config.
/// </summary>
public sealed class NetworkCommandTool(
    IPromptProvider prompts,
    INetworkOpsAdapter adapter,
    NetworkOpsConfig config) : BuiltinTool(prompts)
{
    public override string Name => "network_cmd";
    public override string StatusLabel => "Running network command";
    // Whole tool is mutating so readonly/plan agents never see it; the per-command
    // read/write split is enforced at execution time via AllowMutating.
    public override bool Mutating => true;
    public override bool RequiresWorkspace => false;

    public override JsonObject ParameterSchema
    {
        get
        {
            var names = adapter.SupportedCommands.Select(c => c.Name).ToArray();
            var commandHelp = string.Join("；", adapter.SupportedCommands.Select(c => $"{c.Name}：{c.Description}"));
            return ToolSchema.Object(
                (ToolParams.Command, ToolSchema.String($"要执行的网络命令（{adapter.PlatformName} 可用）：{commandHelp}", names), true),
                (ToolParams.Target, ToolSchema.String("目标主机名、IP 或接口名（ping/traceroute/dns_lookup/ethtool 必填）"), false),
                (ToolParams.Args, ToolSchema.Array(
                    "附加参数（逐项传入，如 [\"-n\",\"4\"]）；不支持管道、重定向或命令链",
                    ToolSchema.String("单个参数")), false),
                ("timeout_seconds", ToolSchema.Integer("超时秒数（默认 60，上限 600）"), false));
        }
    }

    private static class ToolParams
    {
        public const string Command = "command";
        public const string Target = "target";
        public const string Args = "args";
    }

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var command = GetString(arguments, ToolParams.Command)?.Trim();
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Fail($"command 必填。可用命令：{string.Join("、", adapter.SupportedCommands.Select(c => c.Name))}。");

        var spec = adapter.SupportedCommands.FirstOrDefault(c => c.Name.Equals(command, StringComparison.OrdinalIgnoreCase));
        if (spec is null)
            return ToolResult.Fail($"未知命令 {command}（{adapter.PlatformName}）。可用命令：{string.Join("、", adapter.SupportedCommands.Select(c => c.Name))}。");

        var args = arguments[ToolParams.Args] as JsonArray;
        var argList = new List<string>();
        if (args is not null)
        {
            foreach (var node in args)
            {
                if (node is not null) argList.Add(node.ToString());
            }
        }

        if (NetworkOpsCatalog.ValidateArgs(argList) is { } invalid)
            return ToolResult.Fail(invalid);

        var target = GetString(arguments, ToolParams.Target)?.Trim();
        if (spec.NeedsTarget && string.IsNullOrWhiteSpace(target))
            return ToolResult.Fail($"命令 {spec.Name} 需要 target 参数：{spec.Description}。");

        if (NetworkOpsCatalog.IsMutating(spec, argList) && !config.AllowMutating)
            return ToolResult.Fail($"命令 {spec.Name} 属于变更类操作，已被 networkOps.allowMutating=false 禁止。只读查询命令仍可用。");

        var timeout = Math.Clamp(GetInt(arguments, "timeout_seconds") ?? config.TimeoutSeconds, 1, 600);

        var outcome = await adapter.ExecuteAsync(spec.Name, target, argList, timeout, ct).ConfigureAwait(false);
        var header = $"[{adapter.PlatformName}] {spec.Name}{(string.IsNullOrWhiteSpace(target) ? "" : $" {target}")} → exit {outcome.ExitCode}";
        return ToolResult.Ok($"{header}\n{outcome.Output}", $"Network {spec.Name} (exit {outcome.ExitCode})");
    }
}
