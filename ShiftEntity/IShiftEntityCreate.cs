using System;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftEntityCreate<EntityType,CreateDTOType>
    where EntityType : class
    where CreateDTOType : ICrudDTO
{
    public EntityType Create(CreateDTOType crudDto, Guid? userId = null);
}
