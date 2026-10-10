using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Secrets;

namespace Haoyue.Runtime.Configuration;

/// <summary>Root of ~/.haoyue/config.json. All model/provider data is user data — never hard-coded.</summary>
public sealed class HaoyueConfig
{
    /// <summary>配置 schema 版本，由 <see cref="ConfigSchema"/> 统一管理并在加载时迁移推进。</summary>
    public int SchemaVersion { get; set; } = ConfigSchema.CurrentVersion;

    public string? Provider { get; set; }
    public string? Model { get; set; }
    public double? Temperature { get; set; }

    public List<ProviderConfig> Providers { get; set; } = [];
    public RoutingConfig Routing { get; set; } = new();
    public AgentConfig Agent { get; set; } = new();
    public McpConfig Mcp { get; set; } = new();
    public Haoyue.Runtime.ComputerUse.ComputerUseConfig ComputerUse { get; set; } = new();
    public Haoyue.Runtime.NetworkOps.NetworkOpsConfig NetworkOps { get; set; } = new();
    public EvolutionConfig Evolution { get; set; } = new();
    public KnowledgeConfig Knowledge { get; set; } = new();
    public ImageGenConfig ImageGen { get; set; } = new();
    public WebConfig Web { get; set; } = new();


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

    /// <summary>
    /// Resolves the stored API key: a "secret:" reference (DPAPI / keyring / file
    /// store) is decrypted back to plaintext, a legacy plaintext value passes
    /// through unchanged. Unresolvable secrets return null — callers must treat
    /// that as "credential lost" rather than sending ciphertext to a server.
    /// </summary>
    public string? ResolveApiKey() => SecretResolver.Resolve(ApiKey);
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
    /// <summary>
    /// For kind "local": absolute path of the multimodal projector (mmproj) GGUF that
    /// enables image input. Empty auto-discovers a single *mmproj*.gguf in the models
    /// directory; the model stays text-only when none is found.
    /// </summary>
    public string? MmprojPath { get; set; }
    /// <summary>
    /// For kind "local": per-model load &amp; inference settings shown on the desktop model
    /// configuration page. Null keeps provider-level (and built-in) defaults; fields left
    /// null inside the object mean "auto" as well, so remote providers are never affected.
    /// </summary>
    public Providers.LocalModelSettings? Load { get; set; }
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
    /// <summary>
    /// Extra attempts granted to each fallback candidate (the active model always gets
    /// MaxAttempts). Default 0 = single shot; raise to 1-2 when fallback providers hit
    /// transient 429s that an immediate retry would clear.
    /// </summary>
    public int FallbackRetryAttempts { get; set; }
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
    /// <summary>
    /// Model ref preferred for turns that contain images (chat attachments, capture_screen
    /// results). Empty = automatic: the first vision-capable candidate in the routing chain
    /// is used. A stale or non-vision reference falls back to the automatic selection.
    /// </summary>
    public string? VisionModel { get; set; }
    /// <summary>
    /// When false, delegate_task is hidden from the tool view, so the agent cannot spawn
    /// sub-agents and finishes every subtask inline.
    /// </summary>
    public bool DelegationEnabled { get; set; } = true;
    /// <summary>
    /// 子代理嵌套深度上限（1..4）。默认 1 = 子代理不能再委派；调大后每层子代理
    /// 仍持有一层配额。并行批次（delegate_tasks）的子代理一律不能再委派。
    /// </summary>
    public int DelegationMaxDepth { get; set; } = 1;
    /// <summary>
    /// 工具执行强制策略（运行时闸门，先于工具执行评估，模型无法绕过）。
    /// 与提示层的 system/permissions 不同，本策略在参数解析后、工具调用前强制执行。
    /// </summary>
    public Haoyue.Runtime.Tools.ToolPolicyConfig ToolPolicy { get; set; } = new();
    /// <summary>Windows 进程级沙箱（Job Object）。默认关闭；启用后 bash 子进程树受内存/进程数/UI 限制并有 kill-on-close 兜底。网络隔离不在 Job Object 能力范围内。</summary>
    public BashSandboxConfig BashSandbox { get; set; } = new();
}

/// <summary>bash 进程级沙箱配置（Windows Job Object）。</summary>
public sealed class BashSandboxConfig
{
    /// <summary>启用沙箱（仅 Windows 生效；其他平台自动回退直接执行）。</summary>
    public bool Enabled { get; set; }
    /// <summary>作业内存上限（MB，按进程计）。0 = 不限制。</summary>
    public int MemoryLimitMb { get; set; } = 2048;
    /// <summary>作业内活动进程数上限。0 = 不限制。</summary>
    public int MaxProcesses { get; set; } = 256;
    /// <summary>UI 限制（剪贴板读写、系统参数、显示设置、关机、桌面切换、全局原子表）。</summary>
    public bool UiRestrictions { get; set; } = true;
}

/// <summary>知识库配置：语义检索（embedding）层。</summary>
public sealed class KnowledgeConfig
{
    /// <summary>语义检索总开关。关闭或未配置可用 embedding 模型时，检索自动退回纯词法方案。</summary>
    public bool SemanticEnabled { get; set; } = true;
    /// <summary>embedding 模型引用（"providerId/modelId"）。空 = 自动选用第一个 Capabilities.Embedding 的 HTTP 提供商模型。</summary>
    public string? EmbeddingModel { get; set; }
}

/// <summary>图像生成配置（多模态输出）。未配置可用图像模型时 image_generate 工具自动隐藏。</summary>
public sealed class ImageGenConfig
{
    /// <summary>图像模型引用（"providerId/modelId"）。空 = 自动选用第一个 Capabilities.Image 的 HTTP 提供商模型。</summary>
    public string? Model { get; set; }
    /// <summary>生成尺寸（传给 images/generations 的 size）。提供商不支持时由端点裁定。</summary>
    public string Size { get; set; } = "1024x1024";
}

/// <summary>外部网站访问白名单（web_fetch）。空列表 = 不限制；localhost / 127.x / ::1 始终允许。</summary>
public sealed class WebConfig
{
    /// <summary>
    /// 允许的网站规则，形式 scheme://host（host 可为 "*.example.com" 以允许子域；
    /// 根域 example.com 需单独添加）。仅 http/https。空列表 = 不限制外部访问。
    /// 变更立即生效（工具每次执行时读取，无缓存）。
    /// </summary>
    public List<string> AllowedSites { get; set; } = [];
}

public sealed class McpConfig
{
    public Dictionary<string, McpServerConfig> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 进化引擎配置。反思产出的技能草稿始终等待人工采纳（人工闸门），这里只控制
/// 反思回合本身的触发方式：仅手动，或按间隔无人值守自动运行。
/// </summary>
public sealed class EvolutionConfig
{
    /// <summary>按 <see cref="IntervalMinutes"/> 间隔自动运行反思 turn；关闭时仅手动触发。</summary>
    public bool AutoReflect { get; set; }
    /// <summary>自动反思间隔（分钟），读写时夹紧到 30..10080（7 天）。</summary>
    public int IntervalMinutes { get; set; } = 360;
    /// <summary>缺陷阈值触发：待处理缺陷达到 <see cref="ThresholdSignals"/> 时提前自动反思（30 分钟节流）。</summary>
    public bool ThresholdEnabled { get; set; }
    /// <summary>阈值触发的待处理缺陷数下限（1..50）。</summary>
    public int ThresholdSignals { get; set; } = 5;
}

public sealed class McpServerConfig
{
    /// <summary>stdio | sse | http | streamable-http (websocket reserved).</summary>
    public string Transport { get; set; } = "stdio";
    public string? Command { get; set; }
    public List<string>? Args { get; set; }
    public Dictionary<string, string>? Env { get; set; }
    public string? Url { get; set; }
    /// <summary>
    /// Optional HTTP headers for remote transports (sse / http / streamable-http), e.g.
    /// "Authorization": "Bearer …". Values may carry the "secret:" prefix written by
    /// the credential store; they are resolved right before a transport is created and
    /// never echoed back through the daemon API.
    /// </summary>
    public Dictionary<string, string>? Headers { get; set; }
    /// <summary>Remote connect timeout in seconds; defaults to 10 when null.</summary>
    public int? ConnectTimeoutSeconds { get; set; }
    /// <summary>
    /// OAuth access token from the local authorization flow, stored secret:-prefixed.
    /// Applied as a Bearer Authorization header when no explicit one is configured.
    /// </summary>
    public string? OAuthAccessToken { get; set; }
    /// <summary>OAuth refresh token (secret:-prefixed); enables silent re-authorization.</summary>
    public string? OAuthRefreshToken { get; set; }
    /// <summary>Client id from the RFC 7591 dynamic registration bound to the refresh token.</summary>
    public string? OAuthClientId { get; set; }
    /// <summary>Client secret from dynamic registration (secret:-prefixed), when the server issues one.</summary>
    public string? OAuthClientSecret { get; set; }
    /// <summary>Expert-mode escape hatch: never launch the interactive OAuth browser flow for this server.</summary>
    public bool? OAuthDisabled { get; set; }
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
