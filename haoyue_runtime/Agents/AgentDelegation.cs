using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Skills;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;
using Haoyue.Runtime.Verification;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Agents;

/// <summary>
/// Runs self-contained subtasks in fresh sub-agents. Implementations own the
/// isolation policy (clean context, tool restrictions, depth limits).
/// </summary>
public interface IAgentDelegator
{
    Task<AgentTurnResult> RunSubTaskAsync(WorkspaceInfo workspace, string task, CancellationToken ct);

    /// <summary>
    /// S7 角色化委派：role 命中 AgentRole.Builtins 时注入角色纪律提示词并收敛
    /// 工具白名单；null 或未实现时退化为普通委派（默认接口成员保证既有实现兼容）。
    /// </summary>
    async Task<AgentTurnResult> RunSubTaskAsync(WorkspaceInfo workspace, string task, string? role, CancellationToken ct)
    {
        var effectiveTask = role is { Length: > 0 } ? AgentRole.Resolve(role).ComposeTask(task) : task;
        return await RunSubTaskAsync(workspace, effectiveTask, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs several subtasks and returns their results in input order. The default
    /// implementation is a serial fallback; real delegators may parallelize.
    /// </summary>
    async Task<IReadOnlyList<AgentTurnResult>> RunSubTasksAsync(WorkspaceInfo workspace, IReadOnlyList<string> tasks, CancellationToken ct)
    {
        var results = new List<AgentTurnResult>();
        foreach (var task in tasks)
            results.Add(await RunSubTaskAsync(workspace, task, ct).ConfigureAwait(false));
        return results;
    }

    /// <summary>S7：角色化批量委派。默认实现只注入角色提示词（不收敛工具），
    /// AgentDelegator 显式实现时会同时应用角色工具白名单并保持并行。</summary>
    async Task<IReadOnlyList<AgentTurnResult>> RunSubTasksAsync(
        WorkspaceInfo workspace, IReadOnlyList<string> tasks, string? role, CancellationToken ct)
    {
        if (role is not { Length: > 0 })
            return await RunSubTasksAsync(workspace, tasks, ct).ConfigureAwait(false);
        var composed = tasks.Select(AgentRole.Resolve(role).ComposeTask).ToList();
        return await RunSubTasksAsync(workspace, composed, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Tool-registry view that hides the given tool names. Unlike per-agent overlays
/// (<see cref="OverlayToolRegistry"/>) this forwards registrations so MCP re-connects
/// stay visible to sub-agents.
/// </summary>
public sealed class FilteredToolRegistry(IToolRegistry inner, params string[] excludedNames) : IToolRegistry
{
    private readonly HashSet<string> _excluded = excludedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IDisposable Register(ITool tool) => inner.Register(tool);

    public ITool? Resolve(string name) => _excluded.Contains(name) ? null : inner.Resolve(name);

    public IReadOnlyList<ITool> All => inner.All.Where(tool => !_excluded.Contains(tool.Name)).ToList();
}

/// <summary>
/// Layered view for one sub-agent: the shared registry minus the delegation tools,
/// plus this sub-agent's own depth-aware delegation tools. Overlay tools are NOT
/// registered into the shared registry (that would collide across sub-agents);
/// forwarded registrations still reach the inner registry so MCP re-connects work.
/// </summary>
public sealed class OverlayToolRegistry(IToolRegistry inner, ITool[] overlay, params string[] excludedNames) : IToolRegistry
{
    private readonly HashSet<string> _excluded = excludedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IDisposable Register(ITool tool) => inner.Register(tool);

    public ITool? Resolve(string name)
    {
        // Overlay wins over everything (it carries the sub-agent's own delegation tools);
        // the exclusion list only hides the shared-registry originals beneath it.
        var overlaid = overlay.FirstOrDefault(tool => tool.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (overlaid is not null) return overlaid;
        return _excluded.Contains(name) ? null : inner.Resolve(name);
    }

    public IReadOnlyList<ITool> All =>
        overlay.Concat(inner.All.Where(tool => !_excluded.Contains(tool.Name)
            && !overlay.Any(o => o.Name.Equals(tool.Name, StringComparison.OrdinalIgnoreCase)))).ToList();
}

/// <summary>
/// S7 角色工具白名单视图：只放行 allowedTools 中的工具（大小写不敏感），
/// 其余一律视为不存在。白名单为空/null 时等价于透传。
/// </summary>
public sealed class AllowListToolRegistry(IToolRegistry inner, IReadOnlyList<string> allowedTools) : IToolRegistry
{
    private readonly HashSet<string> _allowed = allowedTools.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IDisposable Register(ITool tool) => inner.Register(tool);

    public ITool? Resolve(string name) => _allowed.Contains(name) ? inner.Resolve(name) : null;

    public IReadOnlyList<ITool> All => inner.All.Where(tool => _allowed.Contains(tool.Name)).ToList();
}

/// <summary>
/// Default delegator. Sub-agents share every runtime dependency except the tool view,
/// and each gets a dedicated persisted session so sub-tasks stay auditable. Depth is
/// governed by <c>agent.delegationMaxDepth</c> (default 1 = the current one-level
/// behavior); a batch (<see cref="RunSubTasksAsync"/>) fans sub-agents out in parallel,
/// and parallel children never delegate further regardless of depth.
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
    public const string ParallelDelegateToolName = "delegate_tasks";

    public async Task<AgentTurnResult> RunSubTaskAsync(WorkspaceInfo workspace, string task, CancellationToken ct) =>
        await RunSubTaskAsync(workspace, task, ct, depth: 0, parallel: false, allowedTools: null).ConfigureAwait(false);

    /// <summary>S7：角色化委派——组合角色任务文本并按预设收敛子代理工具视图。</summary>
    async Task<AgentTurnResult> IAgentDelegator.RunSubTaskAsync(
        WorkspaceInfo workspace, string task, string? role, CancellationToken ct)
    {
        var preset = role is { Length: > 0 } ? AgentRole.Resolve(role) : null;
        var effectiveTask = preset?.ComposeTask(task) ?? task;
        return await RunSubTaskAsync(workspace, effectiveTask, ct, depth: 0, parallel: false, allowedTools: preset?.AllowedTools)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AgentTurnResult>> RunSubTasksAsync(
        WorkspaceInfo workspace, IReadOnlyList<string> tasks, CancellationToken ct)
    {
        var runs = tasks.Select(task => RunSubTaskAsync(workspace, task, ct, depth: 0, parallel: true, allowedTools: null));
        return await Task.WhenAll(runs).ConfigureAwait(false);
    }

    /// <summary>S7：角色化批量委派——全部子任务注入同一角色并应用其工具白名单，保持并行。</summary>
    async Task<IReadOnlyList<AgentTurnResult>> IAgentDelegator.RunSubTasksAsync(
        WorkspaceInfo workspace, IReadOnlyList<string> tasks, string? role, CancellationToken ct)
    {
        var preset = role is { Length: > 0 } ? AgentRole.Resolve(role) : null;
        var composed = preset is null ? tasks : tasks.Select(preset.ComposeTask).ToList();
        var runs = composed.Select(task => RunSubTaskAsync(workspace, task, ct, depth: 0, parallel: true, allowedTools: preset?.AllowedTools));
        return await Task.WhenAll(runs).ConfigureAwait(false);
    }

    private async Task<AgentTurnResult> RunSubTaskAsync(
        WorkspaceInfo workspace, string task, CancellationToken ct, int depth, bool parallel,
        IReadOnlyList<string>? allowedTools)
    {
        var subTools = BuildSubToolView(depth, parallel, allowedTools);
        var subAgent = new Agent(
            configStore, providerManager, subTools, promptProvider, promptComposer,
            workspaceManager, sessionStore, verifier, events, fileLocks, lockScope, skills);
        var session = sessionStore.Create(workspace);
        return await subAgent.RunTurnAsync(session, workspace, task, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds one sub-agent's tool view: the shared registry minus delegation tools,
    /// plus depth-aware delegation tools when the child may still delegate. A role
    /// preset (S7) additionally narrows the view to its allowed-tools list.
    /// </summary>
    private IToolRegistry BuildSubToolView(int depth, bool parallel, IReadOnlyList<string>? allowedTools)
    {
        var childDepth = depth + 1;
        var mayDelegate = !parallel && childDepth < ResolveMaxDepth();

        IToolRegistry view = mayDelegate
            ? new OverlayToolRegistry(
                toolRegistry,
                [
                    new DelegateTool(promptProvider, childDelegatorFor(childDepth)),
                    new DelegateTasksTool(promptProvider, childDelegatorFor(childDepth)),
                ],
                DelegateToolName, ParallelDelegateToolName)
            : new FilteredToolRegistry(toolRegistry, DelegateToolName, ParallelDelegateToolName);

        return allowedTools is { Count: > 0 }
            ? new AllowListToolRegistry(view, allowedTools)
            : view;

        ChildDelegator childDelegatorFor(int d) => new(this, d);
    }

    private int ResolveMaxDepth() =>
        Math.Clamp(configStore.Config.Agent.DelegationMaxDepth, 1, 4);

    /// <summary>Binds a sub-agent's delegation tools to its own depth level.</summary>
    private sealed class ChildDelegator(AgentDelegator owner, int depth) : IAgentDelegator
    {
        public Task<AgentTurnResult> RunSubTaskAsync(WorkspaceInfo workspace, string task, CancellationToken ct) =>
            owner.RunSubTaskAsync(workspace, task, ct, depth, parallel: false, allowedTools: null);

        public async Task<IReadOnlyList<AgentTurnResult>> RunSubTasksAsync(
            WorkspaceInfo workspace, IReadOnlyList<string> tasks, CancellationToken ct)
        {
            // A child may fan out too, but its children never delegate (parallel rule).
            var results = await Task.WhenAll(
                tasks.Select(task => owner.RunSubTaskAsync(workspace, task, ct, depth, parallel: true, allowedTools: null)))
                .ConfigureAwait(false);
            return results;
        }
    }
}
