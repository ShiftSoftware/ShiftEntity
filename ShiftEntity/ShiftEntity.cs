using System;

namespace ShiftSoftware.ShiftEntity.Core;

public abstract class ShiftEntity<EntityType> : IShiftEntity
    where EntityType : class
{
    public Guid ID { get; private set; }
    public DateTime CreateDate { get; private set; }
    public DateTime LastSaveDate { get; private set; }
    public Guid? CreatedByUserID { get; private set; }
    public Guid? LastSavedByUserID { get; private set; }
    public bool IsDeleted { get; private set; }

    public EntityType CreateShiftEntity(Guid? userId = null)
    {
        var now = DateTime.UtcNow;

        LastSaveDate = now;
        CreateDate = now;

        CreatedByUserID = userId;
        LastSavedByUserID = userId;

        IsDeleted = false;

        return this as EntityType;
    }

    public EntityType UpdateShiftEntity(Guid? userId = null)
    {
        var now = DateTime.UtcNow;

        LastSaveDate = now;
        LastSavedByUserID = userId;

        return this as EntityType;
    }

    public EntityType DeleteShiftEntity(Guid? userId = null)
    {
        UpdateShiftEntity(userId);

        IsDeleted = true;

        return this as EntityType;
    }
}
