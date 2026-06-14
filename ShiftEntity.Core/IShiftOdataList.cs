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

    /// <summary>
    /// <see cref="GetIQueryable(DateTimeOffset?, List{string}?, bool, bool)"/> with the named
    /// <see cref="RepositoryBypass"/> vocabulary instead of the positional bool pair. A default implementation
    /// forwards to the bool member, so existing implementors get it for free.
    /// </summary>
    public ValueTask<IQueryable<EntityType>> GetIQueryable(
        DateTimeOffset? asOf = null, List<string>? includes = null, RepositoryBypass bypass = RepositoryBypass.None)
        => GetIQueryable(asOf, includes,
            disableDefaultDataLevelAccess: bypass.HasFlag(RepositoryBypass.DataLevelAccess),
            disableGlobalFilters: bypass.HasFlag(RepositoryBypass.GlobalFilters));

    public ValueTask<IQueryable<ListDTO>> ApplyPostODataProcessing(IQueryable<ListDTO> queryable);
}