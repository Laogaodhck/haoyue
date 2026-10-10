using System.Text;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;

namespace Haoyue.Runtime.ComputerUse.SystemControl;

/// <summary>
/// computer_sysinfo: reads system configuration (OS edition/build, machine identity,
/// uptime, locale, power scheme, displays, page file), runtime facts (.NET version,
/// Python availability) and running processes sorted by CPU. Read-only; results feed
/// follow-up decisions (kill a process, free disk, change settings) made through
/// computer_exec.
/// </summary>
public sealed class SystemInfoTool(
    IPromptProvider prompts,
    ISystemControlAdapter adapter,
    ComputerUseConfig config) : BuiltinTool(prompts)
{
    public override string Name => "computer_sysinfo";
    public override string StatusLabel => "Reading system info";
    public override bool Mutating => false;
    public override bool RequiresWorkspace => false;

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("section", ToolSchema.String("Section to read: 'config' (system configuration and settings), 'processes' (running processes), or 'all'", "config", "processes", "all"), false),
        ("process_filter", ToolSchema.String("Case-insensitive substring filter applied to process names"), false),
        ("process_limit", ToolSchema.Integer("Maximum number of processes to list (default 30, max 200)"), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var section = GetString(arguments, "section")?.Trim().ToLowerInvariant() ?? "all";
        var limit = Math.Clamp(GetInt(arguments, "process_limit") ?? 30, 1, 200);
        var filter = GetString(arguments, "process_filter");

        var sb = new StringBuilder();
        sb.AppendLine($"[平台] {adapter.PlatformName} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");

        if (section is "all" or "config")
        {
            sb.AppendLine($"\n========== 系统配置 ({adapter.PlatformName}) ==========");
            sb.AppendLine(await adapter.GetSystemConfigAsync(ct).ConfigureAwait(false));
            sb.AppendLine($"\n========== 运行时环境 ==========");
            sb.AppendLine($".NET Runtime: {Environment.Version} ({(Environment.Is64BitProcess ? "64" : "32")}-bit)");
            sb.AppendLine($"逻辑处理器: {Environment.ProcessorCount} · 机器名: {Environment.MachineName}");
            sb.AppendLine($"Shell 环境: {SystemEnvironment.OsLabel} · computer_exec kind=auto 解析为 '{SystemEnvironment.DefaultShellKind}'（cmd 仅 Windows、bash 仅 POSIX、powershell 需 pwsh/powershell 在 PATH）");
            sb.AppendLine(RenderPythonStatus(config.PythonPath));
        }

        if (section is "all" or "processes")
        {
            sb.AppendLine($"\n========== 运行中进程 ({adapter.PlatformName}) ==========");
            sb.AppendLine(await adapter.ListProcessesAsync(filter, limit, ct).ConfigureAwait(false));
        }

        if (section is not ("all" or "config" or "processes"))
            return ToolResult.Fail("section 必须是 'config'、'processes' 或 'all'。");

        context.Events.Publish(new Haoyue.Runtime.Events.ComputerActionEvent(
            Action: "sysinfo", X: null, Y: null, Target: section,
            Success: true, Error: null, Base64Screenshot: null));

        return ToolResult.Ok(sb.ToString(), $"Read system info ({section})");
    }

    /// <summary>
    /// Python availability (with the probed version) is reported so the model knows
    /// whether computer_exec kind=python will work — and which interpreter will run;
    /// absence degrades to actionable guidance instead of a hard failure at
    /// execution time. Same locator computer_exec uses, so status and execution
    /// can never disagree.
    /// </summary>
    private static string RenderPythonStatus(string? configuredPath)
    {
        var probe = PythonLocator.Probe(configuredPath);
        return probe is not null
            ? $"Python: 已安装（{probe.Install.Executable}，{probe.Version}，来源: {probe.Install.Source}）— computer_exec kind=python 可用"
            : $"Python: 未安装 — computer_exec kind=python 不可用；{(OperatingSystem.IsWindows()
                ? "可将 python.exe 加入 PATH（或重装勾选 Add to PATH）、安装 py 启动器，"
                : "可安装 python3（如 sudo apt install python3），")}或在配置中设置 computerUse.pythonPath 后重试";
    }
}
