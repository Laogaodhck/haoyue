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
        var command = verifier.ResolveCommand(workspace);
        if (command is null) return null;

        events.Publish(new StatusEvent("Verifying", command));
        events.Publish(new VerificationStartedEvent(command, attempt));

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
