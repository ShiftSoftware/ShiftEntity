using System;
using ShiftSoftware.TypeAuth.Core.Actions;

namespace ShiftSoftware.ShiftEntity.Core.DataLevelAccess;

/// <summary>
/// Where a dimension's accessible-id set comes from. The engine resolves this to an actual set at apply time
/// (Phase 2.3/2.4); slice 2.2 only records which source was declared. Closed hierarchy:
/// <see cref="TypeAuthValueSource"/> or <see cref="OwnerClaimValueSource"/>.
/// </summary>
public abstract class DataLevelValueSource
{
    private protected DataLevelValueSource() { }
}

/// <summary>
/// The accessible set comes from a TypeAuth dynamic action (the default — e.g. a data-level action such as
/// <c>Companies</c>). Resolved via <see cref="IAccessibleItemsSource"/> at apply time.
/// </summary>
public sealed class TypeAuthValueSource : DataLevelValueSource
{
    public DynamicAction Action { get; }

    public TypeAuthValueSource(DynamicAction action)
        => Action = action ?? throw new ArgumentNullException(nameof(action));
}

/// <summary>
/// The accessible set is the caller's own id for a claim (owner dimensions): a single value read from
/// <see cref="ICurrentUserProvider"/> at apply time (empty when the claim is absent). Never wildcard.
/// </summary>
public sealed class OwnerClaimValueSource : DataLevelValueSource
{
    public string ClaimType { get; }

    public OwnerClaimValueSource(string claimType)
        => ClaimType = claimType ?? throw new ArgumentNullException(nameof(claimType));
}
