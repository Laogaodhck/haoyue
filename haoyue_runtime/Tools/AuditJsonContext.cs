using System.Text.Json.Serialization;

namespace Haoyue.Runtime.Tools;

/// <summary>
/// Source-generated metadata for the JSONL audit trails (tools.jsonl / bash.jsonl):
/// the reflected overloads are not trim/AOT safe. Row shapes are fixed records, so
/// source generation costs nothing and keeps the serializer honest.
/// </summary>
[JsonSerializable(typeof(ToolAuditRecord))]
[JsonSerializable(typeof(BashAuditRecord))]
internal sealed partial class AuditJsonContext : JsonSerializerContext;
