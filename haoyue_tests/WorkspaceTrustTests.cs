using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Mcp;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Workspaces;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// 工作区信任门禁：仓库里的 .haoyue/config.json 与 mcp/servers.json 可以携带
/// 自动执行的命令，未受信工作区必须整体忽略它们，信任后才参与合并。
/// </summary>
public sealed class WorkspaceTrustTests : IDisposable
{
    private const string WorkspaceConfigJson = """
    {
      "mcp": {
        "servers": {
          "from-config": { "transport": "stdio", "command": "fake-config" }
        }
      }
    }
    """;

    private const string WorkspaceServersJson = """
    {
      "servers": {
        "from-file": { "transport": "stdio", "command": "fake-file" }
      }
    }
    """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-trust-tests", Guid.NewGuid().ToString("N"));

    public WorkspaceTrustTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private (string Project, WorkspaceManager Manager, WorkspaceTrustStore Trust) CreateUntrustedProject()
    {
        var project = Path.GetFullPath(Path.Combine(_dir, "project"));
        Directory.CreateDirectory(Path.Combine(project, ".haoyue", "mcp"));
        File.WriteAllText(Path.Combine(project, ".haoyue", "config.json"), WorkspaceConfigJson);
        File.WriteAllText(Path.Combine(project, ".haoyue", "mcp", "servers.json"), WorkspaceServersJson);

        var home = Path.Combine(_dir, "home");
        Directory.CreateDirectory(home);
        var trust = new WorkspaceTrustStore(Path.Combine(home, "trusted-workspaces.json"));
        return (project, new WorkspaceManager(home, trust), trust);
    }

    [Fact]
    public void Detect_WithoutTrust_HidesWorkspaceConfigButReportsIt()
    {
        var (project, manager, _) = CreateUntrustedProject();

        var workspace = manager.Detect(project);

        Assert.False(workspace.Trusted);
        Assert.Null(workspace.Config);
        Assert.True(workspace.HasWorkspaceConfig);
        Assert.True(workspace.HasWorkspaceMcp);
    }

    [Fact]
    public void Trust_EnablesWorkspaceConfig_AndUntrustDisablesItAgain()
    {
        var (project, manager, trust) = CreateUntrustedProject();

        trust.Trust(project);
        var trusted = manager.Detect(project);
        Assert.True(trusted.Trusted);
        Assert.NotNull(trusted.Config);
        Assert.True(trusted.Config!.Mcp?.Servers.ContainsKey("from-config"));

        trust.Untrust(project);
        Assert.False(manager.Detect(project).Trusted);
        Assert.Null(manager.Detect(project).Config);
    }

    [Fact]
    public void TrustStore_RejectsEscapingOrCorruptFileSilently()
    {
        var home = Path.Combine(_dir, "corrupt-home");
        Directory.CreateDirectory(home);
        var file = Path.Combine(home, "trusted-workspaces.json");
        File.WriteAllText(file, "{ not json");

        var trust = new WorkspaceTrustStore(file);
        Assert.Empty(trust.Trusted);
        Assert.False(trust.IsTrusted(Path.Combine(_dir, "project")));
    }

    [Fact]
    public void LoadServerConfigs_IgnoresWorkspaceDeclarationsUntilTrusted()
    {
        var (project, manager, trust) = CreateUntrustedProject();
        var configStore = new ConfigStore(
            Path.Combine(_dir, "global-config.json"),
            Path.Combine(_dir, "global-state.json"));
        configStore.Config.Mcp.Servers["global"] = new McpServerConfig
        {
            Transport = "stdio",
            Command = "fake-global",
        };
        var mcp = new McpManager(configStore, new ToolRegistry(), new PromptRegistry());

        var untrusted = manager.Detect(project);
        var before = mcp.LoadServerConfigs(untrusted);
        Assert.Contains("global", before.Keys);
        Assert.DoesNotContain("from-file", before.Keys);
        Assert.DoesNotContain("from-config", before.Keys);

        trust.Trust(project);
        var after = mcp.LoadServerConfigs(manager.Detect(project));
        Assert.Contains("global", after.Keys);
        Assert.Contains("from-file", after.Keys);
        Assert.Contains("from-config", after.Keys);
    }
}
