using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Web.Endpoints;
using System.Linq;
using System.Reflection;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service-side registration for attribute-driven CRUD endpoints. A single call wires everything an
/// entity decorated with <c>[ShiftEntityEndpoint&lt;…&gt;]</c> / <c>[ShiftEntitySecureEndpoint&lt;…&gt;]</c>
/// needs: the built-in repository (for repository-less entities), the default AutoMapper map, the
/// entity→DTO registry entry, and the endpoints themselves — mapped automatically, with no
/// <c>app.Map…</c> call (the app just needs endpoint routing enabled, which it has whenever it maps
/// any other endpoint such as controllers).
/// </summary>
public static class ShiftEntityEndpointsServiceCollectionExtensions
{
    /// <summary>
    /// Discovers entity endpoint attributes in <paramref name="assemblies"/> and registers what they
    /// need, including automatic endpoint mapping. Call once, passing the assembly(ies) that contain
    /// your entities (pass them all in a single call).
    /// </summary>
    public static IServiceCollection AddShiftEntityEndpoints<DB>(this IServiceCollection services, params Assembly[] assemblies)
        where DB : ShiftDbContext
    {
        var specs = ShiftEntityEndpointDiscovery.Discover(assemblies);

        if (specs.Count == 0)
            return services;

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

        // A single registry + data source + startup filter aggregate every AddShiftEntityEndpoints
        // call, so calling this twice (or for several DBs) never double-registers a route.
        var registry = GetOrAddRegistry(services);
        foreach (var spec in specs)
            registry.Add(spec, typeof(DB));

        return services;
    }

    private static ShiftEntityGeneratedEndpointRegistry GetOrAddRegistry(IServiceCollection services)
    {
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(ShiftEntityGeneratedEndpointRegistry))
            ?.ImplementationInstance as ShiftEntityGeneratedEndpointRegistry;

        if (existing is not null)
            return existing;

        var registry = new ShiftEntityGeneratedEndpointRegistry();
        services.AddSingleton(registry);

        // The data source builds the endpoints; the startup filter adds it to the app's endpoint route
        // builder (WebApplication does not auto-include DI-registered endpoint data sources).
        services.AddSingleton<EndpointDataSource>(sp => new ShiftEntityGeneratedEndpointDataSource(sp, registry));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupFilter, ShiftEntityGeneratedEndpointsStartupFilter>());

        return registry;
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
