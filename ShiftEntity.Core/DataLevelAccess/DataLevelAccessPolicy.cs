using System;
using System.Collections.Generic;
using System.Linq;
using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Linq;

namespace ShiftSoftware.ShiftEntity.Core.DataLevelAccess;

/// <summary>
/// Compiled from a <see cref="DataLevelAccessBuilder{TEntity}"/>: enforces the declared dimensions on both the query
/// path (<see cref="ApplyQueryFilter"/>) and the row path (<see cref="Authorize"/>), AND-composing them (each
/// dimension's predicate is OR-internal). A wildcard dimension adds no constraint; an empty accessible set matches
/// nothing (fail closed).
/// <para>
/// Both paths run the same per-dimension <see cref="ResolveValues"/> step over a <see cref="DataLevelAccessContext"/> —
/// resolving every declared kind (TypeAuth-action sources via <c>On(...)</c>, with <c>Self</c> self-reference
/// resolution and optional <c>HashId&lt;TDto&gt;</c> id decoding; owner-claim sources via <c>OnOwner(...)</c>) to one
/// decoded value list — then emit either a query filter (<c>WhereIn</c>) or an in-memory membership test from it. One
/// source, two emissions (D5), so the two paths cannot drift.
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
    public IQueryable<TEntity> ApplyQueryFilter(IQueryable<TEntity> query, Access access, DataLevelAccessContext context)
    {
        if (query is null)
            throw new ArgumentNullException(nameof(query));
        if (context is null)
            throw new ArgumentNullException(nameof(context));

        foreach (var dimension in dimensions)
            query = ApplyDimension(query, dimension, access, context);

        return query;
    }

    /// <summary>
    /// Whether the caller may act on a single <paramref name="entity"/> at <paramref name="access"/> — the row twin of
    /// <see cref="ApplyQueryFilter"/>, evaluated in memory against the same per-request <paramref name="context"/>.
    /// AND-composes every declared dimension (all must pass); within a dimension the key columns are OR-internal. A
    /// wildcard dimension passes; an empty accessible set denies (fail closed). An unscoped entity (no declared
    /// dimensions) is always authorized.
    /// <para>
    /// The level is picked per operation by <paramref name="access"/> (View⇒Read, Insert/Edit⇒Write, Delete⇒Delete,
    /// D6): a Read-only grant authorizes a View of the row but gates Insert/Edit/Delete — the row path's reason to
    /// exist, closing the worst case of defect #1 (an unguarded Insert of a wholly out-of-scope row).
    /// </para>
    /// </summary>
    public bool Authorize(TEntity entity, Access access, DataLevelAccessContext context)
    {
        if (entity is null)
            throw new ArgumentNullException(nameof(entity));
        if (context is null)
            throw new ArgumentNullException(nameof(context));

        foreach (var dimension in dimensions)
            if (!AuthorizeDimension(entity, dimension, access, context))
                return false; // one failing dimension denies the row (dimensions AND-compose)

        return true;
    }

    private static IQueryable<TEntity> ApplyDimension(
        IQueryable<TEntity> query, DataLevelDimension<TEntity> dimension, Access access, DataLevelAccessContext context)
    {
        // Resolve the dimension's accessible set to a concrete value list. null ⇒ wildcard (the caller can reach
        // everything on this dimension) ⇒ no constraint; empty ⇒ matches nothing.
        var values = ResolveValues(dimension, access, context);

        switch (dimension.Predicate)
        {
            // Scalar OR across the key columns: col1 ∈ set OR col2 ∈ set … WhereIn handles wildcard (null ⇒ no
            // filter), empty (matches nothing) and the null-entry ⇒ null-FK convention. This is the cross-column OR.
            case KeysPredicate<TEntity> keys:
                return query.WhereIn(values, keys.Selectors.ToArray());

            // Escape hatch: skip entirely when wildcard (no constraint), else hand the decoded set to the factory.
            case MatchPredicate<TEntity> match:
                return values is null ? query : query.Where(match.Match(values));

            default:
                throw new NotSupportedException($"Unsupported data-level predicate '{dimension.Predicate?.GetType().Name}'.");
        }
    }

    /// <summary>
    /// The row twin of <see cref="ApplyDimension"/>: whether <paramref name="entity"/> satisfies one dimension at
    /// <paramref name="access"/>. Resolves the same accessible-value list and applies the same predicate semantics in
    /// memory — wildcard (null) ⇒ passes; <c>Keys</c> ⇒ the set contains any one key column (OR); <c>Match</c> ⇒ the
    /// consumer predicate evaluated against the set.
    /// </summary>
    private static bool AuthorizeDimension(
        TEntity entity, DataLevelDimension<TEntity> dimension, Access access, DataLevelAccessContext context)
    {
        // Same per-dimension resolution as the query path (null ⇒ wildcard, empty ⇒ matches nothing), so the two
        // paths cannot drift.
        var values = ResolveValues(dimension, access, context);

        // Wildcard ⇒ no constraint on this dimension ⇒ it passes (mirrors the query path adding no Where).
        if (values is null)
            return true;

        switch (dimension.Predicate)
        {
            // OR across the key columns: reachable iff the accessible set contains ANY column's value. A null column
            // matches a null entry (a granted null FK), the same convention as WhereIn; an empty set matches nothing.
            case KeysPredicate<TEntity> keys:
                foreach (var selector in keys.CompiledSelectors)
                    if (values.Contains(selector(entity)))
                        return true;
                return false;

            // Escape hatch: evaluate the consumer predicate in memory against the resolved set (wildcard handled above).
            case MatchPredicate<TEntity> match:
                return match.Match(values).Compile()(entity);

            default:
                throw new NotSupportedException($"Unsupported data-level predicate '{dimension.Predicate?.GetType().Name}'.");
        }
    }

    /// <summary>
    /// Resolves a dimension's accessible-id set at <paramref name="access"/> to a value list: <see langword="null"/>
    /// for a wildcard grant (⇒ no constraint), otherwise the decoded ids (a <see langword="null"/> entry ⇒ null-FK
    /// match). Owner-claim dimensions never wildcard — an absent owner claim yields an empty set (⇒ matches nothing,
    /// fail closed).
    /// </summary>
    private static List<long?>? ResolveValues(DataLevelDimension<TEntity> dimension, Access access, DataLevelAccessContext context)
    {
        var converter = Converter(dimension, context.HashIds);

        switch (dimension.ValueSource)
        {
            case TypeAuthValueSource typeAuth:
            {
                // Self(claim): hand the caller's claim value to TypeAuth as the self-id so a self-reference grant
                // resolves to the caller's own id (folded into the accessible set) — exactly as the legacy path does.
                var selfIds = ResolveSelfIds(dimension, context);
                return context.Source.GetByAccess(typeAuth.Action, selfIds).For(access).ConvertIds(converter);
            }

            case OwnerClaimValueSource owner:
            {
                // OnOwner: the set IS the caller's own id, read from a claim — no TypeAuth grant. Absent ⇒ empty set
                // ⇒ matches nothing (fail closed). The claim is decoded with the same converter as a grant id would be.
                var raw = context.GetClaim(owner.ClaimType);
                return raw is null ? new List<long?>() : new List<long?> { converter(raw) };
            }

            default:
                throw new NotSupportedException($"Unsupported data-level value source '{dimension.ValueSource?.GetType().Name}'.");
        }
    }

    /// <summary>
    /// The string→id converter for a dimension: hashid-decode as the declared DTO when <c>HashId&lt;TDto&gt;</c> was
    /// set, else raw <see cref="long.Parse(string)"/>. The <em>same</em> converter decodes the grant ids for both the
    /// query path (<see cref="ApplyDimension"/> → <c>WhereIn</c>) and the row path (<see cref="AuthorizeDimension"/> →
    /// membership of the entity's key columns), so the two paths resolve identical id-spaces and cannot drift (D5).
    /// </summary>
    private static Func<string, long> Converter(DataLevelDimension<TEntity> dimension, IHashIdService hashIds)
    {
        if (dimension.HashIdDtoType is { } dtoType)
            return id => hashIds.Decode(id, dtoType);

        return long.Parse;
    }

    /// <summary>
    /// The self-ids fed to TypeAuth for self-reference resolution: the caller's claim value when <c>Self(claim)</c>
    /// was declared (none when the claim is absent — the self-reference then resolves to nothing). The claim is passed
    /// verbatim, in the same encoding as the grant ids, to be decoded uniformly by <see cref="Converter"/>.
    /// </summary>
    private static string[] ResolveSelfIds(DataLevelDimension<TEntity> dimension, DataLevelAccessContext context)
    {
        if (dimension.SelfClaimType is null)
            return Array.Empty<string>();

        var raw = context.GetClaim(dimension.SelfClaimType);
        return raw is null ? Array.Empty<string>() : new[] { raw };
    }
}
