using System;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftEntityDelete<EntityType> where EntityType : class
{
    public EntityType Delete(EntityType entity, long? userId = null);
}

public interface IShiftEntityDeleteAsync<EntityType> where EntityType : class
{
    public ValueTask<EntityType> DeleteAsync(EntityType entity, long? userId = null);
}
