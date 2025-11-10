using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.OData;
using Microsoft.OData.UriParser;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Web.Services;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;

namespace System.Linq;

public static class IQueryableExtensions
{
    public static async ValueTask<ODataDTO<T>> ToOdataDTO<T>(
        this IQueryable<T> data,
        ODataQueryOptions<T> oDataQueryOptions,
        HttpRequest httpRequest,
        bool isAsync = true
    ) where T : ShiftEntityDTOBase
    {
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

        data = data.ApplyDefaultSoftDeleteFilter();

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

            // Uncomment the following line if you need to check converter cache during debugging
            //ConverterCache = HashIdQueryNodeVisitor<T>._converterCache.ToDictionary(x => $"{x.Key.Item1.FullName}.{x.Key.Item2}", x => x.Value?.GetType()?.FullName)
        };
    }

    private static IQueryable<EntityType> ApplyDefaultSoftDeleteFilter<EntityType>(
        this IQueryable<EntityType> query
    ) where EntityType : ShiftEntityDTOBase
    {
        var finder = new WhereSoftDeletePropertyFinder();

        finder.Visit(query.Expression);

        if (!finder.FoundWhereOnProperty)
        {
            query = query.Where(x => !x.IsDeleted);
        }

        return query;
    }
}

internal class WhereSoftDeletePropertyFinder : ExpressionVisitor
{
    public bool FoundWhereOnProperty { get; private set; } = false;

    public WhereSoftDeletePropertyFinder()
    {
    }

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        // 1. Check if it's a Queryable.Where method
        if (node.Method.DeclaringType == typeof(Queryable) && node.Method.Name == "Where")
        {
            // 2. Get the predicate (the second argument of the Where method)
            if (node.Arguments.Count > 1)
            {
                // The predicate is an Expression<Func<T, bool>>, often wrapped in a UnaryExpression
                // if it's quoted (Expression.Quote). We want the Operand.
                var lambdaExpression = node.Arguments[1];

                if (lambdaExpression is UnaryExpression unaryExpression && unaryExpression.Operand is LambdaExpression lambda)
                {
                    // 3. Inspect the body of the lambda expression
                    if (CheckExpressionForProperty(lambda.Body))
                    {
                        FoundWhereOnProperty = true;
                        // Once found, you can stop visiting
                        return node;
                    }
                }
            }
        }

        // Continue visiting the rest of the expression tree
        return base.VisitMethodCall(node);
    }

    private bool CheckExpressionForProperty(Expression expression)
    {
        // This is simplified to check for a direct property access comparison,
        // e.g., 'x.IsDeleted == true' or '!x.IsDeleted'

        if (expression is BinaryExpression binaryExpression)
        {
            // Check both sides for a property access expression
            return IsPropertyAccess(binaryExpression.Left) || IsPropertyAccess(binaryExpression.Right);
        }
        else if (expression is UnaryExpression unaryExpression && unaryExpression.NodeType == ExpressionType.Not)
        {
            // Check for negation, e.g., !x.IsDeleted
            return IsPropertyAccess(unaryExpression.Operand);
        }
        else if (expression is MemberExpression memberExpression)
        {
            // Check for direct property access as the body, e.g., query.Where(x => x.IsDeleted)
            return IsPropertyAccess(memberExpression);
        }
        else if (expression is MethodCallExpression methodCall)
        {
            // For complex clauses like (x.IsDeleted && x.Name == "Test")
            // you would recursively call CheckExpressionForProperty on the arguments.
            // This simple version skips complex AND/OR/NOT logic for brevity.
        }

        return false;
    }

    private bool IsPropertyAccess(Expression expression)
    {
        if (expression is MemberExpression memberExpression)
        {
            // Check if the member is a Property of the correct name
            return memberExpression.Member is PropertyInfo propertyInfo && propertyInfo.Name == nameof(ShiftEntityDTOBase.IsDeleted);
        }

        // Handle cases like 'true == x.IsDeleted' where the property is nested in a binary expression
        if (expression is BinaryExpression binaryExpression)
        {
            return IsPropertyAccess(binaryExpression.Left) || IsPropertyAccess(binaryExpression.Right);
        }

        return false;
    }
}