namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>
/// Marker interface — implement on an entity to opt it into the attention system with
/// default JSON-shadow storage (signals persisted as a JSON array on the entity row).
/// Good for low/medium-volume entities; signals travel with the row, never require a
/// join. Use <see cref="IHasIndexedAttention"/> instead when cross-entity "needs
/// attention" views matter — implement exactly one of the two, never both.
/// </summary>
/// <remarks>
/// The three summary properties below are maintained by the framework on save. Declare
/// them on the entity class so EF Core maps them as columns, but never assign them
/// manually — the values come from the evaluator pipeline. <see cref="IHasIndexedAttention"/>
/// already extends this interface, so the framework picks the storage mode by checking
/// the more specific interface first.
/// </remarks>
public interface IHasAttention
{
    /// <summary>Framework-maintained. <c>true</c> if any uncleared signal exists for this entity.</summary>
    bool HasActiveAttention { get; set; }

    /// <summary>Framework-maintained. Max severity across active signals, or <c>null</c> if none.</summary>
    AttentionSeverity? HighestSeverity { get; set; }

    /// <summary>Framework-maintained. Count of uncleared signals for this entity.</summary>
    int ActiveSignalCount { get; set; }
}

/// <summary>
/// Variant of <see cref="IHasAttention"/> that stores signals as rows in the universal
/// <c>AttentionSignals</c> table instead of as JSON on the entity row. Use this for
/// high-volume entities, or when cross-entity surfaces (the <c>&lt;NeedsAttentionList&gt;</c>
/// page, aggregation count badge) matter — only indexed entities appear in cross-entity
/// queries. Implement exactly one of <see cref="IHasAttention"/> or this, never both.
/// </summary>
/// <remarks>
/// The three summary properties from <see cref="IHasAttention"/> are still
/// framework-maintained on the entity row, so list-level filtering and sorting against
/// them remain a cheap column lookup — the indexed table is only consulted when reading
/// individual signals or running cross-entity aggregations.
/// </remarks>
public interface IHasIndexedAttention : IHasAttention
{
}
