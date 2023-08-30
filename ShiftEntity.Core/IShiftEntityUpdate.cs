using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftEntityUpdateAsync<EntityType, DTOType>
    where EntityType : class
{
    public ValueTask<EntityType> UpdateAsync(EntityType entity, DTOType dto, long? userId = null);
}
