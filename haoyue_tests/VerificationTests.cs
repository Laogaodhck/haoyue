using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Verification;
using Haoyue.Runtime.Workspaces;
using Xunit.Abstractions;

namespace Haoyue.Tests;

/// <summary>
/// Multi-step verification chain: config resolution priority, fail-fast execution
/// across the chain, and the failure-output summary that feeds the repair prompt.
/// </summary>
public class VerificationTests(ITestOutputHelper output)
{
    private static WorkspaceInfo Workspace(string root, Action<WorkspaceConfig>? setup = null)
    {
        Directory.CreateDirectory(root);
        var config = new WorkspaceConfig();
        setup?.Invoke(config);
        return new WorkspaceInfo { Root = root, ProjectKinds = [], Config = config };
    }

    [Fact]
    public void ResolveCommands_ConfiguredChain_WinsOverEverything()
    {
        var ws = Workspace(Path.Combine(Path.GetTempPath(), "haoyue-vt-" + Guid.NewGuid().ToString("N")[..8]), cfg =>
        {
            cfg.VerifyCommand = "single-command";
            cfg.VerifyCommands = ["step-one", "step-two"];
        });
        var commands = new BuildVerifier().ResolveCommands(ws);
        Assert.Equal(["step-one", "step-two"], commands);
    }

    [Fact]
    public void ResolveCommands_SingleCommand_FallsBackToChainOfOne()
    {
        var ws = Workspace(Path.Combine(Path.GetTempPath(), "haoyue-vt-" + Guid.NewGuid().ToString("N")[..8]), cfg =>
        {
            cfg.VerifyCommand = "dotnet build --nologo";
        });
        var commands = new BuildVerifier().ResolveCommands(ws);
        var command = Assert.Single(commands);
        Assert.Equal("dotnet build --nologo", command);
    }

    [Fact]
    public void ResolveCommands_DotnetProject_UsesBuiltinBuild()
    {
        var root = Path.Combine(Path.GetTempPath(), "haoyue-vt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var ws = new WorkspaceInfo { Root = root, ProjectKinds = ["dotnet"], Config = new WorkspaceConfig() };
        var commands = new BuildVerifier().ResolveCommands(ws);
        var command = Assert.Single(commands);
        Assert.StartsWith("dotnet build", command);
    }

    [Fact]
    public async Task VerifyAsync_AllStepsSucceed_AggregatesOkSummary()
    {
        var ws = Workspace(Path.Combine(Path.GetTempPath(), "haoyue-vt-" + Guid.NewGuid().ToString("N")[..8]), cfg =>
        {
            cfg.VerifyCommands = ["echo verify-step-one", "echo verify-step-two"];
        });

        var result = await new BuildVerifier().VerifyAsync(ws, CancellationToken.None);

        Assert.True(result.Success, result.Output);
        Assert.Contains("verify-step-one", result.Output);
        Assert.Contains("verify-step-two", result.Output);
        Assert.Contains("ok", result.Output);
        output.WriteLine(result.Output);
    }

    [Fact]
    public async Task VerifyAsync_FailingStep_StopsChainAndReportsFailingCommand()
    {
        var ws = Workspace(Path.Combine(Path.GetTempPath(), "haoyue-vt-" + Guid.NewGuid().ToString("N")[..8]), cfg =>
        {
            // "exit 3" works in bash, cmd and pwsh alike; the second step must not run.
            cfg.VerifyCommands = ["echo verify-step-one", "exit 3", "echo verify-step-three"];
        });

        var result = await new BuildVerifier().VerifyAsync(ws, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("exit 3", result.Command); // repair prompt gets the failing command
        Assert.Contains("verify-step-one", result.Output); // earlier step ran and is summarized
        Assert.DoesNotContain("verify-step-three", result.Output); // fail-fast stopped the chain
        Assert.Contains("failed", result.Output);
        output.WriteLine(result.Output);
    }

    [Fact]
    public void SummarizeFailure_LongOutput_PreservesErrorLinesAndStaysBounded()
    {
        var noise = string.Join("\n", Enumerable.Range(1, 400).Select(i => $"compiling module {i}... ok"));
        var text = noise + "\nProgram.cs(12,34): error CS1002: ; expected\n" + noise;

        var summary = BuildVerifier.SummarizeFailure(text);

        Assert.Contains("error CS1002", summary); // the actual error block survived
        Assert.Contains("[output truncated", summary);
        Assert.True(summary.Length < text.Length / 4, $"summary {summary.Length} chars vs input {text.Length}");
        Assert.True(summary.Length <= 4200, "summary stays bounded near the 4000-char target");
    }

    [Fact]
    public void SummarizeFailure_ShortOutput_PassesThroughUnchanged()
    {
        var text = "build failed: exit 1";
        Assert.Equal(text, BuildVerifier.SummarizeFailure(text));
    }
}
