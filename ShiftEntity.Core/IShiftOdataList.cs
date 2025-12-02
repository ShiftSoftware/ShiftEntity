using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftOdataList<EntityType, ListDTO>
    where ListDTO : ShiftEntityDTOBase
    where EntityType : class
{
    public ValueTask<IQueryable<ListDTO>> OdataList(IQueryable<EntityType>? queryable);

    public ValueTask<IQueryable<EntityType>> GetIQueryable(
        DateTimeOffset? asOf,
        List<string>? includes,
        bool disableDefaultDataLevelAccess,
        bool disableGlobalFilters
    );
}