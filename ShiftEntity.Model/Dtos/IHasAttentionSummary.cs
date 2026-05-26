namespace ShiftSoftware.ShiftEntity.Model.Dtos;

/// <summary>
/// DTO-side interface for attention summary properties. List DTOs implement this instead of
/// declaring summary properties by hand. <c>ShiftList</c> auto-detects this interface and
/// wires the attention indicator column and row tint without app code.
/// </summary>
public interface IHasAttentionSummary
{
    /// <summary>Whether any uncleared attention signal exists for this entity.</summary>
    bool HasActiveAttention { get; set; }

    /// <summary>Highest <see cref="ShiftSoftware.ShiftEntity.Core.Attention.AttentionSeverity"/> across active signals (as int), or <c>null</c> if none.</summary>
    int? HighestSeverity { get; set; }

    /// <summary>Count of uncleared attention signals.</summary>
    int ActiveSignalCount { get; set; }
}
