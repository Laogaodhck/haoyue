using Haoyue.Runtime.Configuration;
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
    public void SkillsContribution_InjectionFollowsUserMessageVariable()
    {
        WriteSkill("deploy", "name: deploy\ntriggers:\n  - 部署\n", prompt: "部署内容");
        WriteSkill("always", "name: always\n", prompt: "常驻内容");
        var manager = NewManager(out var registry);
        manager.Attach(Workspace);

        var contribution = registry.All.Single(c => c.Id == "skills");

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
    }
}
