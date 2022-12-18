using System;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftEntityCreate<EntityType,CreateDTOType>
    where EntityType : class
{
    public EntityType Create(CreateDTOType createDto, Guid? userId = null);
}

public interface IShiftEntityCreateAsync<EntityType, CreateDTOType>
    where EntityType : class
{
    public Task<EntityType> CreateAsync(CreateDTOType createDto, Guid? userId = null);
}
