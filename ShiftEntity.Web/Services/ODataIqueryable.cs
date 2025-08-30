using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.OData;
using Microsoft.OData.UriParser;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web.Services;

public class ODataIqueryable
{
    public static async ValueTask<ODataDTO<T>> GetOdataDTOFromIQueryableAsync<T>(IQueryable<T> data, ODataQueryOptions<T> oDataQueryOptions, HttpRequest httpRequest, bool runAsync = true)
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

        var count = runAsync ? await data.CountAsync() : data.Count();

        if (oDataQueryOptions.Skip != null)
            data = data.Skip(oDataQueryOptions.Skip.Value);

        if (oDataQueryOptions.Top != null)
            data = data.Take(oDataQueryOptions.Top.Value);

        return new ODataDTO<T>
        {
            Count = count,
            Value = runAsync ? await data.ToListAsync() : data.ToList(),
        };
    }

    public static IQueryable<T> ApplyDefaultSoftDeleteFilter<T>(IQueryable<T> data, ODataQueryOptions<T> oDataQueryOptions) where T : ShiftEntityDTOBase
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
            data = data.Where(x => x.IsDeleted == false);

        return data;
    }
}