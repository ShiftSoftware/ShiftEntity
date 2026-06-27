namespace ShiftSoftware.ShiftEntity.Core.Attention;

// Physically in ShiftEntity.Model, deliberately under the ...Core.Attention namespace.
//
// In Model because Core references Model (not the other way round), so a type shared by
// the entity-side IHasAttention (Core) and the DTO-side IHasAttentionSummary (Model) must
// live in the lower assembly both can see. That shared type is what removes the per-consumer
// (int?) cast on HighestSeverity when mapping Entity -> ListDTO.
//
// Namespace stays ...Core.Attention because a namespace is a logical grouping, not the
// assembly's filename — the Core layer itself ships as assembly "ShiftSoftware.ShiftEntity",
// not *.Core, so namespace != assembly is already the norm here. The enum belongs with the
// ~18 other attention types, all in ...Core.Attention. Moving it to a ...Model.* namespace
// would orphan it from those siblings and make every consumer add a second using, for no
// real gain. (Same shape as System.IAsyncDisposable shipping from Microsoft.Bcl.AsyncInterfaces.)
// ShiftEntity.Core keeps a [TypeForwardedTo] (AttentionSeverity.TypeForward.cs) for binary compat.
//
// => Do not "fix" this by renaming the namespace.

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
