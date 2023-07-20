using System;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftEntityDeleteAsync<EntityType> where EntityType : ShiftEntity<EntityType>
{
    public ValueTask<EntityType> DeleteAsync(EntityType entity, bool isSoftDelete = false, long? userId = null);
}
