using System;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;
public interface IShiftEntityCreateAsync<EntityType, DTOType> where EntityType : class
{
    public ValueTask<EntityType> CreateAsync(DTOType dto, long? userId = null, Guid? IdempotencyKey = null);
}
