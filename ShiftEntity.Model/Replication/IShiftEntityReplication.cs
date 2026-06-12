namespace ShiftSoftware.ShiftEntity.Model.Replication;

/// <summary>
/// Opt-in surface for entities that replicate to Cosmos — the replication counterpart of the audit seam
/// (<c>IShiftEntityAudit</c> in ShiftEntity.Core). Both sync paths (the after-save trigger and the catch-up
/// service) require it through their generic constraints, and the replication bookkeeping columns live here as a
/// pair — never on the base entity — so a table either participates in replication and carries both columns, or
/// doesn't participate and carries neither. Both columns are written together by the pipeline's
/// <c>MarkReplicated</c> extension (ShiftEntity.CosmosDbReplication); application code should not set either
/// directly.
/// </summary>
public interface IShiftEntityReplication
{
    /// <summary>
    /// The replication <b>watermark</b>, not a timestamp — despite the name, this does not record when replication
    /// ran. It holds the <c>LastSaveDate</c> of the row version that was last replicated to Cosmos: exact
    /// equality with <c>LastSaveDate</c> means "in sync", a later save moves <c>LastSaveDate</c> past the
    /// watermark and the row becomes due for replication again, and <see langword="null"/> means never
    /// replicated. (The two columns therefore always show identical values for in-sync rows.)
    /// </summary>
    /// <remarks>
    /// Deliberately NOT the wall-clock time replication ran (it briefly was, at the feature's birth — fixed in
    /// 815f606): stamping "now" loses concurrent edits. When replication loads version T and a user saves T+1
    /// while the sync is in flight, writing the loaded version's save date (T) keeps the row dirty for the next
    /// sync, while writing "now" (&gt; T+1) would mark it clean and the edit would never reach Cosmos.
    /// </remarks>
    DateTimeOffset? LastReplicationDate { get; set; }

    /// <summary>
    /// The document id + partition key the row was last successfully replicated under. Both replication paths
    /// persist a serialized stamp here on every successful upsert, and use it to detect an id or partition-key
    /// change and delete the stale document under its OLD id + key before upserting the new one.
    /// </summary>
    /// <remarks>
    /// The value is an opaque JSON string owned by the replication pipeline — a serialized
    /// <c>ShiftSoftware.ShiftEntity.CosmosDbReplication.LastReplicationStamp</c>. Like the watermark, it maps as an
    /// ordinary nullable column with no special EF configuration; the interface itself carries no Cosmos
    /// dependency, so any entity project can implement it freely.
    /// </remarks>
    string? LastReplicationStamp { get; set; }
}
