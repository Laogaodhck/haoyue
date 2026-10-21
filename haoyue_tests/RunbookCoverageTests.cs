using System.Reflection;
using Haoyue.Runtime.Skills;
using Haoyue.Runtime.Tools;

namespace Haoyue.Tests;

/// <summary>
/// Runbook 强制化护栏（知识沉淀机制）：
/// 1) 每个注册的内置工具必须有模型描述文件 prompts/tool/&lt;name&gt;.txt 与 runbook 章节；
/// 2) 每个官方技能必须有 runbook 章节。
/// 新增工具/技能而未补 runbook 时，本测试直接失败（见 haoyue_doc/runbooks/README.md）。
/// </summary>
public sealed class RunbookCoverageTests
{
    private static readonly Lazy<string> RepoRoot = new(FindRepoRoot);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "haoyue_doc")))
        {
            dir = dir.Parent!;
        }
        if (dir is null) throw new InvalidOperationException("未能从测试输出目录定位仓库根（缺少 haoyue_doc）");
        return dir.FullName;
    }

    /// <summary>反射扫描 haoyue_runtime 中所有具体 ITool 实现，读取真实 Name（注册清单即实现清单）。</summary>
    private static IReadOnlyList<string> BuiltinToolNames()
    {
        var toolInterface = typeof(ITool);
        var names = new List<string>();
        foreach (var type in toolInterface.Assembly.GetTypes())
        {
            if (type is not { IsClass: true, IsAbstract: false } || !toolInterface.IsAssignableFrom(type))
                continue;
            // 只覆盖静态注册的内置工具（Tools/Skills 命名空间）；MCP 等运行时动态适配器
            // 的构造函数依赖外部客户端实例，且不通过 RegisterBuiltinTools 注册。
            if (!type.Namespace!.StartsWith("Haoyue.Runtime.Tools", StringComparison.Ordinal)
                && !type.Namespace!.StartsWith("Haoyue.Runtime.Skills", StringComparison.Ordinal))
                continue;

            var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(c => c.GetParameters().Length)
                .First();
            var args = ctor.GetParameters()
                .Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
                .ToArray();
            var tool = (ITool)Activator.CreateInstance(type, args)!;
            names.Add(tool.Name);
        }
        return names;
    }

    private static string ReadRunbook(string file) =>
        File.ReadAllText(Path.Combine(RepoRoot.Value, "haoyue_doc", "runbooks", file));

    [Fact]
    public void EveryBuiltinTool_HasPromptFile_AndRunbookSection()
    {
        var runbook = ReadRunbook("tools.md");
        var missing = new List<string>();
        foreach (var name in BuiltinToolNames())
        {
            var promptFile = Path.Combine(AppContext.BaseDirectory, "prompts", "tool", $"{name}.txt");
            if (!File.Exists(promptFile)) missing.Add($"工具 {name} 缺少模型描述文件 prompts/tool/{name}.txt");
            if (!runbook.Contains($"## {name}\n", StringComparison.Ordinal))
                missing.Add($"工具 {name} 缺少 runbook 章节（haoyue_doc/runbooks/tools.md 增加 '## {name}'）");
        }
        Assert.True(missing.Count == 0, "Runbook 覆盖缺口：\n" + string.Join("\n", missing));
    }

    [Fact]
    public void EveryOfficialSkill_HasRunbookSection()
    {
        var runbook = ReadRunbook("skills.md");
        var missing = OfficialSkillCatalog.Entries
            .Where(skill => !runbook.Contains($"## {skill.Slug}\n", StringComparison.Ordinal))
            .Select(skill => $"官方技能 {skill.Slug} 缺少 runbook 章节（haoyue_doc/runbooks/skills.md）")
            .ToList();
        Assert.True(missing.Count == 0, "Runbook 覆盖缺口：\n" + string.Join("\n", missing));
    }

    [Fact]
    public void RunbookSection_IsUnique_PerTool()
    {
        // 同名重复章节意味着有人复制粘贴后忘了改工具名，会造成文档歧义。
        var runbook = ReadRunbook("tools.md");
        var headers = runbook.Split('\n')
            .Where(line => line.StartsWith("## ", StringComparison.Ordinal))
            .Select(line => line[3..].Trim())
            .ToList();
        var duplicates = headers.GroupBy(h => h).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, $"tools.md 存在重复章节：{string.Join(", ", duplicates)}");
    }
    /// <summary>7.8 故障 runbook：必备场景 + 每条四段式（现象/定位命令/恢复动作/升级路径）。</summary>
    private static readonly string[] IncidentScenarios =
    [
        "## daemon 崩溃或桌面端「正在准备」卡住",
        "## 本地模型加载失败 / 回合迟迟不开始",
        "## 显存不足（CUDA out of memory）",
        "## MCP 服务器 / 连接器失效",
        "## usage 数据库（haoyue.db）损坏",
        "## 日志与诊断包使用",
    ];

    /// <summary>四段式小标题：缺任何一条都算「写出来即空话」，护栏直接红灯。</summary>
    private static readonly string[] IncidentSections = ["- **现象**", "- **定位命令**", "- **恢复动作**", "- **升级路径**"];

    /// <summary>
    /// `haoyue &lt;verb&gt;` 命令词白名单，镜像 Program.cs 的顶层命令表（`haoyue --help`）。
    /// 白名单的意义是挡住文档里手滑/凭记忆编出来的命令——新增命令时同步在此登记。
    /// </summary>
    private static readonly string[] KnownCliVerbs =
    [
        "chat", "provider", "model", "usage", "doctor", "switch", "init", "skill", "mcp",
        "connectors", "sessions", "knowledge", "memory", "rules", "expert", "schedule",
        "evolve", "daemon",
    ];

    [Fact]
    public void Incidents_RunbookHasAllScenariosInFourPartForm()
    {
        var runbook = ReadRunbook("incidents.md");
        var problems = new List<string>();
        foreach (var scenario in IncidentScenarios)
        {
            var start = runbook.IndexOf(scenario, StringComparison.Ordinal);
            if (start < 0)
            {
                problems.Add($"缺少场景章节：{scenario[3..]}");
                continue;
            }
            // 章节正文到下一个 ## 为止，检查四段式小标题齐全。
            var next = runbook.IndexOf("\n## ", start + scenario.Length, StringComparison.Ordinal);
            var body = next < 0 ? runbook[start..] : runbook[start..next];
            foreach (var section in IncidentSections)
                if (!body.Contains(section, StringComparison.Ordinal))
                    problems.Add($"场景「{scenario[3..]}」缺少小节：{section}");
        }
        Assert.True(problems.Count == 0, "incidents.md 结构不完整：\n" + string.Join("\n", problems));
    }

    [Fact]
    public void Incidents_OnlyReferencesExistingCliCommands()
    {
        var runbook = ReadRunbook("incidents.md");
        // 抓 `haoyue <verb>`：verb 取第一个词（排除选项与占位符）。
        var verbs = System.Text.RegularExpressions.Regex
            .Matches(runbook, @"haoyue\s+([a-z][a-z-]*)")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(verbs);
        var unknown = verbs.Where(verb => !KnownCliVerbs.Contains(verb, StringComparer.Ordinal)).ToList();
        Assert.True(unknown.Count == 0,
            $"incidents.md 引用了不存在的 CLI 命令：{string.Join(", ", unknown)}（白名单见 RunbookCoverageTests.KnownCliVerbs）");
    }
}

