using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model.Replication;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication;

/// <summary>
/// Write-side of the replication bookkeeping pair. Both <see cref="IShiftEntityReplication"/> columns are stamped
/// here and nowhere else, so the watermark and the stamp cannot drift apart: a successful sync records them
/// together.
/// </summary>
public static class ShiftEntityReplicationExtensions
{
    /// <summary>
    /// Marks the currently-loaded version of a row as replicated: copies <see cref="IShiftEntityAudit.LastSaveDate"/>
    /// into <see cref="IShiftEntityReplication.LastReplicationDate"/> and records the Cosmos coordinates the row
    /// now lives under in <see cref="IShiftEntityReplication.LastReplicationStamp"/>. See the watermark property's
    /// remarks for why this must copy the loaded version's save date and never stamp the current time.
    /// </summary>
    /// <param name="stamp">The serialized <see cref="LastReplicationStamp"/> of the document just upserted.</param>
    public static void MarkReplicated<TEntity>(this TEntity entity, string? stamp)
        where TEntity : class, IShiftEntityAudit, IShiftEntityReplication
    {
        entity.LastReplicationDate = entity.LastSaveDate;
        entity.LastReplicationStamp = stamp;
    }
}
