using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShiftSoftware.ShiftEntity.Core;

public abstract class ShiftEntity<EntityType> : ShiftEntityBase<EntityType> where EntityType : class
{
    public DateTimeOffset CreateDate { get; internal set; }
    public DateTimeOffset LastSaveDate { get; internal set; }
    public DateTimeOffset? LastReplicationDate { get; internal set; }
    public long? CreatedByUserID { get; internal set; }
    public long? LastSavedByUserID { get; internal set; }
    public bool IsDeleted { get; internal set; }

    [NotMapped]
    public bool ReloadAfterSave { get; set; }

    [NotMapped]
    internal Action<EntityType>? BeforeCommitValidation { get; set; }

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
}
