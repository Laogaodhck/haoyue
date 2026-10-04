using Microsoft.Extensions.DependencyInjection;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Extensions;
using Haoyue.Runtime.Mcp;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Projects;
using Haoyue.Runtime.Scheduling;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Skills;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;
using Haoyue.Runtime.Verification;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime;

/// <summary>
/// Composition root and facade of the Haoyue runtime. Every front end
/// (CLI today; GUI / Web / daemon clients tomorrow) drives the runtime through this type.
/// </summary>
public sealed class HaoyueRuntime : IAsyncDisposable, IDisposable
{
    private readonly ServiceProvider _services;

    public WorkspaceInfo Workspace { get; private set; }

    public IConfigStore ConfigStore => _services.GetRequiredService<IConfigStore>();
    public IEventBus Events => _services.GetRequiredService<IEventBus>();
    public IPromptProvider Prompts => _services.GetRequiredService<IPromptProvider>();
    public IPromptRegistry PromptRegistry => _services.GetRequiredService<IPromptRegistry>();
    public IModelRegistry Models => _services.GetRequiredService<IModelRegistry>();
    public IProviderManager Providers => _services.GetRequiredService<IProviderManager>();
    public IUsageTracker Usage => _services.GetRequiredService<IUsageTracker>();
    public IHealthChecker Health => _services.GetRequiredService<IHealthChecker>();
    public IToolRegistry Tools => _services.GetRequiredService<IToolRegistry>();
    public IWorkspaceManager Workspaces => _services.GetRequiredService<IWorkspaceManager>();
    public ISessionStore Sessions => _services.GetRequiredService<ISessionStore>();
    public IProjectStore Projects => _services.GetRequiredService<IProjectStore>();
    public IScheduleStore Schedules => _services.GetRequiredService<IScheduleStore>();
    public HaoyueDatabase Database => _services.GetRequiredService<HaoyueDatabase>();
    public KnowledgeStore Knowledge => _services.GetRequiredService<KnowledgeStore>();
    public SkillManager Skills => _services.GetRequiredService<SkillManager>();
    public ExtensionManager Extensions { get; } = ExtensionManager.CreateDefault();
    public IMcpManager Mcp => _services.GetRequiredService<IMcpManager>();
    public Agent Agent => _services.GetRequiredService<Agent>();

    private HaoyueRuntime(ServiceProvider services, WorkspaceInfo workspace)
    {
        _services = services;
        Workspace = workspace;
    }

    public static HaoyueRuntime Create(string? startDirectory = null)
        => CreateCore(startDirectory, null);

    /// <summary>
    /// Creates an isolated runtime for one concurrent agent turn. The daemon uses one
    /// instance per task so workspace prompts, skills, MCP registrations and event
    /// subscriptions cannot leak between tasks.
    /// </summary>
    internal static HaoyueRuntime CreateIsolated(
        WorkspaceInfo workspace,
        IFileLockCoordinator? coordinator = null,
        string? turnOwner = null,
        Action<IServiceCollection>? configureServices = null)
    {
        HaoyuePaths.EnsureCreated();

        var serviceCollection = new ServiceCollection()
            .AddHaoyueRuntime(coordinator, turnOwner);
        // Lets the daemon inject process-wide shared infrastructure (HttpClient
        // pool, circuit breaker) before per-turn state is added.
        configureServices?.Invoke(serviceCollection);
        var services = serviceCollection.BuildServiceProvider();
        var runtime = new HaoyueRuntime(services, workspace);
        runtime.Prompts.SetWorkspaceRoot(workspace.IsGlobal ? null : workspace.PromptsDir);
        runtime.Skills.Attach(workspace);
        runtime.RegisterBasePromptContributions();
        runtime.RegisterBuiltinTools();
        return runtime;
    }

    internal static HaoyueRuntime Create(
        string startDirectory,
        IConfigStore configStore,
        string? databaseFile = null,
        IHealthChecker? healthChecker = null)
        => CreateCore(startDirectory, services =>
        {
            services.AddSingleton(configStore);
            if (databaseFile is not null)
                services.AddSingleton(new HaoyueDatabase(databaseFile));
            if (healthChecker is not null)
                services.AddSingleton(healthChecker);
        });

    private static HaoyueRuntime CreateCore(
        string? startDirectory,
        Action<IServiceCollection>? configureServices)
    {
        HaoyuePaths.EnsureCreated();

        var serviceCollection = new ServiceCollection().AddHaoyueRuntime();
        configureServices?.Invoke(serviceCollection);
        var services = serviceCollection.BuildServiceProvider();
        var workspace = services.GetRequiredService<IWorkspaceManager>().Detect(startDirectory);

        var runtime = new HaoyueRuntime(services, workspace);
        runtime.Prompts.SetWorkspaceRoot(workspace.PromptsDir);
        runtime.Skills.Attach(workspace);
        runtime.RegisterBasePromptContributions();
        runtime.RegisterBuiltinTools();
        return runtime;
    }

    /// <summary>Re-detects the workspace (used after directory changes or init).</summary>
    public void RefreshWorkspace(string? startDirectory = null)
    {
        Workspace = Workspaces.Detect(startDirectory ?? Directory.GetCurrentDirectory());
        Prompts.SetWorkspaceRoot(Workspace.PromptsDir);
        Skills.Attach(Workspace);
    }

    public Task<IReadOnlyList<McpServerStatus>> ConnectMcpAsync(CancellationToken ct) =>
        Mcp.ConnectAllAsync(Workspace, ct);

    private void RegisterBasePromptContributions()
    {
        var registry = PromptRegistry;
        var prompts = Prompts;
        var configStore = ConfigStore;

        // Main system prompt — key configurable, overridable per workspace.
        registry.Register(new PromptContribution("system", PromptSlot.System, (ctx, _) =>
        {
            if (ctx.Variables.TryGetValue("scope", out var scope) && scope == "global")
                return ValueTask.FromResult(prompts.TryGet("system/global"));

            var defaultKey = ctx.WorkspaceConfig?.SystemPrompt ?? configStore.Config.Agent.SystemPrompt;
            return ValueTask.FromResult(prompts.TryGet(defaultKey));
        }));

        // Personality is a small, swappable voice layer. It never changes tool authority.
        registry.Register(new PromptContribution("personality", PromptSlot.System, (ctx, _) =>
        {
            var personality = ctx.Variables.TryGetValue("personality", out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : "pragmatic";
            return ValueTask.FromResult(prompts.TryGet($"personality/{personality}"));
        }));

        // Reply-language contract: the model detects the source language of each user
        // message and replies in the configured target language (auto follows the OS
        // UI language). Kept next to personality so both user-facing voice layers sit
        // after the workspace-overridable system prompt.
        registry.Register(new PromptContribution("output-language", PromptSlot.System, (ctx, _) =>
        {
            var language = OutputLanguage.Resolve(ctx.WorkspaceConfig, configStore.Config.Agent);
            return ValueTask.FromResult<string?>(OutputLanguage.BuildInstruction(language));
        }));

        // Model capabilities are part of the runtime contract, not an assumption the model
        // must infer from its name. Keep this after the user-configurable system prompt so a
        // workspace override cannot accidentally make a declared vision model deny image input.
        registry.Register(new PromptContribution("model-capabilities", PromptSlot.System, (ctx, _) =>
        {
            var vision = ctx.Variables.TryGetValue("vision", out var visionValue)
                && bool.TryParse(visionValue, out var supportsVision)
                && supportsVision;
            var imageOutput = ctx.Variables.TryGetValue("image", out var imageValue)
                && bool.TryParse(imageValue, out var supportsImageOutput)
                && supportsImageOutput;
            return ValueTask.FromResult<string?>(PromptVariables.BuildCapabilityInstruction(vision, imageOutput));
        }));

        // Collaboration mode supplies the behavioral contract for plan/readonly/auto runs.
        registry.Register(new PromptContribution("collaboration-mode", PromptSlot.System, (ctx, _) =>
        {
            var mode = ctx.Variables.TryGetValue("mode", out var modeValue) ? modeValue : "edit";
            var key = mode switch
            {
                "plan" => "system/planner",
                "readonly" => "system/readonly",
                "auto" => "system/auto",
                _ => null,
            };
            return ValueTask.FromResult(key is null ? null : prompts.TryGet(key));
        }));

        // Permission context is runtime state, not a model assumption. It tells the model
        // what authority it has before it can make a dangerous or disallowed tool call.
        registry.Register(new PromptContribution("permissions", PromptSlot.System, (ctx, _) =>
            ValueTask.FromResult(prompts.TryGet("system/permissions"))));

        // Developer prompts per detected project kind (dotnet, node, python, rust, unity, vue…).
        registry.Register(new PromptContribution("developer", PromptSlot.Developer, (ctx, _) =>
        {
            var parts = ctx.ProjectKinds
                .Select(kind => prompts.TryGet($"developer/{kind}"))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text!.Trim())
                .ToList();
            return ValueTask.FromResult<string?>(parts.Count == 0 ? null : string.Join("\n\n", parts));
        }));

        // Repository instructions (AGENTS.md) are injected as bounded, user-authored context.
        // The global RulesEnabled switch lets users turn rule-file injection off entirely.
        registry.Register(new PromptContribution("agents-md", PromptSlot.Workspace, (ctx, _) =>
        {
            if (!configStore.Config.Agent.RulesEnabled) return ValueTask.FromResult<string?>(null);
            if (!ctx.Variables.TryGetValue("agents_md", out var agentsMd) || string.IsNullOrWhiteSpace(agentsMd))
                return ValueTask.FromResult<string?>(null);
            return ValueTask.FromResult(prompts.TryGet("builtin/agents_md"));
        }));

        // Workspace memory (MEMORY.md), injected through the builtin/memory template.
        // Manual mode appends a read-only contract so the user stays the sole editor.
        registry.Register(new PromptContribution("memory", PromptSlot.Memory, (ctx, _) =>
        {
            if (!ctx.Variables.TryGetValue("memory", out var memory) || string.IsNullOrWhiteSpace(memory))
                return ValueTask.FromResult<string?>(null);
            var template = prompts.TryGet("builtin/memory") ?? memory;
            if (MemoryMode.Normalize(configStore.Config.Agent.MemoryMode) == MemoryMode.Manual)
                template += "\n\n当前记忆处于手动管理模式：请把 MEMORY.md 当作只读上下文，不要主动改写或删除其中内容；仅当用户明确要求记录某条信息时，才以追加方式补充简短条目。";
            return ValueTask.FromResult<string?>(template);
        }));

        // Knowledge base contract — the tools are self-describing, but the model also
        // needs the behavioral rules: when to consult and when to persist.
        registry.Register(new PromptContribution("knowledge", PromptSlot.Memory, (_, _) =>
            ValueTask.FromResult<string?>(prompts.TryGet("builtin/knowledge"))));
    }

    private void RegisterBuiltinTools()
    {
        var prompts = Prompts;
        foreach (var tool in new ITool[]
                 {
                     new PlanTool(prompts),
                     new ReadFileTool(prompts),
                     new WriteFileTool(prompts),
                     new EditFileTool(prompts),
                     new ListDirTool(prompts),
                     new GlobTool(prompts),
                     new GrepTool(prompts),
                     new BashTool(prompts),
                     new WebSearchTool(prompts),
                     new WebFetchTool(prompts),
                     new CaptureScreenTool(prompts),
                     new KnowledgeSearchTool(Knowledge, prompts),
                     new KnowledgeSaveTool(Knowledge, prompts),
                     new KnowledgeForgetTool(Knowledge, prompts),
                     new DelegateTool(prompts, _services.GetRequiredService<IAgentDelegator>()),
                 })
            Tools.Register(tool);

        Extensions.InitializeAll(this);
    }

    public async ValueTask DisposeAsync()
    {
        await Extensions.DisposeAsync().ConfigureAwait(false);
        await Mcp.DisposeAsync().ConfigureAwait(false);
        await _services.DisposeAsync().ConfigureAwait(false);
    }


    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}

public static class RuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddHaoyueRuntime(
        this IServiceCollection services,
        IFileLockCoordinator? coordinator = null,
        string? turnOwner = null)
    {
        // Event journal: key lifecycle/tool/provider events persist to SQLite so
        // clients can query and replay them after a restart. Streaming noise
        // stays memory-only.
        services.AddSingleton<IEventBus>(sp => new JournaledEventBus(
            new EventBus(), sp.GetRequiredService<HaoyueDatabase>()));
        services.AddSingleton<IConfigStore>(_ => new ConfigStore());
        services.AddSingleton<IPromptProvider>(_ => new FilePromptProvider());
        services.AddSingleton<IPromptRegistry, PromptRegistry>();
        services.AddSingleton<PromptComposer>();

        services.AddSingleton<ILlmHttpFactory, LlmHttpFactory>();
        services.AddSingleton<LocalModelCache>();
        services.AddSingleton<ILlmClient, OpenAiCompatibleClient>();
        services.AddSingleton<ILlmClient, AnthropicClient>();
        services.AddSingleton<ILlmClient, LocalLlmClient>();
        services.AddSingleton<ILlmClientFactory, LlmClientFactory>();
        services.AddSingleton<IModelRegistry, ModelRegistry>();
        services.AddSingleton<IUsageTracker>(sp => new UsageTracker(sp.GetRequiredService<IEventBus>()));
        services.AddSingleton<IHealthChecker, HealthChecker>();
        // Resolvable breaker so every runtime has one; the daemon overrides this
        // registration with a process-wide instance shared across turn runtimes.
        services.AddSingleton<CircuitBreaker>(sp =>
            new CircuitBreaker(sp.GetRequiredService<IConfigStore>().Config.Routing.Retry));
        services.AddSingleton<IProviderManager, ProviderManager>();

        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<IWorkspaceManager, WorkspaceManager>();
        services.AddSingleton<HaoyueDatabase>();
        services.AddSingleton<KnowledgeStore>();
        services.AddSingleton<ISessionStore, SessionStore>();
        services.AddSingleton<IProjectStore, ProjectStore>();
        services.AddSingleton<IScheduleStore, ScheduleStore>();
        services.AddSingleton<IVerifier, BuildVerifier>();
        services.AddSingleton(sp => new SkillManager(
            sp.GetRequiredService<IConfigStore>(),
            sp.GetRequiredService<IPromptRegistry>()));
        services.AddSingleton<ISkillManager>(sp => sp.GetRequiredService<SkillManager>());
        services.AddSingleton<IMcpManager, McpManager>();
        // Central file write-lock coordinator shared by all concurrent turns in the
        // daemon; single-turn (CLI) runtimes fall back to the no-op implementation.
        services.AddSingleton<IFileLockCoordinator>(coordinator ?? new NoopFileLockCoordinator());
        services.AddSingleton(new FileLockScope(turnOwner ?? ""));
        services.AddSingleton<IAgentDelegator, AgentDelegator>();
        services.AddSingleton<Agent>();
        return services;
    }
}
