using System.Text.Json;
using System.Text.Json.Serialization;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Scheduling;
using Haoyue.Runtime.Sessions;

namespace Haoyue.Runtime.Configuration;

/// <summary>Source-generated JSON metadata for all persisted types (Native AOT friendly).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(HaoyueConfig))]
[JsonSerializable(typeof(WorkspaceConfig))]
[JsonSerializable(typeof(RuntimeState))]
[JsonSerializable(typeof(SessionHeader))]
[JsonSerializable(typeof(SessionMessage))]
[JsonSerializable(typeof(ScheduledTask))]
[JsonSerializable(typeof(List<ScheduledTask>))]
[JsonSerializable(typeof(Haoyue.Runtime.Data.FactEntry))]
[JsonSerializable(typeof(List<Haoyue.Runtime.Data.FactEntry>))]
[JsonSerializable(typeof(Haoyue.Runtime.Scheduling.BackgroundTaskInfo))]
[JsonSerializable(typeof(List<Haoyue.Runtime.Scheduling.BackgroundTaskInfo>))]
[JsonSerializable(typeof(UsageEntry))]
[JsonSerializable(typeof(McpConfig))]
[JsonSerializable(typeof(List<SessionHeader>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Haoyue.Runtime.Daemon.ActiveTurnRecord))]
[JsonSerializable(typeof(List<Haoyue.Runtime.Daemon.ActiveTurnRecord>))]
[JsonSerializable(typeof(Haoyue.Runtime.Daemon.TurnStepSummary))]
[JsonSerializable(typeof(List<Haoyue.Runtime.Daemon.TurnStepSummary>))]
public sealed partial class HaoyueJsonContext : JsonSerializerContext
{
    /// <summary>Compact variant for embedded session payloads and protocol serialization.</summary>
    public static HaoyueJsonContext Compact { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    });
}
