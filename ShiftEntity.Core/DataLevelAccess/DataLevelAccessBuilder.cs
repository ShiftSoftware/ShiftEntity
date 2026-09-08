using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using ShiftSoftware.TypeAuth.Core.Actions;

namespace ShiftSoftware.ShiftEntity.Core.DataLevelAccess;

/// <summary>
/// Fluent declaration of an entity's data-level dimensions (D11 predicate-contributor model). Passed to
/// <c>ShiftRepositoryOptions.DataLevelAccess(...)</c>. The declaration compiles into the policy that drives both the
/// query filter and row check. Dimensions AND-compose; a dimension's predicate is OR-internal.
/// </summary>
public sealed class DataLevelAccessBuilder<TEntity>
{
    private readonly List<DataLevelDimension<TEntity>> dimensions = new();
    private DataLevelDeniedBehavior? deniedBehavior;

    /// <summary>The dimensions declared so far, in declaration order.</summary>
    public IReadOnlyList<DataLevelDimension<TEntity>> Dimensions => dimensions;

    /// <summary>True once <see cref="Unscoped"/> has been called — the entity intentionally has no data-level scope.</summary>
    public bool IsUnscoped { get; private set; }

    /// <summary>
    /// What a denied single-row View surfaces as (see <see cref="DataLevelDeniedBehavior"/>):
    /// <see cref="DataLevelDeniedBehavior.NotFound"/> unless <see cref="WhenDenied"/> declared otherwise.
    /// </summary>
    public DataLevelDeniedBehavior DeniedBehavior => deniedBehavior ?? DataLevelDeniedBehavior.NotFound;

    /// <summary>
    /// True when <see cref="WhenDenied"/> was explicitly called. Profiles may contribute dimensions only, so the
    /// repository uses this declaration flag (rather than the defaulted <see cref="DeniedBehavior"/> value) to
    /// reject a profile that tries to control row-disclosure behavior.
    /// </summary>
    internal bool HasDeniedBehavior => deniedBehavior is not null;

    /// <summary>Declares a dimension whose accessible set comes from a TypeAuth dynamic action.</summary>
    public DataLevelDimensionBuilder<TEntity> On(DynamicAction action)
        => AddDimension(new TypeAuthValueSource(action));

    /// <summary>Declares an owner dimension whose accessible set is the caller's own id for <paramref name="claimType"/>.</summary>
    public DataLevelDimensionBuilder<TEntity> OnOwner(string claimType)
        => AddDimension(new OwnerClaimValueSource(claimType));

    /// <summary>
    /// Explicitly opts the entity out of data-level scoping (documents "no scope"). Mutually exclusive with
    /// declaring dimensions — calling it after a dimension, or a dimension after it, throws.
    /// </summary>
    public void Unscoped()
    {
        if (dimensions.Count > 0)
            throw new InvalidOperationException("Unscoped() cannot be combined with declared dimensions.");
        if (deniedBehavior is not null)
            throw new InvalidOperationException("Unscoped() cannot be combined with WhenDenied(...) — an unscoped entity never denies access.");

        IsUnscoped = true;
    }

    /// <summary>
    /// Declares what a denied single-row View surfaces as — invisible (<see cref="DataLevelDeniedBehavior.NotFound"/>,
    /// the default) or refused loudly (<see cref="DataLevelDeniedBehavior.Forbidden"/>, disclosing that the row
    /// exists). See <see cref="DataLevelDeniedBehavior"/> for the trade-off. Lists and Insert/Edit/Delete are
    /// unaffected. Meaningless on an unscoped entity (nothing is ever denied) — combining the two throws.
    /// </summary>
    public void WhenDenied(DataLevelDeniedBehavior behavior)
    {
        if (IsUnscoped)
            throw new InvalidOperationException("WhenDenied(...) cannot be combined with Unscoped() — an unscoped entity never denies access.");
        if (deniedBehavior is not null)
            throw new InvalidOperationException("A denied behavior has already been declared.");

        deniedBehavior = behavior;
    }

    /// <summary>
    /// Validates the declarations fail-closed: every declared dimension must also declare a predicate
    /// (Key/Keys/Match), and the declaration as a whole must scope something — dimensions or an explicit
    /// <see cref="Unscoped"/> (an empty declaration would compile to a policy that filters nothing: a silent,
    /// total fail-open on an entity that says it is protected). Called by the policy when it is compiled
    /// (Phase 2.3); exposed for tests.
    /// </summary>
    public void Validate()
    {
        if (dimensions.Count == 0 && !IsUnscoped)
            throw new InvalidOperationException(
                $"Data-level access on {typeof(TEntity).Name} declares no dimensions — declare at least one (On/OnOwner) or call Unscoped() to opt out explicitly.");

        foreach (var dimension in dimensions)
        {
            if (dimension.Predicate is null)
                throw new InvalidOperationException(
                    $"A data-level dimension on {typeof(TEntity).Name} declared a value source but no predicate — call Key, Keys, or Match.");
        }
    }

    /// <summary>
    /// Applies an explicit repository declaration over a builder that was seeded from a default profile. Explicit
    /// TypeAuth dimensions replace seeded dimensions for the same action (the same reference or action-tree path);
    /// all other seeded dimensions remain, and owner/custom dimensions are appended. Several explicit dimensions on
    /// the same action are kept together, allowing a consumer to deliberately AND-compose multiple predicates over
    /// one accessible set.
    /// </summary>
    /// <remarks>
    /// Internal because ShiftRepository owns composition centrally: profiles only contribute defaults and cannot
    /// silently drop the explicit declaration. Action identity follows TypeAuth: singleton reference when paths have
    /// not been populated yet, otherwise the action-tree path identifies equivalent action instances.
    /// </remarks>
    internal void ApplyOverridesFrom(DataLevelAccessBuilder<TEntity> overrides)
    {
        if (overrides is null)
            throw new ArgumentNullException(nameof(overrides));

        if (overrides.IsUnscoped)
        {
            dimensions.Clear();
            deniedBehavior = null;
            IsUnscoped = true;
            return;
        }

        var overriddenActions = new List<DynamicAction>();
        foreach (var dimension in overrides.dimensions)
            if (dimension.ValueSource is TypeAuthValueSource typeAuth
                && !overriddenActions.Exists(action => IsSameAction(action, typeAuth.Action)))
                overriddenActions.Add(typeAuth.Action);

        dimensions.RemoveAll(dimension =>
            dimension.ValueSource is TypeAuthValueSource typeAuth
            && overriddenActions.Exists(action => IsSameAction(action, typeAuth.Action)));

        dimensions.AddRange(overrides.dimensions);

        if (overrides.deniedBehavior is not null)
            deniedBehavior = overrides.deniedBehavior;
    }

    private static bool IsSameAction(DynamicAction left, DynamicAction right)
        => ReferenceEquals(left, right)
            || (left.Path is not null
                && right.Path is not null
                && string.Equals(left.Path, right.Path, StringComparison.Ordinal));

    private DataLevelDimensionBuilder<TEntity> AddDimension(DataLevelValueSource source)
    {
        if (IsUnscoped)
            throw new InvalidOperationException("Cannot declare a dimension after Unscoped().");

        var dimension = new DataLevelDimension<TEntity>(source);
        dimensions.Add(dimension);
        return new DataLevelDimensionBuilder<TEntity>(dimension);
    }
}

/// <summary>
/// Fluent builder for a single dimension, returned by <see cref="DataLevelAccessBuilder{TEntity}.On"/> /
/// <see cref="DataLevelAccessBuilder{TEntity}.OnOwner"/>. Records the predicate and options onto the dimension.
/// </summary>
public sealed class DataLevelDimensionBuilder<TEntity>
{
    private readonly DataLevelDimension<TEntity> dimension;

    internal DataLevelDimensionBuilder(DataLevelDimension<TEntity> dimension) => this.dimension = dimension;

    /// <summary>Matches a single (nullable long) key column against the accessible set.</summary>
    public DataLevelDimensionBuilder<TEntity> Key(Expression<Func<TEntity, long?>> selector)
        => Keys(selector);

    /// <summary>
    /// Matches any one of several key columns against the accessible set (OR). Selectors may be nav paths
    /// (e.g. <c>x =&gt; x.Menu.BrandID</c>). At least one selector is required.
    /// </summary>
    public DataLevelDimensionBuilder<TEntity> Keys(params Expression<Func<TEntity, long?>>[] selectors)
    {
        if (selectors is null)
            throw new ArgumentNullException(nameof(selectors));
        if (selectors.Length == 0)
            throw new ArgumentException("At least one selector is required.", nameof(selectors));

        SetPredicate(new KeysPredicate<TEntity>(selectors));
        return this;
    }

    /// <summary>
    /// Declares a custom predicate over the accessible set (escape hatch for child collections, related entities,
    /// or comparisons Key/Keys can't express).
    /// </summary>
    public DataLevelDimensionBuilder<TEntity> Match(DataLevelMatch<TEntity> match)
    {
        SetPredicate(new MatchPredicate<TEntity>(match));
        return this;
    }

    /// <summary>
    /// Declares that this dimension's ids are hashid-encoded as <typeparamref name="TDto"/> (decode for the query
    /// path, encode for the row check). Omit for raw long ids.
    /// </summary>
    public DataLevelDimensionBuilder<TEntity> HashId<TDto>()
    {
        if (dimension.HashIdDtoType is not null)
            throw new InvalidOperationException("HashId has already been declared for this dimension.");

        dimension.HashIdDtoType = typeof(TDto);
        return this;
    }

    /// <summary>
    /// Declares the single-valued claim whose first value resolves the TypeAuth self-reference key (own-data
    /// access). Valid only on a TypeAuth-action dimension (<see cref="DataLevelAccessBuilder{TEntity}.On"/>).
    /// </summary>
    public DataLevelDimensionBuilder<TEntity> Self(string claimType)
        => SetSelf(claimType, usesAllClaims: false);

    /// <summary>
    /// Declares a repeated claim whose every value resolves the TypeAuth self-reference key. Use this only for a
    /// genuinely multi-valued membership claim such as Teams; scalar identity claims should use <see cref="Self"/>
    /// so duplicate or malformed claims cannot widen access.
    /// </summary>
    public DataLevelDimensionBuilder<TEntity> SelfMany(string claimType)
        => SetSelf(claimType, usesAllClaims: true);

    private DataLevelDimensionBuilder<TEntity> SetSelf(string claimType, bool usesAllClaims)
    {
        if (claimType is null)
            throw new ArgumentNullException(nameof(claimType));
        if (dimension.ValueSource is not TypeAuthValueSource)
            throw new InvalidOperationException("Self(...) and SelfMany(...) apply only to a TypeAuth-action dimension declared with On(...).");
        if (dimension.SelfClaimType is not null)
            throw new InvalidOperationException("Self or SelfMany has already been declared for this dimension.");

        dimension.SelfClaimType = claimType;
        dimension.UsesAllSelfClaims = usesAllClaims;
        return this;
    }

    private void SetPredicate(DataLevelPredicate<TEntity> predicate)
    {
        if (dimension.Predicate is not null)
            throw new InvalidOperationException("A predicate (Key/Keys/Match) has already been declared for this dimension.");

        dimension.Predicate = predicate;
    }
}
