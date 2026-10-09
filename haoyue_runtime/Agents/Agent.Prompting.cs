using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Experts;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Skills;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Agents;

public sealed partial class Agent
{
    // ---------------------------------------------------------------- prompt composition

    private async Task<string> ComposeSystemPromptAsync(
        WorkspaceInfo workspace, ModelInfo model, IReadOnlyList<ITool> tools, bool networkEnabled,
        string? skillTriggerContext = null, string? expertId = null, CancellationToken ct = default)
    {
        var memory = workspaceManager.LoadMemory(workspace);
        var agentsMd = workspaceManager.LoadAgentInstructions(workspace);
        var rawMode = workspace.Config?.Mode ?? configStore.Config.Agent.Mode;
        var mode = AgentModeExtensions.Parse(rawMode);
        var modeName = mode switch
        {
            AgentMode.Plan => "plan",
            AgentMode.ReadOnly => "readonly",
            AgentMode.Auto => "auto",
            _ => "edit",
        };
        var personality = workspace.Config?.Personality ?? configStore.Config.Agent.Personality;
        if (string.IsNullOrWhiteSpace(personality)) personality = "pragmatic";
        var autoVerify = ShouldVerify(workspace, configStore.Config.Agent);
        var variables = PromptVariables.Build(
            workspace,
            model,
            tools.Select(t => t.Name).ToList(),
            memory is null ? "" : ContextPlanner.FitInjectedText(memory),
            modeName,
            networkEnabled,
            autoVerify,
            personality);
        if (!string.IsNullOrWhiteSpace(agentsMd))
            // Fit first (budget covers the payload), then wrap: the provenance envelope
            // pins AGENTS.md below direct user/system instructions even if the file
            // itself contains text that claims higher authority.
            variables["agents_md"] = ContextPlanner.WrapWorkspaceInstructions(
                ContextPlanner.FitInjectedText(agentsMd));
        // Skill triggers evaluate against a stickiness window (current input + recent
        // user turns): follow-ups like "继续" keep previously triggered skills injected
        // instead of silently dropping them mid-task. The value is constant within a
        // turn so the composed-prompt cache stays consistent.
        if (!string.IsNullOrWhiteSpace(skillTriggerContext))
            variables["user_message"] = skillTriggerContext;
        var context = new PromptRenderContext
        {
            Variables = variables,
            WorkspaceRoot = workspace.Root,
            ProjectKinds = workspace.ProjectKinds,
            WorkspaceConfig = workspace.Config,
        };
        var basePrompt = await promptComposer.ComposeAsync(context, ct).ConfigureAwait(false);
        var expert = ExpertCatalog.Find(expertId);
        if (expert is not null)
        {
            basePrompt += $"""

## 专家模式

你现在以「{expert.Avatar} {expert.Name}（{expert.Id}）」的专家身份与用户协作，该身份优先于默认人格。
以下是此专家的角色设定与工作准则：

{expert.Prompt.Trim()}
""";
        }
        return basePrompt;
    }

    private IReadOnlyList<ITool> ActiveTools(WorkspaceInfo workspace, ModelInfo model, bool networkEnabled, string? skillTriggerContext = null)
    {
        var rawMode = workspace.Config?.Mode ?? configStore.Config.Agent.Mode;
        var mode = AgentModeExtensions.Parse(rawMode);

        var disabled = workspace.Config?.DisabledTools;
        var available = disabled is not { Count: > 0 }
            ? toolRegistry.All
            : toolRegistry.All.Where(t => !disabled.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList();

        // Manifest-v2 skills can constrain the toolset: the union of allowed-tools
        // over the skills injected this turn (triggers honored, stickiness window
        // included) becomes a hard allow-list. No declaring skill → no restriction,
        // unchanged behavior.
        var skillPolicy = _skills?.ResolveToolPolicy(workspace, skillTriggerContext);
        if (skillPolicy?.AllowedTools is { Count: > 0 } allowList)
            available = available.Where(t => allowList.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList();

        // The per-session "联网" toggle controls every network tool together
        // (web_search + web_fetch); when off the model never sees them.
        if (!networkEnabled)
            available = available.Where(tool => !tool.RequiresNetwork).ToList();

        if (!model.Model.Capabilities.Vision)
            available = available.Where(tool => !tool.RequiresVision).ToList();

        if (mode.IsReadOnly())
        {
            available = available.Where(t => !t.Mutating).ToList();
        }

        // The complete tool schema is part of the provider's cached prompt prefix. MCP
        // discovery order is not a semantic concern, so canonicalize it for cache stability.
        return available.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    // ---------------------------------------------------------------- skill trigger context

    /// <summary>How many recent user turns (including the current input) feed skill trigger matching.</summary>
    internal const int SkillTriggerWindowTurns = 3;

    /// <summary>Upper bound for the combined trigger context so it cannot bloat the turn.</summary>
    internal const int MaxSkillTriggerContextLength = 4000;

    /// <summary>
    /// Skill triggers must survive multi-turn tasks: matching only against the current
    /// message would drop a skill the moment the user writes a follow-up without its
    /// keywords ("继续", "再加上…"). The context is therefore the current input plus the
    /// previous <see cref="SkillTriggerWindowTurns"/> minus 1 user turns, newest first
    /// and head-truncated so the current input survives the budget.
    /// </summary>
    internal static string? BuildSkillTriggerContext(
        IReadOnlyList<ChatMessage> messages, int turnMessageIndex, string? userInput)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(userInput))
            parts.Add(userInput);

        for (var i = turnMessageIndex - 1; i >= 0 && parts.Count < SkillTriggerWindowTurns; i--)
        {
            var message = messages[i];
            if (message.Role == ChatRole.User && !string.IsNullOrWhiteSpace(message.Text))
                parts.Add(message.Text);
        }

        if (parts.Count == 0) return null;
        var context = string.Join("\n", parts);
        return context.Length <= MaxSkillTriggerContextLength
            ? context
            : context[..MaxSkillTriggerContextLength];
    }
}
