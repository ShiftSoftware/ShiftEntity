using Microsoft.AspNetCore.OData.Query;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Web.Services;

namespace System.Linq;

public static class IQueryableExtensions
{
    public static IQueryable<EntityType> ApplyDefaultSoftDeleteFilter<EntityType>(
        this IQueryable<EntityType> query,
        ODataQueryOptions<EntityType> oDataQueryOptions
    ) where EntityType : ShiftEntityDTOBase
    {
        return ODataIqueryable.ApplyDefaultSoftDeleteFilter(query, oDataQueryOptions);
    }
}