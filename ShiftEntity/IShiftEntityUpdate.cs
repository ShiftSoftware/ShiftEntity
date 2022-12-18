using System;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core
{
    public interface IShiftEntityUpdate<EntityType,UpdateDTOType>
        where EntityType: class
    {
        public EntityType Update(EntityType entity, UpdateDTOType updateDto, Guid? userId = null);
    }

    public interface IShiftEntityUpdateAsync<EntityType, UpdateDTOType>
        where EntityType : class
    {
        public Task<EntityType> UpdateAsync(EntityType entity, UpdateDTOType updateDto, Guid? userId = null);
    }
}
