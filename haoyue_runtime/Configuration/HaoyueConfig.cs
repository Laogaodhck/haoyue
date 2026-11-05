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
    public VideoGenConfig VideoGen { get; set; } = new();
    public SpeechConfig Speech { get; set; } = new();
    public DocRenderConfig DocRender { get; set; } = new();
    public WebConfig Web { get; set; } = new();
    public DaemonConfig Daemon { get; set; } = new();
    /// <summary>桌面端「任务监控」展示偏好（纯 UI 配置，runtime 行为不读取）。</summary>
    public TaskMonitorConfig TaskMonitor { get; set; } = new();
    /// <summary>
    /// 事件钩子：任务/计划任务完成时自动执行的外部动作（Webhook 通知或本地命令）。
    /// 命令会以当前进程身份启动子进程并可访问文件与网络——配置前请确认其安全性。
    /// </summary>
    public HooksConfig Hooks { get; set; } = new();
    /// <summary>「编码」设置：Git 工作流（分支前缀、强制推送）与安全（三层渐进式扫描）。</summary>
    public CodingConfig Coding { get; set; } = new();


    public ProviderConfig? FindProvider(string id) =>
        Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}

public sealed class ProviderConfig
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    /// <summary>
    /// Wire protocol: openai | anthropic | aliyun | volcengine | tencent | baidu | zhipu |
    /// deepseek | local. The domestic cloud vendors expose OpenAI-compatible endpoints, so
    /// they share the OpenAI implementation while keeping a vendor id for Desktop presets
    /// and labels; "local" runs GGUF models in-process without any server.
    /// </summary>
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
    /// <summary>
    /// For kind "local": 备用模型 id（同提供商内）。主模型加载全失败（重试 + CPU 降级
    /// 均不奏效，如文件损坏 / 显存不足）时自动改用它再试一次；回退成功时本次请求由
    /// 备用模型服务。空 = 无回退。仅本地模型生效，且不形成链（备用模型自身不再回退）。
    /// </summary>
    public string? FallbackModelId { get; set; }
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
    /// 2.7 本地→云故障转移（默认 false）：开启后本地模型的非可重试失败（模型文件缺失、
    /// 加载失败等）也走云回退链。默认关闭是「防 surprise billing」的有意设计——静默切到
    /// 付费云可能产生未预期的账单；开启前请确认云侧凭据与计费预期。
    /// </summary>
    public bool LocalFailoverToCloud { get; set; }

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
    /// <summary>
    /// 上下文压缩时保留的「最近对话」占窗口预算的比例（0.1–0.9，默认 0.5 = 历史行为）。
    /// 大窗口模型（云端 1M）可调小——多压缩、少保留原文，摘要调用自身也享受前缀缓存；
    /// 小窗口本地模型（24K）可调大——多保留逐字原文、少依赖摘要保真度。
    /// </summary>
    public double CompactionTailRatio { get; set; } = 0.5;
    /// <summary>Consecutive output-cap truncations allowed before a turn gives up instead of ending silently.</summary>
    public int MaxOutputContinuations { get; set; } = 6;
    /// <summary>
    /// 回合 token 闸门（输入+输出总量，0 = 不限）。越界先给一步收尾，仍不收尾则强制结束回合。
    /// 仅对回传 usage 的 provider 生效：本地模型等不回传用量的通道无法计量，此时靠墙钟兜底。
    /// </summary>
    public int TurnTokenBudget { get; set; }
    /// <summary>回合费用闸门（按模型 input/output 定价折算，与 usage 账本同口径；0 = 不限）。</summary>
    public double TurnCostBudget { get; set; }
    /// <summary>回合墙钟闸门（秒，0 = 不限）。工具等待与模型延迟都计入，是不依赖 usage 的兜底刹车。</summary>
    public int TurnWallClockSeconds { get; set; }
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
    /// 每回合注入的项目事实条数上限。事实层按与当前用户消息的相关性挑 top-K，
    /// 不再是整块灌入：库越大提示越稀释，无关事实还会挤占上下文预算。
    /// </summary>
    public int FactsInjectTopK { get; set; } = 12;
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
    /// 每回合允许的子任务总数（3.1 委派配额，默认 8）：delegate_task 一次占一席，
    /// delegate_tasks 的每个子任务各占一席。超出即显式报错——没有配额时最坏情况
    /// 是 6 路并行子代理各烧满回合预算，父回合却无从感知（委派用量现已汇入父预算，
    /// 配额再把总扇出钉住）。
    /// </summary>
    public int DelegationMaxPerTurn { get; set; } = 8;
    /// <summary>
    /// 读侧工作区边界之外的补充可读根目录（绝对路径）。文件读取工具默认只能访问工作区
    /// 内内容；把目录加入这里即表示用户授权智能体读取该目录（凭据文件仍被排除）。
    /// 全局会话额外隐式允许用户主目录，无需配置。
    /// </summary>
    public List<string> ExtraReadableRoots { get; set; } = [];
    /// <summary>
    /// 工具执行强制策略（运行时闸门，先于工具执行评估，模型无法绕过）。
    /// 与提示层的 system/permissions 不同，本策略在参数解析后、工具调用前强制执行。
    /// </summary>
    public Haoyue.Runtime.Tools.ToolPolicyConfig ToolPolicy { get; set; } = new();
    /// <summary>Windows 进程级沙箱（Job Object）。默认关闭；启用后 bash 子进程树受内存/进程数/UI 限制并有 kill-on-close 兜底。网络隔离不在 Job Object 能力范围内。</summary>
    public BashSandboxConfig BashSandbox { get; set; } = new();
    /// <summary>
    /// L-D 会话保留天数：超过 N 天未更新的会话在维护时自动归档（仅从默认列表隐藏，
    /// 仍可检索、includeArchived 找回，不删数据）；无消息引用的孤儿图片缓存文件与
    /// VACUUM 共用同一维护周期。0 = 关闭归档与孤儿清理。默认 90 天。
    /// </summary>
    public int SessionRetentionDays { get; set; } = 90;
    /// <summary>
    /// H-C 记忆固化总开关：开启后 daemon 定期把近期会话经验沉淀为项目事实
    /// （ConsolidationRunner）。关闭后仅保留回合内的 fact_save 自觉写入。
    /// </summary>
    public bool MemoryConsolidationEnabled { get; set; } = true;
    /// <summary>
    /// H-C 记忆固化扫描窗口（天，1..30）：只消化最近 N 天更新过的会话。
    /// </summary>
    public int MemoryConsolidationLookbackDays { get; set; } = 3;
    /// <summary>
    /// 本地模型同时驻留上限（LRU，1..16）。默认 2：聊天模型与嵌入模型交替请求
    /// 不再互相卸载重载（旧结构同时只驻留一个）。超出预算时驱逐最久未用且无
    /// 活跃推理的权重；服务中的权重永不驱逐。
    /// </summary>
    public int LocalModelMaxResident { get; set; } = 2;
    /// <summary>
    /// 本地推理并发槽数（1..16）。默认 1 = 严格串行（llama.cpp 单 context 的
    /// 保守语义，KV 前缀复用可用）。&gt;1 时每请求独立 context 并行解码，KV 前缀
    /// 复用自动关闭；显存随槽位数增长，请按显卡容量调整。
    /// </summary>
    public int LocalModelConcurrency { get; set; } = 1;
    /// <summary>
    /// 本地模型空闲回收（分钟，0 = 不回收）。权重空闲超过该时长后由 daemon 维护
    /// 周期归还内存/显存；下一次请求按原签名重新加载（有指纹 ETA 预估）。
    /// </summary>
    public int LocalModelIdleUnloadMinutes { get; set; } = 20;
    /// <summary>
    /// daemon 启动时后台预热活跃本地模型（不阻塞启动、不占推理槽）。开启后第一条
    /// 消息无需等待权重加载（实测冷加载 7.0s → 预热后首请求 4.3s，-39%）；关闭则
    /// 保持纯懒加载（首次请求时加载）。预热进度经 model.load.progress 广播，失败
    /// 静默（不影响启动，下一次请求照常懒加载）。默认开启：本地模型的首字延迟是
    /// 体验主项，而预热只在启动期占一次 IO/显存，与回合让路机制互不争抢。
    /// </summary>
    public bool LocalModelPreload { get; set; } = true;
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

/// <summary>
/// 桌面端「任务监控」展示偏好（纯 UI 配置：展示形式 + 四组内容开关）。
/// runtime 行为不读取任何字段；由桌面渲染层按开关决定监控面板展示哪些区块。
/// </summary>
public sealed class TaskMonitorConfig
{
    /// <summary>任务监控形式：fixed（右栏页签）| floating（悬浮窗）。</summary>
    public string Mode { get; set; } = "fixed";
    /// <summary>固定模式默认展示：show（打开右栏默认选中监控页签）| hidden（需手动点开）。</summary>
    public string FixedDefaultShow { get; set; } = "show";
    /// <summary>进度与上下文：任务回顾 / 任务目标 / 计划。</summary>
    public TaskMonitorProgressConfig Progress { get; set; } = new();
    /// <summary>执行活动：子智能体 / 后台进程 / 侧边聊天 / Skill 与 MCP。</summary>
    public TaskMonitorActivityConfig Activity { get; set; } = new();
    /// <summary>结果与来源：产出 / 网页查阅 / 来源 / 知识。</summary>
    public TaskMonitorResultsConfig Results { get; set; } = new();
    /// <summary>辅助入口：记忆更新 / 演示画面。</summary>
    public TaskMonitorAccessConfig Access { get; set; } = new();
}

/// <summary>进度与上下文组（默认全部展示）。</summary>
public sealed class TaskMonitorProgressConfig
{
    public bool Review { get; set; } = true;
    public bool Goal { get; set; } = true;
    public bool Plan { get; set; } = true;
}

/// <summary>执行活动组（默认全部展示）。</summary>
public sealed class TaskMonitorActivityConfig
{
    public bool SubAgents { get; set; } = true;
    public bool BackgroundProcesses { get; set; } = true;
    public bool SideChat { get; set; } = true;
    public bool SkillMcp { get; set; } = true;
}

/// <summary>结果与来源组（默认全部展示）。</summary>
public sealed class TaskMonitorResultsConfig
{
    public bool Outputs { get; set; } = true;
    public bool WebVisits { get; set; } = true;
    public bool Sources { get; set; } = true;
    public bool Knowledge { get; set; } = true;
}

/// <summary>辅助入口组（默认全部展示）。</summary>
public sealed class TaskMonitorAccessConfig
{
    public bool MemoryUpdate { get; set; } = true;
    public bool DemoScreen { get; set; } = true;
}

/// <summary>
/// 事件钩子配置：按触发事件（task.completed / schedule.completed）自动执行外部动作。
/// 每条钩子独立启停；动作二选一——Webhook（POST JSON 通知）或命令（本地子进程执行，
/// 会以运行时代理身份访问文件与网络，配置前务必确认目标命令与参数的安全性）。
/// </summary>
public sealed class HooksConfig
{
    /// <summary>钩子台账每个工作区保留的条数（200..10000，默认 2000）。裁剪前按日期归档。</summary>
    public int MaxLedgerRecords { get; set; } = 2000;

    public List<HookConfig> Hooks { get; set; } = new();
}

/// <summary>单条钩子。Name 为唯一标识；WebhookUrl/WebhookSecret/Command 按 Action 取值。</summary>
public sealed class HookConfig
{
    /// <summary>钩子名称（唯一标识，设置页以此增删改）。</summary>
    public string Name { get; set; } = "";
    /// <summary>触发事件：task.completed（回合完成）| schedule.completed（计划任务完成）。</summary>
    public string Trigger { get; set; } = "task.completed";
    /// <summary>动作类型：webhook（HTTP POST）| command（本地命令）。</summary>
    public string Action { get; set; } = "webhook";
    /// <summary>Webhook 目标地址（action=webhook 必填，http/https）。</summary>
    public string? WebhookUrl { get; set; }
    /// <summary>可选：随请求发送的 Authorization: Bearer 密钥（不回传明文）。</summary>
    public string? WebhookSecret { get; set; }
    /// <summary>本地命令（action=command 必填，非交互，默认超时 30 秒）。</summary>
    public string? Command { get; set; }
    /// <summary>
    /// 匹配条件：逗号分隔的通配符（支持 * 和 ?），匹配 payload.target
    /// （tool.before/tool.after 为工具名，session.start 为会话 id）。空 = 全部匹配。
    /// </summary>
    public string? Match { get; set; }
    /// <summary>单次执行超时秒数（1..120；webhook 实际上限 30 秒）。</summary>
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>失败策略：warn（放行并告警）| block（阻断主流程，仅 tool.before 生效）。</summary>
    public string FailurePolicy { get; set; } = "warn";
    /// <summary>
    /// 等待完成：仅对 tool.after / task.completed / schedule.completed 生效。
    /// true 时主流程等待该钩子执行结束（仍受 TimeoutSeconds 约束）；超时或失败都不阻断。
    /// 默认 false（fire-and-forget），保持既有行为。
    /// </summary>
    public bool WaitForCompletion { get; set; }
    /// <summary>启用开关（缺省 true）。</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// 「编码」设置：Git 工作流与安全。由桌面设置页「编码」分区读写
/// （advanced.get / advanced.set 的 coding 节），runtime 按节点读取生效。
/// </summary>
public sealed class CodingConfig
{
    /// <summary>Git 工作流：分支前缀与推送策略。</summary>
    public GitCodingConfig Git { get; set; } = new();
    /// <summary>安全：三层渐进式扫描与自动修复。</summary>
    public CodingSecurityConfig Security { get; set; } = new();
}

/// <summary>Git 工作流配置（桌面设置页「编码 → Git」）。</summary>
public sealed class GitCodingConfig
{
    /// <summary>新建分支时预填的分支名前缀；留空则不添加前缀。</summary>
    public string BranchPrefix { get; set; } = "";
    /// <summary>推送时使用 --force-with-lease：会改写远端分支历史，但远端已变化时拒绝覆盖。</summary>
    public bool ForcePushWithLease { get; set; } = false;
}

/// <summary>安全配置（桌面设置页「编码 → 安全」）。</summary>
public sealed class CodingSecurityConfig
{
    /// <summary>
    /// 三层渐进式扫描与自动修复总开关。开启后 haoyue 在合适节点智能推荐对应的
    /// 安全扫描能力，用户确认后执行扫描并修复。
    /// </summary>
    public bool ProgressiveScan { get; set; } = false;
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

/// <summary>
/// 视频生成配置（多模态输出）。video_generate 走 OpenAI 兼容端点的异步
/// contents/generations/tasks 链路（如火山方舟 Seedance）。模型引用必填，
/// 未配置时 video_generate / video_status 工具自动隐藏。
/// </summary>
public sealed class VideoGenConfig
{
    /// <summary>视频模型引用（"providerId/modelId"），必填才启用视频工具。空 = 工具隐藏。</summary>
    public string? Model { get; set; }
    /// <summary>默认画幅，传给 contents/generations/tasks 的 ratio（16:9 / 9:16 / 1:1 / adaptive 等）。</summary>
    public string Ratio { get; set; } = "16:9";
    /// <summary>默认时长（秒），夹紧到 5..30。</summary>
    public int Duration { get; set; } = 5;
    /// <summary>是否默认生成配乐/音频轨。</summary>
    public bool GenerateAudio { get; set; } = true;
    /// <summary>是否请求官方水印（未配置时按端点默认）。</summary>
    public bool Watermark { get; set; } = true;
    /// <summary>video_generate 单次轮询等待上限（秒），夹紧到 30..600；超时返回任务 ID 交由 video_status 查询。</summary>
    public int WaitSeconds { get; set; } = 120;
    /// <summary>轮询间隔（秒），夹紧到 5..60。</summary>
    public int PollIntervalSeconds { get; set; } = 10;
}

/// <summary>
/// 语音服务配置（豆包语音同源，openspeech.bytedance.com）。
/// ApiKey 支持 "secret:" 加密引用（与 provider 密钥同一凭据体系）。
/// 未配置 ApiKey 时 speech_to_text / text_to_speech 工具自动隐藏。
/// </summary>
public sealed class SpeechConfig
{
    /// <summary>火山引擎语音 API Key（新版控制台「API Key 管理」），支持 secret: 加密引用。</summary>
    public string? ApiKey { get; set; }
    /// <summary>ASR 资源 ID（录音文件极速版识别）。</summary>
    public string AsrResourceId { get; set; } = "volc.bigasr.auc_turbo";
    /// <summary>ASR 识别接口地址；空 = 默认官方端点。</summary>
    public string AsrBaseUrl { get; set; } = "https://openspeech.bytedance.com/api/v3/auc/bigmodel/recognize/flash";
    /// <summary>TTS 资源 ID（大模型语音合成）。</summary>
    public string TtsResourceId { get; set; } = "seed-tts-2.0";
    /// <summary>TTS 合成接口地址；空 = 默认官方端点。</summary>
    public string TtsBaseUrl { get; set; } = "https://openspeech.bytedance.com/api/v3/tts/unidirectional";
    /// <summary>默认音色 ID（控制台 > 音色库）。</summary>
    public string TtsSpeaker { get; set; } = "zh_female_vv_uranus_bigtts";
    /// <summary>合成音频格式：mp3 / wav / ogg_opus / pcm。</summary>
    public string TtsFormat { get; set; } = "mp3";
    /// <summary>合成采样率 Hz（8000..48000）。</summary>
    public int TtsSampleRate { get; set; } = 24000;
}

/// <summary>
/// 文档渲染配置（doc_render 工具）。工具调用本机 Python 运行 doc_toolkit
/// 完成文档生成；Python 缺失或找不到 doc_toolkit 包时工具自动隐藏。
/// </summary>
public sealed class DocRenderConfig
{
    /// <summary>总开关。关闭后 doc_render 工具从模型工具视图中移除。</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>doc_toolkit 包目录的显式路径（含 __init__.py 的目录本身）。
    /// 空 = 按可执行文件相对目录、环境变量 HAOYUE_DOC_TOOLKIT_PATH 与当前目录逐级探测。</summary>
    public string ToolkitPath { get; set; } = "";
    /// <summary>Python 解释器显式路径。空 = 复用 PythonLocator 的系统探测链。</summary>
    public string PythonPath { get; set; } = "";
}

/// <summary>
/// 外部网站访问白名单（web_fetch 与桌面端内置浏览器共用）。localhost / 127.x / ::1 始终允许。
/// 空列表语义两端不同：web_fetch 视为功能未启用（不限制）；内置浏览器只放行本地主机。
/// </summary>
public sealed class WebConfig
{
    /// <summary>
    /// 允许的网站规则，形式 scheme://host（host 可为 "*.example.com" 以允许子域；
    /// 根域 example.com 需单独添加）。仅 http/https。空列表 = web_fetch 不限制外部访问。
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
    /// Working directory for a stdio child process — absolute, or relative to the
    /// session workspace root. When null the child starts in the workspace root;
    /// leaving it unset otherwise made children inherit the daemon's own cwd (the
    /// app install dir in the packaged desktop), which breaks servers resolving "."
    /// against the project (e.g. mcp-server-git --repository .).
    /// </summary>
    public string? Cwd { get; set; }
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
    /// <summary>
    /// 工作区级工具硬禁用（大小写不敏感，支持 <c>server/tool</c> 通配符）。优先级高于
    /// <see cref="AllowedTools"/>：同一工具同时命中两者时一律拒绝（deny-overrides）。
    /// </summary>
    public List<string>? DisabledTools { get; set; }
    /// <summary>
    /// 工作区级工具白名单（缺省/空 = 不限制）；支持 MCP 的 <c>server/tool</c> 与
    /// <c>server/*</c> 通配符。智能体与子代理只能在此清单内继续收窄，不能放大。
    /// </summary>
    public List<string>? AllowedTools { get; set; }
    /// <summary>
    /// 角色预置/团体成员的工具清单把工具集裁成空时，是否退回仅受工作区策略约束的清单。
    /// 默认 <c>false</c>：fail-closed，宁可不给工具也不静默放权。
    /// </summary>
    public bool AllowToolFallbackToGlobal { get; set; }
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
    /// <summary>
    /// 6.4 skill 版本治理：官方技能 slug → 安装时的版本。用于判断「有可用更新」
    /// 并在更新时保留用户对 prompt.txt 的本地修改（仅刷新 manifest 版本）。
    /// </summary>
    public Dictionary<string, string> InstalledSkillVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
