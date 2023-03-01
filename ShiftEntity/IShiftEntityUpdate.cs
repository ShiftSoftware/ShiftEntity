using System;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core
{
    public interface IShiftEntityUpdate<EntityType,DTOType>
        where EntityType: class
    {
        public EntityType Update(EntityType entity, DTOType dto, long? userId = null);
    }

    public interface IShiftEntityUpdateAsync<EntityType, DTOType>
        where EntityType : class
    {
        public Task<EntityType> UpdateAsync(EntityType entity, DTOType dto, long? userId = null);
    }
}
