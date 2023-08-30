using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShiftSoftware.ShiftEntity.Core;

public abstract class ShiftEntity<EntityType>
    where EntityType : class
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long ID { get; internal set; }
    public DateTime CreateDate { get; internal set; }
    public DateTime LastSaveDate { get; internal set; }
    public DateTime? LastSyncDate { get; internal set; }
    public long? CreatedByUserID { get; internal set; }
    public long? LastSavedByUserID { get; internal set; }
    public bool IsDeleted { get; internal set; }

    public long? RegionID { get; set; }
    public long? CompanyID { get; set; }
    public long? CompanyBranchID { get; set; }

    [NotMapped]
    public bool ReloadAfterSave { get; set; }

    public ShiftEntity()
    {

    }

    public ShiftEntity(long id)
    {
        this.ID = id;
    }

    public void UpdateSyncDate()
    {
        LastSyncDate = DateTime.UtcNow;
    }

    public void MarkAsDeleted()
    {
        this.IsDeleted = true;
    }
}
