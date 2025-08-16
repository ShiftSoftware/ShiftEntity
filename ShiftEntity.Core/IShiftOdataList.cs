using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftOdataList<EntityType, ListDTO> 
    where ListDTO : ShiftEntityDTOBase
    where EntityType : class
{
    public IQueryable<ListDTO> OdataList(IQueryable<EntityType>? queryable = null);

    public IQueryable<EntityType> GetIQueryable(DateTimeOffset? asOf = null, List<string>? includes = null, bool disableDefaultDataLevelAccess = false);
}
