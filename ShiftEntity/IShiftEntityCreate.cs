using System;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftEntityCreate<EntityType,DTOType>
    where EntityType : class
{
    public EntityType Create(DTOType dto, Guid? userId = null);
}

public interface IShiftEntityCreateAsync<EntityType, DTOType>
    where EntityType : class
{
    public Task<EntityType> CreateAsync(DTOType dto, Guid? userId = null);
}
