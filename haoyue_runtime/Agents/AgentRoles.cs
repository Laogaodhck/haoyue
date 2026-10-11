namespace Haoyue.Runtime.Agents;

/// <summary>
/// S7 角色化子代理：在 delegate_task/delegate_tasks 之上叠加「角色预设」——
/// 注入角色提示词并收敛工具白名单。预设是纯提示词/工具边界组合，
/// 复用既有 fan-out、文件锁与深度控制机制，不引入新的执行路径。
/// </summary>
public sealed record AgentRole(
    string Id,
    string Name,
    string Description,
    /// <summary>注入子任务前的角色提示词（说明视角、方法与纪律）。</summary>
    string Prompt,
    /// <summary>工具白名单；null = 不限制（继承默认工具视图）。</summary>
    IReadOnlyList<string>? AllowedTools)
{
    /// <summary>统一汇报模板：所有角色返回同一结构，聚合方稳定解析。</summary>
    public const string ReportTemplate = """

汇报模板（严格遵守）：
## 结论
<一段话给出核心结果或答案>
## 关键发现
- <要点列表；每条独立成立，带证据或数据>
## 涉及文件
- <读/写过的文件路径；未动文件则写「无」>
## 风险与不确定性
- <已识别风险、未验证假设；没有则写「无」>
""";

    /// <summary>内置角色注册表。键为 role 参数的合法取值。</summary>
    public static readonly IReadOnlyDictionary<string, AgentRole> Builtins =
        new Dictionary<string, AgentRole>(StringComparer.OrdinalIgnoreCase)
        {
            ["researcher"] = new AgentRole(
                "researcher",
                "研究员",
                "只读调研：搜集证据、交叉核验、输出结构化结论，不改任何文件。",
                """
你以「研究员」角色执行子任务。纪律：
- 只读：禁止修改/创建/删除任何工作区文件，也禁止执行会改状态的命令（写文件、安装依赖、网络发帖等）。
- 证据优先：每个结论都要有出处——文件路径:行号、URL、命令输出；无法核验的写明「未验证」。
- 广度与深度平衡：先扫全景，再对关键分歧点下钻；两处信息冲突时显式指出而不是选边。
- 引用可追溯：结论中的每个事实性陈述都要能在「关键发现」里找到对应证据。
""",
                ["read_file", "grep", "glob", "list_dir", "bash", "web_search", "web_fetch", "fact_save", "update_plan"]),
            ["implementer"] = new AgentRole(
                "implementer",
                "实现者",
                "动手实现：按任务约束修改代码/文档，遵循既有工程规范并自验。",
                """
你以「实现者」角色执行子任务。纪律：
- 最小改动：只改任务要求的内容；顺手「改进」无关代码是缺陷不是贡献。
- 先读后写：改动前先读目标文件与相邻实现，遵循仓库既有风格、命名与工程规范（不改即视为尊重现状）。
- 自验：完成后运行与之相关的构建/测试或最小验证，并在汇报中给出验证方式与结果。
- 失败透明：无法完成或验证时，直接说明卡点与已尝试路径，不要交付未验证的实现。
""",
                null),
            ["reviewer"] = new AgentRole(
                "reviewer",
                "评审员",
                "只读评审：审查指定对象的质量、风险与规范符合度，输出分级意见。",
                """
你以「评审员」角色执行子任务。纪律：
- 只读：与研究员相同，不改任何文件。
- 分级输出：意见必须标注严重度（P0 阻断 / P1 应修 / P2 建议 / P3 备注），每条意见给位置（文件:行）与修改建议。
- 假设对手方是强者：优先找设计缺陷、边界条件、并发/错误处理遗漏，而不是风格吹毛求疵。
- 下限明确：整体结论用「可合入 / 需修改后合入 / 阻断」三档之一收尾。
""",
                ["read_file", "grep", "glob", "list_dir", "bash", "web_search", "web_fetch", "fact_save"]),
        };

    /// <summary>解析角色预设；unknown 抛 ArgumentException（调用方应先校验）。</summary>
    public static AgentRole Resolve(string role) =>
        Builtins.TryGetValue(role.Trim(), out var preset)
            ? preset
            : throw new ArgumentException(
                $"未知角色「{role}」。可选：{string.Join(" / ", Builtins.Keys.OrderBy(k => k, StringComparer.Ordinal))}");

    /// <summary>合法角色名（供工具 schema 描述与错误提示）。</summary>
    public static string ValidRoles => string.Join(" / ", Builtins.Keys.OrderBy(k => k, StringComparer.Ordinal));

    /// <summary>组装角色化任务文本：角色纪律 + 原任务 + 统一汇报模板。</summary>
    public string ComposeTask(string task) =>
        $"{Prompt}\n\n---\n\n# 子任务\n\n{task.Trim()}\n{ReportTemplate}";
}
