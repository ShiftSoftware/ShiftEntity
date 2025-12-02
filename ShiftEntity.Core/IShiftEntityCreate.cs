using System;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftEntityCreateAsync<EntityType, DTOType> where EntityType : class
{
    public ValueTask<EntityType> CreateAsync(
        DTOType dto,
        long? userId,
        Guid? IdempotencyKey,
        bool disableDefaultDataLevelAccess,
        bool disableGlobalFilters
    );
}