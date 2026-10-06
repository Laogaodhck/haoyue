using Haoyue.Runtime.Providers;

namespace Haoyue.Runtime.Configuration;

/// <summary>Root of ~/.haoyue/config.json. All model/provider data is user data — never hard-coded.</summary>
public sealed class HaoyueConfig
{
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public double? Temperature { get; set; }

    public List<ProviderConfig> Providers { get; set; } = [];
    public RoutingConfig Routing { get; set; } = new();
    public AgentConfig Agent { get; set; } = new();
    public McpConfig Mcp { get; set; } = new();
    public Haoyue.Runtime.ComputerUse.ComputerUseConfig ComputerUse { get; set; } = new();


    public ProviderConfig? FindProvider(string id) =>
        Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}

public sealed class ProviderConfig
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    /// <summary>Wire protocol: openai | anthropic | local. Every OpenAI-compatible service (Ollama, LM Studio, OpenRouter, Azure…) uses "openai"; "local" runs GGUF models in-process without any server.</summary>
    public string Kind { get; set; } = "openai";
    public string BaseUrl { get; set; } = "";
    public string? ApiKey { get; set; }
    public string? Organization { get; set; }
    public string? Proxy { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    /// <summary>For kind "local": directory containing the GGUF files. Empty = auto-detected default (repository "models" folder or ~/.haoyue/models).</summary>
    public string? ModelsDirectory { get; set; }
    /// <summary>
    /// For kind "local": GPU layers to offload (llama.cpp n_gpu_layers). Null keeps CPU-only
    /// inference. Offloading requires a GPU backend (e.g. the LLamaSharp.Backend.Cuda12
    /// package replacing the Cpu backend); otherwise the value is silently ignored by
    /// llama.cpp and inference stays on CPU.
    /// </summary>
    public int? GpuLayers { get; set; }
    /// <summary>For kind "local": CPU inference thread count. Null lets llama.cpp pick (all logical cores).</summary>
    public int? Threads { get; set; }
    /// <summary>
    /// For kind "local": enable llama.cpp flash attention. Required for KV cache
    /// quantization below; without a GPU backend the speedup is limited but still valid.
    /// </summary>
    public bool FlashAttention { get; set; }
    /// <summary>
    /// For kind "local": KV cache quantization for llama.cpp (type_k/type_v).
    /// "none" keeps the native f16 cache; "q8_0" and "q4_0" shrink VRAM usage.
    /// Only applied when <see cref="FlashAttention"/> is enabled — llama.cpp rejects
    /// quantized KV caches with plain attention.
    /// </summary>
    public string? KvCacheQuantization { get; set; }
    /// <summary>
    /// For kind "local": reuse the decoded KV cache across requests when the new prompt
    /// extends the previously decoded one, so multi-step agent turns skip repeated prefill
    /// computation. A mismatched prefix falls back to a full re-prefill automatically.
    /// </summary>
    public bool LocalPrefixReuse { get; set; } = true;
    /// <summary>Optional URL used by the Desktop "fetch models" action; defaults to the provider /models endpoint.</summary>
    public string? ModelListUrl { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    /// <summary>Optional neutral-level to provider wire-value overrides.</summary>
    public Dictionary<string, string>? ReasoningEffortMap { get; set; }
    /// <summary>
    /// Enables provider-native prompt caching hints. OpenAI-compatible providers keep using
    /// their automatic prefix cache; Anthropic requests add explicit cache checkpoints.
    /// Disable this for an Anthropic-compatible endpoint that does not accept cache_control.
    /// </summary>
    public bool PromptCaching { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public List<ModelConfig> Models { get; set; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    /// <summary>True when this provider's models run in-process from GGUF files instead of over HTTP.</summary>
    public bool IsLocal => Kind.Equals("local", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the API key explicitly stored in the configuration file.</summary>
    public string? ResolveApiKey() => ApiKey;
}

public sealed class ModelConfig
{
    public string Id { get; set; } = "";
    public string? Alias { get; set; }
    public int ContextWindow { get; set; } = 128_000;
    public int MaxOutput { get; set; } = 8_192;
    public ModelCapabilities Capabilities { get; set; } = new();
    /// <summary>For kind "local": absolute path of the GGUF file. Empty = the provider's models directory plus the model id.</summary>
    public string? LocalPath { get; set; }
    /// <summary>USD per 1M tokens.</summary>
    public decimal InputPricePerMTok { get; set; }
    public decimal OutputPricePerMTok { get; set; }
    /// <summary>Free-form routing tags: fast, quality, cheap, offline…</summary>
    public List<string>? Tags { get; set; }
}

/// <summary>
/// Unified capability model. Business code must branch on these flags,
/// never on provider or model names.
/// </summary>
public sealed class ModelCapabilities
{
    public bool Streaming { get; set; } = true;
    public bool Thinking { get; set; }
    public bool Vision { get; set; }
    public bool Image { get; set; }
    public bool ToolCalling { get; set; } = true;
    public bool JsonMode { get; set; }
    public bool Reasoning { get; set; }
    /// <summary>Highest neutral reasoning level accepted by this model.</summary>
    public ReasoningLevel MaxReasoningLevel { get; set; } = ReasoningLevel.Max;
    public bool Embedding { get; set; }
    public bool Mcp { get; set; } = true;
}

public sealed class RoutingConfig
{
    /// <summary>
    /// When true, a failed model request automatically fails over to the next candidate in
    /// the routing chain. When false, only the active model is tried and the turn stops with
    /// the real error instead of silently switching to another provider/model.
    /// </summary>
    public bool FailoverEnabled { get; set; } = true;

    /// <summary>
    /// Opt-in DeepSeek-specific request optimizations. The policy is intentionally separate from
    /// model selection so it can evolve independently as DeepSeek models/API surface change.
    /// </summary>
    public bool DeepSeekOptimizationEnabled { get; set; }

    /// <summary>Global failover chain, tried in order after the active model fails.</summary>
    public List<string> Fallback { get; set; } = [];

    /// <summary>priority | roundRobin | leastUsed | lowestCost | fastest | sticky</summary>
    public string LoadBalance { get; set; } = "priority";

    public RetryConfig Retry { get; set; } = new();
}

public sealed class RetryConfig
{
    public int MaxAttempts { get; set; } = 3;
    public double BaseDelaySeconds { get; set; } = 1.0;
    public double MaxDelaySeconds { get; set; } = 20.0;
    /// <summary>Consecutive failures before a model's circuit opens.</summary>
    public int CircuitBreakThreshold { get; set; } = 4;
    public double CircuitCooldownSeconds { get; set; } = 60.0;
    /// <summary>
    /// Wall-clock budget for one whole failover chain (all candidates and retries combined).
    /// Once exceeded the chain stops starting new attempts and surfaces the errors collected
    /// so far, instead of letting worst-case 4 candidates × 3 retries × 120s stretch to
    /// ~25 minutes before the user sees anything.
    /// </summary>
    public double ChainBudgetSeconds { get; set; } = 120.0;
}

public sealed class AgentConfig
{
    public int MaxSteps { get; set; } = 40;
    public bool AutoVerify { get; set; } = true;
    public int MaxRepairAttempts { get; set; } = 3;
    /// <summary>Personality prompt key: pragmatic | friendly. Defaults to the pragmatic engineering voice.</summary>
    public string Personality { get; set; } = "pragmatic";
    /// <summary>Summarizes older history when the context window would overflow, so a single turn can keep going.</summary>
    public bool EnableContextCompaction { get; set; } = true;
    /// <summary>Consecutive output-cap truncations allowed before a turn gives up instead of ending silently.</summary>
    public int MaxOutputContinuations { get; set; } = 6;
    /// <summary>Agent mode: edit | plan | readonly | auto.</summary>
    public string Mode { get; set; } = "edit";
    /// <summary>Prompt key of the main system prompt (relative to prompts/, no extension).</summary>
    public string SystemPrompt { get; set; } = "system/default";
    public int ThinkingBudgetTokens { get; set; } = 16_384;
    public ReasoningLevel ReasoningLevel { get; set; } = ReasoningLevel.High;
    /// <summary>Hard cap safeguard; effective tool output budget adapts to the model context window.</summary>
    public int MaxToolOutputChars { get; set; } = 60_000;
    public int BashTimeoutSeconds { get; set; } = 180;
    /// <summary>Wall-clock budget for one scheduled-task turn; exceeded runs are cancelled and recorded.</summary>
    public int ScheduledTurnTimeoutSeconds { get; set; } = 1_800;
    /// <summary>
    /// Optional webhook endpoint that receives a JSON POST describing every scheduled-task
    /// outcome (taskId/name/status/error/sessionId/output/timestamp). Keep empty to disable.
    /// This is the only notify channel that works while no desktop client is connected;
    /// connected desktops additionally receive <c>schedule.updated</c> events regardless.
    /// </summary>
    public string? ScheduleWebhookUrl { get; set; }
    /// <summary>
    /// Reply language: auto (follow the OS UI language, Chinese systems resolve to Chinese)
    /// | zh (简体中文) | en (English). Injected into the system prompt so the model detects
    /// the source language per message and replies in the resolved target language.
    /// </summary>
    public string Language { get; set; } = "auto";
    /// <summary>
    /// Inject workspace AGENTS.md rule files into the system prompt. When false the
    /// agent ignores rule files entirely and only explicit conversation instructions apply.
    /// </summary>
    public bool RulesEnabled { get; set; } = true;
    /// <summary>
    /// Memory maintenance mode: auto (default) lets the agent update MEMORY.md as it
    /// works; manual treats MEMORY.md as read-only context the agent must not modify
    /// unless the user explicitly asks — users maintain it through the editor instead.
    /// </summary>
    public string MemoryMode { get; set; } = "auto";
    /// <summary>
    /// Global network access switch. When false, network tools (web_search, web_fetch) are stripped
    /// and the agent prompt enforces offline operation across all workspaces.
    /// </summary>
    public bool NetworkEnabled { get; set; } = true;
}

public sealed class McpConfig
{
    public Dictionary<string, McpServerConfig> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class McpServerConfig
{
    /// <summary>stdio | sse | http | streamable-http (websocket reserved).</summary>
    public string Transport { get; set; } = "stdio";
    public string? Command { get; set; }
    public List<string>? Args { get; set; }
    public Dictionary<string, string>? Env { get; set; }
    public string? Url { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Tool names (exact, case-insensitive) always treated as mutating, regardless of the
    /// name heuristic. Use this for tools like send_email / deploy whose names carry no
    /// write keyword but do perform real-world changes.
    /// </summary>
    public List<string>? MutatingTools { get; set; }
    /// <summary>
    /// Tool names (exact, case-insensitive) always treated as read-only. Takes precedence
    /// over <see cref="MutatingTools"/> and the name heuristic.
    /// </summary>
    public List<string>? ReadOnlyTools { get; set; }
    /// <summary>
    /// Escape hatch restoring permissive inference for a known-safe server: when true,
    /// tools whose name carries no mutating keyword are treated as read-only. When false
    /// (the default) unknown-name tools are treated as mutating — MCP exposes no mutating
    /// metadata, so the safe default is to refuse such tools in readonly/plan mode.
    /// </summary>
    public bool TrustReadOnly { get; set; }
}

/// <summary>Per-workspace overrides stored in &lt;workspace&gt;/.haoyue/config.json.</summary>
public sealed class WorkspaceConfig
{
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public double? Temperature { get; set; }
    public string? Mode { get; set; }
    public string? SystemPrompt { get; set; }
    public string? Personality { get; set; }
    /// <summary>Per-workspace reply language override (auto | zh | en). Null falls back to the global Agent.Language.</summary>
    public string? Language { get; set; }
    public List<string>? DisabledSkills { get; set; }
    public List<string>? DisabledTools { get; set; }
    public McpConfig? Mcp { get; set; }
    public bool? AutoVerify { get; set; }
    /// <summary>Overrides the auto-detected build/check command used by the verify loop.</summary>
    public string? VerifyCommand { get; set; }
    /// <summary>
    /// Multi-step verification chain (build → test …) run in order with fail-fast semantics.
    /// Steps run independently, so shell operators like <c>&amp;&amp;</c> are not required
    /// (PowerShell 5.1 does not support them). When non-empty this takes precedence over
    /// <see cref="VerifyCommand"/>; when null the single <see cref="VerifyCommand"/> (or the
    /// auto-detected command) applies.
    /// </summary>
    public List<string>? VerifyCommands { get; set; }
}

/// <summary>Small mutable runtime state persisted in ~/.haoyue/state.json (round-robin cursors, last session…).</summary>
public sealed class RuntimeState
{
    public Dictionary<string, int> RoundRobinCursors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? LastSessionId { get; set; }
    public Dictionary<string, string> DisabledSkills { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
