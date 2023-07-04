using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShiftSoftware.ShiftEntity.Core;

public abstract class ShiftEntity<EntityType> : IShiftEntity
    where EntityType : class
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long ID { get; private set; }
    public DateTime CreateDate { get; private set; }
    public DateTime LastSaveDate { get; private set; }
    public long? CreatedByUserID { get; private set; }
    public long? LastSavedByUserID { get; private set; }
    public bool IsDeleted { get; private set; }

    [NotMapped]
    public bool ReloadAfterSave { get; set; }

    public ShiftEntity()
    {
        
    }

    public ShiftEntity(long id)
    {
        this.ID = id;
    }

    public EntityType CreateShiftEntity(long? userId = null, long? id = null)
    {
        if (id is not null)
            this.ID = id.Value;

        var now = DateTime.UtcNow;

        LastSaveDate = now;
        CreateDate = now;

        CreatedByUserID = userId;
        LastSavedByUserID = userId;

        IsDeleted = false;

        return this as EntityType;
    }

    public EntityType UpdateShiftEntity(long? userId = null)
    {
        var now = DateTime.UtcNow;

        LastSaveDate = now;
        LastSavedByUserID = userId;

        return this as EntityType;
    }

    public EntityType DeleteShiftEntity(long? userId = null)
    {
        UpdateShiftEntity(userId);

        IsDeleted = true;

        return this as EntityType;
    }
}
