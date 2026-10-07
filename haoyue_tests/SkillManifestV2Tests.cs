using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Skills;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>
/// Manifest-v2 skill features: kebab-case allowed-tools/parameters/triggers parsing,
/// trigger-gated injection, the derived tool allow-list and the parameter appendix.
/// </summary>
public sealed class SkillManifestV2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-skills-v2-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private string WorkspaceSkillsDir
    {
        get
        {
            var path = Path.Combine(_dir, "ws", "skills");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    private string GlobalSkillsDir => Path.Combine(_dir, "global-skills");

    private WorkspaceInfo Workspace => new()
    {
        Root = Path.Combine(_dir, "ws"),
        ProjectKinds = [],
        Config = new WorkspaceConfig(),
    };

    private SkillManager NewManager(out PromptRegistry registry)
    {
        registry = new PromptRegistry();
        var configStore = new ConfigStore(
            Path.Combine(_dir, "config.json"),
            Path.Combine(_dir, "state.json"));
        var prompts = new FilePromptProvider([]);
        return new SkillManager(configStore, registry, GlobalSkillsDir, prompts);
    }

    private void WriteSkill(string name, string yaml, string prompt = "技能内容")
    {
        var dir = Path.Combine(WorkspaceSkillsDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "skill.yaml"), yaml);
        File.WriteAllText(Path.Combine(dir, "prompt.txt"), prompt);
    }

    [Fact]
    public void Manifest_ParsesKebabCaseV2Fields()
    {
        WriteSkill("deploy", """
            name: deploy
            description: 部署技能
            version: "2"
            allowed-tools:
              - bash
              - read_file
            triggers:
              - 部署
              - 上线
            parameters:
              - name: env
                description: 目标环境
                required: true
              - name: region
                required: false
                default: cn-north
            """);

        var manager = NewManager(out _);
        var skill = manager.Discover(Workspace).Single();

        Assert.Equal(["bash", "read_file"], skill.Manifest.AllowedTools);
        Assert.Equal(["部署", "上线"], skill.Manifest.Triggers);
        var parameters = skill.Manifest.Parameters!;
        Assert.Equal(2, parameters.Count);
        Assert.Equal("env", parameters[0].Name);
        Assert.True(parameters[0].Required);
        Assert.Equal("cn-north", parameters[1].Default);
        Assert.False(parameters[1].Required);
    }

    [Fact]
    public void Triggered_KeywordGate_IsCaseInsensitiveAndAlwaysOnWithoutTriggers()
    {
        var withTriggers = new SkillManifest { Name = "a", Triggers = ["部署", "Deploy"] };
        var withoutTriggers = new SkillManifest { Name = "b" };

        Assert.True(SkillManager.Triggered(withTriggers, "帮我部署一下"));
        Assert.True(SkillManager.Triggered(withTriggers, "please DEPLOY it"));
        Assert.False(SkillManager.Triggered(withTriggers, "无关消息"));
        Assert.False(SkillManager.Triggered(withTriggers, null));
        // No declared triggers → always-on, regardless of the message.
        Assert.True(SkillManager.Triggered(withoutTriggers, "任何消息"));
        Assert.True(SkillManager.Triggered(withoutTriggers, null));
    }

    [Fact]
    public void SelectInjectedSkills_GatesTriggeredSkillsOnly()
    {
        WriteSkill("deploy", "name: deploy\ntriggers:\n  - 部署\n");
        WriteSkill("always", "name: always\n");
        var manager = NewManager(out _);
        manager.Attach(Workspace);

        Assert.Equal(["always"], manager.SelectInjectedSkills(Workspace, "无关消息").Select(s => s.Name));
        var hit = manager.SelectInjectedSkills(Workspace, "请部署服务");
        Assert.Equal(["always", "deploy"], hit.Select(s => s.Name).OrderBy(n => n));
        // Without a user message, triggered skills stay out.
        Assert.Equal(["always"], manager.SelectInjectedSkills(Workspace, null).Select(s => s.Name));
    }

    [Fact]
    public void ResolveToolPolicy_UnionsTriggeredDeclarations_AndStaysNullOtherwise()
    {
        WriteSkill("deploy", "name: deploy\nallowed-tools:\n  - bash\n  - read_file\n");
        WriteSkill("release", "name: release\nallowed-tools:\n  - bash\n  - web_search\n");
        WriteSkill("plain", "name: plain\n");
        var manager = NewManager(out _);
        manager.Attach(Workspace);

        var union = manager.ResolveToolPolicy(Workspace, "任何消息");
        Assert.NotNull(union.AllowedTools);
        Assert.Equal(["bash", "read_file", "web_search"], union.AllowedTools!.OrderBy(n => n, StringComparer.Ordinal));

        // No declaring skill at all → unrestricted.
        Directory.Delete(Path.Combine(WorkspaceSkillsDir, "deploy"), recursive: true);
        Directory.Delete(Path.Combine(WorkspaceSkillsDir, "release"), recursive: true);
        Assert.Null(manager.ResolveToolPolicy(Workspace, "任何消息").AllowedTools);
    }

    [Fact]
    public void ResolveToolPolicy_IgnoresSkillsNotTriggered()
    {
        WriteSkill("deploy", "name: deploy\nallowed-tools:\n  - bash\ntriggers:\n  - 部署\n");
        var manager = NewManager(out _);
        manager.Attach(Workspace);

        // The skill exists but its trigger does not hit → its boundary must not apply.
        Assert.Null(manager.ResolveToolPolicy(Workspace, "写个脚本").AllowedTools);
        Assert.Equal(["bash"], manager.ResolveToolPolicy(Workspace, "部署吧").AllowedTools);
    }

    [Fact]
    public void RenderSkillPrompt_AppendsParameterCollectAppendix()
    {
        WriteSkill("deploy", "name: deploy\nparameters:\n  - name: env\n    required: true\n    description: 目标环境\n  - name: region\n    default: cn-north\n",
            prompt: "部署到 {{env}}");
        var manager = NewManager(out _);
        var skill = manager.Discover(Workspace).Single();

        var rendered = manager.RenderSkillPrompt(skill);

        Assert.Contains("部署到 {{env}}", rendered); // body kept, placeholder intact
        Assert.Contains("{{env}}（必填） — 目标环境", rendered);
        Assert.Contains("{{region}}（可选（默认 cn-north））", rendered);
    }

    [Fact]
    public void Triggered_AsciiKeywordsMatchOnWordBoundaries()
    {
        var manifest = new SkillManifest { Name = "git", Triggers = ["git"] };

        // Substring hits like "digit" or "gitignore" must not trigger (误调用防护).
        Assert.False(SkillManager.Triggered(manifest, "讨论 digit 的问题"));
        Assert.False(SkillManager.Triggered(manifest, "看看 gitignore 的写法"));
        // Real word hits still trigger, case-insensitively.
        Assert.True(SkillManager.Triggered(manifest, "提交到 git"));
        Assert.True(SkillManager.Triggered(manifest, "run GIT command"));

        // Non-ASCII keywords keep substring semantics (CJK has no word boundaries).
        var chinese = new SkillManifest { Name = "deploy", Triggers = ["部署"] };
        Assert.True(SkillManager.Triggered(chinese, "帮我部署一下"));
        Assert.True(SkillManager.Triggered(chinese, "先完成部署脚本"));
    }

    [Fact]
    public void BuildSkillTriggerContext_CombinesCurrentInputWithRecentUserTurns()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.User("帮我做一个网站"),
            ChatMessage.Assistant("好的，我先搭好骨架。"),
            ChatMessage.User("用 Vue3 技术栈"),
            ChatMessage.Assistant("已配置完成。"),
        };

        // Current input is first; previous user turns follow (newest first), assistant
        // and tool messages are skipped.
        var context = Agent.BuildSkillTriggerContext(history, history.Count, "继续");
        Assert.NotNull(context);
        Assert.Equal("继续\n用 Vue3 技术栈\n帮我做一个网站", context);

        // Sticky window caps at SkillTriggerWindowTurns messages.
        var capped = Agent.BuildSkillTriggerContext(history, history.Count, "继续")!;
        Assert.Equal(3, capped.Split('\n').Length);

        // Empty current input still yields a context from history (stickiness).
        Assert.NotNull(Agent.BuildSkillTriggerContext(history, history.Count, ""));

        // Overlong contexts keep the head so the current input survives truncation.
        var longHistory = new List<ChatMessage>
        {
            ChatMessage.User(new string('旧', Agent.MaxSkillTriggerContextLength)),
            ChatMessage.User("触发词网站"),
        };
        var truncated = Agent.BuildSkillTriggerContext(longHistory, longHistory.Count, "当前输入")!;
        Assert.Equal(Agent.MaxSkillTriggerContextLength, truncated.Length);
        Assert.StartsWith("当前输入", truncated);
    }

    [Fact]
    public void SelectInjectedSkills_StaysInjectedAcrossFollowUpTurns()
    {
        WriteSkill("website", "name: website\ndescription: 建站技能\ntriggers:\n  - 网站\n", prompt: "建站内容");
        var manager = NewManager(out _);
        manager.Attach(Workspace);

        // Turn 1: keyword hit.
        var turn1 = Agent.BuildSkillTriggerContext([], 0, "帮我做一个网站")!;
        Assert.Contains(manager.SelectInjectedSkills(Workspace, turn1), s => s.Name == "website");

        // Turn 2: the follow-up alone has no keyword, but the sticky window keeps the
        // skill injected — same for the derived tool policy.
        var turn2 = Agent.BuildSkillTriggerContext(
            [ChatMessage.User("帮我做一个网站"), ChatMessage.Assistant("已搭好骨架")], 2, "继续加一个登录页")!;
        Assert.Contains(manager.SelectInjectedSkills(Workspace, turn2), s => s.Name == "website");

        // After the window slides past the trigger turn, the skill drops out again.
        var later = Agent.BuildSkillTriggerContext(
        [
            ChatMessage.User("帮我做一个网站"),
            ChatMessage.Assistant("好"),
            ChatMessage.User("随便聊聊天气"),
            ChatMessage.Assistant("好"),
            ChatMessage.User("今天天气如何"),
            ChatMessage.Assistant("好"),
            ChatMessage.User("明天呢"),
        ], 7, "顺便看看新闻")!;
        Assert.DoesNotContain(manager.SelectInjectedSkills(Workspace, later), s => s.Name == "website");
    }

    [Fact]
    public void RenderSkillPrompt_PrependsApplicabilityHeader()
    {
        WriteSkill("deploy",
            "name: deploy\ndescription: 部署服务到目标环境\ntriggers:\n  - 部署\n  - 上线\n",
            prompt: "部署内容");
        var manager = NewManager(out _);
        var skill = manager.Discover(Workspace).Single();

        var rendered = manager.RenderSkillPrompt(skill);

        Assert.Contains("适用场景：部署服务到目标环境", rendered);
        Assert.Contains("触发条件：用户消息命中 部署 / 上线", rendered);
        Assert.Contains("部署内容", rendered);
    }

    [Fact]
    public void SkillsContribution_InjectionFollowsUserMessageVariable()
    {
        WriteSkill("deploy", "name: deploy\ntriggers:\n  - 部署\n", prompt: "部署内容");
        WriteSkill("always", "name: always\n", prompt: "常驻内容");
        var manager = NewManager(out var registry);
        manager.Attach(Workspace);

        var contribution = registry.All.Single(c => c.Id == "skill-bodies");
        var catalog = registry.All.Single(c => c.Id == "skill-catalog");

        var hit = contribution.Resolver(new PromptRenderContext
        {
            Variables = new Dictionary<string, string> { ["user_message"] = "请部署" },
            WorkspaceRoot = Workspace.Root,
        }, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Assert.Contains("部署内容", hit);
        Assert.Contains("常驻内容", hit);

        var miss = contribution.Resolver(new PromptRenderContext
        {
            Variables = new Dictionary<string, string> { ["user_message"] = "无关" },
            WorkspaceRoot = Workspace.Root,
        }, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Assert.Contains("常驻内容", miss);
        Assert.DoesNotContain("部署内容", miss);

        // The catalog is a separate, low-degrade-rank contribution so discovery survives
        // budget enforcement that drops skill bodies. It lists trigger-gated always-listed
        // skills only; the "always" skill has no triggers so it is injected unconditionally.
        var catalogText = catalog.Resolver(new PromptRenderContext
        {
            Variables = new Dictionary<string, string>(),
            WorkspaceRoot = Workspace.Root,
        }, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Assert.Contains("deploy", catalogText);
        Assert.DoesNotContain("常驻内容", catalogText);
    }

    [Fact]
    public void Catalog_ListsTriggerGatedSkills_AndRespectsAlwaysListedFlag()
    {
        WriteSkill("deploy", """
            name: deploy
            description: 部署技能
            triggers:
              - 部署
            """);
        WriteSkill("secret", """
            name: secret
            description: 内部技能
            always-listed: false
            triggers:
              - 内部
            """);
        WriteSkill("always-on", """
            name: always-on
            description: 常驻技能
            """, prompt: "常驻内容");

        var manager = NewManager(out _);
        var catalog = manager.RenderSkillCatalog(Workspace);

        Assert.NotNull(catalog);
        Assert.Contains("deploy", catalog);
        Assert.Contains("部署技能", catalog);
        // always-listed: false 与常驻技能不出现在目录中。
        Assert.DoesNotContain("secret", catalog);
        Assert.DoesNotContain("always-on", catalog);
        Assert.Contains("declare_skill", catalog);
    }

    [Fact]
    public void DeclareForTurn_InjectsListedSkill_AndRejectsUnknownOrHidden()
    {
        WriteSkill("deploy", """
            name: deploy
            description: 部署技能
            triggers:
              - 部署
            """, prompt: "部署内容");
        WriteSkill("secret", """
            name: secret
            description: 内部技能
            always-listed: false
            triggers:
              - 内部
            """, prompt: "内部内容");

        var manager = NewManager(out var registry);
        manager.Attach(Workspace);

        // 关键词未命中时 deploy 不注入。
        var contribution = registry.All.Single(c => c.Id == "skill-bodies");
        string? Resolve(string userMessage) => contribution.Resolver(new PromptRenderContext
        {
            Variables = new Dictionary<string, string> { ["user_message"] = userMessage },
            WorkspaceRoot = Workspace.Root,
        }, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Assert.DoesNotContain("部署内容", Resolve("帮我看看这个报错"));

        // 模型声明后注入（下一 step 生效），并进入工具权威。
        Assert.True(manager.DeclareForTurn(Workspace, "deploy"));
        Assert.Contains("部署内容", Resolve("帮我看看这个报错"));

        // 未知/隐藏/已禁用技能拒绝声明。
        Assert.False(manager.DeclareForTurn(Workspace, "no-such-skill"));
        Assert.False(manager.DeclareForTurn(Workspace, "secret"));
        Assert.False(manager.DeclareForTurn(Workspace, ""));

        // 回合边界：重置后声明集合清空。
        manager.ResetTurnDeclarations();
        Assert.DoesNotContain("部署内容", Resolve("帮我看看这个报错"));
    }
}
