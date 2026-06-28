
using ShiftSoftware.ShiftEntity.Core.Attention;

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

    /// <summary>Highest attention severity across active signals, or <c>null</c> if none. Same
    /// <see cref="AttentionSeverity"/> type as the entity-side <c>IHasAttention.HighestSeverity</c>
    /// </summary>
    AttentionSeverity? HighestSeverity { get; set; }

    /// <summary>Count of uncleared attention signals.</summary>
    int ActiveSignalCount { get; set; }
}
