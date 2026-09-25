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
    /// <param name="stamp">The serialized <see cref="LastReplicationStamp"/> of the document just upserted.</param>
    public static void MarkReplicated<TEntity>(this TEntity entity, string? stamp)
        where TEntity : class, IShiftEntityAudit, IShiftEntityReplication
    {
        entity.LastReplicationDate = entity.LastSaveDate;
        entity.LastReplicationStamp = stamp;
    }

    /// <summary>
    /// Writes the pair <see cref="MarkReplicated"/> set on <paramref name="entity"/> to its row, and nothing else: one
    /// UPDATE of the two replication columns, by key, that neither tracks the instance nor runs triggers or the audit
    /// backfill. The after-save trigger records its sync with this because the instance it holds is the caller's: the
    /// caller's context still tracks it and may have linked new rows to it since. Attaching it to another context
    /// would walk that graph and insert those rows a second time.
    /// </summary>
    internal static Task<int> SaveReplicationBookkeepingAsync<TEntity>(this ShiftDbContext db, TEntity entity,
        CancellationToken cancellationToken = default)
        where TEntity : ShiftEntity<TEntity>, IShiftEntityReplication
    {
        var id = entity.ID;
        var date = entity.LastReplicationDate;
        var stamp = entity.LastReplicationStamp;

        return db.Set<TEntity>().IgnoreQueryFilters()
            .Where(x => x.ID == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastReplicationDate, date)
                .SetProperty(x => x.LastReplicationStamp, stamp), cancellationToken);
    }
}
