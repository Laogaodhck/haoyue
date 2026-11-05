using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Haoyue.Runtime;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Daemon;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Workspaces;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// 5.6 安全配置面：agent.config.get/set 扩展 toolPolicy 与 bashSandbox 的读写——
/// 桌面设置 UI（工具执行策略页）走这两个宽松 RPC，安全配置零手改 config.json。
/// </summary>
public sealed class ToolPolicySettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-tpsettings-" + Guid.NewGuid().ToString("N"));

    public ToolPolicySettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private (DaemonAdminApi Admin, ConfigStore Store) CreateAdmin(WorkspaceInfo? workspace = null)
    {
        var store = new ConfigStore(
            Path.Combine(_dir, "config.json"), Path.Combine(_dir, "state.json"));
        store.Config.Providers.Add(new ProviderConfig
        {
            Id = "p1", Kind = "openai", BaseUrl = "https://test.local/v1",
            Models = [new ModelConfig { Id = "m1", ContextWindow = 8_000, MaxOutput = 256 }],
        });
        workspace ??= new WorkspaceManager().CreateGlobal(Path.Combine(_dir, "global"));
        var runtime = HaoyueRuntime.CreateIsolated(workspace, configureServices: services =>
        {
            services.AddSingleton<IConfigStore>(store);
            services.AddSingleton(new HaoyueDatabase(Path.Combine(_dir, "state.db")));
            services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
            services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
        });
        var admin = new DaemonAdminApi(runtime, workspace, new FileLockCoordinator());
        return (admin, store);
    }

    [Fact]
    public void GetAgentConfig_ExposesToolPolicyAndSandbox()
    {
        var (admin, _) = CreateAdmin();
        var view = JsonNode.Parse(admin.GetAgentConfig())!.AsObject();

        var policy = view["toolPolicy"]!.AsObject();
        Assert.True(policy["enabled"]!.GetValue<bool>());
        Assert.True(policy["riskGating"]!.GetValue<bool>());
        Assert.Equal(["computer_exec"], policy["requireConsentFor"]!.AsArray().Select(n => n!.GetValue<string>()));
        // 5.2 默认同意维度：system + cost（写/网络默认不拦）。
        Assert.Equal(["system", "cost"], policy["consentRiskTiers"]!.AsArray().Select(n => n!.GetValue<string>()));

        var sandbox = view["bashSandbox"]!.AsObject();
        Assert.False(sandbox["enabled"]!.GetValue<bool>()); // 沙箱默认关闭
        Assert.Equal(2048, sandbox["memoryLimitMb"]!.GetValue<int>());
        Assert.True(sandbox["uiRestrictions"]!.GetValue<bool>());
    }

    [Fact]
    public void SetAgentConfig_PartialUpdatesKeepAbsentKeys()
    {
        var (admin, store) = CreateAdmin();

        // 只改 riskGating：其余工具策略键保持存储值。
        var updated = JsonNode.Parse(admin.SetAgentConfig(new JsonObject
        {
            ["toolPolicy"] = new JsonObject { ["riskGating"] = false },
        }))!.AsObject();
        Assert.False(updated["toolPolicy"]!["riskGating"]!.GetValue<bool>());
        Assert.True(updated["toolPolicy"]!["builtinBashGuard"]!.GetValue<bool>());
        Assert.False(store.Config.Agent.ToolPolicy.RiskGating);
        Assert.True(store.Config.Agent.ToolPolicy.Enabled);
    }

    [Fact]
    public void SetAgentConfig_ReplacesAllowAndDenyRulesAsWholeGroups()
    {
        var (admin, store) = CreateAdmin();

        var updated = JsonNode.Parse(admin.SetAgentConfig(new JsonObject
        {
            ["toolPolicy"] = new JsonObject
            {
                ["allow"] = new JsonArray(new JsonObject
                {
                    ["tool"] = "bash",
                    ["pattern"] = "git\\s+(status|diff)",
                    ["reason"] = "只读 git 查询免审",
                }),
                ["deny"] = new JsonArray(new JsonObject
                {
                    ["tool"] = "*",
                    ["pattern"] = "rm\\s+-rf\\s+/ ",
                    ["reason"] = "禁止递归强删根目录",
                }),
            },
        }))!.AsObject();

        var policy = store.Config.Agent.ToolPolicy;
        var allow = Assert.Single(policy.Allow);
        Assert.Equal("bash", allow.Tool);
        Assert.Equal("git\\s+(status|diff)", allow.Pattern);
        Assert.Equal("只读 git 查询免审", allow.Reason);
        var deny = Assert.Single(policy.Deny);
        Assert.Equal("禁止递归强删根目录", deny.Reason);
        // 回显一致（UI 据此刷新表格）。
        Assert.Single(updated["toolPolicy"]!["allow"]!.AsArray());
    }

    [Fact]
    public void SetAgentConfig_ClampsSandboxNumbers()
    {
        var (admin, store) = CreateAdmin();
        var updated = JsonNode.Parse(admin.SetAgentConfig(new JsonObject
        {
            ["bashSandbox"] = new JsonObject
            {
                ["enabled"] = true,
                ["memoryLimitMb"] = 999_999,   // 越界钳制而非拒绝
                ["maxProcesses"] = -5,
            },
        }))!.AsObject();

        Assert.True(store.Config.Agent.BashSandbox.Enabled);
        Assert.Equal(65536, store.Config.Agent.BashSandbox.MemoryLimitMb);
        Assert.Equal(0, store.Config.Agent.BashSandbox.MaxProcesses);
        Assert.Equal(65536, updated["bashSandbox"]!["memoryLimitMb"]!.GetValue<int>());
    }

    [Fact]
    public void SetAgentConfig_RuleWithoutToolField_IsRejected()
    {
        var (admin, store) = CreateAdmin();
        var ex = Assert.Throws<DaemonRequestException>(() => admin.SetAgentConfig(new JsonObject
        {
            ["toolPolicy"] = new JsonObject
            {
                ["allow"] = new JsonArray(new JsonObject { ["pattern"] = "x" }),
            },
        }));
        Assert.Contains("没有 tool 字段", ex.Message);
        Assert.Empty(store.Config.Agent.ToolPolicy.Allow); // 拒绝即不落盘
    }

    [Fact]
    public void SaveWorkspaceConfig_AcceptsToolAllowListAndFallbackSwitch()
    {
        var (admin, _) = CreateAdmin();
        var project = Path.Combine(_dir, "project-ok");
        Directory.CreateDirectory(project);

        var saved = JsonNode.Parse(admin.SaveWorkspaceConfig(
            new JsonObject { ["workspace"] = project },
            new JsonObject
            {
                ["allowedTools"] = new JsonArray("read_file", "github/*", "*/create_issue"),
                ["disabledTools"] = new JsonArray("bash", "mcp__dangerous__*"),
                ["allowToolFallbackToGlobal"] = true,
            }))!.AsObject();
        Assert.Equal("workspace", saved["scope"]!.GetValue<string>());
        // 项目不是当前引擎驻留的工作区：只落盘，不能宣称已同步到内存。
        Assert.False(saved["applied"]!.GetValue<bool>());
        Assert.Equal("other-workspace", saved["appliedReason"]!.GetValue<string>());

        var file = Path.Combine(project, ".haoyue", "config.json");
        var document = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.Equal(
            ["read_file", "github/*", "*/create_issue"],
            document["allowedTools"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal(
            ["bash", "mcp__dangerous__*"],
            document["disabledTools"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.True(document["allowToolFallbackToGlobal"]!.GetValue<bool>());
    }

    [Fact]
    public void SaveWorkspaceConfig_ReportsAppliedOnlyForLiveWorkspace()
    {
        var liveRoot = Path.Combine(_dir, "live");
        Directory.CreateDirectory(liveRoot);
        var liveConfig = new WorkspaceConfig();
        var live = new WorkspaceInfo
        {
            Root = liveRoot,
            ProjectKinds = [],
            Config = liveConfig,
            Trusted = true,
        };
        var (admin, _) = CreateAdmin(live);

        var applied = JsonNode.Parse(admin.SaveWorkspaceConfig(
            new JsonObject { ["workspace"] = liveRoot },
            new JsonObject
            {
                ["allowedTools"] = new JsonArray("read_file"),
                ["disabledTools"] = new JsonArray("bash"),
            }))!.AsObject();
        Assert.True(applied["applied"]!.GetValue<bool>());
        Assert.Equal("applied", applied["appliedReason"]!.GetValue<string>());
        Assert.Equal(["read_file"], liveConfig.AllowedTools);
        Assert.Equal(["bash"], liveConfig.DisabledTools);

        var otherRoot = Path.Combine(_dir, "other");
        Directory.CreateDirectory(otherRoot);
        var notApplied = JsonNode.Parse(admin.SaveWorkspaceConfig(
            new JsonObject { ["workspace"] = otherRoot },
            new JsonObject { ["disabledTools"] = new JsonArray("bash") }))!.AsObject();
        Assert.False(notApplied["applied"]!.GetValue<bool>());
        Assert.Equal("other-workspace", notApplied["appliedReason"]!.GetValue<string>());
        Assert.Equal(["bash"], liveConfig.DisabledTools); // 内存未被另一个工作区的写入影响

        // 同一工作区但未受信：内存中没有 Config，写盘成功但不能宣称已生效。
        var untrustedRoot = Path.Combine(_dir, "untrusted");
        Directory.CreateDirectory(untrustedRoot);
        var untrusted = new WorkspaceInfo
        {
            Root = untrustedRoot,
            ProjectKinds = [],
            Config = null,
            Trusted = false,
        };
        var (untrustedAdmin, _) = CreateAdmin(untrusted);
        var untrustedSaved = JsonNode.Parse(untrustedAdmin.SaveWorkspaceConfig(
            new JsonObject { ["workspace"] = untrustedRoot },
            new JsonObject { ["disabledTools"] = new JsonArray("bash") }))!.AsObject();
        Assert.False(untrustedSaved["applied"]!.GetValue<bool>());
        Assert.Equal("untrusted", untrustedSaved["appliedReason"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(untrustedRoot, ".haoyue", "config.json")));
    }

    [Fact]
    public void SaveWorkspaceConfig_RejectsInvalidToolPatternsWithoutWriting()
    {
        var (admin, _) = CreateAdmin();
        var project = Path.Combine(_dir, "project-bad");
        Directory.CreateDirectory(project);
        var file = Path.Combine(project, ".haoyue", "config.json");

        var ex = Assert.Throws<DaemonRequestException>(() => admin.SaveWorkspaceConfig(
            new JsonObject { ["workspace"] = project },
            new JsonObject { ["allowedTools"] = new JsonArray("read_file", "bad pattern") }));
        Assert.Contains("allowedTools", ex.Message);
        Assert.Contains("不允许字符", ex.Message);
        Assert.False(File.Exists(file)); // 拒绝即不落盘

        var wildcard = Assert.Throws<DaemonRequestException>(() => admin.SaveWorkspaceConfig(
            new JsonObject { ["workspace"] = project },
            new JsonObject { ["allowedTools"] = new JsonArray("github/**") }));
        Assert.Contains("`**`", wildcard.Message);
        Assert.False(File.Exists(file));

        var shape = Assert.Throws<DaemonRequestException>(() => admin.SaveWorkspaceConfig(
            new JsonObject { ["workspace"] = project },
            new JsonObject { ["allowedTools"] = "read_file" }));
        Assert.Contains("字符串数组", shape.Message);

        // 硬禁用与白名单同一套校验：非法模式同样拒绝落盘。
        var disabled = Assert.Throws<DaemonRequestException>(() => admin.SaveWorkspaceConfig(
            new JsonObject { ["workspace"] = project },
            new JsonObject { ["disabledTools"] = new JsonArray("bash", "bad pattern") }));
        Assert.Contains("disabledTools", disabled.Message);
        Assert.Contains("不允许字符", disabled.Message);
        Assert.False(File.Exists(file));

        var disabledShape = Assert.Throws<DaemonRequestException>(() => admin.SaveWorkspaceConfig(
            new JsonObject { ["workspace"] = project },
            new JsonObject { ["disabledTools"] = "bash" }));
        Assert.Contains("disabledTools 必须是字符串数组", disabledShape.Message);
    }
}
