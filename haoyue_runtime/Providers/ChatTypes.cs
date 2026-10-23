using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;

namespace Haoyue.Runtime.Providers;

public enum ChatRole { System, User, Assistant, Tool }

/// <summary>
/// Provider-neutral inline image attached to a user message. <see cref="Data"/> is
/// null when the bytes were deliberately not resolved (paged session reads) — the
/// client then fetches them on demand via the session.image RPC.
/// </summary>
public sealed record ChatImageAttachment(
    string Id,
    string Name,
    string MediaType,
    string? Data,
    long SizeBytes);

/// <summary>Reference recorded on the assistant message that consumed an uploaded image.</summary>
public sealed record ChatImageReference(string Id, string Name);

/// <summary>Provider-neutral conversation message used by the agent loop.</summary>
public sealed class ChatMessage
{
    public ChatRole Role { get; init; }
    public string Text { get; set; } = "";
    public IReadOnlyList<ChatImageAttachment>? Images { get; init; }
    public string? Thinking { get; set; }
    /// <summary>"provider/model" that produced this assistant message (shown by clients).</summary>
    public string? ModelRef { get; set; }
    public IReadOnlyList<ChatImageReference>? ViewedImages { get; init; }
    /// <summary>
    /// Provider 给出的停止原因（已收敛到 <see cref="Haoyue.Runtime.Agents.TurnFinishReason"/>）。
    /// 落进会话历史是为了诚实：半截回答、被截断、拒答在后续回合与界面里都能看出来，
    /// 不再和「答完了」长得一样。仅 assistant 消息有值。
    /// </summary>
    public string? FinishReason { get; set; }
    public IReadOnlyList<ToolCallRequest>? ToolCalls { get; set; }
    /// <summary>Set when Role == Tool.</summary>
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public bool ToolSuccess { get; init; } = true;
    /// <summary>Unified diff produced by a mutating tool result.</summary>
    public string? ToolDiff { get; init; }
    /// <summary>Workspace-relative file path associated with ToolDiff.</summary>
    public string? ToolFilePath { get; init; }
    public DateTimeOffset? Timestamp { get; init; }

    public static ChatMessage User(string text, IReadOnlyList<ChatImageAttachment>? images = null) => new()
    {
        Role = ChatRole.User,
        Text = text,
        Images = images is { Count: > 0 } ? images : null,
    };
    public static ChatMessage Assistant(string text) => new() { Role = ChatRole.Assistant, Text = text };
    public static ChatMessage ToolResult(
        string callId,
        string toolName,
        string text,
        bool success,
        string? diff = null,
        string? filePath = null,
        IReadOnlyList<ChatImageAttachment>? images = null) =>
        new()
        {
            Role = ChatRole.Tool,
            ToolCallId = callId,
            ToolName = toolName,
            Text = text,
            ToolSuccess = success,
            ToolDiff = diff,
            ToolFilePath = filePath,
            Images = images is { Count: > 0 } ? images : null,
        };
}

public sealed record ToolCallRequest(string Id, string Name, string ArgumentsJson);

/// <summary>Provider-neutral tool definition; Parameters is a JSON Schema object.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonObject Parameters);

/// <summary>
/// Call intent behind an <see cref="LlmRequest"/>. Non-chat purposes are short-output,
/// tool-less auxiliary calls (context compaction, prompt rewriting, benchmarks, provider
/// tests): they must never spend their output budget on a chain of thought nobody reads,
/// which is also how the official DeepSeek harness drives its auxiliary calls. The gate is
/// centralized via <see cref="LlmRequest.ThinkingEnabled"/> so every thinking switch the
/// runtime controls (wire flags, budgets, local prompt building) cannot be forgotten by a
/// call site. It is not a universal off-switch: OpenAI reasoning models expose no portable
/// "disable" parameter, so auxiliary calls there only omit reasoning effort and may still
/// reason at the server default — route those to a non-reasoning model when cost matters.
/// </summary>
public enum LlmPurpose
{
    /// <summary>Main agent conversation turn (default).</summary>
    Chat = 0,
    /// <summary>Context-compaction summarization call.</summary>
    Compaction = 1,
    /// <summary>Other short auxiliary calls (prompt rewriting, model tests, benchmarks).</summary>
    Auxiliary = 2,
}

public sealed record LlmRequest
{
    public required ProviderConfig Provider { get; init; }
    public required ModelConfig Model { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public string? System { get; init; }
    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public bool EnableThinking { get; init; }
    /// <summary>Call intent; non-chat purposes never think (see <see cref="LlmPurpose"/>).</summary>
    public LlmPurpose Purpose { get; init; }
    /// <summary>
    /// Effective thinking switch: the request-level flag AND the purpose gate. Every
    /// thinking-related decision (wire parameters, budgets, local prompt building) must
    /// consult this instead of <see cref="EnableThinking"/> directly.
    /// </summary>
    public bool ThinkingEnabled => EnableThinking && Purpose == LlmPurpose.Chat;
    public int ThinkingBudgetTokens { get; init; } = 16_384;
    public ReasoningLevel ReasoningLevel { get; init; } = ReasoningLevel.Medium;
    /// <summary>Enables the opt-in DeepSeek optimization policy for this request.</summary>
    public bool OptimizeDeepSeek { get; init; }
}

// ---------------------------------------------------------------- stream events

public abstract record LlmStreamEvent;

public sealed record LlmTextDelta(string Text) : LlmStreamEvent;

public sealed record LlmThinkingDelta(string Text) : LlmStreamEvent;

/// <summary>Emitted as soon as the model starts a tool call (arguments may still be streaming).</summary>
public sealed record LlmToolCallStarted(string Id, string Name) : LlmStreamEvent;

/// <summary>Terminal event carrying the fully accumulated completion.</summary>
public sealed record LlmCompleted(LlmCompletion Completion) : LlmStreamEvent;

public sealed record LlmCompletion
{
    public string Text { get; init; } = "";
    public string Thinking { get; init; } = "";
    public IReadOnlyList<ToolCallRequest> ToolCalls { get; init; } = [];
    public TokenUsage Usage { get; init; } = new(0, 0);
    public string FinishReason { get; init; } = "";
    /// <summary>
    /// 本地推理的性能观测（TTFT / decode 速率 / KV 复用命中率等）。远程提供商不产生
    /// 该字段（保持 null），客户端据此区分是否展示本地性能面板。
    /// </summary>
    public InferencePerformance? Performance { get; init; }
}

public sealed record TokenUsage(long InputTokens, long OutputTokens)
{
    /// <summary>
    /// Total prompt tokens before cache discounts. Some providers report cached tokens outside
    /// InputTokens, so consumers should use this value when calculating a cache hit rate.
    /// </summary>
    public long TotalInputTokens { get; init; } = InputTokens;
    /// <summary>Input tokens served from the provider's prompt/context cache.</summary>
    public long CachedInputTokens { get; init; }
    /// <summary>Input tokens written to a provider cache, when reported by the provider.</summary>
    public long CacheCreationInputTokens { get; init; }
    public long Total => TotalInputTokens + OutputTokens;
}

/// <summary>Thrown by LLM clients for transport / API errors, carrying retryability info.</summary>
public sealed class LlmException(string message, int? statusCode = null, bool retryable = true, Exception? inner = null)
    : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
    public bool Retryable { get; } = retryable;

    /// <summary>Retry-After hint from the provider (429/503), honored over the local backoff guess.</summary>
    public TimeSpan? RetryAfter { get; init; }
}
