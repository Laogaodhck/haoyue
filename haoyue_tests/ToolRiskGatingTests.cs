using System.Text.Json.Nodes;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// 5.2 工具风险分级：五维标签（read/write/network/cost/system）、分级同意矩阵判定、
/// 内置高危工具的打标正确性，以及 ITool 默认推断（写 = Write，RequiresNetwork = Network）。
/// </summary>
public sealed class ToolRiskGatingTests
{
    [Fact]
    public void Matches_HonorsConfiguredDimensions_CaseInsensitive()
    {
        var bash = ToolRiskTier.Write | ToolRiskTier.System;
        Assert.True(ToolRiskGating.Matches(bash, ["system"]));
        Assert.True(ToolRiskGating.Matches(bash, ["SYSTEM"]));
        Assert.True(ToolRiskGating.Matches(bash, ["cost", "system"]));
        Assert.False(ToolRiskGating.Matches(bash, ["cost"]));
        Assert.False(ToolRiskGating.Matches(bash, ["network"]));
    }

    [Fact]
    public void Matches_IgnoresUnknownLabels_AndEmptyConfigMeansNoGating()
    {
        var tier = ToolRiskTier.Cost;
        Assert.False(ToolRiskGating.Matches(tier, ["super-dangerous"]));
        Assert.False(ToolRiskGating.Matches(tier, []));
        Assert.False(ToolRiskGating.Matches(tier, null));
        // 单条非法标签不会让其余合法标签失效。
        Assert.True(ToolRiskGating.Matches(tier, ["bogus", "cost"]));
    }

    [Fact]
    public void Matches_CoversEveryDimension_ForUiAndConfigReuse()
    {
        Assert.True(ToolRiskGating.Matches(ToolRiskTier.Read, ["read"]));
        Assert.True(ToolRiskGating.Matches(ToolRiskTier.Write, ["write"]));
        Assert.True(ToolRiskGating.Matches(ToolRiskTier.Network, ["network"]));
        Assert.True(ToolRiskGating.Matches(ToolRiskTier.Cost, ["cost"]));
        Assert.True(ToolRiskGating.Matches(ToolRiskTier.System, ["system"]));
    }

    [Fact]
    public void Describe_RendersReadableChineseLabels()
    {
        Assert.Equal("写入 + 系统", ToolRiskGating.Describe(ToolRiskTier.Write | ToolRiskTier.System));
        // 输出按枚举位序（Read→Write→Network→Cost→System），不是入参顺序。
        Assert.Equal("写入 + 外发 + 费用", ToolRiskGating.Describe(ToolRiskTier.Cost | ToolRiskTier.Network | ToolRiskTier.Write));
        Assert.Equal("无风险标记", ToolRiskGating.Describe(ToolRiskTier.None));
    }

    [Fact]
    public void DefaultRiskTier_InfersWriteAndNetwork()
    {
        // 接口默认推断：写工具 + 联网工具都自动拿到正确维度，未打标的工具无需逐个改。
        // 接口默认成员只能经 ITool 引用访问（类实例不暴露默认实现）。
        Assert.Equal(ToolRiskTier.Write, ((ITool)new StubTool("plain-writer", mutating: true)).RiskTier);
        Assert.Equal(ToolRiskTier.Read, ((ITool)new StubTool("plain-reader", mutating: false)).RiskTier);
        Assert.Equal(ToolRiskTier.Read | ToolRiskTier.Network,
            ((ITool)new StubTool("net-reader", mutating: false, network: true)).RiskTier);
    }

    [Fact]
    public void B_49_HighRiskBuiltinTools_AreTaggedSystemOrCost()
    {
        // 打标是「首次必过人工」的前提：执行命令/控制本机 → System；按次计费 → Cost。
        var bash = new Haoyue.Runtime.Tools.Builtin.BashTool(new FilePromptProvider());
        Assert.True((bash.RiskTier & ToolRiskTier.System) != 0, "bash 必须带 System 维度");

        var terminal = new Haoyue.Runtime.Tools.Builtin.TerminalTool(new FilePromptProvider());
        Assert.True((terminal.RiskTier & ToolRiskTier.System) != 0, "terminal 必须带 System 维度");

        // image_generate 的构造依赖 config store 与 HTTP 工厂：用源码声明断言
        // （Cost 维度是「花钱操作首次必过人工」的依据，不能靠默认推断拿到）。
        var imageGenSource = File.ReadAllText(Path.Combine(
            RepoRoot(), "haoyue_runtime", "Tools", "Builtin", "ImageGenTool.cs"));
        Assert.Contains("ToolRiskTier.Cost", imageGenSource, StringComparison.Ordinal);
        Assert.Contains("ToolRiskTier.Network", imageGenSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadOnlyTools_AreNotGatedByDefaultDimensionSet()
    {
        // 默认同意维度 system+cost：日常读写不该被打断。
        var policy = new ToolPolicyConfig();
        ITool readFile = new Haoyue.Runtime.Tools.Builtin.ReadFileTool(new FilePromptProvider());
        ITool writeFile = new Haoyue.Runtime.Tools.Builtin.WriteFileTool(new FilePromptProvider());
        Assert.False(ToolRiskGating.Matches(readFile.RiskTier, policy.ConsentRiskTiers));
        Assert.False(ToolRiskGating.Matches(writeFile.RiskTier, policy.ConsentRiskTiers));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "haoyue_doc")))
            dir = dir.Parent!;
        return dir?.FullName ?? throw new InvalidOperationException("未能定位仓库根");
    }

    private sealed class StubTool(string name, bool mutating, bool network = false) : ITool
    {
        public string Name => name;
        public string Description => name;
        public JsonObject ParameterSchema => new();
        public bool Mutating => mutating;
        public bool RequiresNetwork => network;
        public bool RequiresWorkspace => false;
        public string StatusLabel => name;
        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok("ok"));
    }
}
