using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.GlobalRepositoryFilter;
using ShiftSoftware.ShiftEntity.Model.Flags;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace System.Linq;

public static class IQueryableExtensions
{
    public static IQueryable<EntityType> ApplyDefaultCountryFilter<EntityType>(
        this IQueryable<EntityType> query,
        IDefaultDataLevelAccess defaultDataLevelAccess
    ) where EntityType : IEntityHasCountry<EntityType>
    {
        return defaultDataLevelAccess.ApplyDefaultCountryFilter(query);
    }

    public static IQueryable<EntityType> ApplyDefaultRegionFilter<EntityType>(
        this IQueryable<EntityType> query,
        IDefaultDataLevelAccess defaultDataLevelAccess
    ) where EntityType : IEntityHasRegion<EntityType>
    {
        return defaultDataLevelAccess.ApplyDefaultRegionFilter(query);
    }

    public static IQueryable<EntityType> ApplyDefaultCompanyFilter<EntityType>(
        this IQueryable<EntityType> query,
        IDefaultDataLevelAccess defaultDataLevelAccess
    ) where EntityType : IEntityHasCompany<EntityType>
    {
        return defaultDataLevelAccess.ApplyDefaultCompanyFilter(query);
    }

    public static IQueryable<EntityType> ApplyDefaultBranchFilter<EntityType>(
        this IQueryable<EntityType> query,
        IDefaultDataLevelAccess defaultDataLevelAccess
    ) where EntityType : IEntityHasCompanyBranch<EntityType>
    {
        return defaultDataLevelAccess.ApplyDefaultBranchFilter(query);
    }

    public static IQueryable<EntityType> ApplyDefaultBrandFilter<EntityType>(
        this IQueryable<EntityType> query,
        IDefaultDataLevelAccess defaultDataLevelAccess
    ) where EntityType : IEntityHasBrand<EntityType>
    {
        return defaultDataLevelAccess.ApplyDefaultBrandFilter(query);
    }

    public static IQueryable<EntityType> ApplyDefaultCityFilter<EntityType>(
        this IQueryable<EntityType> query,
        IDefaultDataLevelAccess defaultDataLevelAccess
    ) where EntityType : IEntityHasCity<EntityType>
    {
        return defaultDataLevelAccess.ApplyDefaultCityFilter(query);
    }

    public static IQueryable<EntityType> ApplyDefaultTeamFilter<EntityType>(
        this IQueryable<EntityType> query,
        IDefaultDataLevelAccess defaultDataLevelAccess
    ) where EntityType : IEntityHasTeam<EntityType>
    {
        return defaultDataLevelAccess.ApplyDefaultTeamFilter(query);
    }

    public static IQueryable<EntityType> ApplyDefaultDataLevelAccessFilters<EntityType>(
        this IQueryable<EntityType> query,
        IDefaultDataLevelAccess defaultDataLevelAccess,
        DefaultDataLevelAccessOptions defaultDataLevelAccessOptions
    ) where EntityType : notnull
    {
        return defaultDataLevelAccess.ApplyDefaultDataLevelFilters(defaultDataLevelAccessOptions, query);
    }

    public static async ValueTask<IQueryable<EntityType>> ApplyGlobalRepositoryFiltersAsync<EntityType>(
        this IQueryable<EntityType> query,
        Dictionary<Guid, IGlobalRepositoryFilter> GlobalFilters
    ) where EntityType : ShiftEntity<EntityType>
    {
        if (GlobalFilters.Count == 0)
            return query;

        foreach (var filter in GlobalFilters.Values)
        {
            if (filter.Disabled)
                continue;

            var expression = await filter.GetFilterExpression<EntityType>();

            if (expression != null)
                query = query.Where(expression);
        }

        return query;
    }
}