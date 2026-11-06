using Haoyue.Runtime.Connectors;

namespace Haoyue.Tests;

/// <summary>
/// GitHub CLI 探测：不依赖本机是否安装 gh（runner 注入），覆盖安装/登录/超时/脱敏与可读错误。
/// </summary>
public sealed class GhCliProbeTests
{
    [Fact]
    public void Describe_InstalledAndAuthenticated_ParsesVersionAccountAndScopes()
    {
        var status = GhCliProbe.Describe(args => args[0] == "--version"
            ? new GhCommandResult(0, "gh version 2.40.1 (2024-01-01)\nhttps://github.com/cli/cli/releases/tag/v2.40.1", "")
            : new GhCommandResult(0,
                "github.com\n  ✓ Logged in to github.com account octocat (keyring)\n" +
                "  - Active account: true\n  - Token scopes: 'gist', 'read:org', 'repo'\n", ""));

        Assert.True(status.Installed);
        Assert.Equal("2.40.1", status.Version);
        Assert.True(status.Authenticated);
        Assert.Equal("octocat", status.Account);
        Assert.Contains("github.com", status.Hosts);
        Assert.Contains("repo", status.Scopes);
        Assert.True(status.Available);
        Assert.Null(status.Error);
    }

    [Fact]
    public void Describe_NotLoggedIn_ReportsActionableGuide()
    {
        var status = GhCliProbe.Describe(args => args[0] == "--version"
            ? new GhCommandResult(0, "gh version 2.40.1", "")
            : new GhCommandResult(1, "",
                "You are not logged into any GitHub hosts. Run gh auth login to authenticate."));

        Assert.True(status.Installed);
        Assert.False(status.Authenticated);
        Assert.False(status.Available);
        Assert.Contains("gh auth login", status.Error);
    }

    [Fact]
    public void Describe_MissingGh_ReportsInstallGuide()
    {
        var status = GhCliProbe.Describe(_ => new GhCommandResult(-1, "", "not found", Missing: true));

        Assert.False(status.Installed);
        Assert.False(status.Available);
        Assert.Contains("未检测到 GitHub CLI", status.Error);
        Assert.Equal(GhCliProbe.InstallUrl, status.InstallUrl);
        Assert.Equal(GhCliProbe.LoginCommand, status.LoginCommand);
    }

    [Fact]
    public void Describe_Timeout_ReportsReadableError()
    {
        var status = GhCliProbe.Describe(_ => new GhCommandResult(-1, "", "timeout", TimedOut: true));

        Assert.False(status.Available);
        Assert.Contains("超过 5 秒", status.Error);
    }

    [Fact]
    public void Describe_RedactsTokenShapes()
    {
        var token = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        var status = GhCliProbe.Describe(args => args[0] == "--version"
            ? new GhCommandResult(0, "gh version 2.40.1", "")
            : new GhCommandResult(2, "", $"unexpected failure: {token}"));

        Assert.Contains("gh***", status.Error);
        Assert.DoesNotContain(token, status.Error);
    }
}
