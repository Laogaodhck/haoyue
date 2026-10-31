using System.Text.Json.Nodes;
using Haoyue.Runtime.Events;

namespace Haoyue.Runtime.Daemon;

/// <summary>
/// Turn event forwarding: subscription events are projected onto the JSONL
/// envelope stream and folded into the per-turn usage accumulator.
/// </summary>
public sealed partial class DaemonServer
{
    private static async Task ForwardEventsAsync(
        IEventSubscription subscription,
        StreamWriter writer,
        SemaphoreSlim writerGate,
        long id,
        string sessionId,
        CancellationToken ct,
        SessionUsageAccumulator usage)
    {
        await foreach (var evt in subscription.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            AccumulateSessionUsage(usage, evt);
            // ErrorEvent is a runtime diagnostic, not a terminal protocol response. The turn
            // result below sends the final `error` envelope with the provider's full detail.
            // Forwarding this event as `error` would terminate desktop clients early and lose
            // ErrorEvent.Detail (for example the HTTP status and API response message).
            var payload = evt switch
            {
                AssistantTextDeltaEvent delta => (Name: (string?)"delta", Data: delta.Delta, Details: (JsonObject?)null),
                ThinkingDeltaEvent thinking => (Name: (string?)"thinking", Data: thinking.Delta, Details: (JsonObject?)null),
                ApprovalRequiredEvent approval => (
                    Name: (string?)"approval_request",
                    Data: approval.Summary,
                    Details: (JsonObject?)new JsonObject
                    {
                        ["callId"] = approval.CallId,
                        ["tool"] = approval.ToolName,
                        ["reason"] = approval.Reason,
                    }),
                UserSteerEvent steer => (Name: (string?)"steer", Data: steer.Instruction, Details: (JsonObject?)null),
                ImageViewedEvent image => (
                    Name: (string?)"image_view",
                    Data: image.Name,
                    Details: new JsonObject
                    {
                        ["imageId"] = image.ImageId,
                        ["mediaType"] = image.MediaType,
                    }),
                StatusEvent status => (Name: (string?)"status", Data: status.Status, Details: (JsonObject?)null),
                // 内核告警必须走线：策略拒绝的原因、上下文压缩丢块、输出截断、空回答
                // 都只发 WarningEvent，此前它不在契约三张名单里也没有转发分支，
                // 用户看到的是「被拦了但不知道为什么」。
                WarningEvent warning => (Name: (string?)"warning", Data: warning.Message, Details: (JsonObject?)null),
                ToolCallStartedEvent tool => (
                    Name: (string?)"tool_start",
                    Data: tool.ToolName,
                    Details: new JsonObject
                    {
                        ["callId"] = tool.CallId,
                        ["summary"] = tool.ArgumentSummary,
                    }),
                ToolCallCompletedEvent tool => (
                    Name: (string?)"tool_done",
                    Data: tool.ResultSummary,
                    Details: new JsonObject
                    {
                        ["callId"] = tool.CallId,
                        ["success"] = tool.Success,
                        ["durationMs"] = tool.Duration.TotalMilliseconds,
                    }),
                FileDiffEvent diff => (
                    Name: (string?)"file_diff",
                    Data: diff.FilePath,
                    Details: new JsonObject
                    {
                        ["callId"] = diff.CallId,
                        ["diff"] = diff.UnifiedDiff,
                    }),
                ModelInvocationStartedEvent model => (
                    Name: (string?)"model_start",
                    Data: $"{model.ProviderId}/{model.ModelId}",
                    Details: new JsonObject
                    {
                        ["provider"] = model.ProviderId,
                        ["model"] = model.ModelId,
                        ["step"] = model.Step,
                    }),
                UsageRecordedEvent tokenEvent => (
                    Name: (string?)"usage",
                    Data: $"{tokenEvent.ProviderId}/{tokenEvent.ModelId}",
                    Details: new JsonObject
                    {
                        ["provider"] = tokenEvent.ProviderId,
                        ["model"] = tokenEvent.ModelId,
                        ["inputTokens"] = tokenEvent.InputTokens,
                        ["outputTokens"] = tokenEvent.OutputTokens,
                        ["totalInputTokens"] = tokenEvent.TotalInputTokens,
                        ["cachedInputTokens"] = tokenEvent.CachedInputTokens,
                        ["elapsedMs"] = tokenEvent.Elapsed.TotalMilliseconds,
                        // 本地推理遥测（InferenceMeter 实测）：桌面输入框读数直接消费。
                        // 没有它，遥测只进 usage.jsonl，用户看不到预热 / KV 复用的真实效果。
                        ["inference"] = tokenEvent.Performance is { } perf
                            ? (JsonNode?)new JsonObject
                            {
                                ["ttftMs"] = Math.Round(perf.TtftMs, 1),
                                ["loadWaitMs"] = Math.Round(perf.LoadWaitMs, 1),
                                ["prefillTokensPerSecond"] = Math.Round(perf.PrefillTokensPerSecond, 1),
                                ["decodeTokensPerSecond"] = Math.Round(perf.DecodeTokensPerSecond, 1),
                                ["cacheHitRate"] = Math.Round(perf.CacheHitRate, 4),
                                ["prefixReuse"] = perf.PrefixReuse,
                            }
                            : null,
                    }),
                WorkflowEvent workflow => (
                    Name: (string?)"workflow",
                    Data: workflow.Label,
                    Details: new JsonObject
                    {
                        ["step"] = workflow.Step,
                        ["kind"] = workflow.Kind,
                        ["label"] = workflow.Label,
                        ["detail"] = workflow.Detail,
                    }),
                PlanUpdatedEvent plan => (
                    Name: (string?)"plan_update",
                    Data: plan.StepsJson,
                    Details: new JsonObject
                    {
                        ["steps"] = JsonNode.Parse(plan.StepsJson),
                        ["explanation"] = plan.Explanation,
                    }),
                _ => (Name: (string?)null, Data: "", Details: (JsonObject?)null),
            };
            if (payload.Name is not null)
                await WriteAsync(writer, writerGate, id, payload.Name, payload.Data, ct, sessionId, payload.Details).ConfigureAwait(false);
        }
    }

    private static void AccumulateSessionUsage(SessionUsageAccumulator usage, RuntimeEvent evt)
    {
        switch (evt)
        {
            case ModelInvocationStartedEvent:
                usage.LlmRounds++;
                break;
            case WorkflowEvent workflow:
                if (workflow.Step > usage.LastWorkflowStep)
                {
                    usage.ExecutionSteps += workflow.Step - usage.LastWorkflowStep;
                    usage.LastWorkflowStep = workflow.Step;
                }
                break;
            case UsageRecordedEvent token:
                usage.InputTokens += token.InputTokens;
                usage.TotalInputTokens += token.TotalInputTokens;
                usage.CachedInputTokens += token.CachedInputTokens;
                usage.OutputTokens += token.OutputTokens;
                usage.OutputElapsedMs += (long)token.Elapsed.TotalMilliseconds;
                break;
        }
    }
}
