namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>
/// Severity of an attention signal. Drives UI rendering (colour, icon), the entity's
/// <see cref="IHasAttention.HighestSeverity"/> rollup, and consumer-side filtering.
/// </summary>
public enum AttentionSeverity
{
    Info = 1,
    Warning = 2,
    Critical = 3,
}
