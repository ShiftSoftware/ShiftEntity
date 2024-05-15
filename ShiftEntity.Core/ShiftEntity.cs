using System;
using System.ComponentModel.DataAnnotations;
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

    //public long? RegionID { get; set; }
    //public long? CompanyID { get; set; }
    //public long? CompanyBranchID { get; set; }

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

    public void MarkAsDeleted()
    {
        this.IsDeleted = true;
    }

    /// <summary>
    /// This is useful in non asp.net core projects
    /// </summary>
    /// <param name="userId"></param>
    public void MarkAsDeleted(long? userId)
    {
        MarkAsDeleted();
        Update(userId);
    }

    /// <summary>
    /// This is useful in non asp.net core projects
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="id"></param>
    /// <returns></returns>
    public EntityType Create(long? userId, long? id = null, DateTimeOffset? createDate = null, DateTimeOffset? lastSaveDate = null)
    {
        if(id != null)
            this.ID = id.Value;

        this.CreatedByUserID = userId;
        this.LastSavedByUserID = userId;
        this.IsDeleted = false;

        if(createDate != null)
            this.CreateDate = createDate.Value;

        if(lastSaveDate != null)
            this.LastSaveDate = lastSaveDate.Value;

        return (this as EntityType)!;
    }

    /// <summary>
    /// This is useful in non asp.net core projects
    /// </summary>
    /// <param name="userId"></param>
    /// <returns></returns>
    public EntityType Update(long? userId)
    {
        this.LastSavedByUserID = userId;

        return (this as EntityType)!;
    }
}
