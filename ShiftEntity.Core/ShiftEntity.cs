using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShiftSoftware.ShiftEntity.Core;

public abstract class ShiftEntity<EntityType> : ShiftEntityBase<EntityType> where EntityType : class
{
    public DateTimeOffset CreateDate { get; set; }
    public DateTimeOffset LastSaveDate { get; set; }
    public DateTimeOffset? LastReplicationDate { get; internal set; }
    public long? CreatedByUserID { get; set; }
    public long? LastSavedByUserID { get; set; }
    public bool IsDeleted { get; set; }

    [NotMapped]
    public bool ReloadAfterSave { get; set; }

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
