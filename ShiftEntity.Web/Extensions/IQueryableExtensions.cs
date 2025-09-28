using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.OData;
using Microsoft.OData.UriParser;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Web.Services;
using System.Threading.Tasks;

namespace System.Linq;

public static class IQueryableExtensions
{
    public static async ValueTask<ODataDTO<T>> ToOdataDTO<T>(
        this IQueryable<T> data, 
        ODataQueryOptions<T> oDataQueryOptions, 
        HttpRequest httpRequest, 
        bool isAsync = true
    )
    {
        if (oDataQueryOptions.Filter != null)
        {
            FilterClause? filterClause = oDataQueryOptions.Filter?.FilterClause!;

            var modifiedFilterNode = filterClause.Expression.Accept(new HashIdQueryNodeVisitor());

            FilterClause modifiedFilterClause = new FilterClause(modifiedFilterNode, filterClause.RangeVariable);

            ODataUriParser parser = new ODataUriParser(oDataQueryOptions.Context.Model, new Uri("", UriKind.Relative));

            var odataUri = parser.ParseUri();

            odataUri.Filter = modifiedFilterClause;

            var updatedUrl = odataUri.BuildUri(parser.UrlKeyDelimiter).ToString();

            var newQueryString = new Microsoft.AspNetCore.Http.QueryString(updatedUrl.Substring(updatedUrl.IndexOf("?")));

            httpRequest.QueryString = newQueryString;

            var modifiedODataQueryOptions = new ODataQueryOptions<T>(oDataQueryOptions.Context, httpRequest);

            data = (modifiedODataQueryOptions.Filter.ApplyTo(data, new ODataQuerySettings() { EnsureStableOrdering = true }) as IQueryable<T>)!;
        }

        if (oDataQueryOptions.OrderBy != null)
            data = oDataQueryOptions.OrderBy.ApplyTo(data, new ODataQuerySettings() { EnsureStableOrdering = true }) as IQueryable<T>;

        var count = isAsync ? await data.CountAsync() : data.Count();

        if (oDataQueryOptions.Skip != null)
            data = data.Skip(oDataQueryOptions.Skip.Value);

        if (oDataQueryOptions.Top != null)
            data = data.Take(oDataQueryOptions.Top.Value);

        return new ODataDTO<T>
        {
            Count = count,
            Value = isAsync ? await data.ToListAsync() : data.ToList(),
        };
    }

    public static IQueryable<EntityType> ApplyDefaultSoftDeleteFilter<EntityType>(
        this IQueryable<EntityType> query,
        ODataQueryOptions<EntityType> oDataQueryOptions
    ) where EntityType : ShiftEntityDTOBase
    {
        bool isFilteringByIsDeleted = false;

        FilterClause? filterClause = oDataQueryOptions.Filter?.FilterClause;

        if (filterClause is not null)
        {
            var visitor = new SoftDeleteQueryNodeVisitor();

            var visited = filterClause.Expression.Accept(visitor);

            isFilteringByIsDeleted = visitor.IsFilteringByIsDeleted;
        }

        if (!isFilteringByIsDeleted)
            query = query.Where(x => x.IsDeleted == false);

        return query;
    }
}