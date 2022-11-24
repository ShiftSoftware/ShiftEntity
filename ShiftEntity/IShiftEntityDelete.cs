using System;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftEntityDelete<EntityType> where EntityType : class
{
    public EntityType Delete(Guid? userId = null);
}
