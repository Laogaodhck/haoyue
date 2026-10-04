using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Skills;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Verification;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Agents;

/// <summary>
/// Runs a self-contained subtask in a fresh sub-agent. Implementations own the
/// isolation policy (clean context, tool restrictions, depth limits).
/// </summary>
public interface IAgentDelegator
{
    Task<AgentTurnResult> RunSubTaskAsync(WorkspaceInfo workspace, string task, CancellationToken ct);
}

/// <summary>
/// Tool-registry view that hides the delegation tool, so a delegated sub-agent
/// cannot spawn further sub-agents — delegation is exactly one level deep.
/// Registrations are forwarded so MCP re-connects stay visible to sub-agents.
/// </summary>
public sealed class FilteredToolRegistry(IToolRegistry inner, params string[] excludedNames) : IToolRegistry
{
    private readonly HashSet<string> _excluded = excludedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IDisposable Register(ITool tool) => inner.Register(tool);

    public ITool? Resolve(string name) => _excluded.Contains(name) ? null : inner.Resolve(name);

    public IReadOnlyList<ITool> All => inner.All.Where(tool => !_excluded.Contains(tool.Name)).ToList();
}

/// <summary>
/// Default delegator: creates a real sub-agent that shares every runtime
/// dependency except the tool view, and a dedicated persisted session so the
/// sub-task stays auditable in the session list. The sub-agent runs serially
/// inside the calling tool execution — there is no parallel delegation.
/// </summary>
public sealed class AgentDelegator(
    IConfigStore configStore,
    IProviderManager providerManager,
    IToolRegistry toolRegistry,
    IPromptProvider promptProvider,
    PromptComposer promptComposer,
    IWorkspaceManager workspaceManager,
    ISessionStore sessionStore,
    IVerifier verifier,
    IEventBus events,
    IFileLockCoordinator fileLocks,
    FileLockScope lockScope,
    ISkillManager? skills = null) : IAgentDelegator
{
    public const string DelegateToolName = "delegate_task";

    public async Task<AgentTurnResult> RunSubTaskAsync(WorkspaceInfo workspace, string task, CancellationToken ct)
    {
        // Depth limit: hide delegate_task from the sub-agent's tool view.
        var subTools = new FilteredToolRegistry(toolRegistry, DelegateToolName);
        var subAgent = new Agent(
            configStore, providerManager, subTools, promptProvider, promptComposer,
            workspaceManager, sessionStore, verifier, events, fileLocks, lockScope, skills);
        var session = sessionStore.Create(workspace);
        return await subAgent.RunTurnAsync(session, workspace, task, ct).ConfigureAwait(false);
    }
}
