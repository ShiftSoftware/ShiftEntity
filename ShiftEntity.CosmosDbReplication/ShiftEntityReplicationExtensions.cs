using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
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
    /// <remarks>
    /// For rows the caller loaded itself, as the catch-up does. The after-save trigger does not use it, because its
    /// entity is the saving caller's instance (see <see cref="SaveReplicationBookkeepingAsync{TEntity}"/>).
    /// </remarks>
    /// <param name="stamp">The serialized <see cref="LastReplicationStamp"/> of the document just upserted.</param>
    public static void MarkReplicated<TEntity>(this TEntity entity, string? stamp)
        where TEntity : class, IShiftEntityAudit, IShiftEntityReplication
    {
        entity.LastReplicationDate = entity.LastSaveDate;
        entity.LastReplicationStamp = stamp;
    }

    /// <summary>
    /// Records a sync of the row with key <paramref name="id"/>, and nothing else: one UPDATE of the two replication
    /// columns that runs no triggers and no audit backfill, with query filters ignored. The watermark is
    /// <paramref name="replicatedVersion"/>, the <see cref="IShiftEntityAudit.LastSaveDate"/> of the version the sync
    /// replicated (as <see cref="MarkReplicated{TEntity}"/> copies it); <paramref name="stamp"/> is the serialized
    /// <see cref="LastReplicationStamp"/> of the document the sync wrote. The after-save trigger records its syncs with
    /// this, from values it read from the entity, because the entity is the saving caller's instance: the caller's
    /// context still tracks it and may have linked new rows to it since. Setting the two columns on it would make the
    /// caller's next save write the row again and replicate it once more. Attaching it to another context would walk
    /// that graph and insert those rows a second time.
    /// </summary>
    internal static Task<int> SaveReplicationBookkeepingAsync<TEntity>(this ShiftDbContext db, long id,
        DateTimeOffset replicatedVersion, string? stamp, CancellationToken cancellationToken = default)
        where TEntity : ShiftEntity<TEntity>, IShiftEntityReplication
    {
        DateTimeOffset? date = replicatedVersion;

        return db.Set<TEntity>().IgnoreQueryFilters()
            .Where(x => x.ID == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastReplicationDate, date)
                .SetProperty(x => x.LastReplicationStamp, stamp), cancellationToken);
    }
}
