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

    public static bool HasWhereOnProperty<T>(this IQueryable<T> query, Expression<Func<T, object>> propertyExpression)
    {
        if (propertyExpression == null)
            throw new ArgumentNullException(nameof(propertyExpression));

        var expression = query.Expression;
        var finder = new WherePropertyFinder(propertyExpression);
        finder.Visit(expression);
        return finder.FoundWhereOnProperty;
    }
}

internal class WherePropertyFinder : ExpressionVisitor
{
    private readonly List<string> propertyPath;

    public bool FoundWhereOnProperty { get; private set; } = false;

    public WherePropertyFinder(LambdaExpression propertyExpression)
    {
        propertyPath = GetPropertyPath(propertyExpression.Body);
    }

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        // Early exit if already found
        if (FoundWhereOnProperty)
            return node;

        if (node.Method.DeclaringType == typeof(Queryable) && node.Method.Name == "Where")
        {
            if (node.Arguments.Count > 1)
            {
                var lambdaExpression = node.Arguments[1];

                if (lambdaExpression is UnaryExpression unaryExpression && unaryExpression.Operand is LambdaExpression lambda)
                {
                    var propertyFinder = new PropertyAccessFinder(propertyPath);
                    propertyFinder.Visit(lambda.Body);

                    if (propertyFinder.Found)
                    {
                        FoundWhereOnProperty = true;
                        return node;
                    }
                }
            }
        }
        return base.VisitMethodCall(node);
    }

    private static List<string> GetPropertyPath(Expression expression)
    {
        var path = new List<string>();
        while (true)
        {
            if (expression is MemberExpression member)
            {
                path.Insert(0, member.Member.Name);
                expression = member.Expression!;
            }
            else if (expression is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
            {
                expression = unary.Operand;
            }
            else
            {
                break;
            }
        }
        return path;
    }

    /// <summary>
    /// Inner visitor that traverses the lambda body to find property access.
    /// Stops as soon as the property is found.
    /// </summary>
    private class PropertyAccessFinder : ExpressionVisitor
    {
        private readonly List<string> _targetPath;
        public bool Found { get; private set; }

        public PropertyAccessFinder(List<string> targetPath)
        {
            _targetPath = targetPath;
        }

        public override Expression? Visit(Expression? node)
        {
            // Early exit - stop traversal once found
            if (Found || node == null)
                return node;

            return base.Visit(node);
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (Found)
                return node;

            var path = GetMemberPath(node);
            if (path.SequenceEqual(_targetPath))
            {
                Found = true;
                return node;
            }

            return base.VisitMember(node);
        }

        private static List<string> GetMemberPath(MemberExpression expression)
        {
            var path = new List<string>();
            Expression? current = expression;

            while (current != null)
            {
                if (current is MemberExpression member)
                {
                    path.Insert(0, member.Member.Name);
                    current = member.Expression;
                }
                else if (current is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
                {
                    current = unary.Operand;
                }
                else
                {
                    break;
                }
            }
            return path;
        }
    }
}