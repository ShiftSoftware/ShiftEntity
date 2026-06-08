using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.Core.DataLevelAccess;

/// <summary>
/// Escape-hatch predicate factory: given the dimension's decoded accessible ids (the engine skips wildcard
/// dimensions, so this is never a "wildcard"), returns an entity predicate. Use for child-collection /
/// related-entity / custom comparisons that Key/Keys can't express. To match the caller's <i>own</i> id, declare a
/// separate <c>OnOwner</c> dimension (which AND-composes) — a <c>Self</c> grant is already folded into
/// <paramref name="accessibleIds"/>, so there is no separate "self" argument here.
/// </summary>
/// <typeparam name="TEntity">The queried entity type.</typeparam>
/// <param name="accessibleIds">The decoded accessible ids for this dimension at the requested access level.</param>
public delegate Expression<Func<TEntity, bool>> DataLevelMatch<TEntity>(
    IReadOnlyCollection<long?> accessibleIds);

/// <summary>
/// How a dimension matches an entity against its accessible-id set. Closed hierarchy:
/// <see cref="KeysPredicate{TEntity}"/> (scalar OR over columns) or <see cref="MatchPredicate{TEntity}"/> (escape hatch).
/// </summary>
public abstract class DataLevelPredicate<TEntity>
{
    private protected DataLevelPredicate() { }
}

/// <summary>
/// Scalar OR over one or more (nullable long) key columns: <c>col1 ∈ set OR col2 ∈ set …</c>, null-safe per column.
/// Selectors may be nav paths (e.g. <c>x =&gt; x.Menu.BrandID</c>).
/// </summary>
public sealed class KeysPredicate<TEntity> : DataLevelPredicate<TEntity>
{
    public IReadOnlyList<Expression<Func<TEntity, long?>>> Selectors { get; }

    /// <summary>
    /// The selectors compiled to delegates for the in-memory row path, built once at construction (so a row check
    /// does not recompile per entity) and held in a readonly auto-property for safe publication to the threads that
    /// share a compiled policy. The query path uses <see cref="Selectors"/> (the expression form EF translates); the
    /// row path uses these — one declaration, two emissions (D5), so the paths cannot drift.
    /// </summary>
    public IReadOnlyList<Func<TEntity, long?>> CompiledSelectors { get; }

    public KeysPredicate(IReadOnlyList<Expression<Func<TEntity, long?>>> selectors)
    {
        Selectors = selectors ?? throw new ArgumentNullException(nameof(selectors));
        CompiledSelectors = selectors.Select(selector => selector.Compile()).ToArray();
    }
}

/// <summary>A consumer-supplied predicate factory (the escape hatch).</summary>
public sealed class MatchPredicate<TEntity> : DataLevelPredicate<TEntity>
{
    public DataLevelMatch<TEntity> Match { get; }

    public MatchPredicate(DataLevelMatch<TEntity> match)
        => Match = match ?? throw new ArgumentNullException(nameof(match));
}

/// <summary>
/// One declared data-level dimension: a <see cref="ValueSource"/> (where the accessible set comes from) + a
/// <see cref="Predicate"/> (how the entity is matched against it), plus an optional hashid DTO type and self-claim.
/// Dimensions AND-compose; each dimension's predicate is OR-internal. This is the recorded declaration — the query
/// filter and row check are applied by the policy in Phase 2.3/2.4.
/// </summary>
public sealed class DataLevelDimension<TEntity>
{
    /// <summary>Where this dimension's accessible-id set comes from.</summary>
    public DataLevelValueSource ValueSource { get; }

    /// <summary>How the entity is matched against the set. <see langword="null"/> until a predicate is declared (Key/Keys/Match).</summary>
    public DataLevelPredicate<TEntity>? Predicate { get; internal set; }

    /// <summary>The DTO type whose hashid configuration encodes/decodes this dimension's ids; <see langword="null"/> ⇒ raw long ids.</summary>
    public Type? HashIdDtoType { get; internal set; }

    /// <summary>The claim type whose value resolves the TypeAuth self-reference key; <see langword="null"/> ⇒ no self resolution.</summary>
    public string? SelfClaimType { get; internal set; }

    internal DataLevelDimension(DataLevelValueSource valueSource) => ValueSource = valueSource;
}
