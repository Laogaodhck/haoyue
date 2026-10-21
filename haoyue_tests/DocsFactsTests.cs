using System.Text.Json;
using System.Text.RegularExpressions;

namespace Haoyue.Tests;

/// <summary>
/// 文档数字快照（docs-facts）：README 里的统计数字必须由源码现场推导并对齐，
/// 防止测试数、方法数随演进漂移后文档陈旧。统计口径与 CI 实际执行一致：
/// - xunit 用例 = [Fact] 数 + [InlineData] 行数（与 dotnet test --list-tests 实际枚举数一致，已实证）
/// - vitest 用例 = it(/test( 行数 + it.each 的 case 数（与 pnpm test 输出一致，已实证）
/// - daemon 方法数 = contracts/daemon-contract.json 的 methods 数组长度（快照由代码生成）
/// - agent-eval 任务数 = EvalTasks.cs 中 new EvalTask 出现次数
/// </summary>
public class DocsFactsTests
{
    // ---------------------------------------------------------------- 口径实现

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Haoyue.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("未找到仓库根目录（Haoyue.slnx）");
    }

    /// <summary>
    /// xunit 实际枚举用例数：[Fact] + [InlineData]，计数前剥离注释。
    /// 全量口径（含本文件自身）——与 dotnet test 实际执行总数一致；
    /// 排除自身会让 README 数字系统性偏小（护栏用例也是真实执行的用例）。
    /// </summary>
    private static int CountXunitCases(string testDir) =>
        Directory.EnumerateFiles(testDir, "*.cs", SearchOption.AllDirectories)
            .Sum(f => CountXunitCasesIn(File.ReadAllText(f)));

    private static int CountXunitCasesIn(string src)
    {
        // 先剥离注释，避免文档里出现的「[Fact]」字样混入统计（本文件注释即自匹配实例）。
        var code = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        code = Regex.Replace(code, @"(?<!:)//.*", "");
        return Regex.Matches(code, @"\[Fact\]").Count
            + Regex.Matches(code, @"\[InlineData\(").Count;
    }

    /// <summary>vitest 实际执行用例数与测试文件数：it(/test( 行 + it.each 的 case 数。</summary>
    private static (int Files, int Cases) CountVitestTests(string desktopSrc)
    {
        var files = 0;
        var cases = 0;
        foreach (var f in Directory.EnumerateFiles(desktopSrc, "*.test.ts*", SearchOption.AllDirectories))
        {
            if (!f.EndsWith(".test.ts", StringComparison.Ordinal) && !f.EndsWith(".test.tsx", StringComparison.Ordinal)) continue;
            files++;
            var src = File.ReadAllText(f);
            cases += Regex.Matches(src, @"(?m)^\s*(?:it|test)\(").Count;
            foreach (Match m in Regex.Matches(src, @"it\.each\(\[(.*?)\]\)", RegexOptions.Singleline))
                cases += Regex.Matches(m.Groups[1].Value, @"(?m)^\s*\[").Count;
        }
        return (files, cases);
    }

    private static string ReadRepoFile(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    private static void AssertReadmeMentions(string[] zhNeedles, string[] enNeedles)
    {
        var zh = ReadRepoFile("README.md");
        var en = ReadRepoFile("README_EN.md");
        foreach (var needle in zhNeedles)
            Assert.Contains(needle, zh, StringComparison.Ordinal);
        foreach (var needle in enNeedles)
            Assert.Contains(needle, en, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 快照断言

    [Fact]
    public void Runtime_TestCount_MatchesReadme()
    {
        var n = CountXunitCases(Path.Combine(FindRepoRoot(), "haoyue_tests"));
        Assert.True(n >= 900, $"runtime 用例数异常：{n}");
        AssertReadmeMentions([$"{n} 用例", $"{n}+18+"], [$"{n} cases", $"{n}+18+"]);
    }

    [Fact]
    public void Cli_TestCount_MatchesReadme()
    {
        var n = CountXunitCases(Path.Combine(FindRepoRoot(), "haoyue_cli_tests"));
        Assert.Equal(18, n);
        AssertReadmeMentions([$"haoyue_cli_tests {n} 用例"], [$"haoyue_cli_tests {n} cases"]);
    }

    [Fact]
    public void Desktop_TestCounts_MatchesReadme()
    {
        var (files, cases) = CountVitestTests(Path.Combine(FindRepoRoot(), "haoyue_desktop", "src"));
        Assert.True(files >= 15, $"桌面测试文件数异常：{files}");
        Assert.True(cases >= 150, $"桌面用例数异常：{cases}");
        AssertReadmeMentions([$"{cases} 用例（{files} 个文件）"], [$"{cases} cases across {files} files"]);
    }

    [Fact]
    public void Daemon_MethodCount_MatchesReadme()
    {
        var json = JsonDocument.Parse(ReadRepoFile("contracts", "daemon-contract.json"));
        var n = json.RootElement.GetProperty("methods").GetArrayLength();
        Assert.Equal(124, n);
        AssertReadmeMentions([$"{n} 个 RPC 方法"], [$"all {n} RPC methods"]);
    }

    [Fact]
    public void AgentEval_TaskCount_MatchesReadme()
    {
        var src = ReadRepoFile("benchmarks", "agent-eval", "EvalTasks.cs");
        var n = Regex.Matches(src, @"new EvalTask").Count;
        Assert.True(n >= 8, $"agent-eval 任务数异常：{n}");
        // agent-eval README 的任务表：除表头与分隔行外应恰有 n 行。
        var tableRows = ReadRepoFile("benchmarks", "agent-eval", "README.md")
            .Split('\n')
            .Count(l => l.StartsWith("| ") && !l.Contains("|---") && !l.Contains("| 针对能力"));
        Assert.Equal(n, tableRows);
    }

    /// <summary>
    /// 巨型类行数预算护栏（ADR-20261015-giant-class-budget.md）：
    /// 超额即失败——任何继续膨胀必须先显式上调预算并同步 ADR 与本测试。
    /// 预算 = 2026-10-15 基线行数 + 约 3% 余量。
    /// </summary>
    [Theory]
    [InlineData("haoyue_runtime/Daemon/DaemonAdminApi.cs", 3350)]
    [InlineData("haoyue_runtime/Daemon/DaemonServer.cs", 3150)]
    [InlineData("haoyue_desktop/src/renderer/src/components/SettingsDialog.vue", 2700)]
    [InlineData("haoyue_desktop/src/renderer/src/App.vue", 2400)]
    public void GiantClass_LineBudget_Maintained(string relativePath, int budget)
    {
        var lines = File.ReadAllLines(Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar))).Length;
        Assert.True(lines <= budget,
            $"{relativePath} 已达 {lines} 行，超出预算 {budget}（ADR-20261015-giant-class-budget.md）。" +
            "先做拆分评审或显式上调预算（同步 ADR 与本测试），不要默认继续膨胀。");
    }
}
