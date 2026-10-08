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
}
