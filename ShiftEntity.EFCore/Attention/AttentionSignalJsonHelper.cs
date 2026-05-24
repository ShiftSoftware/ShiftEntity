using System.Text.Json;
using ShiftSoftware.ShiftEntity.Core.Attention;

namespace ShiftSoftware.ShiftEntity.EFCore.Attention;

internal static class AttentionSignalJsonHelper
{
    internal const string ShadowPropertyName = "AttentionSignalsJson";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string Serialize(IReadOnlyList<StoredAttentionSignal> signals)
        => JsonSerializer.Serialize(signals, JsonOptions);

    internal static List<StoredAttentionSignal> Deserialize(string? json)
        => string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize<List<StoredAttentionSignal>>(json, JsonOptions) ?? [];
}
