using Microsoft.AspNetCore.Routing;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Web.Endpoints;
using System.Reflection;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the attribute-driven CRUD endpoints. Discovers entities decorated with
/// <c>[ShiftEntityEndpoint&lt;…&gt;]</c> / <c>[ShiftEntitySecureEndpoint&lt;…&gt;]</c> in
/// <paramref name="assemblies"/> and registers their minimal-API endpoints on the application's route
/// builder. Pair with <c>builder.Services.AddShiftEntityEndpoints&lt;DB&gt;(assemblies)</c>.
/// </summary>
public static class ShiftEntityEndpointsEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapShiftEntityEndpoints<DB>(this IEndpointRouteBuilder endpoints, params Assembly[] assemblies)
        where DB : ShiftDbContext
    {
        var specs = ShiftEntityEndpointDiscovery.Discover(assemblies);
        ShiftEntityGeneratedEndpoints.Generate(endpoints, specs, typeof(DB));
        return endpoints;
    }
}
