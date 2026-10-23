using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Verification;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Agents;

public sealed partial class Agent
{
    /// <summary>M-C: 视觉回合最多保留的含图消息条数（更新的图优先，更早的剥离）。</summary>
    internal const int MaxRecentImageMessages = 6;

    /// <summary>M-C: 单请求图片总数上限（含本回合新产生的图）。</summary>
    internal const int MaxImagesPerRequest = 8;

    // ---------------------------------------------------------------- llm streaming

    /// <summary>
    /// History copy with image payloads removed, used for text-only turns so earlier
    /// attachments are not re-uploaded on every follow-up message (slow) and do not
    /// force the non-streaming provider path.
    /// </summary>
    internal static IReadOnlyList<ChatMessage> WithoutImages(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.All(message => message.Images is not { Count: > 0 }))
            return messages;
        return messages
            .Select(message => message.Images is not { Count: > 0 }
                ? message
                : CloneMessage(message, images: null))
            .ToList();
    }

    /// <summary>
    /// M-C: 视觉回合历史剪裁。全量历史含图重传会让延迟/费用随会话长度线性增长，
    /// 过期截图还会把当前关键截图挤出窗口预算。策略（从新到旧）：保留最近
    /// <see cref="MaxRecentImageMessages"/> 条含图消息、按内容哈希去重（同图只上传
    /// 一次）、单请求图片总数与 token 双预算兜底。被剥离的消息保留 ViewedImages
    /// 引用——模型仍知道自己看过什么，只是不再重新上传 base64。
    /// </summary>
    internal static IReadOnlyList<ChatMessage> PruneVisionHistory(
        IReadOnlyList<ChatMessage> messages, int imageTokenBudget)
    {
        if (messages.All(message => message.Images is not { Count: > 0 }))
            return messages;

        var plan = new Dictionary<int, List<ChatImageAttachment>>();
        var seenHashes = new HashSet<string>(StringComparer.Ordinal);
        var keptImages = 0;
        var keptTokens = 0;

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (keptImages >= MaxImagesPerRequest) break;
            var images = messages[i].Images;
            if (images is not { Count: > 0 }) continue;
            if (plan.Count >= MaxRecentImageMessages) break; // 更早的含图消息全部剥离

            var fresh = new List<ChatImageAttachment>();
            var tokens = 0;
            foreach (var image in images)
            {
                if (!seenHashes.Add(ImageContentHash(image))) continue; // 重复内容只保留最新一次
                fresh.Add(image);
                tokens += ContextPlanner.EstimateImageTokens(image);
            }
            if (fresh.Count == 0) continue;
            // token 预算耗尽时停止向更早的历史保留（已保留的至少一条不受影响）。
            if (keptTokens + tokens > imageTokenBudget && plan.Count > 0) break;

            plan[i] = fresh;
            keptImages += fresh.Count;
            keptTokens += tokens;
        }

        if (plan.Count == 0)
            return WithoutImages(messages);

        var result = new List<ChatMessage>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Images is not { Count: > 0 })
            {
                result.Add(message);
                continue;
            }
            result.Add(CloneMessage(message, plan.TryGetValue(i, out var fresh) ? fresh : null));
        }
        return result;
    }

    /// <summary>M-C: 图片内容哈希（base64 载荷的 SHA-256），用于跨回合去重。</summary>
    private static string ImageContentHash(ChatImageAttachment image) =>
        string.IsNullOrEmpty(image.Data)
            ? image.Id
            : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(image.Data)));

    /// <summary>Clone of <paramref name="message"/> with a replaced image set (null strips
    /// payloads while keeping <see cref="ChatMessage.ViewedImages"/> references).</summary>
    private static ChatMessage CloneMessage(ChatMessage message, IReadOnlyList<ChatImageAttachment>? images) => new()
    {
        Role = message.Role,
        Text = message.Text,
        Thinking = message.Thinking,
        ViewedImages = message.ViewedImages,
        ToolCalls = message.ToolCalls,
        ToolCallId = message.ToolCallId,
        ToolName = message.ToolName,
        ToolSuccess = message.ToolSuccess,
        ToolDiff = message.ToolDiff,
        ToolFilePath = message.ToolFilePath,
        Timestamp = message.Timestamp,
        ModelRef = message.ModelRef,
        FinishReason = message.FinishReason,
        Images = images is { Count: > 0 } ? images : null,
    };

    // ---------------------------------------------------------------- vision model selection

    /// <summary>
    /// Picks the model for a turn that contains images: the configured vision model when
    /// usable (resolved, enabled, vision-capable), otherwise the first vision-capable
    /// candidate in the routing chain. Null when nothing qualifies.
    /// </summary>
    internal static ModelInfo? PickVisionModel(ModelInfo? preferred, IReadOnlyList<ModelInfo> candidates)
    {
        if (preferred is not null && preferred.Provider.Enabled && preferred.Model.Capabilities.Vision)
            return preferred;
        return candidates.FirstOrDefault(candidate => candidate.Model.Capabilities.Vision);
    }

    private ModelInfo ResolveVisionModel(WorkspaceInfo workspace)
    {
        var preferredRef = configStore.Config.Agent.VisionModel;
        var preferred = string.IsNullOrWhiteSpace(preferredRef)
            ? null
            : providerManager.ResolveModel(preferredRef);
        return PickVisionModel(preferred, providerManager.BuildCandidates(workspace.Config))
            ?? throw new LlmException(
                "No configured model supports image understanding.",
                retryable: false);
    }

    private async Task CompactContextAsync(
        AgentSession session,
        WorkspaceInfo workspace,
        ModelInfo model,
        IReadOnlyList<ChatMessage> source,
        string systemPrompt,
        CancellationToken ct,
        int? windowCeiling = null,
        double calibration = 1.0,
        int toolSchemaTokens = 0)
    {
        var (old, recent) = ContextPlanner.SplitForCompaction(
            source, model.Model, systemPrompt, windowCeiling, calibration, toolSchemaTokens,
            configStore.Config.Agent.CompactionTailRatio);
        if (old.Count == 0) return;

        events.Publish(new StatusEvent("Compacting context", $"{old.Count} earlier messages"));
        string? summary;
        try
        {
            summary = await SummarizeContextAsync(model, workspace, old, systemPrompt, ct, windowCeiling, calibration, toolSchemaTokens).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Compaction must never break the turn: fall back to the plain window trim.
            events.Publish(new WarningEvent($"Context compaction failed: {ex.Message}"));
            return;
        }

        if (string.IsNullOrWhiteSpace(summary)) return;

        // 收敛校验：替换后的消息（包装文案 + 摘要 + 消息开销）必须真的比被遮蔽的内容小，
        // 否则这次压缩没有缩小任何体积——下一回合会话依旧超限，却在「压缩→还是超限」
        // 之间空烧 token（摘要比原文更长时甚至每轮都在膨胀）。不收敛就放弃本次压缩，
        // 回退到普通窗口裁剪。
        var summaryMessage = ChatMessage.User(
            ">>> [Context compaction] 之前的对话已被压缩以保持上下文可容纳，以下是阶段性总结：\n\n"
            + summary.Trim());
        var replacementOverhead = ContextPlanner.EstimateTokens(summaryMessage, calibration)
            - ContextPlanner.EstimateTokens(summary.Trim(), calibration);
        if (!ContextPlanner.SummaryConverged(summary, old, calibration, replacementOverhead))
        {
            events.Publish(new WarningEvent(
                $"上下文压缩未收敛：摘要不小于被压缩的 {old.Count} 条消息，已回退为普通历史裁剪。"));
            return;
        }

        session.Messages.Clear();
        session.Messages.Add(summaryMessage);
        session.Messages.AddRange(recent);

        try
        {
            sessionStore.ReplaceHistory(session, session.Messages);
        }
        catch (Exception ex)
        {
            // Keep the in-memory compacted view even if persistence fails.
            events.Publish(new WarningEvent($"Could not persist context compaction: {ex.Message}"));
        }

        events.Publish(new StatusEvent("Context compacted", $"{old.Count} earlier messages summarized"));
    }

    private async Task<string?> SummarizeContextAsync(
        ModelInfo model, WorkspaceInfo workspace, IReadOnlyList<ChatMessage> old, string systemPrompt,
        CancellationToken ct, int? windowCeiling = null, double calibration = 1.0, int toolSchemaTokens = 0)
    {
        // Image payloads are not needed for the summary; dropping them keeps the call small.
        var instruction = promptProvider.TryGet("builtin/summarize");
        if (instruction is null)
        {
            events.Publish(new WarningEvent("Compaction prompt 'builtin/summarize' is missing; falling back to plain history trimming."));
            return null;
        }
        var withoutImages = WithoutImages(old);
        var input = ContextPlanner.FitToWindow(
            withoutImages, model.Model, systemPrompt, windowCeiling, calibration, toolSchemaTokens);
        // The summarize input itself goes through a window fit: when the oldest portion
        // does not fit, it is dropped BEFORE being summarized — those facts would vanish
        // silently. Never hide that from the user.
        if (input.Count < withoutImages.Count)
            events.Publish(new WarningEvent(
                $"上下文压缩：最早期 {withoutImages.Count - input.Count} 条消息超出摘要输入预算，未纳入本次总结，相关信息可能缺失。"));
        // 前缀复用：压缩调用沿用主对话的 system 提示词与历史原文，仅把摘要指令作为
        // 最后一条 user 消息追加。在无图、且摘要输入未被二次裁剪时，这段历史与主回合的
        // 提示词前缀逐 token 一致，可命中提供商的 prompt 前缀缓存（DeepSeek 缓存命中价
        // 约为未命中的几十分之一）；去图或二次裁剪会让缓存从分歧点起失效。
        // 若把指令放在独立 system 消息里，两次调用不共享任何前缀，缓存全部落空。
        var messages = new List<ChatMessage>(input) { ChatMessage.User(instruction) };
        var completion = await CollectCompletionAsync(
            candidate => new LlmRequest
            {
                Provider = candidate.Provider,
                Model = candidate.Model,
                Messages = messages,
                System = systemPrompt,
                MaxTokens = 4_096,
                EnableThinking = false,
                ReasoningLevel = ReasoningLevel.None,
                Purpose = LlmPurpose.Compaction,
            },
            workspace, ct).ConfigureAwait(false);
        return completion?.Text;
    }
}
