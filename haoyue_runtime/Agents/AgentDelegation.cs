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
/// 一次委派的覆盖项：既有角色预置、模型与工具白名单。团体成员用它在共享
/// 委派链路的前提下表达自己的角色定位与权限范围，而不是另起一套子代理构造。
/// </summary>
public sealed record AgentInvocation(
    string? RolePreset = null,
    string? Model = null,
    IReadOnlyList<string>? AllowedTools = null);

/// <summary>
/// Runs self-contained subtasks in fresh sub-agents. Implementations own the
/// isolation policy (clean context, tool restrictions, depth limits).
/// </summary>
public interface IAgentDelegator
{
    Task<AgentTurnResult> RunSubTaskAsync(WorkspaceInfo workspace, string task, CancellationToken ct);

    /// <summary>
    /// 当前工作区可用的子智能体（内置 + 用户级 + 项目级 Markdown 定义）。
    /// 默认实现只返回内置预设，既有实现无需改动；真实 delegator 会带上自定义条目。
    /// </summary>
    IReadOnlyList<AgentRole> Roles(WorkspaceInfo? workspace = null) =>
        AgentRole.Builtins.Values.ToList();

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

    /// <summary>
    /// 带父回合执行台账的委派：子代理共享 <paramref name="parentScope"/>，其文件改动落进
    /// 父回合的补偿栈，<c>agent.undo</c> 一次撤销整回合（含子任务）。默认实现退化回独立子回合
    /// （台账不共享），既有实现（测试桩、进化实验室）无需改动。
    /// </summary>
    Task<AgentTurnResult> RunSubTaskAsync(
        WorkspaceInfo workspace, string task, string? role, TurnExecutionScope? parentScope, CancellationToken ct) =>
        RunSubTaskAsync(workspace, task, role, ct);

    /// <summary>批量委派版的台账共享，语义同 <see cref="RunSubTaskAsync(WorkspaceInfo,string,string?,TurnExecutionScope?,CancellationToken)"/>。</summary>
    Task<IReadOnlyList<AgentTurnResult>> RunSubTasksAsync(
        WorkspaceInfo workspace, IReadOnlyList<string> tasks, string? role, TurnExecutionScope? parentScope, CancellationToken ct) =>
        RunSubTasksAsync(workspace, tasks, role, ct);

    /// <summary>
    /// 带覆盖项的委派（团体成员）：模型与工具白名单可按成员覆盖，仍共享父回合台账。
    /// 默认实现忽略覆盖项、退化为按角色预置委派，既有实现与测试桩无需改动。
    /// </summary>
    Task<AgentTurnResult> RunSubTaskAsync(
        WorkspaceInfo workspace, string task, AgentInvocation invocation,
        TurnExecutionScope? parentScope, CancellationToken ct) =>
        RunSubTaskAsync(workspace, task, invocation.RolePreset, parentScope, ct);
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

    public string? DenialReason(string name)
    {
        if (!_excluded.Contains(name)) return inner.DenialReason(name);
        return IsKnown(inner, name) ? "子代理隔离：该委派工具不在本层视图中" : null;
    }

    /// <summary>更内层是否真的存在这个名字（或已被更内层说明拒绝）。</summary>
    internal static bool IsKnown(IToolRegistry registry, string name) =>
        registry.DenialReason(name) is not null
        || registry.All.Any(tool => tool.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
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

    public string? DenialReason(string name)
    {
        if (overlay.Any(tool => tool.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return null;
        if (!_excluded.Contains(name)) return inner.DenialReason(name);
        return FilteredToolRegistry.IsKnown(inner, name)
            ? "子代理隔离：该委派工具不在本层视图中"
            : null;
    }
}

/// <summary>
/// S7 角色工具白名单视图：只放行 allowedTools 中的工具（大小写不敏感，支持
/// <c>server/tool</c> 通配符），其余一律视为不存在。白名单为空/null 时等价于透传。
/// </summary>
public sealed class AllowListToolRegistry(
    IToolRegistry inner, IReadOnlyList<string> allowedTools, string scope = "角色/成员") : IToolRegistry
{
    public IDisposable Register(ITool tool) => inner.Register(tool);

    public ITool? Resolve(string name) =>
        ToolPermissionPolicy.IsAllowed(allowedTools, name) ? inner.Resolve(name) : null;

    public IReadOnlyList<ITool> All =>
        inner.All.Where(tool => ToolPermissionPolicy.IsAllowed(allowedTools, tool.Name)).ToList();

    public string? DenialReason(string name)
    {
        if (ToolPermissionPolicy.IsAllowed(allowedTools, name)) return inner.DenialReason(name);
        return FilteredToolRegistry.IsKnown(inner, name)
            ? $"不在{scope}的工具白名单内"
            : null;
    }
}

/// <summary>
/// 工作区/成员策略视图：先按硬禁用（deny-overrides），再按白名单裁剪。两层都支持
/// <c>server/tool</c> 通配符；逐层包裹即得交集，不存在放大路径。
/// </summary>
public sealed class ToolPermissionRegistry(
    IToolRegistry inner,
    IReadOnlyList<string>? allowedTools,
    IReadOnlyList<string>? disabledTools,
    string scope) : IToolRegistry
{
    public IDisposable Register(ITool tool) => inner.Register(tool);

    public ITool? Resolve(string name)
    {
        if (ToolPermissionPolicy.IsDenied(disabledTools, name)) return null;
        if (!ToolPermissionPolicy.IsAllowed(allowedTools, name)) return null;
        return inner.Resolve(name);
    }

    public IReadOnlyList<ITool> All => inner.All
        .Where(tool => !ToolPermissionPolicy.IsDenied(disabledTools, tool.Name)
            && ToolPermissionPolicy.IsAllowed(allowedTools, tool.Name))
        .ToList();

    public string? DenialReason(string name)
    {
        if (ToolPermissionPolicy.IsDenied(disabledTools, name))
            return FilteredToolRegistry.IsKnown(inner, name) ? $"被{scope}硬禁用" : null;
        if (!ToolPermissionPolicy.IsAllowed(allowedTools, name))
            return FilteredToolRegistry.IsKnown(inner, name) ? $"不在{scope}的工具白名单内" : null;
        return inner.DenialReason(name);
    }
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
    ISkillManager? skills = null,
    TurnUndoRegistry? undoRegistry = null,
    IToolExecutionPolicy? toolPolicy = null,
    ApprovalRegistry? approvals = null,
    SubAgentCatalog? subAgents = null) : IAgentDelegator
{
    public const string DelegateToolName = "delegate_task";
    public const string ParallelDelegateToolName = "delegate_tasks";

    private readonly ApprovalRegistry? _approvals = approvals;
    private readonly SubAgentCatalog _subAgents = subAgents ?? new SubAgentCatalog();

    public IReadOnlyList<AgentRole> Roles(WorkspaceInfo? workspace = null) =>
        _subAgents.Discover(ProjectRoot(workspace)).Select(entry => entry.Role).ToList();

    public async Task<AgentTurnResult> RunSubTaskAsync(WorkspaceInfo workspace, string task, CancellationToken ct) =>
        await RunSubTaskAsync(workspace, task, ct, depth: 0, parallel: false, allowedTools: null,
            parentScope: null, modelOverride: null).ConfigureAwait(false);

    /// <summary>S7：角色化委派——组合角色任务文本并按预设收敛子代理工具视图。</summary>
    async Task<AgentTurnResult> IAgentDelegator.RunSubTaskAsync(
        WorkspaceInfo workspace, string task, string? role, CancellationToken ct)
    {
        var preset = ResolveRole(workspace, role);
        var effectiveTask = preset?.ComposeTask(task) ?? task;
        return await RunSubTaskAsync(workspace, effectiveTask, ct, depth: 0, parallel: false,
            allowedTools: preset?.AllowedTools, parentScope: null, modelOverride: preset?.Model)
            .ConfigureAwait(false);
    }

    /// <summary>委派时共享父回合台账：子代理写的文件因此可在父回合一次撤销。</summary>
    async Task<AgentTurnResult> IAgentDelegator.RunSubTaskAsync(
        WorkspaceInfo workspace, string task, string? role, TurnExecutionScope? parentScope, CancellationToken ct)
    {
        var preset = ResolveRole(workspace, role);
        var effectiveTask = preset?.ComposeTask(task) ?? task;
        return await RunSubTaskAsync(workspace, effectiveTask, ct, depth: 0, parallel: false,
            allowedTools: preset?.AllowedTools, parentScope: parentScope, modelOverride: preset?.Model)
            .ConfigureAwait(false);
    }

    /// <summary>团体成员的委派入口：角色预置 + 成员级模型/工具收窄，仍共享父台账。</summary>
    async Task<AgentTurnResult> IAgentDelegator.RunSubTaskAsync(
        WorkspaceInfo workspace, string task, AgentInvocation invocation,
        TurnExecutionScope? parentScope, CancellationToken ct)
    {
        var preset = ResolveRole(workspace, invocation.RolePreset);
        var effectiveTask = preset?.ComposeTask(task) ?? task;
        // 角色与成员清单按顺序叠加（交集），成员永远不能放宽角色边界。
        var roleTools = preset?.AllowedTools;
        var memberTools = invocation.AllowedTools is { Count: > 0 } ? invocation.AllowedTools : null;
        var modelOverride = string.IsNullOrWhiteSpace(invocation.Model) ? preset?.Model : invocation.Model;
        return await RunSubTaskAsync(workspace, effectiveTask, ct, depth: 0, parallel: false,
            allowedTools: roleTools, parentScope: parentScope, modelOverride: modelOverride,
            invocationTools: memberTools)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AgentTurnResult>> RunSubTasksAsync(
        WorkspaceInfo workspace, IReadOnlyList<string> tasks, CancellationToken ct)
    {
        var runs = tasks.Select(task => RunSubTaskAsync(workspace, task, ct, depth: 0, parallel: true,
            allowedTools: null, parentScope: null, modelOverride: null));
        return await Task.WhenAll(runs).ConfigureAwait(false);
    }

    /// <summary>S7：角色化批量委派——全部子任务注入同一角色并应用其工具白名单，保持并行。</summary>
    async Task<IReadOnlyList<AgentTurnResult>> IAgentDelegator.RunSubTasksAsync(
        WorkspaceInfo workspace, IReadOnlyList<string> tasks, string? role, CancellationToken ct)
    {
        var preset = ResolveRole(workspace, role);
        var composed = preset is null ? tasks : tasks.Select(preset.ComposeTask).ToList();
        var runs = composed.Select(task => RunSubTaskAsync(workspace, task, ct, depth: 0, parallel: true,
            allowedTools: preset?.AllowedTools, parentScope: null, modelOverride: preset?.Model));
        return await Task.WhenAll(runs).ConfigureAwait(false);
    }

    /// <summary>并行子任务同样共享父台账：补偿栈本身有锁，多个子代理的改动按发生顺序入栈。</summary>
    async Task<IReadOnlyList<AgentTurnResult>> IAgentDelegator.RunSubTasksAsync(
        WorkspaceInfo workspace, IReadOnlyList<string> tasks, string? role, TurnExecutionScope? parentScope, CancellationToken ct)
    {
        var preset = ResolveRole(workspace, role);
        var composed = preset is null ? tasks : tasks.Select(preset.ComposeTask).ToList();
        var runs = composed.Select(task => RunSubTaskAsync(workspace, task, ct, depth: 0, parallel: true,
            allowedTools: preset?.AllowedTools, parentScope: parentScope, modelOverride: preset?.Model));
        return await Task.WhenAll(runs).ConfigureAwait(false);
    }

    private async Task<AgentTurnResult> RunSubTaskAsync(
        WorkspaceInfo workspace, string task, CancellationToken ct, int depth, bool parallel,
        IReadOnlyList<string>? allowedTools, TurnExecutionScope? parentScope, string? modelOverride,
        IReadOnlyList<string>? invocationTools = null)
    {
        var subTools = BuildSubToolView(workspace, depth, parallel, allowedTools, invocationTools);
        var subAgent = new Agent(
            configStore, providerManager, subTools, promptProvider, promptComposer,
            workspaceManager, sessionStore, verifier, events, fileLocks, lockScope, skills,
            undoRegistry, toolPolicy, approvals: _approvals, modelOverride: modelOverride);
        var session = sessionStore.Create(workspace);
        return await subAgent.RunTurnAsync(session, workspace, task, ct, inheritedScope: parentScope)
            .ConfigureAwait(false);
    }

    /// <summary>Resolves built-in or Markdown-defined sub-agents; unknown names fail loudly.</summary>
    private AgentRole? ResolveRole(WorkspaceInfo workspace, string? role)
    {
        if (role is not { Length: > 0 }) return null;
        return _subAgents.Resolve(ProjectRoot(workspace), role)
            ?? throw new ArgumentException(
                $"未知子智能体「{role}」。可选：{string.Join(" / ", _subAgents.Names(ProjectRoot(workspace)))}");
    }

    private static string? ProjectRoot(WorkspaceInfo? workspace) =>
        workspace is null || workspace.IsGlobal ? null : workspace.Root;

    /// <summary>
    /// Builds one sub-agent's tool view: the shared registry minus delegation tools,
    /// plus depth-aware delegation tools when the child may still delegate. The view is
    /// then narrowed layer by layer — workspace policy → role preset → member list —
    /// so every layer can only remove tools, never add them (deny wins throughout).
    /// </summary>
    private IToolRegistry BuildSubToolView(
        WorkspaceInfo workspace, int depth, bool parallel,
        IReadOnlyList<string>? roleTools, IReadOnlyList<string>? invocationTools)
    {
        var childDepth = depth + 1;
        var mayDelegate = !parallel && childDepth < ResolveMaxDepth();

        IToolRegistry baseView = mayDelegate
            ? new OverlayToolRegistry(
                toolRegistry,
                [
                    new DelegateTool(promptProvider, childDelegatorFor(childDepth)),
                    new DelegateTasksTool(promptProvider, childDelegatorFor(childDepth)),
                ],
                DelegateToolName, ParallelDelegateToolName)
            : new FilteredToolRegistry(toolRegistry, DelegateToolName, ParallelDelegateToolName);

        return ComposeToolView(baseView, workspace.Config, roleTools, invocationTools);

        ChildDelegator childDelegatorFor(int d) => new(this, d);
    }

    /// <summary>
    /// 权限层的唯一组装点：workspace（deny 优先于 allow）→ 角色预置 → 成员清单。
    /// 每层都是对上一层结果的再过滤，因此结果天然是交集，任何一层都无法放大权限。
    /// 抽成 internal 静态方法便于直接测试分层语义与降级开关。
    /// </summary>
    internal static IToolRegistry ComposeToolView(
        IToolRegistry baseView, WorkspaceConfig? config,
        IReadOnlyList<string>? roleTools, IReadOnlyList<string>? invocationTools)
    {
        var workspaceView = new ToolPermissionRegistry(
            baseView, config?.AllowedTools, config?.DisabledTools, "工作区");

        var roleApplied = roleTools is { Count: > 0 };
        var memberApplied = invocationTools is { Count: > 0 };
        IToolRegistry view = workspaceView;
        if (roleApplied) view = new AllowListToolRegistry(view, roleTools!, "角色预置");
        if (memberApplied) view = new AllowListToolRegistry(view, invocationTools!, "成员");

        // 默认 fail-closed：裁空就是裁空。显式开关才退回仅受工作区策略约束的清单，
        // 且绝不越过 workspace 的硬禁用/白名单。
        if ((roleApplied || memberApplied) && config?.AllowToolFallbackToGlobal == true && view.All.Count == 0)
            return workspaceView;

        return view;
    }

    private int ResolveMaxDepth() =>
        Math.Clamp(configStore.Config.Agent.DelegationMaxDepth, 1, 4);

    /// <summary>Binds a sub-agent's delegation tools to its own depth level.</summary>
    private sealed class ChildDelegator(AgentDelegator owner, int depth) : IAgentDelegator
    {
        public IReadOnlyList<AgentRole> Roles(WorkspaceInfo? workspace = null) => owner.Roles(workspace);

        public Task<AgentTurnResult> RunSubTaskAsync(WorkspaceInfo workspace, string task, CancellationToken ct) =>
            owner.RunSubTaskAsync(workspace, task, ct, depth, parallel: false, allowedTools: null,
                parentScope: null, modelOverride: null);

        public async Task<IReadOnlyList<AgentTurnResult>> RunSubTasksAsync(
            WorkspaceInfo workspace, IReadOnlyList<string> tasks, CancellationToken ct)
        {
            // A child may fan out too, but its children never delegate (parallel rule).
            var results = await Task.WhenAll(
                tasks.Select(task => owner.RunSubTaskAsync(workspace, task, ct, depth, parallel: true,
                    allowedTools: null, parentScope: null, modelOverride: null)))
                .ConfigureAwait(false);
            return results;
        }

        // 嵌套委派的台账转发：子代理拿到的 turnScope 就是父回合那个对象，
        // 所以这里必须把它继续交给孙子代理，否则第二层以下的改动仍然撤销不了。
        async Task<AgentTurnResult> IAgentDelegator.RunSubTaskAsync(
            WorkspaceInfo workspace, string task, string? role, TurnExecutionScope? parentScope, CancellationToken ct)
        {
            var preset = owner.ResolveRole(workspace, role);
            var effectiveTask = preset?.ComposeTask(task) ?? task;
            return await owner.RunSubTaskAsync(workspace, effectiveTask, ct, depth, parallel: false,
                allowedTools: preset?.AllowedTools, parentScope: parentScope, modelOverride: preset?.Model)
                .ConfigureAwait(false);
        }

        async Task<IReadOnlyList<AgentTurnResult>> IAgentDelegator.RunSubTasksAsync(
            WorkspaceInfo workspace, IReadOnlyList<string> tasks, string? role, TurnExecutionScope? parentScope, CancellationToken ct)
        {
            var preset = owner.ResolveRole(workspace, role);
            var composed = preset is null ? tasks : tasks.Select(preset.ComposeTask).ToList();
            var results = await Task.WhenAll(
                composed.Select(task => owner.RunSubTaskAsync(workspace, task, ct, depth, parallel: true,
                    allowedTools: preset?.AllowedTools, parentScope: parentScope, modelOverride: preset?.Model)))
                .ConfigureAwait(false);
            return results;
        }
    }
}
