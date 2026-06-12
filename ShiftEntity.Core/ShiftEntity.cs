using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// Non-generic seam exposing the audit fields the repository's SaveChanges sweep stamps. Implemented by
/// <see cref="ShiftEntity{EntityType}"/> so the sweep can stamp EVERY changed auditable row in a unit of work —
/// cascaded children and unrelated entities alike — without needing each one's closed generic type.
/// </summary>
public interface IShiftEntityAudit
{
    DateTimeOffset CreateDate { get; set; }
    DateTimeOffset LastSaveDate { get; set; }
    long? CreatedByUserID { get; set; }
    long? LastSavedByUserID { get; set; }
    bool IsDeleted { get; set; }
    bool AuditFieldsAreSet { get; set; }
}

public abstract class ShiftEntity<EntityType> : ShiftEntityBase<EntityType>, IShiftEntityAudit where EntityType : class
{
    public DateTimeOffset CreateDate { get; set; }
    public DateTimeOffset LastSaveDate { get; set; }

    /// <summary>
    /// The replication <b>watermark</b>, not a timestamp — despite the name, this does not record when replication
    /// ran. It holds the <see cref="LastSaveDate"/> of the row version that was last replicated to Cosmos: exact
    /// equality with <see cref="LastSaveDate"/> means "in sync", a later save moves <see cref="LastSaveDate"/>
    /// past the watermark and the row becomes due for replication again, and <see langword="null"/> means never
    /// replicated. (The two columns therefore always show identical values for in-sync rows.)
    /// </summary>
    /// <remarks>
    /// Deliberately NOT the wall-clock time replication ran (it briefly was, at the feature's birth — fixed in
    /// 815f606): stamping "now" loses concurrent edits. When replication loads version T and a user saves T+1
    /// while the sync is in flight, writing the loaded version's save date (T) keeps the row dirty for the next
    /// sync, while writing "now" (&gt; T+1) would mark it clean and the edit would never reach Cosmos.
    /// </remarks>
    public DateTimeOffset? LastReplicationDate { get; internal set; }
    public long? CreatedByUserID { get; set; }
    public long? LastSavedByUserID { get; set; }
    public bool IsDeleted { get; set; }

    [NotMapped]
    public bool ReloadAfterSave { get; set; }

    [NotMapped]
    public bool AuditFieldsAreSet { get; set; }

    public ShiftEntity()
    {

    }

    public ShiftEntity(long id)
    {
        this.ID = id;
    }

    /// <summary>
    /// Marks the currently-loaded version of this row as replicated: copies <see cref="LastSaveDate"/> into
    /// <see cref="LastReplicationDate"/>. See that property's remarks for why this must copy the loaded
    /// version's save date and never stamp the current time.
    /// </summary>
    public void UpdateReplicationDate()
    {
        LastReplicationDate = LastSaveDate;
    }
}
