using ShiftSoftware.ShiftEntity.Core.Attention;

namespace ShiftSoftware.ShiftEntity.EFCore.Entities;

/// <summary>
/// EF Core entity for the universal <c>AttentionSignals</c> table, used by
/// <see cref="IHasIndexedAttention"/> entities. Each row is one raised signal.
/// For JSON-shadow mode entities, signals are stored inline via
/// <see cref="ShiftSoftware.ShiftEntity.EFCore.Attention.AttentionSignalJsonHelper"/> instead.
/// </summary>
public class AttentionSignalEntry
{
    /// <summary>Primary key.</summary>
    public long ID { get; set; }

    /// <summary>CLR type name of the entity that owns this signal.</summary>
    public string EntityType { get; set; } = default!;

    /// <summary>Database ID of the owning entity.</summary>
    public long EntityId { get; set; }

    /// <inheritdoc cref="AttentionSignal.Source"/>
    public string Source { get; set; } = default!;

    /// <inheritdoc cref="AttentionSignal.Category"/>
    public string Category { get; set; } = default!;

    /// <inheritdoc cref="AttentionSignal.Reason"/>
    public string? Reason { get; set; }

    /// <inheritdoc cref="AttentionSignal.Severity"/>
    public AttentionSeverity Severity { get; set; }

    /// <inheritdoc cref="AttentionSignal.PayloadJson"/>
    public string? PayloadJson { get; set; }

    /// <inheritdoc cref="AttentionSignal.ClearScope"/>
    public string? ClearScope { get; set; }

    /// <summary>When the signal was raised.</summary>
    public DateTimeOffset RaisedAt { get; set; }

    /// <summary>When the signal was cleared, or <c>null</c> while still active.</summary>
    public DateTimeOffset? ClearedAt { get; set; }

    /// <summary>User ID that cleared the signal, or <c>null</c> while still active.</summary>
    public long? ClearedByUserId { get; set; }

    /// <summary>Converts this table row to the storage-agnostic <see cref="StoredAttentionSignal"/> shape.</summary>
    public StoredAttentionSignal ToStoredSignal() => new()
    {
        Id = ID,
        EntityType = EntityType,
        EntityId = EntityId.ToString(),
        Source = Source,
        Category = Category,
        Reason = Reason,
        Severity = Severity,
        PayloadJson = PayloadJson,
        ClearScope = ClearScope,
        RaisedAt = RaisedAt,
        ClearedAt = ClearedAt,
        ClearedByUserId = ClearedByUserId,
    };

    /// <summary>Creates a table row from a <see cref="StoredAttentionSignal"/> for indexed-mode persistence.</summary>
    public static AttentionSignalEntry FromStoredSignal(
        StoredAttentionSignal signal,
        string entityType,
        long entityId) => new()
    {
        EntityType = signal.EntityType ?? entityType,
        EntityId = signal.EntityId is not null ? long.Parse(signal.EntityId) : entityId,
        Source = signal.Source,
        Category = signal.Category,
        Reason = signal.Reason,
        Severity = signal.Severity,
        PayloadJson = signal.PayloadJson,
        ClearScope = signal.ClearScope,
        RaisedAt = signal.RaisedAt,
        ClearedAt = signal.ClearedAt,
        ClearedByUserId = signal.ClearedByUserId,
    };
}
