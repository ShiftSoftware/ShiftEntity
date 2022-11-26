using System;

namespace ShiftSoftware.ShiftEntity.Core
{
    public interface IShiftEntityUpdate<EntityType,UpdateDTOType>
        where EntityType: class
        where UpdateDTOType : ICrudDTO
    {
        public EntityType Update(UpdateDTOType crudDto, Guid? userId = null);
    }
}
