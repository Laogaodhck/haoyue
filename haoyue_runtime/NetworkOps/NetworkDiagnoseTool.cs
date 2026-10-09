using System.Text;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;

namespace Haoyue.Runtime.NetworkOps;

/// <summary>
/// network_diagnose: one-shot read-only network health snapshot. Runs the selected
/// sections (interfaces / routes / connections / arp / dns) and returns a labeled
/// bundle, so a diagnosis costs one tool call instead of five.
/// </summary>
public sealed class NetworkDiagnoseTool(
    IPromptProvider prompts,
    INetworkOpsAdapter adapter) : BuiltinTool(prompts)
{
    public override string Name => "network_diagnose";
    public override string StatusLabel => "Diagnosing network";
    public override bool Mutating => false;
    public override bool RequiresWorkspace => false;

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("section", ToolSchema.String(
            "要诊断的部分：all（全部）或 interfaces（接口与 IP）、routes（路由表）、connections（活动连接与端口）、arp（邻居表）、dns（DNS 配置）",
            "all", "interfaces", "routes", "connections", "arp", "dns"), false));

    private static readonly string[] Sections = ["interfaces", "routes", "connections", "arp", "dns"];

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var section = GetString(arguments, "section")?.Trim().ToLowerInvariant() ?? "all";
        if (section is not "all" && !Sections.Contains(section))
            return ToolResult.Fail("section 必须是 all、interfaces、routes、connections、arp 或 dns。");

        var wanted = section == "all" ? Sections : [section];

        var sb = new StringBuilder();
        sb.AppendLine($"[平台] {adapter.PlatformName} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");

        foreach (var part in wanted)
        {
            sb.AppendLine($"\n========== {SectionTitle(part)} ({adapter.PlatformName}) ==========");
            sb.AppendLine(await adapter.DiagnoseSectionAsync(part, ct).ConfigureAwait(false));
        }

        return ToolResult.Ok(sb.ToString(), $"Diagnosed network ({section})");
    }

    private static string SectionTitle(string section) => section switch
    {
        "interfaces" => "网络接口与 IP",
        "routes" => "路由表",
        "connections" => "活动连接与监听端口",
        "arp" => "ARP / 邻居表",
        "dns" => "DNS 配置",
        _ => section,
    };
}
