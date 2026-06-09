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
    public DateTimeOffset? LastReplicationDate { get; internal set; }

    /// <summary>
    /// The Cosmos partition key (serialized) this row was last replicated under. Persisted by the replication
    /// pipeline so the next sync can detect a partition-key change and delete the stale document under the OLD
    /// key before upserting under the new one — without reconstructing the previous entity from temporal history.
    /// </summary>
    public string? LastReplicationPartitionKey { get; internal set; }
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
    /// Set LastReplicationDate to LastSaveDate
    /// </summary>
    public void UpdateReplicationDate()
    {
        LastReplicationDate = LastSaveDate;
    }

    /// <summary>
    /// Record the Cosmos partition key (serialized) this row was last replicated under.
    /// </summary>
    public void SetLastReplicationPartitionKey(string? partitionKey)
    {
        LastReplicationPartitionKey = partitionKey;
    }
}
