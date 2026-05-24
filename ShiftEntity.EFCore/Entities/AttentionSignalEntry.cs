using ShiftSoftware.ShiftEntity.Core.Attention;

namespace ShiftSoftware.ShiftEntity.EFCore.Entities;

public class AttentionSignalEntry
{
    public long ID { get; set; }
    public string EntityType { get; set; } = default!;
    public long EntityId { get; set; }
    public string Source { get; set; } = default!;
    public string Category { get; set; } = default!;
    public string? Reason { get; set; }
    public AttentionSeverity Severity { get; set; }
    public string? PayloadJson { get; set; }
    public DateTimeOffset RaisedAt { get; set; }
    public DateTimeOffset? ClearedAt { get; set; }
    public long? ClearedByUserId { get; set; }

    public StoredAttentionSignal ToStoredSignal() => new()
    {
        Id = ID,
        EntityType = EntityType,
        EntityId = EntityId,
        Source = Source,
        Category = Category,
        Reason = Reason,
        Severity = Severity,
        PayloadJson = PayloadJson,
        RaisedAt = RaisedAt,
        ClearedAt = ClearedAt,
        ClearedByUserId = ClearedByUserId,
    };

    public static AttentionSignalEntry FromStoredSignal(
        StoredAttentionSignal signal,
        string entityType,
        long entityId) => new()
    {
        EntityType = signal.EntityType ?? entityType,
        EntityId = signal.EntityId ?? entityId,
        Source = signal.Source,
        Category = signal.Category,
        Reason = signal.Reason,
        Severity = signal.Severity,
        PayloadJson = signal.PayloadJson,
        RaisedAt = signal.RaisedAt,
        ClearedAt = signal.ClearedAt,
        ClearedByUserId = signal.ClearedByUserId,
    };
}
