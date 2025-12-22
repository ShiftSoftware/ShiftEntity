using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData;
using Microsoft.OData.UriParser;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Web.Services;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace System.Linq;

public static class IQueryableExtensions
{
    public static async ValueTask<ODataDTO<T>> ToOdataDTO<T>(
        this IQueryable<T> data,
        ODataQueryOptions<T> oDataQueryOptions,
        HttpRequest httpRequest,
        bool isAsync = true,
        bool applySoftDeleteFilter = true,
        Func<IQueryable<T>, ValueTask<IQueryable<T>>>? applyPostODataProcessing = null
    ) where T : ShiftEntityDTOBase
    {
        var options = httpRequest.HttpContext.RequestServices.GetRequiredService<ShiftEntityOptions>();

        if (oDataQueryOptions.Filter != null)
        {
            FilterClause? filterClause = oDataQueryOptions.Filter?.FilterClause!;

            var modifiedFilterNode = filterClause.Expression.Accept(new HashIdQueryNodeVisitor<T>());

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

        if (applySoftDeleteFilter)
            data = data.ApplyDefaultSoftDeleteFilter();

        if (applyPostODataProcessing is not null)
            data = isAsync? await applyPostODataProcessing(data) : applyPostODataProcessing(data).Result;

        if (oDataQueryOptions.OrderBy != null)
            data = oDataQueryOptions.OrderBy.ApplyTo(data, new ODataQuerySettings() { EnsureStableOrdering = true }) as IQueryable<T>;

        var count = isAsync ? await data.CountAsync() : data.Count();

        if (oDataQueryOptions.Skip != null)
            data = data.Skip(oDataQueryOptions.Skip.Value);

        var top = options.DefaultTop;
        if (oDataQueryOptions.Top != null)
            top = oDataQueryOptions.Top.Value;

        if (options.MaxTop > 0 && (top == 0 || top > options.MaxTop))
            top = options.MaxTop;

        if (top > 0)
            data = data.Take(top);

        return new ODataDTO<T>
        {
            Count = count,
            Value = isAsync ? await data.ToListAsync() : data.ToList(),

            // Uncomment the following line if you need to check converter cache during debugging
            //ConverterCache = HashIdQueryNodeVisitor<T>._converterCache.ToDictionary(x => $"{x.Key.Item1.FullName}.{x.Key.Item2}", x => x.Value?.GetType()?.FullName)
        };
    }

    private static IQueryable<EntityType> ApplyDefaultSoftDeleteFilter<EntityType>(
        this IQueryable<EntityType> query
    ) where EntityType : ShiftEntityDTOBase
    {
        if (!query.HasWhereOnProperty(x=> x.IsDeleted))
            query = query.Where(x => !x.IsDeleted);

        return query;
    }
}