using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShiftSoftware.ShiftEntity.Core;

public abstract class ShiftEntity<EntityType> : ShiftEntityBase
    where EntityType : class
{

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

        CreatedByUserID = userId;
        LastSavedByUserID = userId;

        return this as EntityType;
    }

    public EntityType UpdateShiftEntity(long? userId = null)
    {
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
