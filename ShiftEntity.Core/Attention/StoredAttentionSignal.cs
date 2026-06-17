using System;

namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>
/// One signal as the framework persists it — the persisted counterpart to
/// <see cref="AttentionSignal"/>. The shape is the same in both storage modes; what
/// differs is <em>where</em> the rows live. Read this from the attention history dialog
/// and from <see cref="IRequiresAttentionHistory{TEntity}"/> evaluators.
/// </summary>
/// <remarks>
/// In default mode (<see cref="IHasAttention"/>), instances are serialized into a JSON
/// array in the entity's framework-managed shadow property. In indexed mode
/// (<see cref="IHasIndexedAttention"/>), each instance is a row in the universal
/// <c>AttentionSignals</c> table — only in that mode are <see cref="Id"/>,
/// <see cref="EntityType"/>, and <see cref="EntityId"/> populated.
/// </remarks>
public sealed record StoredAttentionSignal
{
    /// <summary>Row id in the <c>AttentionSignals</c> table. Populated in indexed mode only.</summary>
    public long? Id { get; init; }

    /// <summary>Entity type name. Populated in indexed mode only.</summary>
    public string? EntityType { get; init; }

    /// <summary>Entity row id (hash-encoded for client serialization). Populated in indexed mode only.</summary>
    public string? EntityId { get; init; }

    /// <inheritdoc cref="AttentionSignal.Source"/>
    public required string Source { get; init; }

    /// <inheritdoc cref="AttentionSignal.Category"/>
    public required string Category { get; init; }

    /// <inheritdoc cref="AttentionSignal.Reason"/>
    public string? Reason { get; init; }

    /// <inheritdoc cref="AttentionSignal.Severity"/>
    public AttentionSeverity Severity { get; init; }

    /// <inheritdoc cref="AttentionSignal.PayloadJson"/>
    public string? PayloadJson { get; init; }

    /// <inheritdoc cref="AttentionSignal.ClearScope"/>
    public string? ClearScope { get; init; }

    /// <summary>When the signal was raised.</summary>
    public DateTimeOffset RaisedAt { get; init; }

    /// <summary>When the signal was cleared, or <c>null</c> while still active.</summary>
    public DateTimeOffset? ClearedAt { get; init; }

    /// <summary>User id that cleared the signal, or <c>null</c> while still active.</summary>
    public long? ClearedByUserId { get; init; }
}
