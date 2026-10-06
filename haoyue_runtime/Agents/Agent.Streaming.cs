using System.Text;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Agents;

public sealed partial class Agent
{
    private async Task<LlmCompletion?> CollectCompletionAsync(
        Func<ModelInfo, LlmRequest> requestFactory, WorkspaceInfo workspace, CancellationToken ct)
    {
        LlmCompletion? completion = null;
        await foreach (var evt in providerManager.StreamAsync(requestFactory, workspace.Config, ct).ConfigureAwait(false))
        {
            if (evt is LlmCompleted completed) completion = completed.Completion;
        }
        return completion;
    }

    private async Task<LlmCompletion> StreamOnceAsync(
        ModelInfo activeModel,
        WorkspaceInfo workspace,
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ITool> tools,
        ReasoningLevel reasoningLevel,
        bool requiresVision,
        CancellationToken ct)
    {
        var config = configStore.Config;
        var temperature = workspace.Config?.Temperature ?? config.Temperature;
        var definitions = tools
            .Select(t => new ToolDefinition(t.Name, t.Description, t.ParameterSchema))
            .ToList();

        LlmRequest RequestFor(ModelInfo model) => new()
        {
            Provider = model.Provider,
            Model = model.Model,
            // Re-fit for the concrete candidate because automatic failover models can have
            // a smaller user-declared context window than the initially selected model.
            Messages = ContextPlanner.FitToWindow(history, model.Model, systemPrompt),
            System = systemPrompt,
            Tools = definitions,
            Temperature = temperature,
            MaxTokens = model.Model.MaxOutput,
            EnableThinking = reasoningLevel != ReasoningLevel.None && model.Model.Capabilities.Thinking,
            ThinkingBudgetTokens = config.Agent.ThinkingBudgetTokens,
            ReasoningLevel = reasoningLevel,
            OptimizeDeepSeek = configStore.Config.Routing.DeepSeekOptimizationEnabled,
        };

        LlmCompletion? completion = null;
        var streamedText = new StringBuilder();
        var streamedThinking = new StringBuilder();
        var thinkingOpen = false;

        try
        {
            Func<ModelInfo, bool>? candidateFilter = requiresVision
                ? static candidate => candidate.Model.Capabilities.Vision
                : null;
            await foreach (var evt in providerManager.StreamAsync(
                               RequestFor, workspace.Config, ct, candidateFilter).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case LlmThinkingDelta thinking:
                        thinkingOpen = true;
                        streamedThinking.Append(thinking.Text);
                        events.Publish(new ThinkingDeltaEvent(thinking.Text));
                        break;
                    case LlmTextDelta text:
                        if (thinkingOpen)
                        {
                            thinkingOpen = false;
                            events.Publish(new ThinkingCompletedEvent());
                        }
                        streamedText.Append(text.Text);
                        events.Publish(new AssistantTextDeltaEvent(text.Text));
                        break;
                    case LlmToolCallStarted started:
                        if (thinkingOpen)
                        {
                            thinkingOpen = false;
                            events.Publish(new ThinkingCompletedEvent());
                        }
                        events.Publish(new StatusEvent("Preparing tool call", started.Name));
                        break;
                    case LlmCompleted done:
                        completion = done.Completion;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && streamedText.Length > 0)
        {
            // Preserve whatever was streamed before the user hit Ctrl+C.
            return new LlmCompletion
            {
                Text = streamedText.ToString(),
                Thinking = streamedThinking.ToString(),
                FinishReason = "cancelled",
            };
        }
        catch (LlmException) when (streamedText.Length > 0)
        {
            // Network drop / server abort after the first token. The text already reached
            // the UI via delta events; without this branch it would never reach the session
            // history (the exception would propagate and the turn would end with nothing
            // persisted), so resuming the session showed a different conversation than the
            // user just saw. Persist the partial output and end the step cleanly instead of
            // throwing — ProviderManager has already exhausted its retry/failover chain, so
            // a rethrow only converts kept work into a lost half-answer.
            if (thinkingOpen) events.Publish(new ThinkingCompletedEvent());
            events.Publish(new WarningEvent(
                "模型连接中断，已保留中断前生成的部分输出。可点击重新生成或继续提问补全。"));
            return new LlmCompletion
            {
                Text = streamedText.ToString(),
                Thinking = streamedThinking.ToString(),
                FinishReason = "interrupted",
            };
        }

        if (thinkingOpen) events.Publish(new ThinkingCompletedEvent());
        return completion ?? new LlmCompletion
        {
            Text = streamedText.ToString(),
            Thinking = streamedThinking.ToString(),
            FinishReason = "incomplete",
        };
    }
}
