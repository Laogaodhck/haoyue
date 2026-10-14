using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Agents;

namespace Haoyue.Tests;

public sealed class PromptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "haoyue-tests", Guid.NewGuid().ToString("N"));

    public PromptTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string MakeRoot(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Render_ReplacesKnownVariables_KeepsUnknown()
    {
        using var provider = new FilePromptProvider([MakeRoot("a")]);
        var result = provider.Render(
            "Hello {{name}}, os={{os}}, missing={{nope}}",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["name"] = "world", ["os"] = "win" });

        Assert.Equal("Hello world, os=win, missing={{nope}}", result);
    }

    [Fact]
    public void TryGet_ResolvesFromRoot_AndCaches()
    {
        var root = MakeRoot("prompts");
        Directory.CreateDirectory(Path.Combine(root, "system"));
        File.WriteAllText(Path.Combine(root, "system", "default.txt"), "MAIN PROMPT");

        using var provider = new FilePromptProvider([root]);
        Assert.Equal("MAIN PROMPT", provider.TryGet("system/default"));
        Assert.Null(provider.TryGet("system/missing"));
    }

    [Fact]
    public void WorkspaceRoot_TakesPriorityOverDefaults()
    {
        var appRoot = MakeRoot("app");
        Directory.CreateDirectory(Path.Combine(appRoot, "system"));
        File.WriteAllText(Path.Combine(appRoot, "system", "default.txt"), "DEFAULT");

        var workspaceRoot = MakeRoot("workspace-prompts");
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "system"));
        File.WriteAllText(Path.Combine(workspaceRoot, "system", "default.txt"), "WORKSPACE");

        using var provider = new FilePromptProvider([appRoot]);
        Assert.Equal("DEFAULT", provider.TryGet("system/default"));

        provider.SetWorkspaceRoot(workspaceRoot);
        Assert.Equal("WORKSPACE", provider.TryGet("system/default"));

        provider.SetWorkspaceRoot(null);
        Assert.Equal("DEFAULT", provider.TryGet("system/default"));
    }

    [Fact]
    public async Task Composer_OrdersContributionsBySlot_AndRendersVariables()
    {
        using var provider = new FilePromptProvider([MakeRoot("empty")]);
        var registry = new PromptRegistry();
        registry.Register(new PromptContribution("mem", PromptSlot.Memory,
            (_, _) => ValueTask.FromResult<string?>("MEMORY {{project}}")));
        registry.Register(new PromptContribution("sys", PromptSlot.System,
            (_, _) => ValueTask.FromResult<string?>("SYSTEM")));
        registry.Register(new PromptContribution("skill", PromptSlot.Skill,
            (_, _) => ValueTask.FromResult<string?>("SKILL")));

        var composer = new PromptComposer(provider, registry);
        var result = await composer.ComposeAsync(new PromptRenderContext
        {
            Variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["project"] = "demo" },
        });

        // Skill is the only turn-variant segment and must compose LAST so a trigger-set
        // change never invalidates the KV/prompt-cache prefix of the stable content.
        Assert.Equal("SYSTEM\n\nMEMORY demo\n\nSKILL", result);
    }

    [Fact]
    public async Task Composer_SkillSegmentComesLast_ForCacheStablePrefix()
    {
        // 缓存稳定性契约：skills 段（唯一逐回合变化的段）必须位于工具 schema 与记忆之后。
        // KV 前缀复用与远程 prompt cache 都按最长公共前缀命中——skills 置中时每次触发集
        // 变化都会重新预填充其后的全部工具 schema（数千 token）。
        using var provider = new FilePromptProvider([MakeRoot("empty-order")]);
        var registry = new PromptRegistry();
        registry.Register(new PromptContribution("skill", PromptSlot.Skill,
            (_, _) => ValueTask.FromResult<string?>("SKILL")));
        registry.Register(new PromptContribution("mem", PromptSlot.Memory,
            (_, _) => ValueTask.FromResult<string?>("MEMORY")));
        registry.Register(new PromptContribution("tool", PromptSlot.Tool,
            (_, _) => ValueTask.FromResult<string?>("TOOLS")));
        registry.Register(new PromptContribution("sys", PromptSlot.System,
            (_, _) => ValueTask.FromResult<string?>("SYSTEM")));

        var composer = new PromptComposer(provider, registry);
        var result = await composer.ComposeAsync(new PromptRenderContext { Variables = new Dictionary<string, string>() });

        var skillAt = result.IndexOf("SKILL", StringComparison.Ordinal);
        var toolAt = result.IndexOf("TOOLS", StringComparison.Ordinal);
        var memAt = result.IndexOf("MEMORY", StringComparison.Ordinal);
        var sysAt = result.IndexOf("SYSTEM", StringComparison.Ordinal);
        Assert.True(sysAt < toolAt && toolAt < memAt && memAt < skillAt,
            $"expected SYSTEM < TOOLS < MEMORY < SKILL, got: {result}");
    }

    [Fact]
    public void Registry_Unregister_RemovesContribution()
    {
        var registry = new PromptRegistry();
        var registration = registry.Register(new PromptContribution("x", PromptSlot.System,
            (_, _) => ValueTask.FromResult<string?>("X")));
        Assert.Single(registry.All);
        registration.Dispose();
        Assert.Empty(registry.All);
    }

    [Fact]
    public void CapabilityInstruction_TellsVisionModelsToUseAttachedImages()
    {
        Assert.Equal("", PromptVariables.BuildCapabilityInstruction(vision: false));

        var visionPrompt = PromptVariables.BuildCapabilityInstruction(vision: true);
        Assert.Contains("MULTIMODAL", visionPrompt);
        Assert.Contains("Image attachments in user messages are available", visionPrompt);
        Assert.Contains("Do not say that you lack a vision encoder", visionPrompt);
        Assert.Contains("provider explicitly exposes another output modality", visionPrompt);

        var imageOutputPrompt = PromptVariables.BuildCapabilityInstruction(vision: true, imageOutput: true);
        Assert.Contains("may also expose image output", imageOutputPrompt);
    }

    [Fact]
    public void PromptVariables_Build_IncludesRuntimePermissionContext()
    {
        var variables = PromptVariables.Build(
            workspace: null,
            model: null,
            toolNames: ["read_file"],
            memory: "memory",
            mode: "plan",
            networkEnabled: false,
            autoVerify: false,
            personality: "friendly");

        Assert.Equal("plan", variables["mode"]);
        Assert.Equal("disabled", variables["network"]);
        Assert.Equal("read_only", variables["sandbox_mode"]);
        Assert.Equal("never", variables["approval_policy"]);
        Assert.Equal("false", variables["auto_verify"]);
        Assert.Equal("friendly", variables["personality"]);
    }

    [Fact]
    public void FitInjectedText_BoundsLongFragments()
    {
        var text = new string('a', 20_000);
        var fit = ContextPlanner.FitInjectedText(text, maxTokens: 100);

        // 截断提示条自身也算预算，所以整体必须落在申请额度内而不是「加个尾巴再超一点」。
        Assert.True(ContextPlanner.EstimateTokens(fit) <= 100);
        Assert.Contains("注入截断", fit);
        // 保头保尾：两端仍是原文的前缀/后缀，中间缺省由提示条说明。
        var head = fit[..fit.IndexOf('\n')];
        var tail = fit[fit.LastIndexOf('\n')..].TrimStart('\n');
        Assert.True(head.Length > 0 && text.StartsWith(head));
        Assert.True(tail.Length > 0 && text.EndsWith(tail));
    }

    [Fact]
    public void FitInjectedText_TrimIsAnnouncedAndPaidForOutOfTheBudget()
    {
        var text = string.Concat(Enumerable.Repeat("记忆内容 memory entry ", 4_000));

        var fit = ContextPlanner.FitInjectedText(text, maxTokens: 600, label: "MEMORY.md");

        // 静默截断是原缺陷：模型读到缺半截却以为那就是全文。
        Assert.Contains("MEMORY.md", fit);
        Assert.Contains("省略", fit);
        Assert.Contains("read_file", fit);
        // 提示条本身也算预算，不能因为加了标记反而超出注入额度。
        Assert.True(ContextPlanner.EstimateTokens(fit) <= 600,
            $"fitted fragment costs {ContextPlanner.EstimateTokens(fit)} tokens over a 600 budget");
    }

    [Fact]
    public async Task Composer_EnforcesSystemPromptBudget_ByDegradeRank()
    {
        using var provider = new FilePromptProvider([MakeRoot("empty-budget")]);
        var registry = new PromptRegistry();
        static string Big(char filler, int tokens) => new(filler, tokens * 4); // Latin ≈ 4 chars/token

        registry.Register(new PromptContribution("sys", PromptSlot.System,
            (_, _) => ValueTask.FromResult<string?>("SYSTEM " + Big('a', 14_000))));
        registry.Register(new PromptContribution("mem-high", PromptSlot.Memory,
            (_, _) => ValueTask.FromResult<string?>(Big('d', 5_000)), DegradeRank: 40));
        registry.Register(new PromptContribution("skill-body", PromptSlot.Skill,
            (_, _) => ValueTask.FromResult<string?>(Big('c', 5_000)), DegradeRank: 30));
        registry.Register(new PromptContribution("mcp", PromptSlot.Tool,
            (_, _) => ValueTask.FromResult<string?>(Big('b', 5_000)), DegradeRank: 20));
        registry.Register(new PromptContribution("mem-low", PromptSlot.Memory,
            (_, _) => ValueTask.FromResult<string?>(Big('e', 1_000)), DegradeRank: 10));

        var composer = new PromptComposer(provider, registry);
        var result = await composer.ComposeAsync(new PromptRenderContext { Variables = new Dictionary<string, string>() });

        // Total ≈ 30k tokens > 24k: mem-high (40) and skill-body (30) degrade first,
        // the remainder fits and stays.
        Assert.Contains("SYSTEM ", result);
        Assert.Contains(Big('b', 5_000), result);   // mcp kept
        Assert.Contains(Big('e', 1_000), result);   // mem-low kept
        Assert.DoesNotContain(Big('d', 5_000), result); // mem-high dropped
        Assert.DoesNotContain(Big('c', 5_000), result); // skill-body dropped
        Assert.Contains(">>> [prompt budget]", result);
        Assert.Contains("mem-high, skill-body", result);
        Assert.Contains("declare_skill", result);
        Assert.True(ContextPlanner.EstimateTokens(result) <= ContextPlanner.MaxSystemPromptTokens + 200);
    }

    [Fact]
    public async Task Composer_SystemAndDeveloperSlots_AreNeverDropped()
    {
        using var provider = new FilePromptProvider([MakeRoot("empty-protected")]);
        var registry = new PromptRegistry();
        static string Big(char filler, int tokens) => new(filler, tokens * 4);

        registry.Register(new PromptContribution("sys", PromptSlot.System,
            (_, _) => ValueTask.FromResult<string?>("SYSTEM " + Big('a', 20_000))));
        registry.Register(new PromptContribution("dev", PromptSlot.Developer,
            (_, _) => ValueTask.FromResult<string?>("DEVELOPER " + Big('f', 6_000))));

        var composer = new PromptComposer(provider, registry);
        var result = await composer.ComposeAsync(new PromptRenderContext { Variables = new Dictionary<string, string>() });

        // Over budget but everything is protected: nothing is dropped, no notice.
        Assert.Contains("SYSTEM ", result);
        Assert.Contains("DEVELOPER ", result);
        Assert.DoesNotContain(">>> [prompt budget]", result);
    }

    [Fact]
    public void WrapWorkspaceInstructions_MarksProvenance_AndPinsPrecedence()
    {
        var wrapped = ContextPlanner.WrapWorkspaceInstructions("use pnpm, never npm");

        Assert.Contains("<<<WORKSPACE INSTRUCTIONS BEGIN>>>", wrapped);
        Assert.Contains("<<<WORKSPACE INSTRUCTIONS END>>>", wrapped);
        Assert.Contains("use pnpm, never npm", wrapped);
        Assert.Contains("user instructions always take precedence", wrapped);
        Assert.Contains("override system rules", wrapped);
    }
}
