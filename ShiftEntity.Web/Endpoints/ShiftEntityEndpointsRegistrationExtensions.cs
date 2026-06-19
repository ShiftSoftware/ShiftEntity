using Microsoft.Extensions.DependencyInjection.Extensions;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Web.Endpoints;
using System.Linq;
using System.Reflection;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service-side registration for attribute-driven CRUD endpoints. Registers, for every entity decorated
/// with <c>[ShiftEntityEndpoint&lt;…&gt;]</c> / <c>[ShiftEntitySecureEndpoint&lt;…&gt;]</c>, the built-in
/// repository (for repository-less entities), the default AutoMapper map, and the entity→DTO registry
/// entry. Pair it with <c>app.MapShiftEntityEndpoints&lt;DB&gt;(assemblies)</c> in the pipeline to map
/// the routes (same Add… + Map… pattern as the other ShiftEntity surfaces).
/// </summary>
public static class ShiftEntityEndpointsServiceCollectionExtensions
{
    /// <summary>
    /// Discovers entity endpoint attributes in <paramref name="assemblies"/> and registers their DI needs.
    /// Call once with the assembly(ies) that contain your entities, then call
    /// <c>app.MapShiftEntityEndpoints&lt;DB&gt;(...)</c> with the same assemblies to map the endpoints.
    /// </summary>
    public static IServiceCollection AddShiftEntityEndpoints<DB>(this IServiceCollection services, params Assembly[] assemblies)
        where DB : ShiftDbContext
    {
        var specs = ShiftEntityEndpointDiscovery.Discover(assemblies);

        foreach (var spec in specs)
        {
            // Built-in repository for repository-less entities (custom repos register themselves /
            // are registered by RegisterShiftRepositories). TryAdd keeps this idempotent.
            services.TryAddScoped(ShiftEntityGeneratedEndpoints.ResolveRepositoryType(spec, typeof(DB)));

            // Default entity↔DTO map (deduped in the AutoMapper config so a custom map wins).
            services.Configure<ShiftEntityOptions>(o => o.AddEndpointDefaultMap(spec.Entity, spec.ListDto, spec.ViewDto));

            // Mirror the entity → view-DTO entry the repository scanner would add (attention/tagging
            // cross-entity endpoints resolve the DTO by entity name from this registry).
            EnsureShiftEntityDtoMap(services).Register(spec.Entity.Name, spec.ViewDto);
        }

        return services;
    }

    private static ShiftEntityDtoMap EnsureShiftEntityDtoMap(IServiceCollection services)
    {
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(ShiftEntityDtoMap));
        var map = existing?.ImplementationInstance as ShiftEntityDtoMap;

        if (map is null)
        {
            map = new ShiftEntityDtoMap();
            services.AddSingleton(map);
        }

        return map;
    }
}
