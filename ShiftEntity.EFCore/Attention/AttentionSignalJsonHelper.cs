using System.Text.Json;
using ShiftSoftware.ShiftEntity.Core.Attention;

namespace ShiftSoftware.ShiftEntity.EFCore.Attention;

/// <summary>
/// Serialization helper for JSON-shadow storage mode (<see cref="IHasAttention"/>).
/// Reads and writes the <see cref="ShadowPropertyName"/> EF Core shadow property on entity rows.
/// </summary>
internal static class AttentionSignalJsonHelper
{
    /// <summary>Name of the EF Core shadow property that stores the JSON signal array.</summary>
    internal const string ShadowPropertyName = "AttentionSignalsJson";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes signals to JSON for shadow-property storage.</summary>
    internal static string Serialize(IReadOnlyList<StoredAttentionSignal> signals)
        => JsonSerializer.Serialize(signals, JsonOptions);

    /// <summary>Deserializes signals from the shadow property's JSON. Returns an empty list for null or empty input.</summary>
    internal static List<StoredAttentionSignal> Deserialize(string? json)
        => string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize<List<StoredAttentionSignal>>(json, JsonOptions) ?? [];
}
