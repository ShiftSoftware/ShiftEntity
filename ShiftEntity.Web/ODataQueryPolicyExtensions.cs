using Microsoft.AspNetCore.OData.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData.UriParser;
using System;

namespace ShiftSoftware.ShiftEntity.Web;

internal static class ODataQueryPolicyExtensions
{
    public static SingleValueNode? ApplyPolicies<T>(this ODataQueryOptions<T> options, IServiceProvider services) where T : class
    {
        var filter = options.Filter?.FilterClause.Expression;
        foreach (var policy in services.GetServices<IODataQueryPolicy>())
            filter = policy.Apply(options, filter);
        return filter;
    }
}
