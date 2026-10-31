using System.Text.Json.Serialization;

namespace Haoyue.Runtime.Events;

/// <summary>
/// Source-generated metadata for every concrete <see cref="RuntimeEvent"/>.
/// The journal serializes by runtime type and the evolution engine deserializes
/// journal rows back into events; both go through this context instead of the
/// reflection-based overloads so trimming/AOT keeps the property graph.
/// A new event type must be listed here — <c>EventJsonContextTests</c> enforces it.
/// </summary>
[JsonSerializable(typeof(TurnStartedEvent))]
[JsonSerializable(typeof(TurnCompletedEvent))]
[JsonSerializable(typeof(UserSteerEvent))]
[JsonSerializable(typeof(StatusEvent))]
[JsonSerializable(typeof(AssistantTextDeltaEvent))]
[JsonSerializable(typeof(AssistantMessageCompletedEvent))]
[JsonSerializable(typeof(ThinkingDeltaEvent))]
[JsonSerializable(typeof(ThinkingCompletedEvent))]
[JsonSerializable(typeof(ImageViewedEvent))]
[JsonSerializable(typeof(ToolCallStartedEvent))]
[JsonSerializable(typeof(ToolCallProgressEvent))]
[JsonSerializable(typeof(ToolCallCompletedEvent))]
[JsonSerializable(typeof(FileDiffEvent))]
[JsonSerializable(typeof(RetrievalMissEvent))]
[JsonSerializable(typeof(ModelInvocationStartedEvent))]
[JsonSerializable(typeof(ProviderRetryEvent))]
[JsonSerializable(typeof(ProviderSwitchedEvent))]
[JsonSerializable(typeof(UsageRecordedEvent))]
[JsonSerializable(typeof(WorkflowEvent))]
[JsonSerializable(typeof(PlanUpdatedEvent))]
[JsonSerializable(typeof(VerificationStartedEvent))]
[JsonSerializable(typeof(VerificationCompletedEvent))]
[JsonSerializable(typeof(ScheduledTaskUpcomingEvent))]
[JsonSerializable(typeof(ScheduledTaskCompletedEvent))]
[JsonSerializable(typeof(ComputerActionEvent))]
[JsonSerializable(typeof(WarningEvent))]
[JsonSerializable(typeof(ErrorEvent))]
[JsonSerializable(typeof(ApprovalRequiredEvent))]
[JsonSerializable(typeof(EvolutionReflectionCompletedEvent))]
[JsonSerializable(typeof(MemoryConsolidationCompletedEvent))]
[JsonSerializable(typeof(UserFeedbackEvent))]
[JsonSerializable(typeof(BackgroundTaskEvent))]
[JsonSerializable(typeof(ModelLoadProgressEvent))]
public sealed partial class EventJsonContext : JsonSerializerContext;
