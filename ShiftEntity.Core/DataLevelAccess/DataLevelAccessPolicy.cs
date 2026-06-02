using System;
using System.Collections.Generic;
using System.Linq;
using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Linq;

namespace ShiftSoftware.ShiftEntity.Core.DataLevelAccess;

/// <summary>
/// Compiled from a <see cref="DataLevelAccessBuilder{TEntity}"/>: applies the declared dimensions to a query,
/// AND-composing them (each dimension's predicate is OR-internal). A wildcard dimension adds no constraint.
/// <para>
/// This slice implements the query path for <b>TypeAuth-action dimensions</b> (<c>On(...).Key/Keys/Match</c>) —
/// the cross-column OR. Owner-claim sources, <c>Self</c> resolution, and <c>HashId</c> decoding <b>fail closed</b>
/// (throw) until a follow-up slice wires <see cref="ICurrentUserProvider"/> / <c>IHashIdService</c>; failing closed
/// keeps a declared-but-not-yet-applied dimension from silently widening access.
/// </para>
/// </summary>
public sealed class DataLevelAccessPolicy<TEntity>
{
    private readonly IReadOnlyList<DataLevelDimension<TEntity>> dimensions;

    /// <summary>True when the entity was explicitly declared <see cref="DataLevelAccessBuilder{TEntity}.Unscoped"/>.</summary>
    public bool IsUnscoped { get; }

    public DataLevelAccessPolicy(DataLevelAccessBuilder<TEntity> builder)
    {
        if (builder is null)
            throw new ArgumentNullException(nameof(builder));

        builder.Validate(); // fail-closed: reject a dimension declared without a predicate
        dimensions = builder.Dimensions.ToList();
        IsUnscoped = builder.IsUnscoped;
    }

    /// <summary>
    /// Narrows <paramref name="query"/> to the rows the caller can reach at <paramref name="access"/>, AND-composing
    /// every declared dimension. A wildcard dimension adds no constraint; an empty accessible set matches nothing.
    /// </summary>
    public IQueryable<TEntity> ApplyQueryFilter(IQueryable<TEntity> query, Access access, IAccessibleItemsSource source)
    {
        if (query is null)
            throw new ArgumentNullException(nameof(query));
        if (source is null)
            throw new ArgumentNullException(nameof(source));

        foreach (var dimension in dimensions)
            query = ApplyDimension(query, dimension, access, source);

        return query;
    }

    private static IQueryable<TEntity> ApplyDimension(
        IQueryable<TEntity> query, DataLevelDimension<TEntity> dimension, Access access, IAccessibleItemsSource source)
    {
        // Deferred to the next slice (need ICurrentUserProvider / IHashIdService). Fail closed rather than silently
        // skip — an unapplied dimension would widen access.
        if (dimension.ValueSource is not TypeAuthValueSource typeAuth)
            throw new NotSupportedException("Owner-claim dimensions (OnOwner) are not applied yet.");
        if (dimension.SelfClaimType is not null)
            throw new NotSupportedException("Self-claim resolution is not applied yet.");
        if (dimension.HashIdDtoType is not null)
            throw new NotSupportedException("HashId decoding is not applied yet.");

        var accessible = source.GetByAccess(typeAuth.Action).For(access);

        switch (dimension.Predicate)
        {
            // Scalar OR across the key columns: col1 ∈ set OR col2 ∈ set … WhereAccessible handles wildcard (no
            // filter), empty (matches nothing) and the EmptyOrNullKey ⇒ null-FK convention. This is the cross-column OR.
            case KeysPredicate<TEntity> keys:
                return query.WhereAccessible(accessible, long.Parse, keys.Selectors.ToArray());

            // Escape hatch: skip entirely when wildcard (no constraint), else hand the decoded set to the factory.
            case MatchPredicate<TEntity> match:
                if (accessible.WildCard)
                    return query;
                var accessibleIds = accessible.ConvertIds(long.Parse)!; // non-null: not wildcard
                return query.Where(match.Match(accessibleIds, Array.Empty<long?>()));

            default:
                throw new NotSupportedException($"Unsupported data-level predicate '{dimension.Predicate?.GetType().Name}'.");
        }
    }
}
