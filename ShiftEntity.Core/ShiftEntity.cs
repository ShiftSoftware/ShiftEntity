using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShiftSoftware.ShiftEntity.Core;

public abstract class ShiftEntity<EntityType> where EntityType : class
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long ID { get; internal set; }
    public DateTime CreateDate { get; internal set; }
    public DateTime LastSaveDate { get; internal set; }
    public DateTime? LastReplicationDate { get; internal set; }
    public long? CreatedByUserID { get; internal set; }
    public long? LastSavedByUserID { get; internal set; }
    public bool IsDeleted { get; internal set; }

    public long? RegionID { get; set; }
    public long? CompanyID { get; set; }
    public long? CompanyBranchID { get; set; }

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

    public void UpdateReplicationDate()
    {
        LastReplicationDate = LastSaveDate;
    }

    public void MarkAsDeleted()
    {
        this.IsDeleted = true;
    }

    public void MarkAsDeleted(long? userId)
    {
        MarkAsDeleted();
        Update(userId);
    }

    public EntityType Create(long? userId, long? id = null)
    {
        if(id != null)
            this.ID = id.Value;

        this.CreatedByUserID = userId;
        this.LastSavedByUserID = userId;
        this.IsDeleted = false;

        return (this as EntityType)!;
    }

    public EntityType Update(long? userId)
    {
        this.LastSavedByUserID = userId;

        return (this as EntityType)!;
    }
}
