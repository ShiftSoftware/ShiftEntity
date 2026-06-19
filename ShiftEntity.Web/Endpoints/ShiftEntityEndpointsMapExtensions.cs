using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Web.Endpoints;
using System;
using System.Linq;
using System.Reflection;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the attribute-driven CRUD endpoints for entities decorated with
/// <c>[ShiftEntityEndpoint&lt;…&gt;]</c> / <c>[ShiftEntitySecureEndpoint&lt;…&gt;]</c>. The DI side is wired
/// automatically by <c>RegisterShiftRepositories(...)</c>; this just maps the routes. When no
/// <paramref name="assemblies"/> are passed it uses the data assemblies registered via
/// <c>AddShiftEntityWeb(x =&gt; x.AddDataAssembly(...))</c>.
/// </summary>
public static class ShiftEntityEndpointsEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapShiftEntityEndpoints<DB>(this IEndpointRouteBuilder endpoints, params Assembly[] assemblies)
        where DB : ShiftDbContext
    {
        if (assemblies is null || assemblies.Length == 0)
            assemblies = endpoints.ServiceProvider.GetService<ShiftEntityOptions>()?.DataAssemblies.ToArray() ?? Array.Empty<Assembly>();

        var specs = ShiftEntityEndpointDiscovery.Discover(assemblies);
        ShiftEntityGeneratedEndpoints.Generate(endpoints, specs, typeof(DB));
        return endpoints;
    }
}
