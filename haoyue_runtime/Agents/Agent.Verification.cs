using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Verification;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Agents;

public sealed partial class Agent
{
    // ---------------------------------------------------------------- verification

    private bool ShouldVerify(WorkspaceInfo workspace, AgentConfig agentConfig) =>
        !workspace.IsGlobal && (workspace.Config?.AutoVerify ?? agentConfig.AutoVerify);

    private async Task<string?> RunVerificationAsync(WorkspaceInfo workspace, int attempt, CancellationToken ct)
    {
        var commands = verifier.ResolveCommands(workspace);
        if (commands.Count == 0) return null;

        // A multi-step chain (build → test …) is displayed as one label; the verify result
        // reports which concrete step failed via result.Command.
        var chainLabel = string.Join(" → ", commands);
        events.Publish(new StatusEvent("Verifying", chainLabel));
        events.Publish(new VerificationStartedEvent(chainLabel, attempt));

        var result = await verifier.VerifyAsync(workspace, ct).ConfigureAwait(false);
        events.Publish(new VerificationCompletedEvent(result.Success, Firstline(result.Output), attempt));
        if (result.Success) return null;

        var template = promptProvider.TryGet("builtin/repair");
        if (template is null)
        {
            events.Publish(new WarningEvent("Repair prompt 'builtin/repair' is missing; verification failure cannot be repaired automatically."));
            return null;
        }
        return promptProvider.Render(template, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["command"] = result.Command,
            ["error"] = result.Output,
        });
    }
}
