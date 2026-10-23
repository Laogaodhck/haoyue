using System.Text.Json;
using System.Text.Json.Serialization;
using Haoyue.Runtime.Configuration;

namespace Haoyue.Runtime.Providers;

/// <summary>
/// Provider-neutral reasoning depth. UI and session code only exchange this enum; wire clients
/// are responsible for translating it to provider-specific parameters.
/// </summary>
[JsonConverter(typeof(ReasoningLevelJsonConverter))]
public enum ReasoningLevel
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Max = 4,
    XHigh = 5,
    Ultra = 6,
}

public sealed class ReasoningLevelJsonConverter : JsonConverter<ReasoningLevel>
{
    public override ReasoningLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && ReasoningLevelExtensions.TryParse(reader.GetString(), out var parsed))
            return parsed;
        if (reader.TokenType == JsonTokenType.Number
            && reader.TryGetInt32(out var numeric)
            && Enum.IsDefined(typeof(ReasoningLevel), numeric))
            return (ReasoningLevel)numeric;
        throw new JsonException("Invalid reasoning level");
    }

    public override void Write(Utf8JsonWriter writer, ReasoningLevel value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireValue());
}

public static class ReasoningLevelExtensions
{
    public static string ToWireValue(this ReasoningLevel level) => level switch
    {
        ReasoningLevel.None => "none",
        ReasoningLevel.Low => "low",
        ReasoningLevel.Medium => "medium",
        ReasoningLevel.High => "high",
        ReasoningLevel.Max => "max",
        ReasoningLevel.XHigh => "xhigh",
        ReasoningLevel.Ultra => "ultra",
        _ => "high",
    };

    public static bool TryParse(string? value, out ReasoningLevel level)
    {
        level = ReasoningLevel.None;
        return !string.IsNullOrWhiteSpace(value)
               && Enum.TryParse(value, ignoreCase: true, out level)
               && Enum.IsDefined(level);
    }
}

/// <summary>Central provider/model adaptation for the neutral reasoning depth.</summary>
public static class ReasoningLevelAdapter
{
    public static ReasoningLevel Normalize(
        ProviderConfig provider,
        ModelConfig model,
        ReasoningLevel requested)
    {
        var deepSeek = IsDeepSeek(provider, model);
        var explicitlyMapped = provider.ReasoningEffortMap is { Count: > 0 };
        if (!model.Capabilities.Thinking && !model.Capabilities.Reasoning && !deepSeek && !explicitlyMapped)
            return ReasoningLevel.None;

        var supportedMaximum = model.Capabilities.MaxReasoningLevel;

        // DeepSeek's wire vocabulary now reaches Ultra (the highest portable level), so no
        // extra clamp is needed here; OpenAiEffort() narrows the extended levels to the
        // official low/high/max table when the request is serialized.
        return requested > supportedMaximum ? supportedMaximum : requested;
    }

    public static string? OpenAiEffort(LlmRequest request)
    {
        // Auxiliary purposes (compaction, benchmarks, tests) never think, so an effort
        // value would be dead weight in the request body.
        if (request.Purpose != LlmPurpose.Chat) return null;

        var effective = Normalize(request.Provider, request.Model, request.ReasoningLevel);
        if (effective == ReasoningLevel.None &&
            !request.Model.Capabilities.Thinking && !request.Model.Capabilities.Reasoning)
            return null;

        if (effective == ReasoningLevel.None)
            return null;

        var key = effective.ToWireValue();
        var mapped = request.Provider.ReasoningEffortMap?
            .FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
        if (!string.IsNullOrWhiteSpace(mapped)) return mapped;

        // DeepSeek wire protocol accepts only low/high/max when thinking is enabled, and omits reasoning_effort when disabled.
        if (IsDeepSeek(request.Provider, request.Model))
        {
            // Official effort table (api-docs.deepseek.com): requested minimal/low → low,
            // medium/high/xhigh → high, max/ultra → max. Without an enabled thinking
            // switch the server ignores the value, so omit it instead.
            if (!request.ThinkingEnabled) return null;
            return effective switch
            {
                ReasoningLevel.Low => "low",
                ReasoningLevel.Medium or ReasoningLevel.High or ReasoningLevel.XHigh => "high",
                ReasoningLevel.Max or ReasoningLevel.Ultra => "max",
                _ => "high",
            };
        }

        return key;
    }

    public static int AnthropicBudget(LlmRequest request)
    {
        // Auxiliary purposes never think; a budget would be dead weight in the body.
        if (request.Purpose != LlmPurpose.Chat) return 0;

        var effective = Normalize(request.Provider, request.Model, request.ReasoningLevel);
        if (effective == ReasoningLevel.None) return 0;

        var baseBudget = Math.Max(1_024, request.ThinkingBudgetTokens);
        var multiplier = effective switch
        {
            ReasoningLevel.Low => 0.25,
            ReasoningLevel.Medium => 1.0,
            ReasoningLevel.High => 2.0,
            ReasoningLevel.Max => 4.0,
            ReasoningLevel.XHigh => 8.0,
            ReasoningLevel.Ultra => 16.0,
            _ => 0.0,
        };

        var maxOutput = request.MaxTokens ?? request.Model.MaxOutput;
        if (maxOutput < 2_048) return 0; // no room for a meaningful thinking budget plus an answer

        // The old cap of max_tokens / 2 truncated long reasoning phases mid-thought, forcing
        // the model to stop thinking and answer early on large tasks. Reserve only a small
        // slice for the final answer so thinking can use almost the entire output window.
        var answerReserve = Math.Min(4_096, Math.Max(1_024, maxOutput / 4));
        var budgetCap = maxOutput - answerReserve;

        var requested = (int)Math.Clamp(baseBudget * multiplier, 1_024, int.MaxValue);
        return Math.Min(requested, budgetCap);
    }

    internal static bool IsDeepSeek(ProviderConfig provider, ModelConfig model) =>
        provider.Id.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
        || provider.BaseUrl.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
        || model.Id.Contains("deepseek", StringComparison.OrdinalIgnoreCase);
}
